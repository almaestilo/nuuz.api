using Google.Cloud.Firestore;
using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Application.Abstraction;
public interface IArticleRepository : IBaseRepository<Article>
{
    //Task<Article?> GetAsync(string id);
//Task AddAsync(Article a);
    Task<List<Article>> QueryAsync(Google.Cloud.Firestore.Query query);
    Task SetSparkNotesAsync(string id, string html, string text, CancellationToken ct = default);
}
