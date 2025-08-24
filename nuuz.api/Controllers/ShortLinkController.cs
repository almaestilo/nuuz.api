using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuuz.Infrastructure.Repositories;

namespace Nuuz.Api.Controllers;

[ApiController]
[AllowAnonymous]
public sealed class ShortLinkController : ControllerBase
{
    private readonly IShareLinkRepository _links;

    public ShortLinkController(IShareLinkRepository links) { _links = links; }

    // GET /s/{id}
    [HttpGet("/s/{id}")]
    public async Task<IActionResult> Go([FromRoute] string id)
    {
        var link = await _links.GetAsync(id);
        if (link is null) return NotFound();

        await _links.IncrementClicksAsync(id);
        return Redirect(link.TargetUrl);
    }
}
