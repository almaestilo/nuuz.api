using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Nuuz.Application.Services;

namespace Nuuz.Infrastructure.Services
{
    public sealed class OpenAiSummarizer : IAISummarizer
    {
        private readonly HttpClient _http;
        private readonly string _model;

        public OpenAiSummarizer(HttpClient http, IConfiguration cfg)
        {
            _http = http;
            var baseUrl = cfg["OpenAI:BaseUrl"] ?? "https://api.openai.com";
            var apiKey = cfg["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey missing");
            _model = cfg["OpenAI:Model"] ?? "gpt-4o-mini";

            _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        // -------- existing (unchanged) --------
        public async Task<(string summary, string vibe, string[] tags)>
            SummarizeAsync(string title, string text, CancellationToken ct = default)
        {
            var sys = "You are a concise news assistant. Return strict JSON: " +
                      "{\"summary\":\"...\",\"vibe\":\"...\",\"tags\":[\"...\"]}. " +
                      "summary: <= 80 words. vibe: one of [Upbeat, Analytical, Cautionary, Wholesome, Excited, Neutral]. " +
                      "tags: 3-6 short topical tags.";
            var user = $"TITLE:\n{title}\n\nTEXT:\n{text}";

            var req = new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = sys },
                    new { role = "user",   content = user }
                },
                temperature = 0.2,
                response_format = new { type = "json_object" }
            };

            using var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("v1/chat/completions", content, ct);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var json = doc.RootElement.GetProperty("choices")[0]
                                      .GetProperty("message").GetProperty("content").GetString() ?? "{}";

            using var inner = JsonDocument.Parse(json);
            var root = inner.RootElement;

            string summary = root.TryGetProperty("summary", out var s) ? (s.GetString() ?? "") : "";
            string vibe = root.TryGetProperty("vibe", out var v) ? (v.GetString() ?? "Neutral") : "Neutral";
            string[] tags = root.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                            ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                            : Array.Empty<string>();

            return (summary, vibe, tags);
        }

        // -------- existing (unchanged) --------
        public async Task<(string summary, string vibe, string[] tags, double? sentiment, double? sentimentVar, double? arousal)>
            SummarizeRichAsync(string title, string text, CancellationToken ct = default)
        {
            var sys =
                "You are a concise news assistant. Return strict JSON:\n" +
                "{\"summary\":\"...\",\"vibe\":\"...\",\"tags\":[\"...\"],\"sentiment\":{\"overall\":-1..1,\"var\":0..1},\"arousal\":0..1}.\n" +
                "summary: <= 80 words. vibe: one of [Upbeat, Analytical, Cautionary, Wholesome, Excited, Neutral].\n" +
                "tags: 3-6 short topical tags. Use neutral sentiment if unclear; arousal is urgency/energy from 0..1.";

            var user = $"TITLE:\n{title}\n\nTEXT:\n{text}";

            var req = new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = sys },
                    new { role = "user",   content = user }
                },
                temperature = 0.2,
                response_format = new { type = "json_object" }
            };

            using var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("v1/chat/completions", content, ct);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var json = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";

            using var inner = JsonDocument.Parse(json);
            var root = inner.RootElement;

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

            return (summary, vibe, tags, sentiment, sentimentVar, arousal);
        }

        // -------- NEW: signals for vibe-scoring --------
        public async Task<RichSignals> SummarizeSignalsAsync(string title, string text, CancellationToken ct = default)
        {
            var sys =
@"Return ONLY this JSON:
{
 ""summary"": ""... (<=80 words)"",
 ""vibe"": ""Upbeat|Analytical|Cautionary|Wholesome|Excited|Neutral"",
 ""tags"": [""..."",""...""],
 ""sentiment"": { ""overall"": -1..1, ""var"": 0..1 },
 ""arousal"": 0..1,
 ""signals"": {
   ""depth"": 0..1,
   ""readMinutes"": 1..60,
   ""conflict"": 0..1,
   ""practicality"": 0..1,
   ""optimism"": 0..1,
   ""novelty"": 0..1,
   ""humanInterest"": 0..1,
   ""hype"": 0..1,
   ""explainer"": 0..1,
   ""analysis"": 0..1,
   ""wholesome"": 0..1,
   ""genre"": ""Explainer|Analysis|Report|Profile|HowTo|Q&A|List|Recap"",
   ""eventStage"": ""Breaking|Update|Launch|Aftermath|Feature"",
   ""format"": ""Short|Standard|Longform|Visual""
 }
}";
            var user = $"TITLE:\n{title}\n\nTEXT:\n{text}";

            var req = new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = sys },
                    new { role = "user",   content = user }
                },
                temperature = 0.1,
                response_format = new { type = "json_object" }
            };

            using var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("v1/chat/completions", content, ct);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var json = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";

            using var inner = JsonDocument.Parse(json);
            var root = inner.RootElement;

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

            // signals
            var sig = root.TryGetProperty("signals", out var sigObj) && sigObj.ValueKind == JsonValueKind.Object
                ? ParseSignals(sigObj)
                : FallbackSignals(title, text);

            return new RichSignals(summary, vibe, tags, sentiment, sentimentVar, arousal, sig);
        }

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
            // crude estimates
            var words = Math.Max(50, text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length);
            var readMin = Math.Clamp((int)Math.Round(words / 220.0), 1, 60);

            string lower = (title + " " + text).ToLowerInvariant();

            double hit(params string[] ks) => ks.Any(k => lower.Contains(k)) ? 1 : 0;

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

            static bool RegexLike(string s, string pattern)
                => System.Text.RegularExpressions.Regex.IsMatch(s, @"\b(" + pattern + @")\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static double Clamp01(double v) => Math.Min(1, Math.Max(0, v));
    }
}
