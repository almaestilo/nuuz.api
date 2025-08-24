using Google.Cloud.Firestore;
using Nuuz.Application.DTOs;
using Nuuz.Application.Services;
using Nuuz.Domain.Entities;
using Nuuz.Application.Abstraction; // for IArticleRepository
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Services
{
    /// <summary>
    /// Feature EMAs + per-mood centroids stored in Firestore.
    ///
    /// Collections:
    ///   - MoodAffinities           (feature EMAs)
    ///       DocId: {userId}|{mood}|{type}|{normalizedKey}
    ///       Fields: userId, mood, type, key, score (-1..+1), count (int), updatedAt (Timestamp)
    ///
    ///   - UserMoodCentroids        (per user, per mood)
    ///       DocId: {userId}_{Mood}
    ///       Fields: userId, mood, vec (double[] L2), count (int), updatedAt
    ///
    ///   - MoodCentroids            (global per mood)
    ///       DocId: {Mood}
    ///       Fields: mood, vec (double[] L2), count (int), updatedAt
    ///
    ///   - ArticleFeedback          (event log)
    ///       DocId: GUID
    ///       Fields: userId, articleId, mood, action, createdAt
    /// </summary>
    public sealed class MoodFeedbackService : IMoodFeedbackService
    {
        private readonly FirestoreDb _db;
        private readonly IArticleRepository _articles;

        // EMA smoothing constant for features. Higher -> faster learning.
        private const double AlphaFeature = 0.35;

        // Centroid steps
        private const double AlphaUserPositive = 0.12;
        private const double AlphaUserNegative = 0.08;
        private const double AlphaGlobalPositive = 0.03; // gentle global drift

        public MoodFeedbackService(FirestoreDb db, IArticleRepository articles)
        {
            _db = db;
            _articles = articles;
        }

        // -------------------- PUBLIC API --------------------

        public async Task<(double[]? user, double[]? global)> GetMoodCentroidsAsync(
            string firebaseUid, string mood, CancellationToken ct)
        {
            var userDocId = $"{firebaseUid}_{NormalizeMood(mood)}";

            double[]? user = null, global = null;

            var userSnap = await _db.Collection("UserMoodCentroids").Document(userDocId).GetSnapshotAsync(ct);
            if (userSnap.Exists && userSnap.TryGetValue<List<double>>("vec", out var uv) && uv is { Count: > 0 })
                user = Normalize(uv);

            var globSnap = await _db.Collection("MoodCentroids").Document(NormalizeMood(mood)).GetSnapshotAsync(ct);
            if (globSnap.Exists && globSnap.TryGetValue<List<double>>("vec", out var gv) && gv is { Count: > 0 })
                global = Normalize(gv);

            return (user, global);
        }

        public async Task RecordFeedbackAsync(string firebaseUid, RecordMoodFeedbackDto dto, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(dto.ArticleId)) return;

            var mood = NormalizeMood(dto.Mood);
            var action = dto.Action;

            // 1) Load the article (for features + embedding)
            var art = await _articles.GetAsync(dto.ArticleId);
            if (art is null) return;

            // 2) Log the event
            var evtRef = _db.Collection("ArticleFeedback").Document(Guid.NewGuid().ToString("N"));
            await evtRef.SetAsync(new
            {
                userId = firebaseUid,
                articleId = dto.ArticleId,
                mood,
                action = action.ToString(),
                createdAt = Timestamp.FromDateTime(DateTime.UtcNow)
            }, cancellationToken: ct);

            // 3) Update feature EMAs (positive pulls up, negative pushes down)
            var features = new List<IMoodFeedbackService.Feature>(IMoodFeedbackService.ExtractFeaturesFromArticle(art));

            // Add a few high-signal extras if present
            if (!string.IsNullOrWhiteSpace(art.Genre)) features.Add(new IMoodFeedbackService.Feature("genre", art.Genre!));
            if (!string.IsNullOrWhiteSpace(art.EventStage)) features.Add(new IMoodFeedbackService.Feature("event", art.EventStage!));
            if (!string.IsNullOrWhiteSpace(art.Format)) features.Add(new IMoodFeedbackService.Feature("format", art.Format!));

            var signal = new IMoodFeedbackService.MoodSignal
            {
                UserId = firebaseUid,
                Mood = mood,
                Signal = IsPositive(action) ? +1 : -1,
                Timestamp = DateTimeOffset.UtcNow,
                Features = features
            };
            await RecordAsync(signal, ct);

            // 4) Update centroids
            if (art.TopicEmbedding is { Count: > 0 })
            {
                var sample = Normalize(art.TopicEmbedding);

                // 4a) User centroid (toward/away)
                var userDocId = $"{firebaseUid}_{mood}";
                var userRef = _db.Collection("UserMoodCentroids").Document(userDocId);
                var uSnap = await userRef.GetSnapshotAsync(ct);

                List<double>? currentU = null;
                int countU = 0;
                if (uSnap.Exists)
                {
                    _ = uSnap.TryGetValue("vec", out currentU);
                    _ = uSnap.TryGetValue("count", out countU);
                }

                var newUser = UpdateCentroid(currentU, sample,
                    toward: IsPositive(action),
                    alpha: IsPositive(action) ? AlphaUserPositive : AlphaUserNegative);

                await userRef.SetAsync(new
                {
                    id = userDocId,
                    userId = firebaseUid,
                    mood,
                    vec = newUser.ToList(),
                    count = Math.Max(1, countU + 1),
                    updatedAt = Timestamp.FromDateTime(DateTime.UtcNow)
                }, SetOptions.MergeAll, ct);

                // 4b) Global centroid only on positive actions
                if (IsPositive(action))
                {
                    var globRef = _db.Collection("MoodCentroids").Document(mood);
                    var gSnap = await globRef.GetSnapshotAsync(ct);

                    List<double>? currentG = null;
                    int countG = 0;
                    if (gSnap.Exists)
                    {
                        _ = gSnap.TryGetValue("vec", out currentG);
                        _ = gSnap.TryGetValue("count", out countG);
                    }

                    var newGlob = UpdateCentroid(currentG, sample, toward: true, alpha: AlphaGlobalPositive);

                    await globRef.SetAsync(new
                    {
                        id = mood,
                        mood,
                        vec = newGlob.ToList(),
                        count = Math.Max(1, countG + 1),
                        updatedAt = Timestamp.FromDateTime(DateTime.UtcNow)
                    }, SetOptions.MergeAll, ct);
                }
            }
        }

        public async Task RecordAsync(IMoodFeedbackService.MoodSignal signal, CancellationToken ct = default)
        {
            // Write shell docs first (idempotent merge), then read-modify-write scores to apply EMA
            var ts = Timestamp.FromDateTimeOffset(signal.Timestamp);

            // Pre-write shells in a batch (faster when many features)
            var batch = _db.StartBatch();
            foreach (var f in signal.Features)
            {
                var type = Norm(f.Type);
                var key = Norm(f.Key);
                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(key)) continue;

                var docId = $"{signal.UserId}|{Norm(signal.Mood)}|{type}|{key}";
                var docRef = _db.Collection("MoodAffinities").Document(docId);
                batch.Set(docRef, new
                {
                    userId = signal.UserId,
                    mood = Norm(signal.Mood),
                    type,
                    key,
                    updatedAt = ts
                }, SetOptions.MergeAll);
            }
            await batch.CommitAsync(ct);

            // Now apply EMA per doc
            foreach (var f in signal.Features)
            {
                var type = Norm(f.Type);
                var key = Norm(f.Key);
                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(key)) continue;

                var docId = $"{signal.UserId}|{Norm(signal.Mood)}|{type}|{key}";
                var docRef = _db.Collection("MoodAffinities").Document(docId);
                var snap = await docRef.GetSnapshotAsync(ct);

                double current = 0; int count = 0;
                if (snap.Exists)
                {
                    current = snap.TryGetValue<double>("score", out var s) ? s : 0;
                    count = snap.TryGetValue<int>("count", out var c) ? c : 0;
                }

                var target = Math.Clamp(signal.Signal, -1, 1);
                var updated = (1 - AlphaFeature) * current + AlphaFeature * target;
                var updatedCount = count + 1;

                await docRef.SetAsync(new
                {
                    userId = signal.UserId,
                    mood = Norm(signal.Mood),
                    type,
                    key,
                    score = Math.Max(-1, Math.Min(1, updated)),
                    count = updatedCount,
                    updatedAt = ts
                }, SetOptions.MergeAll, ct);
            }
        }

        public async Task<Dictionary<string, Dictionary<string, double>>> GetProfileAsync(string userId, string mood, CancellationToken ct = default)
        {
            var normMood = Norm(mood);
            var q = _db.Collection("MoodAffinities")
                       .WhereEqualTo("userId", userId)
                       .WhereEqualTo("mood", normMood)
                       .Limit(800); // enough for lots of tags/sources

            var snap = await q.GetSnapshotAsync(ct);
            var result = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in snap.Documents)
            {
                var type = d.TryGetValue<string>("type", out var t) ? t : null;
                var key = d.TryGetValue<string>("key", out var k) ? k : null;
                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(key)) continue;

                var score = d.TryGetValue<double>("score", out var s) ? s : 0.0;
                if (!result.TryGetValue(type, out var bucket))
                {
                    bucket = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    result[type] = bucket;
                }
                bucket[key] = score;
            }
            return result;
        }

        // -------------------- HELPERS --------------------

        private static string Norm(string x) => (x ?? "").Trim().ToLowerInvariant();

        private static string NormalizeMood(string m)
        {
            var k = (m ?? "").Trim().ToLowerInvariant();
            return k switch
            {
                "calm" => "Calm",
                "focused" => "Focused",
                "curious" => "Curious",
                "hyped" => "Hyped",
                "meh" => "Meh",
                "stressed" => "Stressed",
                "sad" => "Sad",
                _ => "Calm"
            };
        }

        private static bool IsPositive(MoodFeedbackAction a)
            => a is MoodFeedbackAction.MoreLikeThis
               or MoodFeedbackAction.GreatExplainer
               or MoodFeedbackAction.MoreLaunches;

        /// <summary>
        /// Update centroid with an EMA step toward/away a unit sample. Returns a new unit vector.
        /// </summary>
        private static double[] UpdateCentroid(IReadOnlyList<double>? current, double[] sampleUnit, bool toward, double alpha)
        {
            // If no current, initialize to sample
            if (current is null || current.Count == 0)
                return sampleUnit.ToArray();

            var len = Math.Min(current.Count, sampleUnit.Length);
            var v = new double[len];

            if (toward)
            {
                // v = (1 - alpha) * current + alpha * sample
                for (int i = 0; i < len; i++)
                    v[i] = current[i] * (1 - alpha) + sampleUnit[i] * alpha;
            }
            else
            {
                // push away a bit: v = current - alpha * sample
                for (int i = 0; i < len; i++)
                    v[i] = current[i] - sampleUnit[i] * alpha;
            }

            return Normalize(v);
        }

        private static double[] Normalize(IReadOnlyList<double> v)
        {
            var arr = v.ToArray();
            var n = Math.Sqrt(arr.Select(x => x * x).Sum());
            if (n <= 1e-9) return arr.Select(_ => 0.0).ToArray();
            for (int i = 0; i < arr.Length; i++) arr[i] /= n;
            return arr;
        }
    }
}
