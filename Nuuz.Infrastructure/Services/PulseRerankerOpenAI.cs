using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nuuz.Application.Services;

namespace Nuuz.Infrastructure.Services
{
    public sealed class PulseRerankerOpenAI : IPulseReranker
    {
        private readonly HttpClient _http;
        private readonly ILogger<PulseRerankerOpenAI> _log;
        private readonly string _model;
        private readonly int _timeoutSeconds;

        public PulseRerankerOpenAI(HttpClient http, IConfiguration cfg, ILogger<PulseRerankerOpenAI> log)
        {
            _http = http;
            _log = log;

            var baseUrl = cfg["OpenAI:BaseUrl"] ?? "https://api.openai.com";
            var apiKey = cfg["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey missing");
            _model = cfg["Pulse:Reranker:Model"] ?? cfg["OpenAI:Model"] ?? "gpt-4o-mini";
            _timeoutSeconds = ParseInt(cfg["Pulse:Reranker:TimeoutSeconds"], 12);
            if (_timeoutSeconds < 4) _timeoutSeconds = 4;

            _http.BaseAddress = new Uri(baseUrl.TrimEnd('/')); // we'll use relative "v1/..."
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (!_http.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/json"))
                _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<IReadOnlyList<IPulseReranker.Choice>> RerankAsync(
            IReadOnlyList<IPulseReranker.Input> items,
            int topK,
            CancellationToken ct = default)
        {
            if (items is null || items.Count == 0 || topK <= 0)
                return Array.Empty<IPulseReranker.Choice>();

            // Compact payload; trim overly long strings to reduce payload size
            var compact = items.Select(i => new
            {
                id = i.Id,
                t = Trim(i.Title, 240),
                s = i.SourceId,
                p = i.PublishedAt.ToUniversalTime().ToString("O"),
                sum = Trim(i.Summary, 320),
                tags = i.Tags?.Take(8)
            }).ToArray();

            var sys =
                "You are a front-page editor. Rank the MOST important stories for a general audience TODAY. " +
                "Prioritize: policy/geopolitics, major corporate actions, disasters, war/ceasefire, security, " +
                "macro/markets, public health. Deprioritize: deals/discounts, product reviews, shopping guides, " +
                "minor app updates, gossip. Prefer stories corroborated by reputable outlets and with wider impact. " +
                "Return strict JSON: {\"top\":[{\"id\":\"...\",\"score\":0..1,\"reasons\":[\"...\"]}]} " +
                $"Limit to top {topK}. Keep scores monotonic (desc).";

            var user = JsonSerializer.Serialize(new { items = compact });

            var payload = new
            {
                model = _model,
                temperature = 0.1,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = sys },
                    new { role = "user",   content = user }
                }
            };

            // simple retry policy for transient failures
            var attempts = 0;
            var maxAttempts = 3;
            Exception? last = null;

            while (attempts < maxAttempts && !ct.IsCancellationRequested)
            {
                attempts++;
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
              //  linkedCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") { Content = content };
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = SafeReadString(resp, linkedCts.Token);
                        _log.LogWarning("Reranker HTTP {Status}: {Body}", (int)resp.StatusCode, body);
                        // 429/5xx → retry; others → break
                        if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                        {
                            await Backoff(attempts, linkedCts.Token);
                            continue;
                        }
                        return Array.Empty<IPulseReranker.Choice>();
                    }

                    // Read+parse content
                    using var stream = await resp.Content.ReadAsStreamAsync(linkedCts.Token);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: linkedCts.Token);
                    var json = doc.RootElement.GetProperty("choices")[0]
                                              .GetProperty("message")
                                              .GetProperty("content")
                                              .GetString() ?? "{}";

                    using var parsed = JsonDocument.Parse(json);
                    if (!parsed.RootElement.TryGetProperty("top", out var arr) || arr.ValueKind != JsonValueKind.Array)
                        return Array.Empty<IPulseReranker.Choice>();

                    var results = new List<IPulseReranker.Choice>();
                    foreach (var el in arr.EnumerateArray())
                    {
                        var id = el.TryGetProperty("id", out var pid) ? (pid.GetString() ?? "") : "";
                        if (string.IsNullOrWhiteSpace(id)) continue;

                        var score = 0.5;
                        if (el.TryGetProperty("score", out var ps) && ps.TryGetDouble(out var d))
                            score = Math.Clamp(d, 0, 1);

                        string[] reasons = Array.Empty<string>();
                        if (el.TryGetProperty("reasons", out var pr) && pr.ValueKind == JsonValueKind.Array)
                            reasons = pr.EnumerateArray()
                                        .Select(x => x.GetString() ?? "")
                                        .Where(x => x.Length > 0)
                                        .Take(3)
                                        .ToArray();

                        results.Add(new IPulseReranker.Choice(id, score, reasons));
                    }
                    return results;
                }
                catch (OperationCanceledException oce) when (ct.IsCancellationRequested)
                {
                    // caller cancelled: bubble out
                    _log.LogDebug("Reranker cancelled by caller.");
                    throw;
                }
                catch (OperationCanceledException oce) // our per-call timeout
                {
                    last = oce;
                    _log.LogWarning("Reranker timed out after {s}s (attempt {a}/{m})", _timeoutSeconds, attempts, maxAttempts);
                    await Backoff(attempts, CancellationToken.None);
                }
                catch (HttpRequestException hre) when (IsTransient(hre))
                {
                    last = hre;
                    _log.LogWarning(hre, "Reranker transient HTTP error (attempt {a}/{m})", attempts, maxAttempts);
                    await Backoff(attempts, CancellationToken.None);
                }
                catch (IOException ioe)
                {
                    last = ioe;
                    _log.LogWarning(ioe, "Reranker IO error (attempt {a}/{m})", attempts, maxAttempts);
                    await Backoff(attempts, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    last = ex;
                    _log.LogError(ex, "Reranker failed (attempt {a}/{m})", attempts, maxAttempts);
                    // unlikely to succeed on retry if it's a parsing bug etc.
                    break;
                }
            }

            // Fail soft: return empty so Pulse falls back to heuristics
            if (last != null)
                _log.LogWarning("Reranker giving up after {m} attempts: {Message}", attempts, last.Message);

            return Array.Empty<IPulseReranker.Choice>();
        }

        // ---- helpers ----
        private static string Trim(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : (s.AsSpan(0, max).ToString());
        }

        private static int ParseInt(string? s, int fallback)
            => int.TryParse(s, out var v) ? v : fallback;

        private static bool IsTransient(HttpRequestException ex)
        {
            // Treat connection resets and HTTP/2 GOAWAY as transient
            return ex.InnerException is IOException;
        }

        private static async Task Backoff(int attempt, CancellationToken ct)
        {
            // jittered exponential backoff: 250ms, 500ms, 1s
            var ms = (int)(250 * Math.Pow(2, attempt - 1));
            var jitter = new Random().Next(0, 120);
            try { await Task.Delay(ms + jitter, ct); } catch { }
        }

        private static string SafeReadString(HttpResponseMessage resp, CancellationToken ct)
        {
            try { return resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult(); }
            catch { return "<unreadable>"; }
        }
    }
}
