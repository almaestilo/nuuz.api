using Google.Cloud.Firestore;
using Nuuz.Application.Abstraction;
using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Repositories;
public class ArticleRepository : BaseRepository<Article>, IArticleRepository
{
    public ArticleRepository(FirestoreDb db) : base(db, "Articles") { }

    public Task<List<Article>> QueryAsync(Query query) => QueryRecordsAsync(query);


    public async Task SetSparkNotesAsync(string id, string html, string text, CancellationToken ct = default)
    {
        var docRef = _firestoreDb.Collection(_collectionName).Document(id);
        var updates = new Dictionary<string, object?>
        {
            ["SparkNotesHtml"] = html,
            ["SparkNotesText"] = text
        };
        await docRef.UpdateAsync(updates, cancellationToken: ct);
    }
}
