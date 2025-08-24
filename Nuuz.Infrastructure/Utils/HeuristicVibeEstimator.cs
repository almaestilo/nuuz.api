using System.Text.RegularExpressions;

namespace Nuuz.Infrastructure.Services;

public static class HeuristicVibeEstimator
{
    public static double EstimateArousal(string title, string text)
    {
        var s = (title + " " + (text ?? "")).ToLowerInvariant();
        double a = 0;
        if (s.Contains("breaking") || s.Contains("urgent") || s.Contains("just in")) a += 0.6;
        a += Math.Min(0.4, Count('!', title) * 0.1);
        a += Math.Min(0.3, AllCapsRatio(title) * 0.6);
        return Math.Clamp(a, 0, 1);
    }

    public static (double overall, double variance) EstimateSentiment(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0.0, 0.1);
        var s = text.ToLowerInvariant();
        var pos = CountAny(s, new[] { "win", "growth", "surge", "record", "beat", "breakthrough", "uplift", "upli" });
        var neg = CountAny(s, new[] { "fall", "drop", "loss", "scandal", "lawsuit", "crash", "panic", "dead", "kills" });
        var score = Math.Clamp((pos - neg) * 0.1, -1, 1);
        var variance = Math.Clamp((pos + neg) * 0.05, 0, 1);
        return (score, variance);
    }

    public static (string vibe, string[] tags) GuessVibeTags(string title, string text)
    {
        var s = (title + " " + (text ?? "")).ToLowerInvariant();
        var tags = new List<string>();
        if (ContainsAny(s, "how to", "guide", "tips")) tags.Add("how-to");
        if (ContainsAny(s, "analysis", "deep dive", "explainer")) tags.Add("analysis");
        if (ContainsAny(s, "launch", "unveils", "introduces", "announces")) tags.Add("launch");
        if (ContainsAny(s, "earnings", "revenue", "stock", "market")) tags.Add("finance");
        if (ContainsAny(s, "nba", "nfl", "mlb", "soccer", "goal", "tournament")) tags.Add("sports");
        if (ContainsAny(s, "climate", "emissions", "renewable")) tags.Add("climate");
        if (ContainsAny(s, "ai", "artificial intelligence", "model", "neural")) tags.Add("ai");

        var vibe = "Neutral";
        if (tags.Contains("analysis")) vibe = "Analytical";
        else if (tags.Contains("how-to")) vibe = "Wholesome";
        else if (ContainsAny(s, "wins", "record", "surge", "soars")) vibe = "Excited";
        else if (ContainsAny(s, "warning", "concern", "caution")) vibe = "Cautionary";

        return (vibe, tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    static int Count(char c, string s) => s.Count(ch => ch == c);
    static double AllCapsRatio(string s)
    {
        int cap = s.Count(char.IsUpper); int letters = s.Count(char.IsLetter);
        return letters == 0 ? 0 : (double)cap / letters;
    }
    static bool ContainsAny(string s, params string[] needles) => needles.Any(n => s.Contains(n));
    static int CountAny(string s, IEnumerable<string> needles) =>
        needles.Sum(n => Regex.Matches(s, @"\b" + Regex.Escape(n) + @"\b").Count);
}
