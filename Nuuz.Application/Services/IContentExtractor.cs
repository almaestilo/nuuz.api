// Nuuz.Application/Services/IContentExtractor.cs
namespace Nuuz.Application.Services;

public sealed class ExtractedContent
{
    public string Text { get; set; } = "";
    public string? LeadImageUrl { get; set; }
}

public interface IContentExtractor
{
    Task<ExtractedContent> ExtractAsync(string url, string? rssHtmlOrSummary, CancellationToken ct = default);
}
