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
public class ArticleSummaryRepository : BaseRepository<ArticleSummary>, IArticleSummaryRepository
{
    public ArticleSummaryRepository(FirestoreDb firestoreDb)
        : base(firestoreDb, "ArticleSummaries")
    {
    }
}
