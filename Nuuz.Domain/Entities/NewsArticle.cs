using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Domain.Entities;
/// <summary>
/// Represents a news article fetched from an external source.
/// </summary>
public class NewsArticle
{
    public Guid Id { get; set; }
    public string Title { get; set; } = null!;
    public string Url { get; set; } = null!;
    public string Source { get; set; } = null!;
    public DateTime PublishedAt { get; set; }
}
