using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nuuz.Application.Abstraction;
using Nuuz.Application.DTOs;

namespace Nuuz.Infrastructure.Services
{
    public sealed class EmbedProbeService : IEmbedProbeService
    {
        private readonly IHttpClientFactory _http;

        public EmbedProbeService(IHttpClientFactory http) => _http = http;

        public async Task<EmbedCheckResult> CheckAsync(string url, CancellationToken ct = default)
        {
            var client = _http.CreateClient(nameof(EmbedProbeService));
            client.Timeout = TimeSpan.FromSeconds(7);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Nuuz/1.0 (+https://nuuz.app)");

            async Task<(HttpResponseMessage resp, string finalUrl)> HeadOrGetAsync(string u)
            {
                // Try HEAD first (many sites support it), else fallback to GET with no body read
                try
                {
                    var head = new HttpRequestMessage(HttpMethod.Head, u);
                    var r = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
                    return (r, r.RequestMessage!.RequestUri!.ToString());
                }
                catch
                {
                    var get = new HttpRequestMessage(HttpMethod.Get, u);
                    var r = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
                    return (r, r.RequestMessage!.RequestUri!.ToString());
                }
            }

            try
            {
                var (resp, finalUrl) = await HeadOrGetAsync(url);
                using (resp)
                {
                    var xfo = resp.Headers.TryGetValues("X-Frame-Options", out var xfoVals)
                        ? string.Join(", ", xfoVals)
                        : null;

                    var csp = resp.Headers.TryGetValues("Content-Security-Policy", out var cspVals)
                        ? string.Join(" ", cspVals)
                        : null;

                    // Quick checks
                    if (!string.IsNullOrWhiteSpace(xfo))
                    {
                        var v = xfo.ToUpperInvariant();
                        if (v.Contains("DENY"))
                            return Blocked($"X-Frame-Options: DENY", finalUrl, (int)resp.StatusCode);
                        if (v.Contains("SAMEORIGIN"))
                            return Blocked($"X-Frame-Options: SAMEORIGIN", finalUrl, (int)resp.StatusCode);
                        if (v.Contains("ALLOW-FROM"))
                            return Blocked($"X-Frame-Options: ALLOW-FROM (legacy; generally blocks third-party)", finalUrl, (int)resp.StatusCode);
                    }

                    if (!string.IsNullOrWhiteSpace(csp))
                    {
                        // Parse frame-ancestors directive
                        var reason = ParseCspFrameAncestors(csp);
                        if (reason is { Blocked: true, Message: { } msg })
                            return Blocked(msg, finalUrl, (int)resp.StatusCode);
                    }

                    // If status is an error, we can still allow trying
                    return new EmbedCheckResult
                    {
                        Embeddable = true,
                        Reason = null,
                        FinalUrl = finalUrl,
                        StatusCode = (int)resp.StatusCode
                    };
                }
            }
            catch (Exception ex)
            {
                // Network/timeout: unknown, let UI offer graceful fallback
                return new EmbedCheckResult
                {
                    Embeddable = false,
                    Reason = $"Network error: {ex.GetType().Name}",
                    FinalUrl = url,
                    StatusCode = 0
                };
            }

            static (bool Blocked, string? Message) ParseCspFrameAncestors(string csp)
            {
                // Extract "frame-ancestors ..." directive
                // Very simple parse that handles common cases
                var parts = csp.Split(';', StringSplitOptions.RemoveEmptyEntries);
                var fa = parts.FirstOrDefault(p => p.TrimStart().StartsWith("frame-ancestors", StringComparison.OrdinalIgnoreCase));
                if (fa == null) return (false, null);

                var tokens = fa.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray(); // after directive name
                if (tokens.Length == 0) return (false, null);

                // Common blocks:
                // 'none'  => blocks all
                // 'self' only => blocks third-party
                // explicit hosts => blocks unless present (we're third-party)
                if (tokens.Contains("'none'"))
                    return (true, "CSP frame-ancestors: 'none'");

                // If only 'self' appears (and no wildcard/https:), assume blocked
                var hasWildcard = tokens.Any(t => t == "*" || t == "https:" || t.EndsWith("://*"));
                var onlySelf = tokens.All(t => t == "'self'");
                if (onlySelf && !hasWildcard)
                    return (true, "CSP frame-ancestors: 'self' (third-party blocked)");

                // If there are specific hosts and none is wildcard, treat as blocked
                var hasSpecificHosts = tokens.Any(t => !t.StartsWith("'") && !t.Contains("*") && !t.Equals("https:", StringComparison.OrdinalIgnoreCase));
                if (hasSpecificHosts && !hasWildcard)
                    return (true, "CSP frame-ancestors: restricted hosts");

                return (false, null);
            }

            static EmbedCheckResult Blocked(string reason, string finalUrl, int status)
                => new()
                {
                    Embeddable = false,
                    Reason = reason,
                    FinalUrl = finalUrl,
                    StatusCode = status
                };
        }
    }
}
