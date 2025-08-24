using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuuz.Application.DTOs;
using Nuuz.Application.Services;
using System.Security.Claims;

namespace Nuuz.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/mood")]
public sealed class MoodController : ControllerBase
{
    private readonly IMoodService _moods;

    public MoodController(IMoodService moods) => _moods = moods;

    private string Uid() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new UnauthorizedAccessException();

    // GET /api/mood  → returns current mood if set, else 204 NoContent
    [HttpGet]
    public async Task<ActionResult<MoodDto>> Get()
    {
        var dto = await _moods.GetAsync(Uid());
        if (dto is null) return NoContent();
        return Ok(dto);
    }

    // POST /api/mood  { mood, blend, source? }  → upsert
    [HttpPost]
    public async Task<ActionResult<MoodDto>> Set([FromBody] SetMoodRequest body)
    {
        var dto = await _moods.SetAsync(Uid(), body);
        return Ok(dto);
    }
}
