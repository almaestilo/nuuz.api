using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Configuration;
using Nuuz.Application.Abstraction;
using Nuuz.Application.Services;
using Nuuz.Domain.Entities;
using Nuuz.Infrastructure.Repositories;
using Nuuz.Infrastructure.Security;

namespace Nuuz.Infrastructure.Services;

public sealed class BlueskyShareProvider : IShareProvider, ISecretConnectShareProvider
{
    private readonly IHttpClientFactory _http;
    private readonly FirestoreDb _db;
    private readonly IArticleRepository _articles;
    private readonly IConnectedAccountRepository _accounts;
    private readonly IShareLinkRepository _links;
    private readonly IShareEventRepository _events;
    private readonly IOAuthStateRepository _oauth;
    private readonly IConfiguration _cfg;

    public string Key => "bluesky";
    public ProviderLimits Limits => new(300, 0, true); // 300 chars, no special URL weight, multiline ok

    public BlueskyShareProvider(
        IHttpClientFactory http,
        FirestoreDb db,
        IArticleRepository articles,
        IConnectedAccountRepository accounts,
        IShareLinkRepository links,
        IShareEventRepository events,
        IOAuthStateRepository oauth,
        IConfiguration cfg)
    {
        _http = http; _db = db; _articles = articles; _accounts = accounts;
        _links = links; _events = events; _oauth = oauth; _cfg = cfg;
    }

    private HttpClient H()
    {
        var c = _http.CreateClient();
        c.Timeout = TimeSpan.FromSeconds(_cfg.GetValue("Bluesky:ApiTimeoutSeconds", 15));
        return c;
    }

    private string ConfigXrpcBase => _cfg["Bluesky:XrpcBase"]!.TrimEnd('/');           // e.g. https://bsky.social/xrpc
    private string AuthorizeEndpoint => _cfg["BlueskyOAuth:AuthorizeEndpoint"]!;
    private string TokenEndpoint => _cfg["BlueskyOAuth:TokenEndpoint"]!;
    private string ClientId => _cfg["BlueskyOAuth:ClientId"]!;
    private string RedirectUri => _cfg["BlueskyOAuth:RedirectUri"]!;

    // IMPORTANT: include the create-post repo scope by default
    private string Scopes =>
        _cfg["BlueskyOAuth:Scopes"]
        ?? "atproto offline_access repo:app.bsky.feed.post?action=create";

    private string GetXrpcFor(ConnectedAccount acc) =>
        string.IsNullOrWhiteSpace(acc.PdsBase) ? ConfigXrpcBase : $"{acc.PdsBase!.TrimEnd('/')}/xrpc";

    // ============================================================
    // OAuth 2.0 + PKCE + DPoP
    // ============================================================

    public async Task<string> ConnectStartAsync(string userId, string? redirectTo, CancellationToken ct = default)
    {
        var state = Base64Url(RandomBytes(24));
        var codeVerifier = Base64Url(RandomBytes(32));
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        var dpop = DPoP.GenerateP256(); // ephemeral key for token exchange

        await _oauth.AddAsync(new OAuthState
        {
            Id = state,
            UserId = userId,
            Provider = Key,
            CodeVerifier = codeVerifier,
            RedirectTo = redirectTo,
            DpopPrivateJwk = dpop.PrivateJwkJson
        });

        var url =
            $"{AuthorizeEndpoint}?response_type=code" +
            $"&client_id={Uri.EscapeDataString(ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&scope={Uri.EscapeDataString(Scopes)}" +
            $"&state={state}" +
            $"&code_challenge={codeChallenge}" +
            $"&code_challenge_method=S256" +
            $"&dpop_jkt={dpop.Thumbprint}";

        return url;
    }

    public async Task<bool> ConnectCallbackAsync(string state, string code, CancellationToken ct = default)
    {
        var st = await _oauth.GetAsync(state);
        if (st is null) return false;

        var http = H();
        string? nonce = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["code_verifier"] = st.CodeVerifier
            };

            var proof = DPoP.CreateProof("POST", TokenEndpoint, st.DpopPrivateJwk!, nonce);
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form)
            };
            req.Headers.Add("DPoP", proof);
            req.Headers.Accept.ParseAdd("application/json");

            var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (res.IsSuccessStatusCode)
            {
                await FinalizeOAuthSuccess(st, body, ct);
                return true;
            }

            var nextNonce = ExtractDpopNonce(res) ?? ExtractDpopNonceFromBody(body);
            if (!string.IsNullOrWhiteSpace(nextNonce))
            {
                nonce = nextNonce;
                continue;
            }

            throw new InvalidOperationException($"Bluesky OAuth token exchange failed: {body}");
        }

        throw new InvalidOperationException("Bluesky OAuth token exchange failed after nonce retries.");
    }

    private async Task FinalizeOAuthSuccess(OAuthState st, string tokenJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(tokenJson);
        var root = doc.RootElement;

        var access = root.GetProperty("access_token").GetString()!;
        var refresh = root.TryGetProperty("refresh_token", out var rr) ? rr.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

        // DID lives in JWT payload 'sub'
        var did = TryGetJwtSub(access) ?? throw new InvalidOperationException("OAuth token missing 'sub' (DID).");

        // Discover the user's PDS from PLC so we post to the right host
        var pdsBase = await ResolvePdsBaseForDid(did, ct); // e.g. https://bsky.social

        var acc = await _accounts.GetForUserAsync(st.UserId, Key) ?? new ConnectedAccount
        {
            Id = $"ca_{st.UserId}_{Key}",
            UserId = st.UserId,
            Provider = Key,
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        acc.AccessToken = access;
        acc.RefreshToken = refresh;
        acc.ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60));
        acc.DpopPrivateJwk = st.DpopPrivateJwk;
        acc.Did = did;
        acc.PdsBase = pdsBase; // <—— store PDS base!
        acc.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        if (await _accounts.GetAsync(acc.Id) is null) await _accounts.AddAsync(acc);
        else await _accounts.UpdateAsync(acc);

        await _oauth.DeleteAsync(st.Id);
    }

    // ============================================================
    // App-password connect (fallback)
    // ============================================================

    public async Task<bool> ConnectWithSecretAsync(string userId, string identifier, string appPassword, CancellationToken ct = default)
    {
        // App-password sessions are created at the user’s PDS. We'll start with config XRPC,
        // then discover/patch the PDS host after we know the DID.
        var http = H();
        var url = $"{ConfigXrpcBase}/com.atproto.server.createSession";

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { identifier, password = appPassword }), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        var res = await http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException("Bluesky rejected the credentials (handle or app password invalid).");

        using var doc = JsonDocument.Parse(json);
        var access = doc.RootElement.GetProperty("accessJwt").GetString()!;
        var refresh = doc.RootElement.TryGetProperty("refreshJwt", out var rj) ? rj.GetString() : null;
        var did = doc.RootElement.GetProperty("did").GetString()!;
        var handle = doc.RootElement.TryGetProperty("handle", out var h) ? h.GetString() : null;

        var pdsBase = await ResolvePdsBaseForDid(did, ct);

        var acc = await _accounts.GetForUserAsync(userId, Key) ?? new ConnectedAccount
        {
            Id = $"ca_{userId}_{Key}",
            UserId = userId,
            Provider = Key,
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        acc.AccessToken = access;
        acc.RefreshToken = refresh;
        acc.ExpiresAt = null;                 // session JWT (no fixed exp exposed)
        acc.DpopPrivateJwk = null;            // not using OAuth
        acc.Did = did;
        acc.Handle = handle is null ? did : $"@{handle}";
        acc.PdsBase = pdsBase;
        acc.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        if (await _accounts.GetAsync(acc.Id) is null) await _accounts.AddAsync(acc);
        else await _accounts.UpdateAsync(acc);

        return true;
    }

    // ============================================================
    // Prepare + Post
    // ============================================================

    public async Task<PrepareShareResult> PrepareAsync(string userId, string articleId, CancellationToken ct = default)
    {
        var acc = await _accounts.GetForUserAsync(userId, Key);
        var a = await _articles.GetAsync(articleId) ?? throw new KeyNotFoundException("Article not found.");

        string host;
        try { host = new Uri(a.Url!).Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase); }
        catch { host = a.SourceId ?? "source"; }

        var title = a.Title ?? "Interesting read";
        var textTemplate = $"{title} — {host}\n";
        var hashtags = (a.Tags ?? new List<string>()).Take(3)
            .Select(ToHash).Where(s => s.Length > 1).ToArray();

        // short link
        var shareId = $"sh_{Guid.NewGuid():N}";
        var appBase = _cfg["Share:PublicAppBaseUrl"]!.TrimEnd('/');
        var apiBase = _cfg["Share:ApiBaseUrl"]!.TrimEnd('/');
        var shortUrl = $"{apiBase}{_cfg["Share:ShortPrefix"]}/{shareId}";
        var targetUrl = $"{appBase}/read/{Uri.EscapeDataString(articleId)}?from=share&sid={shareId}";

        var link = new ShareLink
        {
            Id = shareId,
            ShortPath = shareId,
            UserId = userId,
            ArticleId = articleId,
            Provider = Key,
            TargetUrl = targetUrl
        };
        await _links.AddAsync(link);

        return new PrepareShareResult(
            shareId,
            title,
            textTemplate,
            hashtags,
            shortUrl,
            Limits,
            acc is not null
        );

        static string ToHash(string t)
        {
            var clean = new string((t ?? "").Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrWhiteSpace(clean)) return "";
            return char.IsLetter(clean[0]) ? clean : $"_{clean}";
        }
    }

    public async Task<PostShareResult> PostAsync(string userId, string shareId, string text, CancellationToken ct = default)
    {
        var acc = await _accounts.GetForUserAsync(userId, Key);
        if (acc is null) return new("error", null, null, "Not connected");

        // Post body
        var record = new BskyPostRecord
        {
            Type = "app.bsky.feed.post",
            text = text,
            createdAt = DateTime.UtcNow.ToString("o")
        };

        // Ensure we have a PDS base; if missing, try to discover again
        if (string.IsNullOrWhiteSpace(acc.PdsBase) && !string.IsNullOrWhiteSpace(acc.Did))
        {
            acc.PdsBase = await ResolvePdsBaseForDid(acc.Did!, ct);
            acc.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
            await _accounts.UpdateAsync(acc);
        }

        if (!string.IsNullOrWhiteSpace(acc.DpopPrivateJwk))
        {
            // ===== OAuth + DPoP =====
            var token = await EnsureFreshOAuthAsync(acc, ct);

            if (string.IsNullOrWhiteSpace(acc.Did))
            {
                // Recover DID from token if needed
                var sub = TryGetJwtSub(token);
                if (!string.IsNullOrWhiteSpace(sub)) { acc.Did = sub; await _accounts.UpdateAsync(acc); }
            }

            var createUrl = $"{GetXrpcFor(acc)}/com.atproto.repo.createRecord";

            var http = H();

            // Prefetch nonce
            var preNonce = await ProbeNonceAsync(http, createUrl, acc.DpopPrivateJwk!, token, ct);

            // First try with nonce + ath
            var res = await SendOnceWithDpopAsync(
                http, HttpMethod.Post, createUrl, acc.DpopPrivateJwk!, token, preNonce,
                () =>
                {
                    var body = new { repo = acc.Did!, collection = "app.bsky.feed.post", record = record };
                    return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                },
                ct);

            // Retry once if nonce rotates
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                var respBody = await SafeReadAsync(res, ct);
                var nextNonce = ExtractDpopNonce(res) ?? ExtractDpopNonceFromBody(respBody);
                if (!string.IsNullOrWhiteSpace(nextNonce))
                {
                    res.Dispose();
                    res = await SendOnceWithDpopAsync(
                        http, HttpMethod.Post, createUrl, acc.DpopPrivateJwk!, token, nextNonce,
                        () =>
                        {
                            var body = new { repo = acc.Did!, collection = "app.bsky.feed.post", record = record };
                            return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                        },
                        ct);
                }
            }

            var jsonResp = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                await _events.AddAsync(new ShareEvent
                {
                    Id = $"se_{Guid.NewGuid():N}",
                    ShareId = shareId, UserId = userId, Provider = Key,
                    Mode = "api", Status = "error", Error = jsonResp
                });
                return new("error", null, null, jsonResp);
            }

            using var doc = JsonDocument.Parse(jsonResp);
            var uri = doc.RootElement.GetProperty("uri").GetString()!;
            var rkey = uri.Split('/').LastOrDefault();
            var profile = (acc.Handle ?? acc.Did ?? "user").TrimStart('@');
            var permalink = $"https://bsky.app/profile/{profile}/post/{rkey}";

            await _events.AddAsync(new ShareEvent
            {
                Id = $"se_{Guid.NewGuid():N}",
                ShareId = shareId, UserId = userId, Provider = Key,
                Mode = "api", Status = "posted",
                ProviderPostId = rkey, ProviderPermalink = permalink
            });
            return new("posted", rkey, permalink);
        }
        else
        {
            // ===== Legacy app-password session =====
            var payload = new
            {
                repo = (acc.Handle ?? acc.Did ?? "me").TrimStart('@'),
                collection = "app.bsky.feed.post",
                record = record
            };

            var http = H();
            var url = $"{GetXrpcFor(acc)}/com.atproto.repo.createRecord";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acc.AccessToken);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var res = await http.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                await _events.AddAsync(new ShareEvent
                {
                    Id = $"se_{Guid.NewGuid():N}",
                    ShareId = shareId, UserId = userId, Provider = Key,
                    Mode = "api", Status = "error", Error = json
                });
                return new("error", null, null, json);
            }

            using var doc = JsonDocument.Parse(json);
            var uri = doc.RootElement.GetProperty("uri").GetString()!;
            var rkey = uri.Split('/').LastOrDefault();
            var profile = (acc.Handle ?? acc.Did ?? "user").TrimStart('@');
            var permalink = $"https://bsky.app/profile/{profile}/post/{rkey}";

            await _events.AddAsync(new ShareEvent
            {
                Id = $"se_{Guid.NewGuid():N}",
                ShareId = shareId, UserId = userId, Provider = Key,
                Mode = "api", Status = "posted",
                ProviderPostId = rkey, ProviderPermalink = permalink
            });
            return new("posted", rkey, permalink);
        }
    }

    // ============================================================
    // Helpers
    // ============================================================

    private async Task<string?> ResolvePdsBaseForDid(string did, CancellationToken ct)
    {
        try
        {
            var http = H();
            var url = $"https://plc.directory/{Uri.EscapeDataString(did)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");

            var res = await http.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("service", out var svcArr) && svcArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in svcArr.EnumerateArray())
                {
                    var type = s.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (!string.Equals(type, "AtprotoPersonalDataServer", StringComparison.OrdinalIgnoreCase)) continue;
                    var endpoint = s.TryGetProperty("serviceEndpoint", out var ep) ? ep.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(endpoint)) return endpoint!.TrimEnd('/');
                }
            }
        }
        catch { /* ignore and fallback */ }
        return null;
    }

    private async Task<string> EnsureFreshOAuthAsync(ConnectedAccount acc, CancellationToken ct)
    {
        if (acc.ExpiresAt?.ToDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(2))
            return acc.AccessToken!;

        if (string.IsNullOrWhiteSpace(acc.RefreshToken) || string.IsNullOrWhiteSpace(acc.DpopPrivateJwk))
            return acc.AccessToken!;

        var http = H();
        string? nonce = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                // NOTE: adding scope here doesn't *increase* scopes on most servers,
                // but including it is harmless and keeps parity with the original grant.
                ["scope"] = Scopes,
                ["refresh_token"] = acc.RefreshToken!
            };

            var proof = DPoP.CreateProof("POST", TokenEndpoint, acc.DpopPrivateJwk!, nonce);
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
            req.Headers.Add("DPoP", proof);
            req.Headers.Accept.ParseAdd("application/json");

            var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                var access = doc.RootElement.GetProperty("access_token").GetString()!;
                var refresh = doc.RootElement.TryGetProperty("refresh_token", out var rr) ? rr.GetString() : acc.RefreshToken;
                var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

                acc.AccessToken = access;
                acc.RefreshToken = refresh;
                acc.ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60));
                acc.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
                await _accounts.UpdateAsync(acc);

                return access;
            }

            var nextNonce = ExtractDpopNonce(res) ?? ExtractDpopNonceFromBody(body);
            if (!string.IsNullOrWhiteSpace(nextNonce))
            {
                nonce = nextNonce;
                continue; // retry with nonce
            }

            return acc.AccessToken!; // fallback without blocking
        }
        return acc.AccessToken!;
    }

    private static async Task<string?> ProbeNonceAsync(HttpClient http, string url, string dpopPrivateJwkJson, string accessToken, CancellationToken ct)
    {
        // HEAD
        using (var head = new HttpRequestMessage(HttpMethod.Head, url))
        {
            head.Headers.Authorization = new AuthenticationHeaderValue("DPoP", accessToken);
            head.Headers.Add("DPoP", DPoP.CreateProof("HEAD", url, dpopPrivateJwkJson, nonce: null, accessTokenForAth: accessToken));
            head.Headers.Accept.ParseAdd("application/json");
            try
            {
                var r = await http.SendAsync(head, ct);
                var n = ExtractDpopNonce(r);
                if (!string.IsNullOrWhiteSpace(n)) return n;
                var body = await SafeReadAsync(r, ct);
                n = ExtractDpopNonceFromBody(body);
                if (!string.IsNullOrWhiteSpace(n)) return n;
            }
            catch { /* ignore */ }
        }
        // GET
        using (var get = new HttpRequestMessage(HttpMethod.Get, url))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("DPoP", accessToken);
            get.Headers.Add("DPoP", DPoP.CreateProof("GET", url, dpopPrivateJwkJson, nonce: null, accessTokenForAth: accessToken));
            get.Headers.Accept.ParseAdd("application/json");
            try
            {
                var r = await http.SendAsync(get, ct);
                var n = ExtractDpopNonce(r);
                if (!string.IsNullOrWhiteSpace(n)) return n;
                var body = await SafeReadAsync(r, ct);
                n = ExtractDpopNonceFromBody(body);
                if (!string.IsNullOrWhiteSpace(n)) return n;
            }
            catch { /* ignore */ }
        }
        return null;
    }

    private static async Task<HttpResponseMessage> SendOnceWithDpopAsync(
        HttpClient http,
        HttpMethod method,
        string url,
        string dpopPrivateJwkJson,
        string accessToken,
        string? nonce,
        Func<HttpContent> contentFactory,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("DPoP", accessToken);
        req.Headers.Add("DPoP", DPoP.CreateProof(method.Method, url, dpopPrivateJwkJson, nonce, accessToken));
        req.Headers.Accept.ParseAdd("application/json");
        req.Content = contentFactory(); // fresh instance each time
        return await http.SendAsync(req, ct);
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try { return await res.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string? ExtractDpopNonce(HttpResponseMessage res)
    {
        if (res.Headers.TryGetValues("DPoP-Nonce", out var v1))
        {
            var n = v1.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(n)) return n;
        }
        if (res.Headers.TryGetValues("dpop-nonce", out var v2))
        {
            var n = v2.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(n)) return n;
        }
        foreach (var kv in res.Headers)
        {
            if (kv.Key.IndexOf("dpop", StringComparison.OrdinalIgnoreCase) >= 0 &&
                kv.Key.IndexOf("nonce", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var n = kv.Value.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(n)) return n;
            }
        }
        foreach (var wa in res.Headers.WwwAuthenticate)
        {
            var p = wa.Parameter ?? "";
            var key = "dpop_nonce=\"";
            var i = p.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                var start = i + key.Length;
                var end = p.IndexOf('"', start);
                if (end > start) return p.Substring(start, end - start);
            }
        }
        return null;
    }

    private static string? ExtractDpopNonceFromBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("dpop_nonce", out var a) && a.ValueKind == JsonValueKind.String) return a.GetString();
            if (root.TryGetProperty("dpopNonce", out var b) && b.ValueKind == JsonValueKind.String) return b.GetString();
            if (root.TryGetProperty("nonce", out var c) && c.ValueKind == JsonValueKind.String) return c.GetString();
        }
        catch { }
        return null;
    }

    private static string? TryGetJwtSub(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;

            static byte[] FromB64Url(string s)
            {
                s = s.Replace('-', '+').Replace('_', '/');
                return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
            }

            var payloadJson = Encoding.UTF8.GetString(FromB64Url(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.TryGetProperty("sub", out var subEl) ? subEl.GetString() : null;
        }
        catch { return null; }
    }

    private static byte[] RandomBytes(int len) { var b = new byte[len]; RandomNumberGenerator.Fill(b); return b; }
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class BskyPostRecord
    {
        [JsonPropertyName("$type")]
        public string Type { get; init; } = "app.bsky.feed.post";
        public string text { get; init; } = default!;
        public string createdAt { get; init; } = default!;
    }
}
