using Google.Cloud.Firestore;
using Microsoft.ML;
using Microsoft.ML.Data;
using Nuuz.Application.Abstraction;
using Nuuz.Application.Services;
using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Services
{
    /// <summary>
    /// Trains lightweight logistic models per (user,mood) from ArticleFeedback.
    /// Models are stored in Firestore collection 'MoodModels'.
    /// </summary>
    public sealed class MoodModelService : IMoodModelService
    {
        private readonly FirestoreDb _db;
        private readonly IArticleRepository _articles;
        private readonly MLContext _ml = new();

        public MoodModelService(FirestoreDb db, IArticleRepository articles)
        {
            _db = db;
            _articles = articles;
        }

        public async Task<MoodModel?> GetModelAsync(string userId, string mood, CancellationToken ct = default)
        {
            var norm = NormalizeMood(mood);
            var docId = $"{userId}_{norm}";
            var snap = await _db.Collection("MoodModels").Document(docId).GetSnapshotAsync(ct);
            if (!snap.Exists)
            {
                // Fallback to global per-mood model
                var g = await _db.Collection("MoodModels").Document($"GLOBAL_{norm}").GetSnapshotAsync(ct);
                if (!g.Exists) return null;
                var w0 = g.TryGetValue<List<double>>("weights", out var gw) ? gw : null;
                var b0 = g.TryGetValue<double>("bias", out var gb) ? gb : double.NaN;
                if (w0 is null || double.IsNaN(b0)) return null;
                return new MoodModel { Weights = w0.ToArray(), Bias = b0 };
            }
            var weights = snap.TryGetValue<List<double>>("weights", out var w) ? w : null;
            var bias = snap.TryGetValue<double>("bias", out var b) ? b : double.NaN;
            if (weights is null || double.IsNaN(bias)) return null;
            return new MoodModel { Weights = weights.ToArray(), Bias = b };
        }

        public async Task TrainAsync(string userId, string mood, CancellationToken ct = default)
        {
            var normMood = NormalizeMood(mood);
            var q = _db.Collection("ArticleFeedback")
                       .WhereEqualTo("userId", userId)
                       .WhereEqualTo("mood", normMood)
                       .OrderByDescending("createdAt")
                       .Limit(500);
            var snap = await q.GetSnapshotAsync(ct);
            var data = new List<Input>();
            // Optional auxiliary signals for better user training
            var globalModel = await GetModelAsync("GLOBAL", normMood, ct); // may be null
            var affinities = await LoadAffinitiesForUserMoodAsync(userId, normMood, ct);
            int pos = 0, neg = 0;
            foreach (var d in snap.Documents)
            {
                var action = d.TryGetValue<string>("action", out var a) ? a : null;
                var articleId = d.TryGetValue<string>("articleId", out var aid) ? aid : null;
                if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(articleId)) continue;
                var article = await _articles.GetAsync(articleId);
                if (article is null) continue;
                var features = MoodModel.ExtractFeatures(article);
                bool label = IsPositive(action);
                if (label) pos++; else neg++;

                float globalScore = 0f;
                if (globalModel is not null)
                {
                    try { globalScore = (float)globalModel.Predict(article); } catch { }
                }
                float affinity = (float)ComputeAffinity(article, affinities);

                // Recency weight: recent feedback gets more weight (half-life ~3 days)
                var createdAt = d.TryGetValue<Timestamp>("createdAt", out var ts) ? ts.ToDateTimeOffset() : DateTimeOffset.UtcNow;
                var hours = Math.Max(0.0, (DateTimeOffset.UtcNow - createdAt).TotalHours);
                var recency = Math.Exp(-hours / 72.0);

                data.Add(new Input
                {
                    Label = label,
                    Arousal = (float)features[0],
                    Sentiment = (float)features[1],
                    Depth = (float)features[2],
                    Conflict = (float)features[3],
                    Practicality = (float)features[4],
                    Optimism = (float)features[5],
                    Novelty = (float)features[6],
                    Human = (float)features[7],
                    Hype = (float)features[8],
                    Explainer = (float)features[9],
                    Analysis = (float)features[10],
                    Wholesome = (float)features[11],
                    ReadMinutes = (float)features[12],
                    GlobalScore = globalScore,
                    Affinity = affinity,
                    Weight = (float)recency
                });
            }
            if (data.Count < 10) return; // not enough samples/ not enough samples

            // Class balancing: rebalance positive/negative counts
            if (pos > 0 && neg > 0)
            {
                var wPos = (pos + neg) / (2.0 * pos);
                var wNeg = (pos + neg) / (2.0 * neg);
                for (int i = 0; i < data.Count; i++)
                {
                    data[i].Weight *= data[i].Label ? (float)wPos : (float)wNeg;
                }
            }

            var trainData = _ml.Data.LoadFromEnumerable(data);
            var pipeline = _ml.Transforms.Concatenate("Features",
                                    nameof(Input.Arousal),
                                    nameof(Input.Sentiment),
                                    nameof(Input.Depth),
                                    nameof(Input.Conflict),
                                    nameof(Input.Practicality),
                                    nameof(Input.Optimism),
                                    nameof(Input.Novelty),
                                    nameof(Input.Human),
                                    nameof(Input.Hype),
                                    nameof(Input.Explainer),
                                    nameof(Input.Analysis),
                                    nameof(Input.Wholesome),
                                    nameof(Input.ReadMinutes),
                                    nameof(Input.GlobalScore),
                                    nameof(Input.Affinity))
                            .Append(_ml.BinaryClassification.Trainers.SdcaLogisticRegression(labelColumnName: "Label", featureColumnName: "Features", exampleWeightColumnName: "Weight"));
            var model = pipeline.Fit(trainData);
            var calibrated = model.LastTransformer.Model as Microsoft.ML.Calibrators.CalibratedModelParametersBase<Microsoft.ML.Trainers.LinearBinaryModelParameters, Microsoft.ML.Calibrators.PlattCalibrator>;
            if (calibrated is null) return;
            var linear = calibrated.SubModel;
            var weights = linear.Weights.ToArray().Select(v => (double)v).ToArray();
            var bias = linear.Bias;
            var docRef = _db.Collection("MoodModels").Document($"{userId}_{normMood}");
            await docRef.SetAsync(new
            {
                userId,
                mood = normMood,
                weights = weights.ToList(),
                bias,
                updatedAt = Timestamp.FromDateTime(DateTime.UtcNow)
            }, SetOptions.MergeAll, ct);
        }

        public async Task TrainGlobalAsync(string mood, CancellationToken ct = default)
        {
            var normMood = NormalizeMood(mood);
            var q = _db.Collection("ArticleFeedback")
                       .WhereEqualTo("mood", normMood)
                       .OrderByDescending("createdAt")
                       .Limit(2000);
            var snap = await q.GetSnapshotAsync(ct);
            var data = new List<Input>();
            foreach (var d in snap.Documents)
            {
                var action = d.TryGetValue<string>("action", out var a) ? a : null;
                var articleId = d.TryGetValue<string>("articleId", out var aid) ? aid : null;
                if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(articleId)) continue;
                var article = await _articles.GetAsync(articleId);
                if (article is null) continue;
                var features = MoodModel.ExtractFeatures(article);
                data.Add(new Input
                {
                    Label = IsPositive(action),
                    Arousal = (float)features[0],
                    Sentiment = (float)features[1],
                    Depth = (float)features[2],
                    Conflict = (float)features[3],
                    Practicality = (float)features[4],
                    Optimism = (float)features[5],
                    Novelty = (float)features[6],
                    Human = (float)features[7],
                    Hype = (float)features[8],
                    Explainer = (float)features[9],
                    Analysis = (float)features[10],
                    Wholesome = (float)features[11],
                    ReadMinutes = (float)features[12]
                });
            }
            if (data.Count < 20) return; // need more signal for global

            var trainData = _ml.Data.LoadFromEnumerable(data);
            var pipeline = _ml.Transforms.Concatenate("Features",
                                    nameof(Input.Arousal),
                                    nameof(Input.Sentiment),
                                    nameof(Input.Depth),
                                    nameof(Input.Conflict),
                                    nameof(Input.Practicality),
                                    nameof(Input.Optimism),
                                    nameof(Input.Novelty),
                                    nameof(Input.Human),
                                    nameof(Input.Hype),
                                    nameof(Input.Explainer),
                                    nameof(Input.Analysis),
                                    nameof(Input.Wholesome),
                                    nameof(Input.ReadMinutes))
                            .Append(_ml.BinaryClassification.Trainers.SdcaLogisticRegression());
            var model = pipeline.Fit(trainData);
            var calibrated = model.LastTransformer.Model as Microsoft.ML.Calibrators.CalibratedModelParametersBase<Microsoft.ML.Trainers.LinearBinaryModelParameters, Microsoft.ML.Calibrators.PlattCalibrator>;
            if (calibrated is null) return;
            var linear = calibrated.SubModel;
            var weights = linear.Weights.ToArray().Select(v => (double)v).ToArray();
            var bias = linear.Bias;
            var docRef = _db.Collection("MoodModels").Document($"GLOBAL_{normMood}");
            await docRef.SetAsync(new
            {
                userId = (string?)null,
                mood = normMood,
                scope = "global",
                weights = weights.ToList(),
                bias,
                updatedAt = Timestamp.FromDateTime(DateTime.UtcNow)
            }, SetOptions.MergeAll, ct);
        }

        public Task RecordEvaluationAsync(string userId, string mood, string articleId, double heuristicScore, double? modelScore, CancellationToken ct = default)
        {
            var docRef = _db.Collection("MoodModelEval").Document();
            return docRef.SetAsync(new
            {
                userId,
                mood = NormalizeMood(mood),
                articleId,
                heuristic = heuristicScore,
                model = modelScore,
                createdAt = Timestamp.FromDateTime(DateTime.UtcNow)
            }, cancellationToken: ct);
        }

        private static bool IsPositive(string action)
            => action is "MoreLikeThis" or "GreatExplainer" or "MoreLaunches";

        private static string NormalizeMood(string? m)
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

        private sealed class Input
        {
            [ColumnName("Label")] public bool Label { get; set; }
            public float Arousal { get; set; }
            public float Sentiment { get; set; }
            public float Depth { get; set; }
            public float Conflict { get; set; }
            public float Practicality { get; set; }
            public float Optimism { get; set; }
            public float Novelty { get; set; }
            public float Human { get; set; }
            public float Hype { get; set; }
            public float Explainer { get; set; }
            public float Analysis { get; set; }
            public float Wholesome { get; set; }
            public float ReadMinutes { get; set; }
            // Extra derived signals for user models
            public float GlobalScore { get; set; }
            public float Affinity { get; set; }
            public float Weight { get; set; } = 1f;
        }

        // ---------- Helpers for improved user training ----------
        private async Task<Dictionary<string, Dictionary<string, double>>> LoadAffinitiesForUserMoodAsync(string userId, string mood, CancellationToken ct)
        {
            var res = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            var q = _db.Collection("MoodAffinities")
                       .WhereEqualTo("userId", userId)
                       .WhereEqualTo("mood", NormalizeMood(mood))
                       .Limit(1000);
            var snap = await q.GetSnapshotAsync(ct);
            foreach (var d in snap.Documents)
            {
                var type = d.TryGetValue<string>("type", out var t) ? t : null;
                var key = d.TryGetValue<string>("key", out var k) ? k : null;
                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(key)) continue;
                var score = d.TryGetValue<double>("score", out var s) ? s : 0.0;
                if (!res.TryGetValue(type, out var bucket))
                {
                    bucket = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    res[type] = bucket;
                }
                bucket[key] = score;
            }
            return res;
        }

        private static double ComputeAffinity(Article a, Dictionary<string, Dictionary<string, double>> profile)
        {
            double sum = 0;
            double Fetch(string type, string key)
            {
                if (profile.TryGetValue(type, out var bucket) && bucket.TryGetValue(key, out var v)) return v;
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(a.SourceId)) sum += 0.4 * Fetch("source", a.SourceId);
            foreach (var id in a.InterestMatches ?? new List<string>()) sum += 0.5 * Fetch("interest", id);
            foreach (var t in a.Tags ?? new List<string>())
            {
                var k = (t ?? "").Trim(); if (k.Length > 0) sum += 0.3 * Fetch("tag", k);
            }
            var title = (a.Title ?? "").ToLowerInvariant();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(title, "[a-z0-9+#]{3,}"))
            {
                var tok = m.Value.Trim(); if (tok.Length >= 3 && tok.Length <= 24) sum += 0.2 * Fetch("tok", tok);
            }
            if (!string.IsNullOrWhiteSpace(a.Genre)) sum += 0.2 * Fetch("genre", a.Genre);
            if (!string.IsNullOrWhiteSpace(a.EventStage)) sum += 0.2 * Fetch("event", a.EventStage);
            if (!string.IsNullOrWhiteSpace(a.Format)) sum += 0.2 * Fetch("format", a.Format);

            return Math.Max(-1, Math.Min(1, sum));
        }
    }
}

