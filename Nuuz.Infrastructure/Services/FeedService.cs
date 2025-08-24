using Google.Cloud.Firestore;
using Microsoft.Extensions.Configuration;
using Nuuz.Application.Abstraction;
using Nuuz.Application.DTOs;
using Nuuz.Application.Services;
using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Services
{
    public class FeedService : IFeedService
    {
        private readonly FirestoreDb _db;
        private readonly IArticleRepository _articles;
        private readonly IUserRepository _users;
        private readonly IUserSaveRepository _saves;
        private readonly IInterestRepository _interests;
        private readonly IMoodFeedbackService _moodFeedback;

        // Editorial tuning
        private readonly string[] _penaltyKeywords;     // lowercased
        private readonly string[] _boostKeywords;       // lowercased
        private readonly HashSet<string> _tier1Sources; // lowercased

        public FeedService(
            FirestoreDb db,
            IArticleRepository articles,
            IUserRepository users,
            IUserSaveRepository saves,
            IInterestRepository interests,
            IMoodFeedbackService moodFeedback,
            IConfiguration cfg
        )
        {
            _db = db;
            _articles = articles;
            _users = users;
            _saves = saves;
            _interests = interests;
            _moodFeedback = moodFeedback;

            _penaltyKeywords = cfg.GetSection("Pulse:PenaltyKeywords").GetChildren()
                                  .Select(c => (c.Value ?? "").Trim().ToLowerInvariant())
                                  .Where(s => s.Length > 0).ToArray();

            _boostKeywords = cfg.GetSection("Pulse:BoostKeywords").GetChildren()
                                .Select(c => (c.Value ?? "").Trim().ToLowerInvariant())
                                .Where(s => s.Length > 0).ToArray();

            _tier1Sources = cfg.GetSection("Pulse:Tier1Sources").GetChildren()
                               .Select(c => (c.Value ?? "").Trim().ToLowerInvariant())
                               .Where(s => s.Length > 0).ToHashSet();
        }

        // ---------- MAIN FEED ----------
        public async Task<FeedPageDto> GetFeedAsync(
            string firebaseUid, int limit = 20, string? cursor = null,
            string? mood = null, double? blend = null, bool overrideMood = false)
        {
            var user = await _users.GetByFirebaseUidAsync(firebaseUid)
                       ?? throw new KeyNotFoundException("User not found.");

            limit = Math.Clamp(limit, 5, 50);

            // Use PascalCase field names to match stored docs
            Query qPublished = _db.Collection("Articles").OrderByDescending("PublishedAt");
            Query qCreated = _db.Collection("Articles").OrderByDescending("CreatedAt");
            Query qArbitrary = _db.Collection("Articles"); // last resort

            if (!string.IsNullOrWhiteSpace(cursor) && long.TryParse(cursor, out var ticks))
            {
                var ts = Timestamp.FromDateTimeOffset(new DateTimeOffset(ticks, TimeSpan.Zero));
                qPublished = qPublished.StartAfter(ts);
                qCreated = qCreated.StartAfter(ts);
            }

            // ── OVERRIDE: mood paused → show recency only
            if (overrideMood)
            {
                var recent = await TryRecentAsync(limit * 2, qPublished, qCreated, qArbitrary);
                recent = DistinctByCanonicalUrl(DistinctById(recent));

                var items = await ProjectDtosWithSaved(firebaseUid, recent.Take(limit).ToList(), user, moodWhy: null);

                var nextCursor = recent.Count > 0
                    ? (recent.Last().PublishedAt != null
                        ? recent.Last().PublishedAt.ToDateTimeOffset().UtcTicks.ToString()
                        : (recent.Last().CreatedAt != null
                            ? recent.Last().CreatedAt.ToDateTimeOffset().UtcTicks.ToString()
                            : null))
                    : null;

                return new FeedPageDto
                {
                    Items = items,
                    NextCursor = nextCursor,
                    Tuned = false,
                    Explanations = new List<string> { "Showing all (mood paused)" }
                };
            }

            // ── NORMAL PATH
            var selectedIds = (user.InterestIds ?? new List<string>()).Take(10).ToList();

            Query baseQ = qPublished; // prefer PublishedAt
            Query q = baseQ.Limit(limit * 3);
            if (selectedIds.Count > 0)
                q = q.WhereArrayContainsAny("InterestMatches", selectedIds); // PascalCase

            var docs = await _articles.QueryAsync(q);

            // Backfills
            if (docs.Count < limit)
            {
                var more = await _articles.QueryAsync(qPublished.Limit(limit * 3));
                var seen = docs.Select(d => d.Id).ToHashSet();
                foreach (var a in more) if (seen.Add(a.Id)) docs.Add(a);
            }
            if (docs.Count < limit)
            {
                var snap = await qCreated.Limit(limit * 3).GetSnapshotAsync();
                var more = snap.Documents.Select(d => d.ConvertTo<Article>()).ToList();
                var seen = docs.Select(d => d.Id).ToHashSet();
                foreach (var a in more) if (seen.Add(a.Id)) docs.Add(a);
            }
            if (docs.Count == 0)
            {
                var snap = await qArbitrary.Limit(limit * 3).GetSnapshotAsync();
                docs = snap.Documents.Select(d => d.ConvertTo<Article>()).ToList();
            }

            // Final pre-ranking de-dupe
            docs = DistinctByCanonicalUrl(DistinctById(docs));

            // Prefer interest/token hits but keep others
            if (selectedIds.Count > 0 && docs.Count > 0)
            {
                var idToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in selectedIds)
                {
                    var interest = await _interests.GetAsync(id);
                    if (interest != null && !string.IsNullOrWhiteSpace(interest.Name))
                        idToName[id] = interest.Name.Trim();
                }
                var synonymTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in idToName.Values)
                    foreach (var syn in GetSynonymsForInterest(name))
                        foreach (var tok in Tokenize(syn)) synonymTokens.Add(tok);

                var preferred = new List<Article>();
                var others = new List<Article>();
                foreach (var a in docs)
                {
                    var titleTags = ((a.Title ?? "") + " " + string.Join(' ', a.Tags ?? new List<string>()));
                    var articleTokens = Tokenize(titleTags);
                    var tokenHit = articleTokens.Overlaps(synonymTokens);
                    var idHit = (a.InterestMatches ?? new List<string>()).Intersect(selectedIds).Any();
                    (tokenHit || idHit ? preferred : others).Add(a);
                }
                docs = preferred.Concat(others).ToList();
            }

            // ---------- scoring / ordering ----------
            var tuning = MoodTuning.Create(mood, blend, overrideMood);

            // learned per-mood profile (keep existing API even if empty)
            Dictionary<string, Dictionary<string, double>> profile =
                tuning.Enabled ? await _moodFeedback.GetProfileAsync(firebaseUid, tuning.Mood, System.Threading.CancellationToken.None)
                               : new();

            // mood centroids (user + global) for vector similarity boost
            double[]? userCentroid = null, globalCentroid = null;
            if (tuning.Enabled)
            {
                var pair = await _moodFeedback.GetMoodCentroidsAsync(firebaseUid, tuning.Mood, System.Threading.CancellationToken.None);
                userCentroid = pair.user;
                globalCentroid = pair.global;
            }

            var now = DateTimeOffset.UtcNow;

            // weights tuned to give mood a dominant say
            const double W_RECENCY = 0.55;
            const double W_INTEREST = 0.65;
            const double W_MOOD = 2.35;  // strong
            const double W_NOVELTY = 0.30;
            const double W_LEARNED = 0.35;

            // Mood-first selection knobs
            const double MOOD_STRONG_THRESHOLD = 0.62;
            int targetMoodStrong(int lim) => Math.Max((int)Math.Round(lim * 0.70), Math.Min(9, lim));

            var scored = new List<(Article A, double Total, double Mood, string? Why)>(docs.Count);

            foreach (var a in docs)
            {
                var published = a.PublishedAt.ToDateTimeOffset();
                double recencyHours = Math.Max(1, (now - published).TotalHours);
                double recencyScore = 1.0 / Math.Pow(recencyHours, 0.35);

                bool interestHit = (a.InterestMatches ?? new List<string>()).Intersect(selectedIds).Any();
                double interestScore = interestHit ? 0.8 : 0.2;

                double moodScore = 0; string? moodWhy = null;
                if (tuning.Enabled) moodScore = tuning.ScoreArticle(a, out moodWhy);

                double noveltyScore = tuning.ChallengeFactor > 0 ? Math.Min(0.5, (a.Tags?.Count ?? 0) * 0.05) : 0.0;
                double learned = tuning.Enabled ? LearnedAffinityBoost(a, profile) : 0.0;

                var (editorialDelta, editorialWhy) = EditorialAdjust(a, _penaltyKeywords, _boostKeywords, _tier1Sources);
                var why = MergeWhy(moodWhy, editorialWhy);

                // Vector similarity bonus (user + global centroids)
                double vecBoost = 0.0;
                if ((a.TopicEmbedding?.Count ?? 0) > 0 && (userCentroid is not null || globalCentroid is not null))
                {
                    var emb = a.TopicEmbedding!.ToArray();
                    var n = Math.Sqrt(emb.Sum(x => x * x));
                    if (n > 1e-9) for (int i = 0; i < emb.Length; i++) emb[i] /= n;

                    const double W_VEC_GLOBAL = 0.18;
                    const double W_VEC_USER = 0.22;

                    if (globalCentroid is not null)
                    {
                        var cg = Cosine(emb, globalCentroid);
                        if (!double.IsNaN(cg)) vecBoost += W_VEC_GLOBAL * Math.Max(0, cg);
                    }
                    if (userCentroid is not null)
                    {
                        var cu = Cosine(emb, userCentroid);
                        if (!double.IsNaN(cu))
                        {
                            vecBoost += W_VEC_USER * Math.Max(0, cu);
                            if (cu > 0.52) why = MergeWhy(why, "matches your vibe history");
                        }
                    }
                }

                double total =
                    recencyScore * W_RECENCY +
                    interestScore * W_INTEREST +
                    moodScore * W_MOOD +
                    noveltyScore * W_NOVELTY +
                    learned * W_LEARNED +
                    editorialDelta +
                    vecBoost;

                // amplify total by mood alignment (caps prevent runaway)
                if (tuning.Enabled)
                {
                    double moodEmphasis = 1 + (moodScore - 0.5) * 0.6; // ~0.7..1.3 typical
                    total *= Math.Clamp(moodEmphasis, 0.7, 1.3);
                }

                scored.Add((a, total, moodScore, why));
            }

            // Primary ordering by total
            var ordered = scored
                .OrderByDescending(s => s.Total)
                .ThenByDescending(s => s.A.PublishedAt.ToDateTimeOffset())
                .ToList();

            // Mood-first selection with threshold relaxation
            List<(Article A, string? Why)> selected;
            if (tuning.Enabled)
            {
                var pick = new List<(Article A, string? Why)>(limit);
                var wantStrong = targetMoodStrong(limit);
                double thresh = MOOD_STRONG_THRESHOLD;

                // try to fill with strong-fit; relax threshold if pool is thin
                while (pick.Count < wantStrong && thresh >= 0.45)
                {
                    var strongNow = ordered.Where(x => x.Mood >= thresh)
                                           .Where(x => !pick.Any(p => p.A.Id == x.A.Id))
                                           .Take(wantStrong - pick.Count)
                                           .Select(x => (x.A, x.Why));
                    var added = strongNow.ToList();
                    if (added.Count == 0) thresh -= 0.03; // relax and retry
                    pick.AddRange(added);
                    if (added.Count == 0 && thresh < 0.45) break;
                }

                // fill the rest by overall score while avoiding dupes
                foreach (var x in ordered)
                {
                    if (pick.Count >= limit) break;
                    if (pick.Any(p => p.A.Id == x.A.Id)) continue;
                    pick.Add((x.A, x.Why));
                }

                selected = pick;
            }
            else
            {
                selected = ordered.Take(limit).Select(s => (s.A, s.Why)).ToList();
            }

            // diversity by source (after mood-first selection window formed)
            var bySource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var diversified = new List<(Article A, string? Why)>();
            foreach (var x in selected)
            {
                var src = x.A.SourceId ?? "source";
                bySource.TryGetValue(src, out var cnt);
                if (cnt <= 2 || diversified.Count < limit)
                {
                    diversified.Add(x);
                    bySource[src] = cnt + 1;
                }
                if (diversified.Count >= limit) break;
            }

            // final de-dupe (ID)
            var finalDocs = DistinctPairsById(diversified).Take(limit).ToList();

            var items2 = await ProjectDtosWithSaved(
                firebaseUid,
                finalDocs.Select(x => x.A).ToList(),
                user,
                moodWhySelector: id => finalDocs.First(y => y.A.Id == id).Why
            );

            var lastTime = finalDocs.LastOrDefault().A.PublishedAt;
            var nextCursor2 = lastTime.ToDateTimeOffset().UtcTicks.ToString();

            var explanations = new List<string> {
                tuning.Enabled ? $"Tuned for {tuning.Mood} • {tuning.BlendLabel}" : "Showing all (no mood filter)"
            };
            if (profile.Count > 0) explanations.Add("Learning your vibe (thanks for the feedback)");

            return new FeedPageDto { Items = items2, NextCursor = nextCursor2, Tuned = tuning.Enabled, Explanations = explanations };
        }

        // ---------- SAVED FEED ----------
        public async Task<FeedPageDto> GetSavedAsync(
            string firebaseUid, int limit = 20, string? cursor = null,
            string? mood = null, double? blend = null, bool overrideMood = false)
        {
            limit = Math.Clamp(limit, 5, 50);

            var q = _db.Collection("UserSaves")
                .WhereEqualTo("userId", firebaseUid)
                .OrderByDescending("savedAt");

            if (!string.IsNullOrWhiteSpace(cursor) && long.TryParse(cursor, out var ticks))
            {
                var ts = Timestamp.FromDateTimeOffset(new DateTimeOffset(ticks, TimeSpan.Zero));
                q = q.StartAfter(ts);
            }

            var savesSnap = await q.Limit(limit).GetSnapshotAsync();
            var saves = savesSnap.Documents.Select(d => d.ConvertTo<UserSave>()).ToList();

            var ids = saves.Select(s => s.ArticleId).ToList();
            var articles = new List<Article>();
            foreach (var id in ids)
            {
                var a = await _articles.GetAsync(id);
                if (a != null) articles.Add(a);
            }

            // Defensive de-dupe
            articles = DistinctByCanonicalUrl(DistinctById(articles));

            var tuning = MoodTuning.Create(mood, blend, overrideMood);
            var explanations = new List<string>();
            if (tuning.Enabled) explanations.Add($"Sorted for {tuning.Mood} • {tuning.BlendLabel}");
            else explanations.Add("Sorted by date saved");

            List<Article> ordered;
            if (tuning.Enabled)
            {
                ordered = articles
                    .Select(a => (A: a, Score: tuning.ScoreArticle(a, out _)))
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.A.PublishedAt.ToDateTimeOffset())
                    .Select(x => x.A)
                    .ToList();
            }
            else
            {
                var map = articles.ToDictionary(a => a.Id);
                ordered = saves.Select(s => map.TryGetValue(s.ArticleId, out var a) ? a : null)
                               .Where(a => a != null)!.
                               ToList()!;
            }

            var items = new List<ArticleDto>();
            foreach (var s in saves)
            {
                var a = ordered.FirstOrDefault(x => x.Id == s.ArticleId);
                if (a == null) continue;

                items.Add(new ArticleDto
                {
                    Id = a.Id,
                    Url = a.Url,
                    SourceId = a.SourceId,
                    Title = a.Title,
                    Author = a.Author,
                    PublishedAt = a.PublishedAt.ToDateTimeOffset(),
                    ImageUrl = a.ImageUrl,
                    Summary = a.Summary,
                    Vibe = a.Vibe,
                    Tags = a.Tags,
                    Saved = true,
                    Why = tuning.Enabled ? $"Saved • tuned for {tuning.Mood}" : "You saved this.",
                    Collections = s.Collections ?? new List<string>(),
                    Note = s.Note
                });
            }

            var nextSaved = savesSnap.Documents.LastOrDefault()?.GetValue<Timestamp>("savedAt")
                .ToDateTimeOffset().UtcTicks.ToString();

            return new FeedPageDto { Items = items, NextCursor = nextSaved, Tuned = tuning.Enabled, Explanations = explanations };
        }

        // ---------- SAVE / UNSAVE / META (added back) ----------
        public async Task SaveAsync(string firebaseUid, string articleId)
        {
            var existing = await _saves.GetByUserAndArticleAsync(firebaseUid, articleId);
            if (existing != null) return;

            var s = new UserSave
            {
                Id = Guid.NewGuid().ToString(),
                UserId = firebaseUid,
                ArticleId = articleId,
                SavedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
            };
            await _saves.AddAsync(s);
        }

        public async Task UnsaveAsync(string firebaseUid, string articleId)
        {
            var existing = await _saves.GetByUserAndArticleAsync(firebaseUid, articleId);
            if (existing == null) return;
            await _saves.DeleteAsync(existing.Id);
        }

        public async Task UpdateSaveMetaAsync(string firebaseUid, string articleId, List<string>? collections, string? note)
        {
            var save = await _saves.GetByUserAndArticleAsync(firebaseUid, articleId);
            if (save == null)
            {
                save = new UserSave
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = firebaseUid,
                    ArticleId = articleId,
                    SavedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                };
            }

            if (collections is not null)
                save.Collections = collections
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (note is not null)
                save.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

            await _saves.AddOrUpdateAsync(save.Id, save);
        }

        public Task<IEnumerable<ArticleSummaryDto>> GetPersonalizedFeedAsync(Guid userId)
            => throw new NotImplementedException();

        // -------- helpers --------
        private async Task<List<Article>> TryRecentAsync(int take, params Query[] queries)
        {
            foreach (var q in queries)
            {
                try
                {
                    var snap = await q.Limit(take).GetSnapshotAsync();
                    var list = snap.Documents.Select(d => d.ConvertTo<Article>()).ToList();
                    if (list.Count > 0) return list;
                }
                catch { /* ignore */ }
            }
            return new List<Article>();
        }

        private async Task<List<ArticleDto>> ProjectDtosWithSaved(
            string firebaseUid,
            List<Article> articles,
            Nuuz.Domain.Entities.User user,
            string? moodWhy = null,
            Func<string, string?>? moodWhySelector = null)
        {
            var ids = articles.Select(a => a.Id).ToList();
            var saved = new HashSet<string>();
            if (ids.Count > 0)
            {
                foreach (var chunk in ChunkBy(ids, 10))
                {
                    var snap = await _db.Collection("UserSaves")
                        .WhereEqualTo("userId", firebaseUid)
                        .WhereIn("articleId", chunk)
                        .GetSnapshotAsync();
                    foreach (var s in snap.Documents)
                        saved.Add(s.ConvertTo<UserSave>().ArticleId);
                }
            }

            var list = new List<ArticleDto>(articles.Count);
            foreach (var a in articles)
            {
                var whyPart = moodWhySelector?.Invoke(a.Id) ?? moodWhy;
                list.Add(new ArticleDto
                {
                    Id = a.Id,
                    Url = a.Url,
                    SourceId = a.SourceId,
                    Title = a.Title,
                    Author = a.Author,
                    PublishedAt = a.PublishedAt.ToDateTimeOffset(),
                    ImageUrl = a.ImageUrl,
                    Summary = a.Summary,
                    Vibe = a.Vibe,
                    Tags = a.Tags,
                    Saved = saved.Contains(a.Id),
                    Why = BuildWhy(a, user, whyPart)
                });
            }
            return list;
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

        private static string BuildWhy(Article a, Nuuz.Domain.Entities.User u, string? moodWhy)
        {
            var bits = new List<string>();
            var systemIds = u.InterestIds ?? new();
            var matches = (a.InterestMatches ?? new()).Intersect(systemIds).Any();
            if (matches) bits.Add("Matches your topics");
            bits.Add("Fresh");
            if (!string.IsNullOrWhiteSpace(moodWhy)) bits.Add(moodWhy);
            return string.Join(" • ", bits.Distinct());
        }

        private static string? MergeWhy(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a)) return string.IsNullOrWhiteSpace(b) ? null : b;
            if (string.IsNullOrWhiteSpace(b)) return a;
            return $"{a}; {b}";
        }

        private static HashSet<string> Tokenize(string text)
        {
            var tokens = Regex
                .Matches(text?.ToLowerInvariant() ?? "", @"[a-z0-9+#]+")
                .Select(m => m.Value)
                .Where(t => t.Length >= 2)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return tokens;
        }

        private static IEnumerable<string> GetSynonymsForInterest(string name)
        {
            var key = name.Trim().ToLowerInvariant();
            return key switch
            {
                "technology" or "tech" => new[]
                {
                    "technology","tech","software","hardware","gadget","gadgets",
                    "artificial intelligence","machine learning","ml","llm",
                    "chip","chips","semiconductor","semiconductors","cpu","gpu",
                    "cloud","saas","app","apps","startup","programming","coding","dev","developer"
                },
                "ai" => new[] { "artificial intelligence", "machine learning", "ml", "deep learning", "llm", "neural network", "model", "inference", "training" },
                "sports" => new[] { "sports", "game", "match", "tournament", "league", "playoffs", "nba", "nfl", "mlb", "soccer", "goal", "coach" },
                "space" => new[] { "space", "nasa", "rocket", "launch", "orbital", "satellite", "spacex", "moon", "mars" },
                "politics" => new[] { "politics", "election", "policy", "law", "senate", "congress", "parliament", "minister", "president", "tariff" },
                "health" => new[] { "health", "medicine", "medical", "doctor", "hospital", "vaccine", "public health", "glucose", "sleep" },
                "finance" => new[] { "finance", "markets", "stocks", "crypto", "bank", "inflation", "interest rate", "fed", "earnings" },
                "pop culture" => new[] { "pop culture", "celebrity", "film", "movie", "tv", "music", "hollywood", "box office" },
                "art & design" => new[] { "art", "design", "illustration", "indie game", "creative", "artist", "graphics", "ux", "ui" },
                "climate" => new[] { "climate", "emissions", "heat wave", "renewable", "solar", "wind", "carbon", "co2", "sustainability" },
                "good news only" => new[] { "good news", "uplifting", "wholesome", "positive", "feel-good" },
                _ => new[] { key }
            };
        }

        // ---- Mood tuning core (using extended signals if present) ----
        private sealed class MoodTuning
        {
            public bool Enabled { get; private set; }
            public string Mood { get; private set; } = "Calm";
            public double Blend { get; private set; } = 0.3; // 0=Comfort, 1=Challenge
            public double ChallengeFactor => Math.Max(0, Blend - 0.5) * 2.0;
            public string BlendLabel => Blend <= 0.3 ? "Comforting" : (Blend >= 0.7 ? "Challenging" : "Balanced");

            public static MoodTuning Create(string? mood, double? blend, bool overrideMood)
            {
                var t = new MoodTuning();
                if (overrideMood) return t;
                if (!string.IsNullOrWhiteSpace(mood))
                {
                    t.Mood = NormalizeMood(mood);
                    t.Enabled = true;
                }
                if (blend.HasValue) t.Blend = Math.Clamp(blend.Value, 0, 1);
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

            public double ScoreArticle(Article a, out string? reason)
            {
                // Pull out features with sane defaults (if your Article doesn't define these,
                // ensure you added them in the Domain model per prior step)
                double arousal = D(a.Arousal, 0.5);
                double valence = D(a.Sentiment, 0.0);
                double depth = D(a.Depth, 0.35);
                double conflict = D(a.Conflict, 0.25);
                double pract = D(a.Practicality, 0.25);
                double optim = D(a.Optimism, 0.3);
                double novelty = D(a.Novelty, 0.3);
                double human = D(a.HumanInterest, 0.25);
                double hype = D(a.Hype, 0.25);
                double expl = D(a.Explainer, 0.25);
                double anal = D(a.Analysis, 0.25);
                double whole = D(a.Wholesome, 0.25);
                int mins = a.ReadMinutes is > 0 and < 120 ? a.ReadMinutes!.Value : 5;
                string genre = a.Genre ?? "";
                string stage = a.EventStage ?? "";
                string format = a.Format ?? (mins <= 4 ? "Short" : mins >= 14 ? "Longform" : "Standard");

                double s = 0;
                var why = new List<string>(3);

                switch (Mood)
                {
                    case "Calm":
                        s += FitHigh(1 - arousal, 0.45);          // prefer low arousal
                        s += FitLow(conflict, 0.30);
                        s += 0.40 * whole + 0.25 * pract + 0.20 * human + 0.15 * Clamp01(valence);
                        if (mins is >= 3 and <= 10) s += 0.1;
                        if (genre is "HowTo" or "Profile" or "Feature") s += 0.05;
                        why.Add("low arousal, low conflict");
                        break;

                    case "Focused":
                        s += 0.55 * depth + 0.35 * anal + 0.25 * expl;
                        s += Band(arousal, 0.35, 0.65, 0.20);
                        s += Band01(mins, 6, 20, 0.15);
                        if (genre is "Analysis" or "Explainer") s += 0.05;
                        why.Add("deep dive / analysis");
                        break;

                    case "Curious":
                        s += 0.50 * expl + 0.35 * novelty;
                        s += Band01(mins, 4, 12, 0.15);
                        if (genre is "Explainer" or "Q&A") s += 0.05;
                        why.Add("explainer & discovery");
                        break;

                    case "Hyped":
                        s += 0.60 * hype + 0.25 * novelty + Band(arousal, 0.6, 1.0, 0.25);
                        if (stage is "Launch" or "Breaking") s += 0.20;
                        if (valence >= -0.2) s += 0.05;
                        why.Add("high energy / launches");
                        break;

                    case "Meh":
                        s += InvBand01(mins, 0, 4, 0.60);
                        if (format == "Short") s += 0.15;
                        s += 0.20 * expl;
                        why.Add("short & scannable");
                        break;

                    case "Stressed":
                        s += 0.50 * pract + 0.25 * whole + Band(arousal, 0.0, 0.55, 0.20);
                        s += FitLow(conflict, 0.40) + 0.10 * Clamp01(valence);
                        if (genre is "HowTo" or "Explainer") s += 0.05;
                        why.Add("soothing, useful");
                        break;

                    case "Sad":
                        s += 0.45 * optim + 0.35 * human + Band(arousal, 0.0, 0.55, 0.15);
                        s += 0.20 * whole;
                        why.Add("uplifting human stories");
                        break;
                }

                // Comfort–Challenge targeting via arousal
                var arousalTarget = 1 - Blend; // your UI semantics
                s += 0.20 * (1 - Math.Abs(arousal - arousalTarget)); // closeness

                // Allow some contrast picks when user wants challenge
                if (ChallengeFactor > 0 && Oppositional(a.Vibe ?? "Neutral", Mood))
                {
                    s += 0.15 * ChallengeFactor;
                    why.Add("contrast");
                }

                reason = string.Join(" • ", why.Distinct());
                return Clamp01(s);
            }

            private static bool Oppositional(string vibe, string mood)
                => (mood == "Calm" && vibe.Contains("Excited", StringComparison.OrdinalIgnoreCase))
                || (mood == "Hyped" && vibe.Contains("Analytical", StringComparison.OrdinalIgnoreCase));

            private static double D(double? v, double def) => v.HasValue ? v.Value : def;
            private static double Clamp01(double v) => Math.Min(1, Math.Max(0, v));

            // score helpers
            private static double FitLow(double v, double cutoff) => v <= cutoff ? 0.25 + (cutoff - v) * 0.5 / Math.Max(0.0001, cutoff) : 0;
            private static double FitHigh(double v, double cutoff) => v >= cutoff ? 0.25 + (v - cutoff) * 0.5 / Math.Max(0.0001, 1 - cutoff) : 0;

            // Reward values inside a numeric band [lo,hi] with weight w (triangle)
            private static double Band(double v, double lo, double hi, double w)
            {
                if (hi <= lo) return 0;
                if (v < lo || v > hi) return 0;
                var mid = (lo + hi) * 0.5;
                var d = 1 - (Math.Abs(v - mid) / (hi - lo) * 2); // 1 at center, 0 at edges
                return Math.Max(0, d) * w;
            }

            // Same but for integer minutes
            private static double Band01(int minutes, int lo, int hi, double w)
            {
                if (hi <= lo) return 0;
                if (minutes < lo || minutes > hi) return 0;
                var mid = (lo + hi) * 0.5;
                var d = 1 - (Math.Abs(minutes - mid) / (hi - lo) * 2);
                return Math.Max(0, d) * w;
            }

            // Prefer small values (e.g., short reads). Reward if minutes <= hi.
            private static double InvBand01(int minutes, int lo, int hi, double w)
            {
                if (minutes <= lo) return w;
                if (minutes >= hi) return 0;
                var t = 1 - (minutes - lo) / (double)Math.Max(1, hi - lo);
                return Math.Max(0, t) * w;
            }
        }

        // Learned profile → affinity boost (kept from your original)
        private static double LearnedAffinityBoost(Article a, Dictionary<string, Dictionary<string, double>> profile)
        {
            double s = 0;

            if (!string.IsNullOrWhiteSpace(a.SourceId))
                s += Lookup(profile, "source", a.SourceId);

            foreach (var id in a.InterestMatches ?? new List<string>())
                s += Lookup(profile, "interest", id);

            foreach (var tag in a.Tags ?? new List<string>())
                s += Lookup(profile, "tag", tag);

            foreach (Match m in Regex.Matches((a.Title ?? "").ToLowerInvariant(), @"[a-z0-9+#]{3,}"))
                s += Lookup(profile, "tok", m.Value);

            s = Math.Max(-6, Math.Min(6, s));
            var boost = 0.5 * Math.Tanh(s / 4.0);
            return boost;
        }

        private static double Lookup(Dictionary<string, Dictionary<string, double>> p, string type, string key)
            => (p.TryGetValue(type, out var bucket) && bucket.TryGetValue(key, out var v)) ? v : 0.0;

        // ------------- Editorial adjustments -------------
        private static (double delta, string? why) EditorialAdjust(
            Article a,
            string[] penaltyKeywords,
            string[] boostKeywords,
            HashSet<string> tier1SourcesLower)
        {
            var srcLower = (a.SourceId ?? "").Trim().ToLowerInvariant();
            var text = $"{a.Title} {a.Summary} {string.Join(' ', a.Tags ?? new List<string>())}".ToLowerInvariant();

            int penHits = 0;
            foreach (var kw in penaltyKeywords)
                if (!string.IsNullOrWhiteSpace(kw) && text.Contains(kw)) penHits++;

            int boostHits = 0;
            foreach (var kw in boostKeywords)
                if (!string.IsNullOrWhiteSpace(kw) && text.Contains(kw)) boostHits++;

            var delta = 0.0;
            string? why = null;

            if (penHits > 0)
            {
                var penalty = Math.Min(0.8, 0.5 + 0.15 * (penHits - 1)); // 0.5..0.8
                delta -= penalty;
                why = "de-emphasized: deal/review";
            }

            if (boostHits > 0)
            {
                var boost = Math.Min(0.6, 0.25 + 0.10 * (boostHits - 1)); // 0.25..0.6
                delta += boost;
                why = string.IsNullOrWhiteSpace(why) ? "high-impact topic" : $"{why}; high-impact topic";
            }

            if (tier1SourcesLower.Contains(srcLower))
            {
                delta += 0.05; // tiny nudge
                if (string.IsNullOrWhiteSpace(why)) why = "reputable source";
                else if (!why.Contains("reputable source")) why += "; reputable source";
            }

            return (delta, why);
        }

        // ------------- De-dupe helpers -------------
        private static List<Article> DistinctById(List<Article> input)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<Article>(input.Count);
            foreach (var a in input)
            {
                if (string.IsNullOrWhiteSpace(a.Id)) continue;
                if (seen.Add(a.Id)) result.Add(a);
            }
            return result;
        }

        private static List<Article> DistinctByCanonicalUrl(List<Article> input)
        {
            var groups = input
                .Where(a => !string.IsNullOrWhiteSpace(a.Url))
                .GroupBy(a => CanonicalUrl(a.Url!), StringComparer.OrdinalIgnoreCase);

            var chosen = new Dictionary<string, Article>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var best = g
                    .OrderByDescending(a => a.PublishedAt.ToDateTimeOffset())
                    .ThenByDescending(a => a.CreatedAt.ToDateTimeOffset())
                    .First();
                chosen[g.Key] = best;
            }

            var noUrl = input.Where(a => string.IsNullOrWhiteSpace(a.Url)).ToList();

            return chosen.Values.Concat(noUrl).ToList();
        }

        private static IEnumerable<(Article A, string? Why)> DistinctPairsById(IEnumerable<(Article A, string? Why)> input)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var x in input)
            {
                if (string.IsNullOrWhiteSpace(x.A.Id)) continue;
                if (seen.Add(x.A.Id)) yield return x;
            }
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
                    if (lk.StartsWith("utm_") || lk is "fbclid" or "gclid" or "igshid" or "ref" or "ref_src")
                        continue;
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

        // ---- cosine helper for centroid boosts ----
        private static double Cosine(double[] a, double[] b)
        {
            if (a.Length == 0 || b.Length == 0) return double.NaN;
            int len = Math.Min(a.Length, b.Length);
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < len; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            if (na <= 1e-9 || nb <= 1e-9) return double.NaN;
            return dot / Math.Sqrt(na * nb);
        }
    }
}
