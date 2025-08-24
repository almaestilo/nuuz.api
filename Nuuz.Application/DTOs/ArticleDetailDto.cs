namespace Nuuz.Application.DTOs;

public class ArticleDetailDto
{
    public string Id { get; set; } = default!;
    public string Url { get; set; } = default!;
    public string SourceId { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string? Author { get; set; }
    public DateTime PublishedAt { get; set; }
    public string? ImageUrl { get; set; }
    public string? Summary { get; set; }
    public string? Vibe { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool Saved { get; set; }

    public string? ContentHtml { get; set; }  // sanitized!
    public string? ContentText { get; set; }  // plain text for TTS/read-time
}
