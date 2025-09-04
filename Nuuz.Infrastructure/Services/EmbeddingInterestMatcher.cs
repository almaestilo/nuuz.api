using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nuuz.Application.Abstraction;
using Nuuz.Application.Services;

namespace Nuuz.Infrastructure.Services
{
    public sealed class EmbeddingInterestMatcher : IInterestMatcher
    {
        private readonly IInterestRepository _repo;
        private readonly ITextEmbedder _embedder;

        // Cache interest embeddings in-memory: interestId -> vector
        private readonly ConcurrentDictionary<string, float[]> _cache = new();

        public EmbeddingInterestMatcher(IInterestRepository repo, ITextEmbedder embedder)
        {
            _repo = repo;
            _embedder = embedder;
        }

        public async Task<List<string>> MatchAsync(string title, string? text)
        {
            var interests = await _repo.GetAllOrderedAsync();
            if (interests.Count == 0) return new List<string>();

            var doc = (title ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(text)) doc += "\n" + text;

            // quick prefilter with strict token/word-boundary to reduce embedding calls if there's an obvious match
            var hay = doc.ToLowerInvariant();
            var tokens = Regex.Matches(hay, @"[a-z0-9+#]+").Select(m => m.Value).ToHashSet();
            var wordBoundary = new Func<string, bool>(key => Regex.IsMatch(hay, $@"\b{Regex.Escape(key)}\b"));

            // Compute doc embedding once
            var docVec = await _embedder.EmbedAsync(doc);

            var scored = new List<(string Id, double Score)>();
            foreach (var it in interests)
            {
                var name = (it.Name ?? string.Empty).Trim();
                if (name.Length == 0) continue;
                var key = name.ToLowerInvariant();

                // strict lexical hints boost
                double boost = 0.0;
                if (tokens.Contains(key)) boost += 0.10;
                if (wordBoundary(key)) boost += 0.10;

                // get cached vector or compute
                if (!_cache.TryGetValue(it.Id, out var vec))
                {
                    vec = await _embedder.EmbedAsync(name);
                    _cache[it.Id] = vec;
                }
                var cos = Cosine(docVec, vec);
                var score = Math.Max(0, cos) + boost; // simple blend

                if (score >= 0.23) // tuned low threshold; adjust based on data
                    scored.Add((it.Id, score));
            }

            return scored
                .OrderByDescending(s => s.Score)
                .Select(s => s.Id)
                .Distinct()
                .Take(10)
                .ToList();
        }

        private static double Cosine(float[] a, float[] b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            int len = Math.Min(a.Length, b.Length);
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < len; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            if (na <= 1e-9 || nb <= 1e-9) return 0;
            return dot / Math.Sqrt(na * nb);
        }
    }
}
