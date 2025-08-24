using System.Text.RegularExpressions;
using Nuuz.Application.Abstraction;
using Nuuz.Application.Services;

namespace Nuuz.Infrastructure.Services;

/// <summary>
/// Super-pragmatic matcher: case-insensitive "contains" with word-boundary bias.
/// Replace later with embeddings if you want smarter matches.
/// </summary>
public sealed class InterestMatcher : IInterestMatcher
{
    private readonly IInterestRepository _repo;

    public InterestMatcher(IInterestRepository repo) => _repo = repo;

    public async Task<List<string>> MatchAsync(string title, string? text)
    {
        var interests = await _repo.GetAllOrderedAsync(); // has Id + Name
        if (interests.Count == 0) return new List<string>();

        var hay = ((title ?? "") + " " + (text ?? "")).ToLowerInvariant();

        // Basic tokenization to help word-boundary matching
        // e.g., "ai," -> "ai" | "game-art" -> "game", "art"
        var tokens = Regex.Matches(hay, @"[a-z0-9+#]+")
                          .Select(m => m.Value)
                          .ToHashSet();

        // Score interests: strong hit = word-boundary, weak hit = substring
        var scored = new List<(string Id, int Score)>();

        foreach (var i in interests)
        {
            var name = (i.Name ?? "").Trim();
            if (name.Length == 0) continue;

            var key = name.ToLowerInvariant();

            int score = 0;

            // Exact token match (e.g., "ai", "sports")
            if (tokens.Contains(key)) score += 3;

            // Word-boundary regex (e.g., matches " space " or "(space)")
            // avoid anchoring to start/end; \b handles alnum boundaries
            if (Regex.IsMatch(hay, $@"\b{Regex.Escape(key)}\b")) score += 2;

            // Loose contains (e.g., "technology" in "biotechnology") – last resort
            if (score == 0 && hay.Contains(key)) score += 1;

            if (score > 0)
                scored.Add((i.Id, score));
        }

        // Order by score, distinct, and cap to 10 for Firestore array-contains-any
        return scored
            .OrderByDescending(s => s.Score)
            .Select(s => s.Id)
            .Distinct()
            .Take(10)
            .ToList();
    }
}
