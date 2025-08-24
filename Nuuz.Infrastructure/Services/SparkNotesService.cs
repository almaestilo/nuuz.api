using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http; // IHttpClientFactory + HttpCompletionOption
using HtmlAgilityPack;
using Nuuz.Application.Services;
using IHttpClientFactory = System.Net.Http.IHttpClientFactory;

namespace Nuuz.Infrastructure.Services
{
    public sealed class SparkNotesService : ISparkNotesService
    {
        private readonly IHttpClientFactory _http;
        private readonly ILLMClient _llm;

        public SparkNotesService(IHttpClientFactory http, ILLMClient llm)
        {
            _http = http;
            _llm = llm;
        }

        public async Task<SparkNotesResult> BuildAsync(string url, string title, string seedText, CancellationToken ct = default)
        {
            var context = seedText;
            if (string.IsNullOrWhiteSpace(context))
            {
                var html = await DownloadAsync(url, ct);
                context = ExtractPlain(html);
            }
            context = Truncate((title ?? string.Empty) + "\n\n" + context, 7000);

            // 🔄 New prompt: no branded headings; clean, minimal HTML sections only when relevant.
            var system =
                "You are Nuuz, rewriting a news article into a clean, neutral, scannable brief. " +
                "Write in your OWN words (no verbatim copying). Up to two short quotes (<= 75 words) only if present in the context. " +
                "OUTPUT clean minimal HTML (no scripts/styles). If a section has no content, OMIT it.";

            var user =
                "Source URL: " + url +
                " | Title: " + (title ?? "Untitled") +
                " || Context: \"\"\"" + context + "\"\"\" || " +
                "Produce HTML with these sections WHEN RELEVANT (omit empties): " +
                "<h3>TL;DR</h3><ul><li>3–5 crisp bullets</li></ul> " +
                "<h3>What happened</h3><p>2–3 short paragraphs</p> " +
                "<h3>Why it matters</h3><ul><li>2–4 bullets</li></ul> " +
                "<h3>Key timeline</h3><ul><li><strong>YYYY-MM-DD:</strong> event</li></ul> " +
                "<h3>Quotes</h3><blockquote>“<= 75 words from the context”</blockquote> " +
                "<p class=\"cta\"><a href=\"" + url + "\" target=\"_blank\" rel=\"noopener nofollow\">Read the original at the source</a></p>";

            var htmlOut = await _llm.GenerateAsync(system, user, temperature: 0.2, maxOutputTokens: 1400, ct);
            if (string.IsNullOrWhiteSpace(htmlOut))
            {
                htmlOut = "<p>We couldn’t generate a brief this time.</p><p class=\"cta\"><a href=\"" + url + "\" target=\"_blank\" rel=\"noopener nofollow\">Read the original</a></p>";
            }

            htmlOut = NormalizeHtmlConservative(htmlOut, url, title ?? "Untitled");

            var plain = HtmlToPlain(htmlOut);
            return new SparkNotesResult(htmlOut.Trim(), plain);
        }

        // ---------------- HTTP + extraction helpers ----------------

        private async Task<string> DownloadAsync(string url, CancellationToken ct)
        {
            var client = _http.CreateClient(nameof(SparkNotesService));
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (NuuzNotes; +https://nuuz.app)");
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var charset = resp.Content.Headers.ContentType?.CharSet;
            try { if (!string.IsNullOrWhiteSpace(charset)) return Encoding.GetEncoding(charset).GetString(bytes); } catch { }
            return Encoding.UTF8.GetString(bytes);
        }

        private static string ExtractPlain(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html ?? string.Empty);
            var art = doc.DocumentNode.SelectSingleNode("//article") ?? Largest(doc.DocumentNode);
            var text = art?.InnerText ?? string.Empty;
            return Regex.Replace(HtmlEntity.DeEntitize(text), @"\s+", " ").Trim();
        }

        private static HtmlNode? Largest(HtmlNode root)
        {
            HtmlNode? best = null; var bestScore = 0;
            var nodes = root.SelectNodes("//main|//div|//section|//body") ?? new HtmlNodeCollection(null);
            foreach (var n in nodes)
            {
                var s = n.InnerText?.Count(ch => !char.IsWhiteSpace(ch)) ?? 0;
                if (s > bestScore) { bestScore = s; best = n; }
            }
            return best;
        }

        private static string HtmlToPlain(string html)
        {
            var d = new HtmlDocument(); d.LoadHtml(html ?? string.Empty);
            var t = d.DocumentNode.InnerText ?? string.Empty;
            return Regex.Replace(t, @"\s+", " ").Trim();
        }

        private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max) + "…");

        private static string Escape(string s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

        // ---------------- Conservative normalizer ----------------
        // NOTE: This version strips any "Nuuz SparkNotes"/"Nuuz Notes" headings if the model still produced them,
        // wraps in <article class="nuuz-sparknotes">, and ensures a CTA exists. It does NOT insert any branded heading.
        private static string NormalizeHtmlConservative(string rawHtml, string url, string title)
        {
            if (string.IsNullOrWhiteSpace(rawHtml))
                return "<article class=\"nuuz-sparknotes\"><p>We couldn’t generate a brief this time.</p><p class=\"cta\"><a href=\"" + url + "\" target=\"_blank\" rel=\"noopener nofollow\">Read the original</a></p></article>";

            var html = rawHtml.Trim();

            // 0) Remove any branded headings the LLM might have emitted.
            html = Regex.Replace(html, @"<h[12][^>]*>\s*Nuuz\s+(SparkNotes|Notes)\s*</h[12]>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<p[^>]*class\s*=\s*[""']\s*kicker\s*[""'][^>]*>.*?</p>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 1) Remove obvious placeholder timeline items like "N/A"
            html = Regex.Replace(html, @"<li[^>]*>\s*[^<]*\bN/?A\b[^<]*\s*</li>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 2) If a list item contains ONLY a section heading (e.g., <li><h3>TL;DR</h3></li>), lift it out of the list
            html = Regex.Replace(html, @"<li[^>]*>\s*(<h3[^>]*>.*?</h3>)\s*</li>", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 3) Ensure it's wrapped in <article class="nuuz-sparknotes">
            if (Regex.IsMatch(html, @"<\s*article\b", RegexOptions.IgnoreCase))
            {
                if (!Regex.IsMatch(html, @"<\s*article\b[^>]*class\s*=\s*[""'][^""']*nuuz-sparknotes", RegexOptions.IgnoreCase))
                {
                    html = Regex.Replace(html, @"<\s*article\b([^>]*)>", m =>
                    {
                        var attrs = m.Groups[1].Value;
                        if (Regex.IsMatch(attrs, @"class\s*=", RegexOptions.IgnoreCase))
                            return "<article" + Regex.Replace(attrs, @"class\s*=\s*(['""])(.*?)\1", m2 => $" class={m2.Groups[1].Value}{m2.Groups[2].Value} nuuz-sparknotes{m2.Groups[1].Value}") + ">";
                        else
                            return "<article class=\"nuuz-sparknotes\"" + attrs + ">";
                    }, RegexOptions.IgnoreCase);
                }
            }
            else
            {
                html = "<article class=\"nuuz-sparknotes\">" + html + "</article>";
            }

            // 4) Ensure CTA exists (.cta)
            if (!Regex.IsMatch(html, @"class\s*=\s*(['""]).*?\bcta\b.*?\1", RegexOptions.IgnoreCase))
            {
                var cta = "<p class=\"cta\"><a href=\"" + url + "\" target=\"_blank\" rel=\"noopener nofollow\">Read the original at the source</a></p>";
                html = Regex.Replace(html, @"</\s*article\s*>", cta + "</article>", RegexOptions.IgnoreCase);
            }

            // 5) Collapse whitespace
            html = Regex.Replace(html, @"\s{2,}", " ");

            return html.Trim();
        }
    }
}
