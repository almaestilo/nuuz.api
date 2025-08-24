using Google.Cloud.Firestore;
using Nuuz.Application.Abstraction;
using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Repositories;
/// <inheritdoc/>
public class NewsArticleRepository : BaseRepository<NewsArticle>, INewsArticleRepository
{
    public NewsArticleRepository(FirestoreDb firestoreDb)
        : base(firestoreDb, "NewsArticles")
    {
    }
}
