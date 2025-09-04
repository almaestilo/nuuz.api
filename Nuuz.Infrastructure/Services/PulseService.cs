// Nuuz.Infrastructure/Services/PulseService.cs
using Google.Cloud.Firestore;
using Microsoft.Extensions.Configuration;
using Nuuz.Application.Abstraction;
using Nuuz.Application.DTOs;
using Nuuz.Application.Services;
using Nuuz.Domain.Entities;
using Nuuz.Infrastructure.Repositories;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Services
{
    public interface IPulseService
    {
        // Back-compat (no personal overlay)
        Task<PulseTodayDto> GetTodayAsync(string timezone, int take = 12, CancellationToken ct = default);

        // User-aware + optional mood + optional blend override
        Task<PulseTodayDto> GetTodayAsync(
            string timezone,
            string userFirebaseUid,
            string? mood = null,
            double? blendOverride = null,
            int take = 12,
            CancellationToken ct = default);

        Task GenerateHourAsync(string timezone, int? overrideHourUtc = null, int take = 12, bool heuristicsOnly = false, bool onlyIfMissing = false, CancellationToken ct = default);
    }

    public sealed class PulseService : IPulseService
    {
        private readonly FirestoreDb _db;
        private readonly IArticleRepository _articles;
        private readonly IPulseSnapshotRepository _repo;
        private readonly IConfiguration _cfg;
        private readonly IPulseReranker? _reranker; // optional

        // User + interests
        private readonly IUserRepository _users;
        private readonly IInterestRepository _interests;

        // Saved mood service
        private readonly IMoodService _moodService;

        public PulseService(
            FirestoreDb db,
            IArticleRepository articles,
            IPulseSnapshotRepository repo,
            IConfiguration cfg,
            IUserRepository users,
            IInterestRepository interests,
            IMoodService moodService,
            IPulseReranker? reranker = null)
        {
            _db = db;
            _articles = articles;
            _repo = repo;
            _cfg = cfg;
            _users = users;
            _interests = interests;
            _moodService = moodService;
            _reranker = reranker;
        }

        // ------------- READ (no personal) -------------
        public async Task<PulseTodayDto> GetTodayAsync(string timezone, int take = 12, CancellationToken ct = default)
        {
            var (dateYmd, localHour) = NowLocal(timezone);
            var tzInfo = FindTz(timezone);
            var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tzInfo);
            int minute = nowLocal.Minute;

            var cur = await _repo.GetAsync(dateYmd, localHour)
                      ?? new PulseSnapshotHour { Id = localHour.ToString("D2"), UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow), Items = new() };
            var hours = await _repo.ListHoursAsync(dateYmd);

            // Smooth rollover and optional on-demand heuristics warmup
            int warmupMin = Math.Clamp(ParseInt(_cfg["Pulse:WarmupMinutes"], 5), 0, 20);
            int ondemandAfterMin = Math.Clamp(ParseInt(_cfg["Pulse:OnDemand:HeuristicsAfterMinutes"], 8), 0, 59);
            if ((cur.Items?.Count ?? 0) == 0)
            {
                if (minute < warmupMin)
                {
                    var prev = hours.Where(h => h.Hour < localHour)
                                     .OrderByDescending(h => h.Hour)
                                     .Select(h => h.Doc)
                                     .FirstOrDefault(d => (d.Items?.Count ?? 0) > 0);
                    if (prev is not null) cur = prev;
                }
                else if (minute >= ondemandAfterMin)
                {
                    await GenerateHourAsync(timezone, take: Math.Max(take, 12), heuristicsOnly: true, onlyIfMissing: true, ct: ct);
                    var freshly = await _repo.GetAsync(dateYmd, localHour);
                    if (freshly is not null) cur = freshly;
                }
            }

            var items = (cur.Items ?? new List<PulseItem>());

            var dto = new PulseTodayDto
            {
                Date = dateYmd,
                CurrentHour = localHour,
                UpdatedAt = cur.UpdatedAt.ToDateTimeOffset(),
                Global = items
                    .OrderByDescending(i => i.Heat)
                    .Take(take)
                    .Select(ToDto)
                    .ToList(),
                Personal = new List<PulseItemDto>(),
                Timeline = hours.Select(h => new PulseTimelineHourDto
                {
                    Hour = h.Hour,
                    Count = h.Doc.Items?.Count ?? 0,
                    UpdatedAt = h.Doc.UpdatedAt.ToDateTimeOffset()
                }).ToList()
            };
            return dto;

            static PulseItemDto ToDto(PulseItem x) => new()
            {
                ArticleId = x.ArticleId,
                Title = x.Title,
                SourceId = x.SourceId ?? "",
                PublishedAt = x.PublishedAt.ToDateTimeOffset(),
                Summary = x.Summary,
                ImageUrl = x.ImageUrl,
                Heat = Math.Clamp(x.Heat, 0, 1),
                Trend = x.Trend,
                Reasons = (x.Reasons ?? new List<string>()).ToArray(),
                Topics = (x.Topics ?? new List<string>()).ToArray(),
                ScoreGlobal = x.ScoreGlobal
            };
        }

        // ------------- READ (personal overlay; uses saved mood unless overridden) -------------
        public async Task<PulseTodayDto> GetTodayAsync(
            string timezone,
            string userFirebaseUid,
            string? mood = null,
            double? blendOverride = null,
            int take = 12,
            CancellationToken ct = default)
        {
            // Load saved mood for defaults; allow overrides from caller
            MoodDto? savedMood = await _moodService.GetAsync(userFirebaseUid); // ok if null
            string? effectiveMood = mood ?? savedMood?.Mood;                   // may be null (acts like "no mood")
            double blend = blendOverride ?? savedMood?.Blend ?? 0.3;           // 0..1

            var (dateYmd, localHour) = NowLocal(timezone);
            var tzInfo = FindTz(timezone);
            var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tzInfo);
            int minute = nowLocal.Minute;

            // Fetch current hour and hour list
            var cur = await _repo.GetAsync(dateYmd, localHour)
                      ?? new PulseSnapshotHour { Id = localHour.ToString("D2"), UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow), Items = new() };
            var hours = await _repo.ListHoursAsync(dateYmd);

            int warmupMin = Math.Clamp(ParseInt(_cfg["Pulse:WarmupMinutes"], 5), 0, 20);
            int ondemandAfterMin = Math.Clamp(ParseInt(_cfg["Pulse:OnDemand:HeuristicsAfterMinutes"], 8), 0, 59);
            if ((cur.Items?.Count ?? 0) == 0)
            {
                if (minute < warmupMin)
                {
                    var prev = hours.Where(h => h.Hour < localHour)
                                     .OrderByDescending(h => h.Hour)
                                     .Select(h => h.Doc)
                                     .FirstOrDefault(d => (d.Items?.Count ?? 0) > 0);
                    if (prev is not null) cur = prev;
                }
                else if (minute >= ondemandAfterMin)
                {
                    await GenerateHourAsync(timezone, take: Math.Max(take, 12), heuristicsOnly: true, onlyIfMissing: true, ct: ct);
                    var freshly = await _repo.GetAsync(dateYmd, localHour);
                    if (freshly is not null) cur = freshly;
                }
            }

            // Global = current hour top N (unchanged)
            var global = (cur.Items ?? new List<PulseItem>())
                .OrderByDescending(i => i.Heat)
                .Take(take)
                .Select(ToDto)
                .ToList();

            // Build personal pool from last N hours (mood-aware + blend)
            int LOOKBACK_HOURS = MoodLookback(effectiveMood, blend);
            int PERSONAL_TARGET = Math.Clamp(take - 5, 3, 10);

            var recentHours = hours
                .Where(h => h.Hour >= Math.Max(0, localHour - LOOKBACK_HOURS + 1) && h.Hour <= localHour)
                .OrderByDescending(h => h.Hour)
                .ToList();

            var pool = new List<PulseItem>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in recentHours)
            {
                foreach (var it in (h.Doc.Items ?? new List<PulseItem>()))
                {
                    if (string.IsNullOrWhiteSpace(it.ArticleId)) continue;
                    if (seenIds.Add(it.ArticleId)) pool.Add(it);
                }
            }

            // If pool is thin, fall back to the current hour pool
            if (pool.Count < PERSONAL_TARGET * 2)
                pool = (cur.Items ?? new List<PulseItem>()).ToList();

            var interestNames = await ResolveUserInterestNamesAsync(userFirebaseUid, ct);

            // Avoid duplicating items already in Global (normally)
            var exclude = global.Select(g => g.ArticleId).ToHashSet(StringComparer.Ordinal);

            // ---------- Early-day safeguard ----------
            // If excluding Global leaves too few candidates for Personal (common in the first hours of the day),
            // relax the exclusion so "Your Pulse" still shows items (even if some overlap with Global).
            int distinctPoolCount = pool.Select(p => p.ArticleId).Distinct(StringComparer.Ordinal).Count();
            int availableAfterExclusion = Math.Max(0, distinctPoolCount - exclude.Count);
            int minNeeded = Math.Max(3, PERSONAL_TARGET / 2); // need at least half target (min 3)
            if (availableAfterExclusion < minNeeded)
            {
                exclude.Clear();
            }
            // ----------------------------------------

            var personal = ComputePersonalFromPool(
                pool: pool,
                userInterestNames: interestNames,
                mood: effectiveMood,
                blend: blend,
                take: PERSONAL_TARGET,
                excludeArticleIds: exclude
            );

            // ─────────────────────────────────────────────
            // NEW: Saved flags (batched Firestore lookup)
            // ─────────────────────────────────────────────
            var allIds = global.Select(g => g.ArticleId)
                               .Concat(personal.Select(p => p.ArticleId))
                               .Distinct()
                               .ToList();

            var savedSet = await GetSavedSetAsync(userFirebaseUid, allIds, ct);

            foreach (var g in global) g.Saved = savedSet.Contains(g.ArticleId);
            foreach (var p in personal) p.Saved = savedSet.Contains(p.ArticleId);
            // ─────────────────────────────────────────────

            var dto = new PulseTodayDto
            {
                Date = dateYmd,
                CurrentHour = localHour,
                UpdatedAt = cur.UpdatedAt.ToDateTimeOffset(),
                Global = global,
                Personal = personal,
                Timeline = hours.Select(h => new PulseTimelineHourDto
                {
                    Hour = h.Hour,
                    Count = h.Doc.Items?.Count ?? 0,
                    UpdatedAt = h.Doc.UpdatedAt.ToDateTimeOffset()
                }).ToList()
            };
            return dto;

            static PulseItemDto ToDto(PulseItem x) => new()
            {
                ArticleId = x.ArticleId,
                Title = x.Title,
                SourceId = x.SourceId ?? "",
                PublishedAt = x.PublishedAt.ToDateTimeOffset(),
                Summary = x.Summary,
                ImageUrl = x.ImageUrl,
                Heat = Math.Clamp(x.Heat, 0, 1),
                Trend = x.Trend,
                Reasons = (x.Reasons ?? new List<string>()).ToArray(),
                Topics = (x.Topics ?? new List<string>()).ToArray(),
                ScoreGlobal = x.ScoreGlobal
            };
        }

        private async Task<List<string>> ResolveUserInterestNamesAsync(string firebaseUid, CancellationToken ct)
        {
            var names = new List<string>();

            var user = await _users.GetByFirebaseUidAsync(firebaseUid);
            if (user is null) return names;

            // Custom interests (free-form)
            if (user.CustomInterests is not null)
            {
                foreach (var ci in user.CustomInterests)
                {
                    if (!string.IsNullOrWhiteSpace(ci?.Name))
                        names.Add(ci.Name.Trim());
                }
            }

            // System interest IDs -> Names (via repository)
            if (user.InterestIds is not null && user.InterestIds.Count > 0)
            {
                foreach (var id in user.InterestIds)
                {
                    var interest = await _interests.GetAsync(id);
                    if (interest != null && !string.IsNullOrWhiteSpace(interest.Name))
                        names.Add(interest.Name.Trim());
                }
            }

            return names
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ---------- PERSONAL OVERLAY (interest + mood + blend) ----------
        private List<PulseItemDto> ComputePersonalFromPool(
            List<PulseItem> pool,
            List<string> userInterestNames,
            string? mood,
            double blend,
            int take,
            HashSet<string>? excludeArticleIds = null)
        {
            excludeArticleIds ??= new(StringComparer.Ordinal);

            // Diversity knobs
            int PER_SOURCE_CAP = Math.Clamp(ParseInt(_cfg["Pulse:Personal:PerSourceCap"], 2), 1, 4);
            int MIN_DISTINCT_BUCKETS = Math.Clamp(ParseInt(_cfg["Pulse:Personal:MinBuckets"], 4), 2, 8);

            // How different from Global should personal be?
            double MIN_DELTA_FROM_GLOBAL = Math.Clamp(
                double.TryParse(_cfg["Pulse:Personal:MinDeltaFromGlobal"], out var md) ? md : 0.12,
                0.0, 0.5);

            // Prepare
            var interestSet = new HashSet<string>(
                userInterestNames.Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            var tuning = PulseMoodTuning.Create(mood, blend);

            // -------- pool-level base scoring & normalization --------
            var now = DateTimeOffset.UtcNow;
            var poolFiltered = pool.Where(i => !excludeArticleIds.Contains(i.ArticleId)).ToList();
            if (poolFiltered.Count == 0) return new List<PulseItemDto>();

            var blendedVals = new double[poolFiltered.Count];
            for (int idx = 0; idx < poolFiltered.Count; idx++)
            {
                var i = poolFiltered[idx];
                var hours = Math.Max(0.25, (now - i.PublishedAt.ToDateTimeOffset()).TotalHours);
                var recency = 1.0 / Math.Pow(hours, 0.45);
                var raw = (i.ScoreGlobal > 0 ? i.ScoreGlobal : i.Heat);
                blendedVals[idx] = (0.7 * raw) + (0.3 * recency);
            }
            double baseMin = blendedVals.Min();
            double baseMax = blendedVals.Max();
            double baseSpan = Math.Max(1e-6, baseMax - baseMin);

            // -------- score each item with mood & interest, add off-mood penalty and global delta --------
            var scored = new List<(PulseItem Item, double Score, double Base, double MoodScore, double Overlap, string[] Reasons)>(poolFiltered.Count);

            for (int idx = 0; idx < poolFiltered.Count; idx++)
            {
                var i = poolFiltered[idx];
                var baseScore = (blendedVals[idx] - baseMin) / baseSpan; // 0..1

                var topics = (i.Topics ?? new List<string>()).Select(t => t.Trim().ToLowerInvariant()).ToList();
                var overlap = topics.Count == 0 ? 0.0 : topics.Count(t => interestSet.Contains(t)) / (double)topics.Count;

                var moodScore = tuning.ScoreSnapshotItem(i, out _);

                // Weights modulated by blend:
                // - higher blend (challenge) puts more weight on mood/recency (W_MOOD up)
                // - lower blend (comfort) nudges interest overlap (W_INTEREST up)
                const double W_INTEREST_BASE = 1.05;
                const double W_MOOD_BASE = 1.25;
                double W_MOOD = W_MOOD_BASE + (blend - 0.5) * 0.6;         // +/- 0.3 swing
                double W_INTEREST = W_INTEREST_BASE + (0.5 - blend) * 0.3; // +/- 0.15 swing

                // off-mood penalty softer when seeking challenge
                double offMoodPenalty = moodScore < 0.48
                    ? (1.0 - Math.Min(blend <= 0.5 ? 0.08 : 0.05, 0.48 - moodScore))
                    : 1.0;

                var rawForDelta = (i.ScoreGlobal > 0 ? i.ScoreGlobal : i.Heat);
                double globalDeltaPenalty = 1.0;
                if (rawForDelta >= 0.85 && moodScore < 0.62) globalDeltaPenalty -= MIN_DELTA_FROM_GLOBAL;

                var personal = baseScore * (1.0 + W_INTEREST * overlap + W_MOOD * (moodScore - 0.5));

                // Older items damped; more damping when blend is high (challenge favors fresher)
                var hours = Math.Max(0.25, (now - i.PublishedAt.ToDateTimeOffset()).TotalHours);
                double ageDamp = 0.92 - (blend * 0.06); // 0.92@0 → 0.86@1
                if (hours > 6) personal *= ageDamp;

                personal *= offMoodPenalty * globalDeltaPenalty;

                // tiny deterministic jitter to break ties
                personal *= (0.995 + (0.01 * (Math.Abs(i.ArticleId.GetHashCode()) % 100) / 100.0));

                var reasons = MergeReasons(i.Reasons, overlap, tuning.Enabled ? tuning.Mood : null, moodScore);
                scored.Add((i, Math.Max(0.0001, personal), baseScore, moodScore, overlap, reasons));
            }

            if (scored.Count == 0) return new List<PulseItemDto>();

            // -------- SOFTMAX SAMPLING WITHOUT REPLACEMENT (score-proportional) --------
            var selected = SampleTopKWeighted(scored, take, PER_SOURCE_CAP, MIN_DISTINCT_BUCKETS);

            // Final normalize to 0..1 for ScorePersonal
            var maxS = selected.Max(c => c.Score);
            var minS = selected.Min(c => c.Score);
            var spanS = Math.Max(1e-6, maxS - minS);

            return selected.Select(c => new PulseItemDto
            {
                ArticleId = c.Item.ArticleId,
                Title = c.Item.Title,
                SourceId = c.Item.SourceId ?? "",
                PublishedAt = c.Item.PublishedAt.ToDateTimeOffset(),
                Summary = ApplyMoodTone(c.Item.Summary, tuning.Enabled ? tuning.Mood : null),
                ImageUrl = c.Item.ImageUrl,
                Heat = c.Item.Heat,
                Trend = c.Item.Trend,
                Topics = (c.Item.Topics ?? new List<string>()).ToArray(),
                Reasons = c.Reasons,
                ScoreGlobal = c.Item.ScoreGlobal,
                ScorePersonal = (c.Score - minS) / spanS
            }).ToList();
        }

        // Weighted sampling with diversity constraints
        private static List<(PulseItem Item, double Score, string[] Reasons)> SampleTopKWeighted(
            List<(PulseItem Item, double Score, double Base, double MoodScore, double Overlap, string[] Reasons)> scored,
            int k,
            int perSourceCap,
            int minDistinctBuckets)
        {
            var rng = new Random(unchecked((int)DateTime.UtcNow.Ticks));

            var pool = scored.OrderByDescending(x => x.Score).Take(Math.Max(k * 4, 24)).ToList();

            double temperature = 0.9;
            var exp = pool.Select(x => Math.Exp(x.Score / Math.Max(1e-6, temperature))).ToArray();
            double sumExp = exp.Sum();
            if (sumExp <= 1e-12)
            {
                return pool.Take(k).Select(p => (p.Item, p.Score, p.Reasons)).ToList();
            }

            var probs = exp.Select(v => v / sumExp).ToList();

            var bySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var buckets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var chosen = new List<(PulseItem Item, double Score, string[] Reasons)>(k);

            int SampleIndex(IReadOnlyList<double> p)
            {
                double r = rng.NextDouble();
                double cum = 0;
                for (int i = 0; i < p.Count; i++)
                {
                    cum += p[i];
                    if (r <= cum) return i;
                }
                return p.Count - 1;
            }

            var active = new List<int>(Enumerable.Range(0, pool.Count));
            var activeProbs = probs.ToList();

            while (chosen.Count < k && active.Count > 0)
            {
                int pickIdx = SampleIndex(activeProbs);
                int poolIdx = active[pickIdx];
                var cand = pool[poolIdx];

                var src = cand.Item.SourceId ?? "source";
                bySource.TryGetValue(src, out var sCnt);

                if (sCnt < perSourceCap)
                {
                    chosen.Add((cand.Item, cand.Score, cand.Reasons));
                    bySource[src] = sCnt + 1;
                    buckets.Add(CoarseBucket(cand.Item.Topics));
                }

                active.RemoveAt(pickIdx);
                activeProbs.RemoveAt(pickIdx);

                double sum = activeProbs.Sum();
                if (sum <= 1e-12) break;
                for (int i = 0; i < activeProbs.Count; i++) activeProbs[i] /= sum;
            }

            if (buckets.Count < minDistinctBuckets)
            {
                foreach (var cand in pool)
                {
                    if (chosen.Any(c => c.Item.ArticleId == cand.Item.ArticleId)) continue;
                    var src = cand.Item.SourceId ?? "source";
                    bySource.TryGetValue(src, out var sCnt);
                    if (sCnt >= perSourceCap) continue;

                    chosen.Add((cand.Item, cand.Score, cand.Reasons));
                    bySource[src] = sCnt + 1;
                    buckets.Add(CoarseBucket(cand.Item.Topics));
                    if (chosen.Count >= k && buckets.Count >= minDistinctBuckets) break;
                }
            }

            if (chosen.Count < k)
            {
                foreach (var cand in pool)
                {
                    if (chosen.Any(c => c.Item.ArticleId == cand.Item.ArticleId)) continue;
                    chosen.Add((cand.Item, cand.Score, cand.Reasons));
                    if (chosen.Count >= k) break;
                }
            }

            return chosen;
        }

        private static string[] MergeReasons(List<string>? baseReasons, double overlap, string? mood, double moodScore)
        {
            var r = new List<string>(baseReasons ?? new());
            if (overlap > 0.15) r.Add("Matches your topics");
            if (!string.IsNullOrWhiteSpace(mood) && moodScore > 0.55) r.Add($"Tuned for {mood}");
            return r.Distinct().Take(4).ToArray();
        }

        // Mood scorer; includes blend (0..1)
        private sealed class PulseMoodTuning
        {
            public bool Enabled { get; private set; }
            public string Mood { get; private set; } = "Calm";
            public double Blend01 { get; private set; } = 0.3;

            public static PulseMoodTuning Create(string? mood, double blend01)
            {
                var t = new PulseMoodTuning();
                t.Blend01 = Math.Clamp(blend01, 0.0, 1.0);
                if (!string.IsNullOrWhiteSpace(mood))
                {
                    t.Mood = NormalizeMood(mood);
                    t.Enabled = true;
                }
                return t;
            }

            public static string NormalizeMood(string val)
            {
                var k = (val ?? "").Trim().ToLowerInvariant();
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

            // Score 0..1 (center ~0.5). Uses title/summary/topics + recency heuristics.
            public double ScoreSnapshotItem(PulseItem i, out string? why)
            {
                why = null;
                if (!Enabled) return 0.5;

                string text = $"{i.Title} {i.Summary} {string.Join(' ', i.Topics ?? new())}".ToLowerInvariant();
                bool Has(string s) => text.Contains(s);

                bool IsExplainer() => Regex.IsMatch(text, @"\b(what is|why|how|explainer|q&a|faq|guide|analysis|deep dive)\b");
                bool IsLiveBreaking() => Regex.IsMatch(text, @"\b(live|breaking|wins|win|beats|defeats|launch|announces)\b");

                var hours = Math.Max(0.25, (DateTimeOffset.UtcNow - i.PublishedAt.ToDateTimeOffset()).TotalHours);
                double recency = 1.0 / Math.Pow(hours, 0.35); // 0..~1

                double affinity = 0.0;

                switch (Mood)
                {
                    case "Calm":
                        if (Has("wholesome") || Has("nature") || Has("uplift") || Has("guide")) affinity += 0.6;
                        if (!IsLiveBreaking()) affinity += 0.15;
                        why = "lower arousal + positive tilt";
                        break;

                    case "Focused":
                        if (IsExplainer() || Has("analysis") || Has("policy") || Has("report")) affinity += 0.7;
                        if (hours >= 2) affinity += 0.1; else affinity -= 0.05;
                        why = "deep-dive/analysis fit";
                        break;

                    case "Curious":
                        if (IsExplainer() || Has("science") || Has("research") || Has("discovery") || Has("space")) affinity += 0.7;
                        why = "explainer/discovery";
                        break;

                    case "Hyped":
                        if (IsLiveBreaking() || Has("sports") || Has("launch") || Has("win")) affinity += 0.7;
                        if (hours <= 3) affinity += 0.25;
                        why = "high energy/launch";
                        break;

                    case "Meh":
                        if (Has("roundup") || Has("recap") || Has("list") || Has("visual") || Has("summary")) affinity += 0.65;
                        why = "short & scannable";
                        break;

                    case "Stressed":
                        if (Has("solutions") || Has("how to") || Has("how-to") || IsExplainer()) affinity += 0.7;
                        if (hours >= 1.5) affinity += 0.05;
                        why = "solutions & soothing";
                        break;

                    case "Sad":
                        if (Has("human") || Has("good news") || Has("wholesome") || Has("community") || Has("uplift")) affinity += 0.75;
                        why = "uplifting human stories";
                        break;
                }

                // Blend bias: higher blend (challenge) rewards recency a bit for all moods
                double blendRecency = Math.Min(0.18, recency * (0.12 + 0.18 * Blend01));

                var score = 0.5 + 0.4 * Math.Tanh(affinity) + blendRecency;
                return Math.Clamp(score, 0.0, 1.0);
            }
        }

        private static string ApplyMoodTone(string? summary, string? mood)
        {
            if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(mood))
                return summary ?? "";
            return mood switch
            {
                "Calm" => $"🌿 {summary}",
                "Focused" => $"📊 {summary}",
                "Curious" => $"🔍 {summary}",
                "Hyped" => $"🔥 {summary}",
                "Meh" => $"☕ {summary}",
                "Stressed" => $"🧩 {summary}",
                "Sad" => $"💙 {summary}",
                _ => summary
            };
        }

        // ------------- WRITE (hourly snapshot) -------------
        public async Task GenerateHourAsync(string timezone, int? overrideHourUtc = null, int take = 12, bool heuristicsOnly = false, bool onlyIfMissing = false, CancellationToken ct = default)
        {
            // --- knobs (pool size & diversity) ---
            int storeCount = Math.Clamp(ParseInt(_cfg["Pulse:Snapshot:StoreCount"], 60), 20, 120);
            int perSourceCap = Math.Clamp(ParseInt(_cfg["Pulse:Snapshot:PerSourceCap"], 3), 1, 5);
            int perBucketCap = Math.Clamp(ParseInt(_cfg["Pulse:Snapshot:PerBucketCap"], 12), 4, 24);

            // Establish “today” window in the app’s timezone
            var tz = FindTz(timezone);
            var nowUtc = DateTimeOffset.UtcNow;
            var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, tz);
            var localDay = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, DateTimeKind.Unspecified);
            var startLocal = new DateTimeOffset(localDay, nowLocal.Offset);
            var endLocal = nowLocal; // up to “now” (local)

            var startUtc = startLocal.ToUniversalTime();
            var endUtc = endLocal.ToUniversalTime();

            if (onlyIfMissing)
            {
                var exists = await _repo.ExistsAsync($"{nowLocal:yyyy-MM-dd}", nowLocal.Hour);
                if (exists) return;
            }

            // Fetch TODAY’s articles
            Query q = _db.Collection("Articles")
                         .WhereGreaterThanOrEqualTo("PublishedAt", Timestamp.FromDateTime(startUtc.UtcDateTime))
                         .WhereLessThanOrEqualTo("PublishedAt", Timestamp.FromDateTime(endUtc.UtcDateTime))
                         .OrderByDescending("PublishedAt")
                         .Limit(400);

            var today = await _articles.QueryAsync(q);

            if (today.Count < 10)
            {
                var q2 = _db.Collection("Articles")
                            .WhereGreaterThanOrEqualTo("CreatedAt", Timestamp.FromDateTime(startUtc.UtcDateTime))
                            .WhereLessThanOrEqualTo("CreatedAt", Timestamp.FromDateTime(endUtc.UtcDateTime))
                            .OrderByDescending("CreatedAt")
                            .Limit(400);

                var more = await _articles.QueryAsync(q2);
                var seen = today.Select(a => a.Id).ToHashSet();
                foreach (var a in more) if (seen.Add(a.Id)) today.Add(a);
            }

            if (today.Count == 0)
            {
                var (dateYmd0, hour0) = NowLocal(timezone);
                await _repo.SetAsync(dateYmd0, hour0, new PulseSnapshotHour
                {
                    Id = hour0.ToString("D2"),
                    UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                    Items = new()
                });
                return;
            }

            // De-dupe by canonical URL (cluster) and select representative per cluster
            var clusters = today.GroupBy(a => CanonicalUrl(a.Url ?? a.Id));
            var reps = new List<(string ClusterId, Nuuz.Domain.Entities.Article A)>();
            foreach (var g in clusters)
            {
                var best = g.OrderByDescending(x => x.PublishedAt.ToDateTimeOffset())
                            .ThenByDescending(x => x.CreatedAt.ToDateTimeOffset())
                            .First();
                reps.Add((g.Key, best));
            }

            // ---------- Improved heuristic importance scoring ----------
            var tier1 = GetSet("Pulse:Tier1Sources",
                new[]
                {
                    "NYT > Top Stories","AP News","Reuters","The Wall Street Journal",
                    "Financial Times","Bloomberg","BBC News","The Washington Post",
                    "The Associated Press","NPR"
                });

            var boostKeywords = GetArray("Pulse:BoostKeywords",
                new[]
                {
                    "ceasefire","election","verdict","lawsuit","indictment","sanction","tariff",
                    "acquisition","merger","bankruptcy","recall","breach","strike","earthquake",
                    "hurricane","wildfire","explosion","shooting","casualties","evacuation",
                    "gdp","inflation","jobs report","interest rate","earnings"
                });

            var penaltyKeywords = GetArray("Pulse:PenaltyKeywords",
                new[]
                {
                    "deal","% off","discount","sale","coupon","promo","hands-on","review",
                    "best price","buying guide","how to","tips","roundup"
                });

            // Cross-source corroboration per cluster (distinct sources count)
            var clusterSizes = today
                .GroupBy(a => CanonicalUrl(a.Url ?? a.Id))
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.SourceId ?? "src").Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    StringComparer.OrdinalIgnoreCase);

            double SourceAuthority(string? src)
                => string.IsNullOrWhiteSpace(src) ? 1.0 : (tier1.Contains(src) ? 1.25 : 1.0);

            double EventBoost(string title, string? summary, IReadOnlyList<string> tags)
            {
                var hay = (title + " " + (summary ?? "") + " " + string.Join(' ', tags)).ToLowerInvariant();
                double b = 0;

                foreach (var kw in boostKeywords)
                    if (!string.IsNullOrWhiteSpace(kw) && hay.Contains(kw)) b += 0.25;

                foreach (var kw in penaltyKeywords)
                    if (!string.IsNullOrWhiteSpace(kw) && hay.Contains(kw)) b -= 0.35;

                if (Regex.IsMatch(hay, @"\b(\d+)\s+(dead|killed|injured)\b")) b += 0.25;
                if (Regex.IsMatch(hay, @"\b(ban|verdict|ruling|fine|sanction|tariff|indictment)\b")) b += 0.2;

                return b;
            }

            var now = DateTimeOffset.UtcNow;
            var cands = new List<(string ClusterId, Nuuz.Domain.Entities.Article A, double Raw, List<string> Why)>();

            foreach (var r in reps)
            {
                var a = r.A;
                var published = a.PublishedAt.ToDateTimeOffset();
                var hoursSince = Math.Max(0.5, (now - published).TotalHours);

                var recency = 1.0 / Math.Pow(hoursSince, 0.45);
                var authority = SourceAuthority(a.SourceId);
                var corroboration = Math.Log10(1 + (clusterSizes.TryGetValue(r.ClusterId, out var c) ? c : 1));
                var arousal = (a.Arousal ?? 0.5);
                var eventB = EventBoost(a.Title ?? "", a.Summary, a.Tags ?? new List<string>());

                var raw = (recency * 1.1) + (corroboration * 0.7) + (arousal * 0.25);
                raw *= authority;
                raw += eventB;

                var why = new List<string>();
                if (corroboration >= 0.3) why.Add("Multi-source");
                if (authority > 1.1) why.Add("Tier-1 source");
                if (eventB > 0.2) why.Add("High-impact keywords");
                if (hoursSince < 3) why.Add("Very fresh");

                cands.Add((r.ClusterId, a, raw, why));
            }

            var maxCandidates = ClampInt(ParseInt(_cfg["Pulse:Reranker:MaxCandidates"], 80), 20, 200);
            var window = cands.OrderByDescending(x => x.Raw).Take(maxCandidates).ToList();

            var useLLM = (!heuristicsOnly) && ParseBool(_cfg["Pulse:Reranker:Enabled"], true) && _reranker is not null;
            List<PulseItem> items;

            if (useLLM && window.Count >= 10)
            {
                var topK = ClampInt(ParseInt(_cfg["Pulse:Reranker:TopK"], take), 6, Math.Max(6, take));

                var inputs = window.Select(w => new IPulseReranker.Input(
                    w.A.Id,
                    w.A.Title ?? "",
                    w.A.SourceId ?? "source",
                    w.A.PublishedAt.ToDateTimeOffset(),
                    w.A.Summary,
                    (w.A.Tags ?? new List<string>()).Take(8).ToList()
                )).ToList();

                try
                {
                    var ranked = await _reranker!.RerankAsync(inputs, topK, ct);
                    var map = window.ToDictionary(w => w.A.Id, w => w);
                    var chosen = new List<(Nuuz.Domain.Entities.Article A, double Score, List<string> Why)>();
                    foreach (var ch in ranked)
                    {
                        if (map.TryGetValue(ch.Id, out var win))
                        {
                            var reasons = win.Why.Concat(ch.Reasons).Distinct().Take(4).ToList();
                            chosen.Add((win.A, Math.Max(0.0001, ch.Score), reasons));
                        }
                    }

                    if (chosen.Count < Math.Min(topK, window.Count))
                    {
                        var needed = Math.Min(topK, window.Count) - chosen.Count;
                        var extras = window.Where(w => !chosen.Any(c => c.A.Id == w.A.Id))
                                           .Take(needed)
                                           .Select(w => (w.A, Score: w.Raw, w.Why))
                                           .ToList();
                        chosen.AddRange(extras);
                    }

                    var max = chosen.Max(x => x.Score);
                    var min = chosen.Min(x => x.Score);
                    var span = Math.Max(1e-6, max - min);

                    items = chosen.Select(x => new PulseItem
                    {
                        ArticleId = x.A.Id,
                        ScoreGlobal = x.Score,
                        Heat = Math.Clamp((x.Score - min) / span, 0, 1),
                        Trend = "STEADY",
                        Reasons = x.Why,
                        ClusterId = CanonicalUrl(x.A.Url ?? x.A.Id),
                        Title = x.A.Title ?? "",
                        SourceId = x.A.SourceId,
                        PublishedAt = x.A.PublishedAt,
                        Summary = x.A.Summary,
                        ImageUrl = x.A.ImageUrl,
                        Topics = (x.A.Tags ?? new List<string>()).Take(6).ToList()
                    }).ToList();
                }
                catch
                {
                    items = HeuristicOnly(window);
                }
            }
            else
            {
                items = HeuristicOnly(window);
            }

            foreach (var it in items)
            {
                if (it.Topics is null || it.Topics.Count == 0)
                {
                    it.Topics = ExtractTopics(it.Title, it.Summary);
                }
                it.Topics = it.Topics.Select(t => (t ?? "").Trim())
                                     .Where(t => t.Length > 0)
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .Take(8)
                                     .ToList();
            }

            var bySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var byBucket = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var diversified = new List<PulseItem>(storeCount);

            foreach (var it in items.OrderByDescending(i => i.Heat))
            {
                var src = it.SourceId ?? "source";
                var bucket = CoarseBucket(it.Topics);

                bySource.TryGetValue(src, out var sCnt);
                byBucket.TryGetValue(bucket, out var bCnt);

                if (sCnt >= perSourceCap) continue;
                if (bCnt >= perBucketCap) continue;

                diversified.Add(it);
                bySource[src] = sCnt + 1;
                byBucket[bucket] = bCnt + 1;

                if (diversified.Count >= storeCount) break;
            }

            if (diversified.Count < storeCount)
            {
                foreach (var it in items.OrderByDescending(i => i.Heat))
                {
                    if (diversified.Any(d => d.ArticleId == it.ArticleId)) continue;
                    diversified.Add(it);
                    if (diversified.Count >= storeCount) break;
                }
            }

            var (dateYmd, localHour) = NowLocal(timezone);
            var prev = await _repo.GetAsync(dateYmd, Math.Max(0, localHour - 1));
            var rankNow = diversified.Select((it, idx) => (it.ArticleId, Rank: idx + 1))
                                     .ToDictionary(x => x.ArticleId, x => x.Rank, StringComparer.Ordinal);
            var rankPrev = prev?.Items?.Select((it, idx) => (it.ArticleId, Rank: idx + 1))
                                .ToDictionary(x => x.ArticleId, x => x.Rank, StringComparer.Ordinal)
                         ?? new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var it in diversified)
            {
                if (!rankPrev.TryGetValue(it.ArticleId, out var prevRank))
                {
                    it.Trend = "NEW";
                }
                else
                {
                    var nowRank = rankNow[it.ArticleId];
                    var delta = prevRank - nowRank; // positive = moved up
                    if (delta >= 3) it.Trend = "UP";
                    else if (delta <= -3) it.Trend = "DOWN";
                    else it.Trend = "STEADY";
                }
            }

            var snapshot = new PulseSnapshotHour
            {
                Id = localHour.ToString("D2"),
                UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                Items = diversified
            };

            await _repo.SetAsync(dateYmd, localHour, snapshot);

            List<PulseItem> HeuristicOnly(List<(string ClusterId, Nuuz.Domain.Entities.Article A, double Raw, List<string> Why)> win)
            {
                var max = win.Max(x => x.Raw);
                var min = win.Min(x => x.Raw);
                var span = Math.Max(1e-6, max - min);

                return win
                    .OrderByDescending(x => x.Raw)
                    .Take(Math.Max(storeCount, Math.Max(take * 2, 12)))
                    .Select(x => new PulseItem
                    {
                        ArticleId = x.A.Id,
                        ScoreGlobal = x.Raw,
                        Heat = Math.Clamp((x.Raw - min) / span, 0, 1),
                        Trend = "STEADY",
                        Reasons = x.Why,
                        ClusterId = x.ClusterId,
                        Title = x.A.Title ?? "",
                        SourceId = x.A.SourceId,
                        PublishedAt = x.A.PublishedAt,
                        Summary = x.A.Summary,
                        ImageUrl = x.A.ImageUrl,
                        Topics = (x.A.Tags ?? new List<string>()).Take(6).ToList()
                    })
                    .ToList();
            }
        }

        // ---- helpers ----
        private static (string Ymd, int Hour) NowLocal(string timezone)
        {
            var tz = FindTz(timezone);
            var nowUtc = DateTimeOffset.UtcNow;
            var local = TimeZoneInfo.ConvertTime(nowUtc, tz);
            var ymd = $"{local:yyyy-MM-dd}";
            return (ymd, local.Hour);
        }

        private int MoodLookback(string? mood, double blend)
        {
            string m = PulseMoodTuning.NormalizeMood(mood ?? "Calm");
            int Get(string key, int def) => Math.Clamp(ParseInt(_cfg[$"Pulse:Personal:Lookback:{key}"], def), 2, 12);
            int baseLb = m switch
            {
                "Hyped" => Get("Hyped", 3),
                "Focused" => Get("Focused", 8),
                "Curious" => Get("Curious", 6),
                "Meh" => Get("Meh", 4),
                "Stressed" => Get("Stressed", 5),
                "Sad" => Get("Sad", 6),
                _ => Get("Calm", 6)
            };
            // Challenge (blend→1) shrinks lookback (favor recency); Comfort grows it a bit
            int delta = (int)Math.Round((0.5 - blend) * 2); // -1..+1 approx
            return Math.Clamp(baseLb + delta, 2, 12);
        }

        private static TimeZoneInfo FindTz(string tzId)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(tzId); }
            catch { return TimeZoneInfo.Utc; }
        }

        private static string CanonicalUrl(string raw)
        {
            try
            {
                var u = new Uri(raw);
                var host = u.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
                var path = u.AbsolutePath.TrimEnd('/');

                var qs = System.Web.HttpUtility.ParseQueryString(u.Query);
                var keep = new List<string>();
                foreach (string key in qs)
                {
                    if (key is null) continue;
                    var lk = key.ToLowerInvariant();
                    if (lk.StartsWith("utm_") || lk is "fbclid" or "gclid" or "igshid" or "ref" or "ref_src") continue;
                    keep.Add($"{key}={qs[key]}");
                }
                var query = keep.Count > 0 ? "?" + string.Join("&", keep) : string.Empty;

                return $"{u.Scheme}://{host}{path}{query}";
            }
            catch
            {
                var lower = raw.Trim().ToLowerInvariant();
                lower = Regex.Replace(lower, @"#.*$", "");
                lower = Regex.Replace(lower, @"(\?|&)(utm_[^=&]+|fbclid|gclid|igshid|ref|ref_src)=[^&]*", "");
                lower = lower.Replace("www.", "");
                return lower.TrimEnd('/');
            }
        }

        private static List<string> ExtractTopics(string? title, string? summary)
        {
            var text = $"{title} {summary}".ToLowerInvariant();

            var map = new (string token, string topic)[]
            {
                ("ai","ai"),("artificial intelligence","ai"),("machine learning","ai"),("llm","ai"),
                ("nasa","space"),("spacex","space"),("rocket","space"),("orbit","space"),("mars","space"),
                ("nfl","sports"),("nba","sports"),("mlb","sports"),("goal","sports"),("match","sports"),
                ("election","politics"),("senate","politics"),("congress","politics"),("minister","politics"),("president","politics"),
                ("gdp","finance"),("inflation","finance"),("interest rate","finance"),("fed","finance"),("earnings","finance"),
                ("climate","climate"),("emissions","climate"),("heat wave","climate"),("renewable","climate"),("solar","climate"),
                ("health","health"),("medicine","health"),("vaccine","health"),
                ("review","gadgets"),("hands-on","gadgets"),("chip","chips"),("semiconductor","chips"),
                ("movie","entertainment"),("tv","entertainment"),("box office","entertainment"),
                ("earthquake","disaster"),("hurricane","disaster"),("wildfire","disaster")
            };

            var topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (token, topic) in map)
                if (text.Contains(token)) topics.Add(topic);

            if (topics.Count == 0)
            {
                foreach (Match m in Regex.Matches(text, @"\b[a-z0-9+#]{4,}\b"))
                {
                    if (topics.Count >= 6) break;
                    topics.Add(m.Value);
                }
            }
            return topics.ToList();
        }

        private static string CoarseBucket(List<string>? topics)
        {
            if (topics is null || topics.Count == 0) return "misc";
            var preferred = new[] { "politics", "finance", "ai", "tech", "science", "space", "sports", "health", "climate", "entertainment", "disaster", "world" };
            foreach (var p in preferred)
                if (topics.Any(t => t.Equals(p, StringComparison.OrdinalIgnoreCase))) return p;
            return topics.First().ToLowerInvariant();
        }

        // ------- config helpers -------
        private static int ParseInt(string? s, int fallback)
            => int.TryParse(s, out var v) ? v : fallback;

        private static bool ParseBool(string? s, bool fallback)
            => bool.TryParse(s, out var v) ? v : fallback;

        private static int ClampInt(int value, int min, int max)
            => Math.Min(Math.Max(value, min), max);

        private HashSet<string> GetSet(string key, IEnumerable<string> defaults)
        {
            var list = _cfg.GetSection(key).GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
            if (list.Length == 0) list = defaults.ToArray();
            return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        }

        private string[] GetArray(string key, IEnumerable<string> defaults)
        {
            var list = _cfg.GetSection(key).GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
            if (list.Length == 0) list = defaults.ToArray();
            for (int i = 0; i < list.Length; i++) list[i] = list[i]!.Trim().ToLowerInvariant();
            return list!;
        }

        // ─────────────── NEW: Saved lookup helpers ───────────────
        private async Task<HashSet<string>> GetSavedSetAsync(string firebaseUid, IEnumerable<string> articleIds, CancellationToken ct)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var ids = articleIds.Distinct().ToList();
            if (ids.Count == 0) return set;

            foreach (var chunk in ChunkBy(ids, 10)) // Firestore WhereIn limit is 10
            {
                ct.ThrowIfCancellationRequested();
                var snap = await _db.Collection("UserSaves")
                    .WhereEqualTo("userId", firebaseUid)
                    .WhereIn("articleId", chunk)
                    .GetSnapshotAsync();

                foreach (var d in snap.Documents)
                    set.Add(d.ConvertTo<UserSave>().ArticleId);
            }
            return set;
        }

        private static IEnumerable<List<T>> ChunkBy<T>(IEnumerable<T> source, int size)
        {
            var chunk = new List<T>(size);
            foreach (var item in source)
            {
                chunk.Add(item);
                if (chunk.Count == size)
                {
                    yield return chunk;
                    chunk = new List<T>(size);
                }
            }
            if (chunk.Count > 0) yield return chunk;
        }
        // ──────────────────────────────────────────────────────────
    }
}
