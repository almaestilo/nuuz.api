using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuuz.Application.DTOs;
using Nuuz.Application.Services;
using Nuuz.Application.Abstraction;
using System.Security.Claims;
using System.ComponentModel.DataAnnotations;

namespace Nuuz.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/feed")]
public class FeedController : ControllerBase
{
    private readonly IFeedService _feed;
    private readonly IArticleRepository _articles;           // NEW
    private readonly IMoodFeedbackService _moodFeedback;     // NEW

    public FeedController(
        IFeedService feed,
        IArticleRepository articles,                         // NEW
        IMoodFeedbackService moodFeedback                    // NEW
    )
    {
        _feed = feed;
        _articles = articles;
        _moodFeedback = moodFeedback;
    }

    private string Uid() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new UnauthorizedAccessException();

    // GET /api/feed?limit=&cursor=&mood=&blend=&override=
    [HttpGet]
    public async Task<ActionResult<FeedPageDto>> Get(
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
        [FromQuery] string? mood = null,
        [FromQuery] double? blend = null,
        [FromQuery(Name = "override")] bool overrideMood = false)
    {
        var page = await _feed.GetFeedAsync(Uid(), Math.Clamp(limit, 5, 50), cursor, mood, blend, overrideMood);
        return Ok(page);
    }

    // POST /api/feed/{articleId}/save
    [HttpPost("{articleId}/save")]
    [HttpPost("save/{articleId}")]
    public async Task<IActionResult> Save(string articleId)
    {
        await _feed.SaveAsync(Uid(), articleId);
        return NoContent();
    }

    // DELETE /api/feed/{articleId}/save
    [HttpDelete("{articleId}/save")]
    [HttpDelete("save/{articleId}")]
    public async Task<IActionResult> Unsave(string articleId)
    {
        await _feed.UnsaveAsync(Uid(), articleId);
        return NoContent();
    }

    // GET /api/feed/saved?limit=&cursor=&mood=&blend=&override=
    [HttpGet("saved")]
    public async Task<ActionResult<FeedPageDto>> Saved(
        [FromQuery] int limit = 20,
        [FromQuery] string? cursor = null,
        [FromQuery] string? mood = null,
        [FromQuery] double? blend = null,
        [FromQuery(Name = "override")] bool overrideMood = false)
    {
        var page = await _feed.GetSavedAsync(Uid(), Math.Clamp(limit, 5, 50), cursor, mood, blend, overrideMood);
        return Ok(page);
    }

    public class SaveMetaDto
    {
        public List<string>? Collections { get; set; }
        public string? Note { get; set; }
    }

    [HttpPatch("save/{articleId}/meta")]
    [HttpPatch("{articleId}/save/meta")]
    public async Task<IActionResult> UpdateMeta(string articleId, [FromBody] SaveMetaDto dto)
    {
        await _feed.UpdateSaveMetaAsync(Uid(), articleId, dto.Collections, dto.Note);
        return NoContent();
    }

    // ───────────────────────────────────────────────────────────────
    // NEW: Mood feedback endpoint (absolute route, not under /api/feed)
    // POST /api/feedback/mood
    // ───────────────────────────────────────────────────────────────
    public sealed class MoodFeedbackDto
    {
        [Required] public string ArticleId { get; set; } = default!;
        [Required] public string Mood { get; set; } = default!;   // Calm, Focused, Curious...
        [Range(-1, 1)] public int Signal { get; set; }            // +1 more like this, -1 less like this
        public DateTimeOffset? Ts { get; set; }                   // optional client timestamp
    }

    [HttpPost("/api/feedback/mood")]
    public async Task<IActionResult> PostMood([FromBody] MoodFeedbackDto dto)
    {
        var userId = Uid();

        var article = await _articles.GetAsync(dto.ArticleId);
        if (article is null) return NotFound("Article not found.");

        var features = IMoodFeedbackService.ExtractFeaturesFromArticle(article);

        var signal = new IMoodFeedbackService.MoodSignal
        {
            UserId = userId,
            Mood = dto.Mood,
            Signal = Math.Sign(dto.Signal) == 0 ? 1 : Math.Sign(dto.Signal),
            Timestamp = dto.Ts ?? DateTimeOffset.UtcNow,
            Features = features
        };

        await _moodFeedback.RecordAsync(signal);
        return NoContent();
    }

    [HttpPost("mood")]
    public async Task<IActionResult> Record([FromBody] RecordMoodFeedbackDto dto, CancellationToken ct)
    {
        var uid = Uid();
        await _moodFeedback.RecordFeedbackAsync(uid, dto, ct);
        return Ok(new { ok = true });
    }
}
