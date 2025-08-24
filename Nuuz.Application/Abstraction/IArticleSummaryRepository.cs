using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Application.Abstraction;
/// <summary>
/// Repository for <see cref="ArticleSummary"/> entities.
/// </summary>
public interface IArticleSummaryRepository : IBaseRepository<ArticleSummary>
{
}
