using Nuuz.Application.DTOs;
using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Application.Services
{
    /// <summary>
    /// Records per-mood "more/less like this" and exposes:
    /// 1) a compact feature-EMA profile used in feed scoring
    /// 2) per-mood (user,global) semantic centroids for vector boosts
    /// </summary>
    public interface IMoodFeedbackService
    {
        /// <summary>Keyed feature extracted from articles.</summary>
        public sealed record Feature(string Type, string Key);

        // ---------- CENTROIDS ----------
        /// <summary>Fetch per-mood centroids. Vectors are L2-normalized or null if unavailable.</summary>
        Task<(double[]? user, double[]? global)> GetMoodCentroidsAsync(
            string firebaseUid, string mood, CancellationToken ct);

        /// <summary>
        /// Record a UI feedback action (e.g., MoreLikeThis, TooIntense) for an article under the current mood.
        /// Updates:
        ///   - ArticleFeedback log
        ///   - Per-(user,mood) centroid (toward/away article embedding)
        ///   - Global mood centroid (gentle drift on positive actions)
        ///   - Feature EMA scores for personalization
        /// </summary>
        Task RecordFeedbackAsync(
            string firebaseUid,
            RecordMoodFeedbackDto dto,
            CancellationToken ct);

        // ---------- FEATURE EMAs ----------
        /// <summary>Incoming (user,mood) signal with extracted features.</summary>
        public sealed class MoodSignal
        {
            public string UserId { get; set; } = default!;
            public string Mood { get; set; } = default!;
            public int Signal { get; set; } // +1 or -1
            public DateTimeOffset Timestamp { get; set; }
            public IReadOnlyList<Feature> Features { get; set; } = Array.Empty<Feature>();
        }

        /// <summary>
        /// Extracts learnable features from an article (source, tags, interests, lightweight tokens).
        /// Keep in sync with the scoring side to get lift.
        /// </summary>
        public static IReadOnlyList<Feature> ExtractFeaturesFromArticle(Article a)
        {
            var list = new List<Feature>(32);

            if (!string.IsNullOrWhiteSpace(a.SourceId))
                list.Add(new Feature("source", a.SourceId.Trim()));

            foreach (var id in a.InterestMatches ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(id)) list.Add(new Feature("interest", id.Trim()));

            foreach (var t in a.Tags ?? new List<string>())
            {
                var k = (t ?? "").Trim();
                if (k.Length > 0) list.Add(new Feature("tag", k));
            }

            // Super-light tokenizer for title to catch recurring topics/phrases
            var title = (a.Title ?? "").ToLowerInvariant();
            foreach (var m in System.Text.RegularExpressions.Regex.Matches(title, @"[a-z0-9+#]{3,}").Cast<System.Text.RegularExpressions.Match>())
            {
                var tok = m.Value.Trim();
                if (tok.Length >= 3 && tok.Length <= 24) list.Add(new Feature("tok", tok));
            }

            return list;
        }

        /// <summary>Record a single feedback event and update feature EMAs only.</summary>
        Task RecordAsync(MoodSignal signal, CancellationToken ct = default);

        /// <summary>
        /// Fetch a compact per-mood profile for a user: [featureType][key] = score (roughly -1..+1).
        /// </summary>
        Task<Dictionary<string, Dictionary<string, double>>> GetProfileAsync(string userId, string mood, CancellationToken ct = default);
    }
}
