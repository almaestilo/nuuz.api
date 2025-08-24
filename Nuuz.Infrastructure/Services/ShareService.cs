using System.Text;

namespace Nuuz.Infrastructure.Services;

using Nuuz.Application.Services;

public sealed class ShareService : IShareService
{
    public string ComposeWithLimits(string template, string shortUrl, IEnumerable<string> hashtags, ProviderLimits limits)
    {
        var sb = new StringBuilder();
        sb.Append(template?.TrimEnd() ?? "");
        if (!sb.ToString().EndsWith("\n")) sb.Append('\n');
        sb.Append(shortUrl);

        var currentLen = WeightedLength(sb.ToString(), limits.UrlWeight);
        foreach (var h in hashtags)
        {
            var tag = "#" + h;
            var candidate = sb + " " + tag;
            if (WeightedLength(candidate, limits.UrlWeight) <= limits.MaxChars) { sb.Append(' ').Append(tag); }
            else break;
        }
        // enforce final cut
        var final = sb.ToString();
        while (WeightedLength(final, limits.UrlWeight) > limits.MaxChars)
        {
            // trim title line (before newline)
            var lines = final.Split('\n');
            if (lines.Length > 0 && lines[0].Length > 5)
            {
                lines[0] = lines[0].TrimEnd();
                lines[0] = lines[0].Length > 5 ? lines[0].Substring(0, lines[0].Length - 1) : lines[0];
                final = string.Join('\n', lines);
            }
            else break;
        }
        return final.Trim();
    }

    private static int WeightedLength(string s, int urlWeight)
    {
        // crude: any http(s):// token counts as urlWeight
        var words = s.Split(' ', '\n', '\r', '\t');
        int count = 0;
        foreach (var w in words)
        {
            if (w.StartsWith("http://") || w.StartsWith("https://")) count += urlWeight;
            else count += w.Length;
            count++; // spaces/newlines
        }
        return count;
    }
}
