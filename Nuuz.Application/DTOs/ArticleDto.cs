using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Application.DTOs;
public class ArticleDto
{
    public string Id { get; set; } = null!;
    public string Url { get; set; } = null!;
    public string SourceId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Author { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public string? ImageUrl { get; set; }
    public string Summary { get; set; } = "";   // AI snippet
    public string Vibe { get; set; } = "Neutral";
    public IEnumerable<string> Tags { get; set; } = Array.Empty<string>();
    public bool Saved { get; set; }            // computed per user
    public string Why { get; set; } = "";      // “Because you follow …”
    public List<string> Collections { get; set; } = new();
    public string? Note { get; set; }

}
