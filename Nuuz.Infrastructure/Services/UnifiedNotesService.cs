using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using Nuuz.Application.Services;

namespace Nuuz.Infrastructure.Services
{
    public sealed class UnifiedNotesService : IUnifiedNotesService
    {
        private readonly HttpClient _http; // OpenAI
        private readonly IHttpClientFactory _httpFactory; // for fetching source when seed missing
        private readonly string _model;

        public UnifiedNotesService(HttpClient http, IHttpClientFactory httpFactory, IConfiguration cfg)
        {
            _http = http;
            _httpFactory = httpFactory;

            var baseUrl = cfg["OpenAI:BaseUrl"] ?? "https://api.openai.com";
            var apiKey = cfg["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey missing");
            _model = cfg["OpenAI:Model"] ?? "gpt-4o-mini";

            _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<UnifiedNotesResult> BuildAsync(string url, string title, string seedText, CancellationToken ct = default)
        {
            // Build context: prefer seedText; otherwise fetch + extract main text
            var context = seedText;
            if (string.IsNullOrWhiteSpace(context))
            {
                try
                {
                    var html = await DownloadAsync(url, ct);
                    context = ExtractPlain(html);
                }
                catch
                {
                    context = title ?? string.Empty;
                }
            }
            context = Truncate((title ?? string.Empty) + "\n\n" + (context ?? string.Empty), 7000);

            var system = """
You are Nuuz, a concise, neutral news assistant. Return ONLY strict JSON with the following shape:
{
  "summary": "<=80 words...",
  "vibe": "Upbeat|Analytical|Cautionary|Wholesome|Excited|Neutral",
  "tags": ["..."],
  "sentiment": { "overall": -1..1, "var": 0..1 },
  "arousal": 0..1,
  "signals": {
    "depth": 0..1,
    "readMinutes": 1..60,
    "conflict": 0..1,
    "practicality": 0..1,
    "optimism": 0..1,
    "novelty": 0..1,
    "humanInterest": 0..1,
    "hype": 0..1,
    "explainer": 0..1,
    "analysis": 0..1,
    "wholesome": 0..1,
    "genre": "Explainer|Analysis|Report|Profile|HowTo|Q&A|List|Recap",
    "eventStage": "Breaking|Update|Launch|Aftermath|Feature",
    "format": "Short|Standard|Longform|Visual"
  },
  "sparkNotesHtml": "<article ...>clean minimal HTML...</article>"
}

Rules:
- Use your OWN words (no verbatim copying). Up to two short quotes (<= 75 words each) only if present in the context.
- tags: 3-6 short topical tags.
- The sparkNotesHtml must be clean minimal HTML (no script/style). Omit sections that would be empty.
""";

            var user = $""""
Source URL: {url} | Title: {title ?? "Untitled"} || Context: """{context}""" || Produce sparkNotesHtml with these sections WHEN RELEVANT (omit empties):
<h3>TL;DR</h3><ul><li>3-5 crisp bullets</li></ul>
<h3>What happened</h3><p>2-3 short paragraphs</p>
<h3>Why it matters</h3><ul><li>2-4 bullets</li></ul>
<h3>Key timeline</h3><ul><li><strong>YYYY-MM-DD:</strong> event</li></ul>
<h3>Quotes</h3><blockquote>"<= 75 words from the context"</blockquote>
<p class="cta"><a href="{url}" target="_blank" rel="noopener nofollow">Read the original at the source</a></p>
"""";

            var req = new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user",   content = user }
                },
                temperature = 0.15,
                max_tokens = 1600,
                response_format = new { type = "json_object" }
            };

            using var contentJson = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("v1/chat/completions", contentJson, ct);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var innerJson = doc.RootElement.GetProperty("choices")[0]
                                 .GetProperty("message").GetProperty("content").GetString() ?? "{}";

            using var inner = JsonDocument.Parse(innerJson);
            var root = inner.RootElement;

            // Parse summarization pieces
            string summary = root.TryGetProperty("summary", out var s) ? (s.GetString() ?? "") : "";
            string vibe = root.TryGetProperty("vibe", out var v) ? (v.GetString() ?? "Neutral") : "Neutral";
            string[] tags = root.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                            ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                            : Array.Empty<string>();

            double? sentiment = null, sentimentVar = null, arousal = null;
            if (root.TryGetProperty("sentiment", out var sObj) && sObj.ValueKind == JsonValueKind.Object)
            {
                if (sObj.TryGetProperty("overall", out var ov) && ov.TryGetDouble(out var d)) sentiment = Math.Clamp(d, -1, 1);
                if (sObj.TryGetProperty("var", out var sv) && sv.TryGetDouble(out var v2)) sentimentVar = Math.Clamp(v2, 0, 1);
            }
            if (root.TryGetProperty("arousal", out var ar) && ar.TryGetDouble(out var a)) arousal = Math.Clamp(a, 0, 1);

            var features = root.TryGetProperty("signals", out var sigObj) && sigObj.ValueKind == JsonValueKind.Object
                ? ParseSignals(sigObj)
                : FallbackSignals(title ?? string.Empty, context ?? string.Empty);

            // Parse SparkNotes HTML
            string rawHtml = root.TryGetProperty("sparkNotesHtml", out var h) ? (h.GetString() ?? "") : "";
            string normalizedHtml = NormalizeHtmlConservative(rawHtml, url, title ?? "Untitled");
            string plain = HtmlToPlain(normalizedHtml);

            var rich = new RichSignals(summary, vibe, tags, sentiment, sentimentVar, arousal, features);
            var spark = new SparkNotesResult(normalizedHtml, plain);

            return new UnifiedNotesResult(rich, spark);
        }

        // ---------------- HTTP + extraction helpers ----------------
        private async Task<string> DownloadAsync(string url, CancellationToken ct)
        {
            var client = _httpFactory.CreateClient(nameof(UnifiedNotesService));
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

        private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max) + ".");

        // ---------------- Signals helpers ----------------
        private static Signals ParseSignals(JsonElement o)
        {
            double GetD(string n, double def = 0) => o.TryGetProperty(n, out var p) && p.TryGetDouble(out var v) ? Clamp01(v) : def;
            int GetI(string n, int def = 4) => o.TryGetProperty(n, out var p) && p.TryGetInt32(out var v) ? Math.Clamp(v, 1, 60) : def;
            string GetS(string n, string def = "") => o.TryGetProperty(n, out var p) ? (p.GetString() ?? def) : def;

            return new Signals(
                Depth: GetD("depth"),
                ReadMinutes: GetI("readMinutes"),
                Conflict: GetD("conflict"),
                Practicality: GetD("practicality"),
                Optimism: GetD("optimism"),
                Novelty: GetD("novelty"),
                HumanInterest: GetD("humanInterest"),
                Hype: GetD("hype"),
                Explainer: GetD("explainer"),
                Analysis: GetD("analysis"),
                Wholesome: GetD("wholesome"),
                Genre: GetS("genre"),
                EventStage: GetS("eventStage"),
                Format: GetS("format")
            );
        }

        // Lightweight heuristics if LLM fails or returns malformed JSON
        private static Signals FallbackSignals(string title, string text)
        {
            var words = Math.Max(50, (text ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length);
            var readMin = Math.Clamp((int)Math.Round(words / 220.0), 1, 60);
            string lower = ((title ?? "") + " " + (text ?? "")).ToLowerInvariant();

            double hit(params string[] ks) => ks.Any(k => lower.Contains(k)) ? 1 : 0;

            bool RegexLike(string s, string pattern) => System.Text.RegularExpressions.Regex.IsMatch(s, @"\b(" + pattern + @")\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            double explainer = RegexLike(lower, "what is|why|how |explainer|faq|q&a|primer|guide|deep dive") ? 0.7 : 0.15;
            double analysis = RegexLike(lower, "analysis|opinion|column|our take|we find|we estimate") ? 0.6 : 0.2;
            double hype = hit("wins", "beats", "smashes", "record", "soars", "launch", "unveils", "announces", "ipo") > 0 ? 0.7 : 0.15;
            double conflict = hit("vs", "versus", "lawsuit", "ban", "war", "protest", "attack", "accuses", "criticism", "feud") > 0 ? 0.6 : 0.2;
            double pract = RegexLike(lower, "how to|step-by-step|tips|checklist|template|best practices") ? 0.7 : 0.15;
            double wholesome = hit("wholesome", "kindness", "community", "wildlife", "nature", "feel-good", "donates", "rescues") > 0 ? 0.6 : 0.15;
            double human = hit("profile", "story", "mother", "father", "teacher", "student", "child", "family", "nurse") > 0 ? 0.55 : 0.2;
            double novelty = hit("first", "new", "launched", "breakthrough", "discovery", "reveals", "debuts") > 0 ? 0.6 : 0.25;
            double optimism = hit("improves", "better", "growth", "surge", "rebound", "record", "hope", "progress", "recover") > 0 ? 0.5 : 0.25;

            return new Signals(
                Depth: Math.Clamp((analysis * 0.6 + explainer * 0.3 + (words / 2000.0)), 0, 1),
                ReadMinutes: readMin,
                Conflict: conflict,
                Practicality: pract,
                Optimism: optimism,
                Novelty: novelty,
                HumanInterest: human,
                Hype: hype,
                Explainer: explainer,
                Analysis: analysis,
                Wholesome: wholesome,
                Genre: explainer >= analysis ? "Explainer" : "Analysis",
                EventStage: hype >= 0.6 ? "Launch" : "Feature",
                Format: readMin <= 4 ? "Short" : (readMin >= 14 ? "Longform" : "Standard")
            );
        }

        private static double Clamp01(double v) => Math.Min(1, Math.Max(0, v));

        // ---------------- Conservative normalizer (mirrors SparkNotesService) ----------------
        private static string NormalizeHtmlConservative(string rawHtml, string url, string title)
        {
            if (string.IsNullOrWhiteSpace(rawHtml))
                return "<article class=\"nuuz-sparknotes\"><p>We couldn't generate a brief this time.</p><p class=\"cta\"><a href=\"" + url + "\" target=\"_blank\" rel=\"noopener nofollow\">Read the original</a></p></article>";

            var html = rawHtml.Trim();

            // 0) Remove any branded headings the LLM might have emitted.
            html = Regex.Replace(html, @"<h[12][^>]*>\s*Nuuz\s+(SparkNotes|Notes)\s*</h[12]>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<p[^>]*class\s*=\s*[""']\s*kicker\s*[""'][^>]*>.*?</p>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 1) Remove obvious placeholder timeline items like "N/A"
            html = Regex.Replace(html, @"<li[^>]*>\s*[^<]*\bN/?A\b[^<]*\s*</li>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 2) If a list item contains ONLY a section heading (e.g., <li><h3>TL;DR</h3></li>), lift it out of the list
            html = Regex.Replace(html, @"<li[^>]*>\s*(<h3[^>]*>.*?</h3>)\s*</li>", "$1", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // 3) Ensure it's wrapped in <article class=\"nuuz-sparknotes\">
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

