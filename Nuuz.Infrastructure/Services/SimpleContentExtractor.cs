// Nuuz.Infrastructure/Services/SimpleContentExtractor.cs
using System.Net.Http;
using HtmlAgilityPack;
using Nuuz.Application.Services;

namespace Nuuz.Infrastructure.Services;

public sealed class SimpleContentExtractor : IContentExtractor
{
    private readonly HttpClient _http;

    public SimpleContentExtractor(HttpClient http) => _http = http;

    public async Task<ExtractedContent> ExtractAsync(string url, string? rssHtmlOrSummary, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var html = await resp.Content.ReadAsStringAsync(ct);

            // crude readability: remove script/style/nav/footer/aside, pick longest text block
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            RemoveNodes(doc, new[] { "//script", "//style", "//nav", "//footer", "//aside", "//form" });

            var paragraphs = doc.DocumentNode.SelectNodes("//p") ?? new HtmlNodeCollection(null);
            var texts = paragraphs.Select(p => p.InnerText.Trim())
                                  .Where(t => t.Length > 0)
                                  .ToList();

            string mainText = string.Join("\n\n", texts);
            if (string.IsNullOrWhiteSpace(mainText) && !string.IsNullOrWhiteSpace(rssHtmlOrSummary))
            {
                // fallback to RSS content/summary stripped
                mainText = HtmlEntity.DeEntitize(StripTags(rssHtmlOrSummary));
            }

            string? leadImg = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", null)
                            ?? doc.DocumentNode.SelectSingleNode("//img[@src]")?.GetAttributeValue("src", null);

            return new ExtractedContent { Text = mainText ?? "", LeadImageUrl = leadImg };
        }
        catch
        {
            // fallback: just use rss summary if fetch fails
            var text = HtmlEntity.DeEntitize(StripTags(rssHtmlOrSummary ?? ""));
            return new ExtractedContent { Text = text, LeadImageUrl = null };
        }
    }

    private static void RemoveNodes(HtmlDocument doc, IEnumerable<string> xpaths)
    {
        foreach (var xp in xpaths)
        {
            var nodes = doc.DocumentNode.SelectNodes(xp);
            if (nodes == null) continue;
            foreach (var n in nodes) n.Remove();
        }
    }

    private static string StripTags(string html)
    {
        var d = new HtmlDocument();
        d.LoadHtml(html);
        return d.DocumentNode.InnerText;
    }
}
