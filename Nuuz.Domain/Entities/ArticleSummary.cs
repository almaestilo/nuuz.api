using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Domain.Entities;
/// <summary>
/// Stores a generated summary for a user-article pair.
/// </summary>
public class ArticleSummary
{
    public Guid Id { get; set; }
    public Guid NewsArticleId { get; set; }
    public Guid UserId { get; set; }
    public string SummaryText { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
