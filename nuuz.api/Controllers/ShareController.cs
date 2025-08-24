using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuuz.Application.Services;
using Nuuz.Application.Abstraction;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Nuuz.Infrastructure.Repositories;

namespace Nuuz.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/share")]
public sealed class ShareController : ControllerBase
{
    private readonly IEnumerable<IShareProvider> _providers;
    private readonly IShareService _shareSvc;
    private readonly IConnectedAccountRepository _accounts;

    public ShareController(
        IEnumerable<IShareProvider> providers,
        IShareService shareSvc,
        IConnectedAccountRepository accounts)
    {
        _providers = providers;
        _shareSvc = shareSvc;
        _accounts = accounts;
    }

    private string Uid() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new UnauthorizedAccessException();

    private IShareProvider GetProvider(string key) =>
        _providers.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Share provider '{key}' is not registered.");

    // ---------- Providers ----------
    [HttpGet("providers")]
    public async Task<IActionResult> Providers(CancellationToken ct)
    {
        var userId = Uid();

        var items = await Task.WhenAll(_providers.Select(async p =>
        {
            var acc = await _accounts.GetForUserAsync(userId, p.Key);
            var connected = acc is not null;

            string? handle = acc?.Handle;
            if (string.IsNullOrWhiteSpace(handle) &&
                string.Equals(p.Key, "bluesky", StringComparison.OrdinalIgnoreCase))
            {
                handle = acc?.Did;
            }

            return new
            {
                key = p.Key,
                name = p.Key switch
                {
                    "twitter" => "Twitter",
                    "bluesky" => "Bluesky",
                    "facebook" => "Facebook",
                    _ => p.Key
                },
                connected,
                handle
            };
        }));

        return Ok(items);
    }

    public sealed class PrepareDto
    {
        [Required] public string ArticleId { get; set; } = default!;
        public string Provider { get; set; } = "twitter";
    }

    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare([FromBody] PrepareDto dto, CancellationToken ct)
    {
        var userId = Uid();
        var prov = GetProvider(dto.Provider ?? "twitter");

        var prep = await prov.PrepareAsync(userId, dto.ArticleId, ct);
        var text = _shareSvc.ComposeWithLimits(prep.TextTemplate, prep.ShortUrl, prep.Hashtags, prov.Limits);

        return Ok(new
        {
            prep.ShareId,
            prep.Title,
            textTemplate = prep.TextTemplate,
            hashtags = prep.Hashtags,
            shortUrl = prep.ShortUrl,
            limits = new { prov.Limits.MaxChars, prov.Limits.UrlWeight },
            connected = prep.Connected,
            defaultText = text
        });
    }

    // ---------- Twitter ----------
    [HttpPost("twitter/connect/start")]
    public async Task<IActionResult> TwitterConnectStart([FromQuery] string? redirectTo, CancellationToken ct)
    {
        var url = await GetProvider("twitter").ConnectStartAsync(Uid(), redirectTo, ct);
        return Ok(new { authUrl = url });
    }

    [AllowAnonymous]
    [HttpGet("twitter/connect/callback")]
    public async Task<IActionResult> TwitterConnectCallback([FromQuery] string code, [FromQuery] string state, [FromQuery] string? redirect)
    {
        var ok = await GetProvider("twitter").ConnectCallbackAsync(state, code);
        var appBase = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Share:PublicAppBaseUrl"]!.TrimEnd('/');
        var target = $"{appBase}/share/connected?provider=twitter&ok={(ok ? 1 : 0)}";
        return Redirect(target);
    }

    public sealed class PostDto
    {
        [Required] public string ShareId { get; set; } = default!;
        [Required] public string Text { get; set; } = default!;
    }

    [HttpPost("twitter/post")]
    public async Task<IActionResult> TwitterPost([FromBody] PostDto dto, CancellationToken ct)
    {
        var result = await GetProvider("twitter").PostAsync(Uid(), dto.ShareId, dto.Text, ct);
        return Ok(result);
    }

    // ---------- BLUESKY OAuth ----------
    [HttpPost("bluesky/connect/start")]
    public async Task<IActionResult> BlueskyConnectStart([FromQuery] string? redirectTo, CancellationToken ct)
    {
        var url = await GetProvider("bluesky").ConnectStartAsync(Uid(), redirectTo, ct);
        return Ok(new { authUrl = url });
    }

    [AllowAnonymous]
    [HttpGet("bluesky/connect/callback")]
    public async Task<IActionResult> BlueskyConnectCallback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? iss,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription)
    {
        var appBase = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Share:PublicAppBaseUrl"]!.TrimEnd('/');

        if (!string.IsNullOrEmpty(error))
        {
            var targetErr = $"{appBase}/share/connected?provider=bluesky&ok=0&err={Uri.EscapeDataString(error)}&desc={Uri.EscapeDataString(errorDescription ?? "")}";
            return Redirect(targetErr);
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            var targetBad = $"{appBase}/share/connected?provider=bluesky&ok=0&err=missing_code_or_state";
            return Redirect(targetBad);
        }

        var ok = await GetProvider("bluesky").ConnectCallbackAsync(state, code);
        var targetOk = $"{appBase}/share/connected?provider=bluesky&ok={(ok ? 1 : 0)}";
        return Redirect(targetOk);
    }

    // -------- legacy Bluesky (app password)
    public sealed class BlueskyConnectDto
    {
        [Required] public string Identifier { get; set; } = default!;
        [Required] public string AppPassword { get; set; } = default!;
    }

    [HttpPost("bluesky/connect/save")]
    public async Task<IActionResult> BlueskyConnectSave([FromBody] BlueskyConnectDto dto, CancellationToken ct)
    {
        var prov = _providers.OfType<ISecretConnectShareProvider>().FirstOrDefault(p => p.Key.Equals("bluesky", StringComparison.OrdinalIgnoreCase));
        if (prov is null) return BadRequest(new { ok = false, error = "Bluesky provider not registered." });
        try
        {
            var ok = await prov.ConnectWithSecretAsync(Uid(), dto.Identifier, dto.AppPassword, ct);
            return Ok(new { ok });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { ok = false, error = ex.Message }); }
    }

    [HttpPost("bluesky/post")]
    public async Task<IActionResult> BlueskyPost([FromBody] PostDto dto, CancellationToken ct)
    {
        var result = await GetProvider("bluesky").PostAsync(Uid(), dto.ShareId, dto.Text, ct);
        return Ok(result);
    }

    [HttpDelete("{provider}/connection")]
    public async Task<IActionResult> Disconnect(string provider, CancellationToken ct)
    {
        _ = GetProvider(provider); // validate key exists
        var deleted = await _accounts.DeleteForUserAsync(Uid(), provider, ct);
        return deleted ? NoContent() : NoContent(); // idempotent
    }

}
