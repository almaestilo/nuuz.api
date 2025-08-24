using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Application.DTOs;
public class ArticleSummaryDto
{
    public NewsArticleDto Article { get; set; } = null!;
    public string SummaryText { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
