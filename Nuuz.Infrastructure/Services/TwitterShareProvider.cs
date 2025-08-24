using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Configuration;
using Nuuz.Application.Abstraction;
using Nuuz.Application.Services;
using Nuuz.Domain.Entities;
using Nuuz.Infrastructure.Repositories;

namespace Nuuz.Infrastructure.Services;

public sealed class TwitterShareProvider : IShareProvider
{
    private readonly IHttpClientFactory _http;
    private readonly FirestoreDb _db;
    private readonly IArticleRepository _articles;
    private readonly IConnectedAccountRepository _accounts;
    private readonly IShareLinkRepository _links;
    private readonly IShareEventRepository _events;
    private readonly IOAuthStateRepository _oauth;
    private readonly IConfiguration _cfg;

    public string Key => "twitter";
    public ProviderLimits Limits => new(280, 23, false);

    public TwitterShareProvider(
        IHttpClientFactory http,
        FirestoreDb db,
        IArticleRepository articles,
        IConnectedAccountRepository accounts,
        IShareLinkRepository links,
        IShareEventRepository events,
        IOAuthStateRepository oauth,
        IConfiguration cfg)
    {
        _http = http; _db = db; _articles = articles; _accounts = accounts; _links = links; _events = events; _oauth = oauth; _cfg = cfg;
    }

    // ---------- OAuth (PKCE) ----------
    public async Task<string> ConnectStartAsync(string userId, string? redirectTo, CancellationToken ct = default)
    {
        var state = Base64Url(RandomBytes(24));
        var codeVerifier = Base64Url(RandomBytes(32));
        var codeChallenge = Base64Url(Sha256(codeVerifier));

        var st = new OAuthState { Id = state, UserId = userId, Provider = Key, CodeVerifier = codeVerifier, RedirectTo = redirectTo };
        await _oauth.AddAsync(st);

        var p = _cfg.GetSection("Twitter");
        var authBase = p["AuthBase"]!;
        var clientId = p["ClientId"]!;
        var redirectUri = p["RedirectUri"]!;
        var scopes = p["Scopes"] ?? "tweet.read tweet.write users.read offline.access";

        var url =
            $"{authBase}?response_type=code" +
            $"&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scopes)}" +
            $"&state={state}" +
            $"&code_challenge={codeChallenge}" +
            $"&code_challenge_method=S256";

        return url;
    }

    public async Task<bool> ConnectCallbackAsync(string state, string code, CancellationToken ct = default)
    {
        var st = await _oauth.GetAsync(state);
        if (st is null) return false;

        var p = _cfg.GetSection("Twitter");
        var tokenUrl = p["TokenUrl"]!;
        var clientId = p["ClientId"]!;
        var redirectUri = p["RedirectUri"]!;
        var secret = p["ClientSecret"]; // optional

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code_verifier"] = st.CodeVerifier,
            ["code"] = code,
            ["redirect_uri"] = redirectUri
        };

        var http = _http.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        { Content = new FormUrlEncodedContent(form) };

        if (!string.IsNullOrWhiteSpace(secret))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        var res = await http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Twitter token exchange failed: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString()!;
        var refresh = root.TryGetProperty("refresh_token", out var rr) ? rr.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

        var me = await FetchHandleAsync(access, ct);

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
        acc.Handle = me;
        acc.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        if (await _accounts.GetAsync(acc.Id) is null)
            await _accounts.AddAsync(acc);
        else
            await _accounts.UpdateAsync(acc);

        // one-time state can be deleted
        await _oauth.DeleteAsync(state);
        return true;
    }

    private async Task<string?> RefreshAsync(ConnectedAccount acc, CancellationToken ct)
    {
        if (acc.ExpiresAt?.ToDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(2))
            return acc.AccessToken; // still valid

        if (string.IsNullOrWhiteSpace(acc.RefreshToken)) return acc.AccessToken;

        var p = _cfg.GetSection("Twitter");
        var tokenUrl = p["TokenUrl"]!;
        var clientId = p["ClientId"]!;
        var secret = p["ClientSecret"];

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = acc.RefreshToken!,
            ["client_id"] = clientId
        };

        var http = _http.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        { Content = new FormUrlEncodedContent(form) };

        if (!string.IsNullOrWhiteSpace(secret))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        var res = await http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return acc.AccessToken;

        using var doc = JsonDocument.Parse(json);
        var access = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

        acc.AccessToken = access;
        acc.ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60));
        acc.UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await _accounts.UpdateAsync(acc);

        return access;
    }

    private async Task<string?> FetchHandleAsync(string accessToken, CancellationToken ct)
    {
        var http = _http.CreateClient();
        var meUrl = $"{_cfg["Twitter:ApiBase"]}/users/me?user.fields=username";
        using var req = new HttpRequestMessage(HttpMethod.Get, meUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var res = await http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(json);
        var username = doc.RootElement.GetProperty("data").GetProperty("username").GetString();
        return username is null ? null : $"@{username}";
    }

    // ---------- Prepare + Post ----------
    public async Task<PrepareShareResult> PrepareAsync(string userId, string articleId, CancellationToken ct = default)
    {
        var acc = await _accounts.GetForUserAsync(userId, Key);
        var a = await _articles.GetAsync(articleId) ?? throw new KeyNotFoundException("Article not found.");

        string host;
        try { host = new Uri(a.Url!).Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase); }
        catch { host = a.SourceId ?? "source"; }

        var title = a.Title ?? "Interesting read";
        var textTemplate = $"{title} — {host}\n";
        var hashtags = (a.Tags ?? new List<string>()).Take(3).Select(ToHash).Where(s => s.Length > 1).ToArray();

        // short link
        var shareId = $"sh_{Guid.NewGuid():N}";
        var appBase = _cfg["Share:PublicAppBaseUrl"]!.TrimEnd('/');
        var apiBase = _cfg["Share:ApiBaseUrl"]!.TrimEnd('/');
        var shortPath = shareId;
        var shortUrl = $"{apiBase}{_cfg["Share:ShortPrefix"]}/{shortPath}";
        var targetUrl = $"{appBase}/read/{Uri.EscapeDataString(articleId)}?from=share&sid={shareId}";

        var link = new ShareLink
        {
            Id = shareId,
            ShortPath = shortPath,
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
            var clean = new string((t ?? "").Where(ch => char.IsLetterOrDigit(ch)).ToArray());
            if (string.IsNullOrWhiteSpace(clean)) return "";
            return char.IsLetter(clean[0]) ? clean : $"_{clean}";
        }
    }

    public async Task<PostShareResult> PostAsync(string userId, string shareId, string text, CancellationToken ct = default)
    {
        var acc = await _accounts.GetForUserAsync(userId, Key);
        if (acc is null) return new("error", null, null, "Not connected");

        var token = await RefreshAsync(acc, ct);
        var http = _http.CreateClient();

        var url = $"{_cfg["Twitter:ApiBase"]}/tweets";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json");

        var res = await http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            await _events.AddAsync(new ShareEvent { Id = $"se_{Guid.NewGuid():N}", ShareId = shareId, UserId = userId, Provider = Key, Mode = "api", Status = "error", Error = json });
            return new("error", null, null, json);
        }

        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        var permalink = $"https://twitter.com/i/web/status/{id}";

        await _events.AddAsync(new ShareEvent
        {
            Id = $"se_{Guid.NewGuid():N}",
            ShareId = shareId,
            UserId = userId,
            Provider = Key,
            Mode = "api",
            Status = "posted",
            ProviderPostId = id,
            ProviderPermalink = permalink
        });

        return new("posted", id, permalink);
    }

    // ---------- helpers ----------
    private static byte[] RandomBytes(int len) { var b = new byte[len]; RandomNumberGenerator.Fill(b); return b; }
    private static byte[] Sha256(string s) => SHA256.HashData(Encoding.ASCII.GetBytes(s));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
