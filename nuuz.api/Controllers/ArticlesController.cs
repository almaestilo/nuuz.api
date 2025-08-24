using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nuuz.Application.Abstraction;
using Nuuz.Application.DTOs;
using Nuuz.Application.Services;
using Nuuz.Domain.Entities;
using System.Security.Claims;

namespace Nuuz.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/articles")]
public class ArticlesController : ControllerBase
{
    private readonly IArticleRepository _articles;
    private readonly ISparkNotesService _spark; // fallback if not yet generated
    private readonly IUserSaveRepository _saves;
    public ArticlesController(IArticleRepository articles, ISparkNotesService spark, IUserSaveRepository saves)
    {
        _articles = articles;
        _spark = spark;
        _saves = saves;
    }

    private string Uid() =>
     User.FindFirst(ClaimTypes.NameIdentifier)?.Value
     ?? throw new UnauthorizedAccessException();

    [HttpGet("{id}")]
    public async Task<ActionResult<ArticleDetailDto>> Get(string id, CancellationToken ct)
    {
        var a = await _articles.GetAsync(id);
        if (a is null) return NotFound();

        string? html = a.SparkNotesHtml;
        string? text = a.SparkNotesText;
        var userId = Uid();
        var save = await _saves.GetByUserAndArticleAsync(userId, id);
        // Fallback: if ingestion missed it, generate once here and cache
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(text))
        {
            try
            {
                var seed = a.Summary ?? a.Title ?? "";
                var notes = await _spark.BuildAsync(a.Url, a.Title ?? "Untitled", seed, ct);
                html = notes.Html;
                text = notes.PlainText;

                await _articles.SetSparkNotesAsync(a.Id, html, text, ct);
            }
            catch
            {
                html ??= $@"<h2>Nuuz SparkNotes</h2>
<p class=""kicker"">{System.Net.WebUtility.HtmlEncode(a.Title ?? "Untitled")}</p>
<p>We couldn't generate a brief yet. Tap “Read original”.</p>
<p class=""cta""><a href=""{a.Url}"" target=""_blank"" rel=""noopener nofollow"">Read the original</a></p>";
                text ??= a.Title ?? "";
            }
        }

        var dto = new ArticleDetailDto
        {
            Id = a.Id,
            Url = a.Url,
            SourceId = a.SourceId ?? "Source",
            Title = a.Title ?? "Untitled",
            Author = a.Author,
            PublishedAt = a.PublishedAt.ToDateTime(),
            ImageUrl = a.ImageUrl,
            Summary = a.Summary,
            Vibe = a.Vibe,
            Tags = a.Tags ?? new(),
            Saved = (save != null),
            ContentHtml = html,
            ContentText = text
        };

        return Ok(dto);
    }
}
