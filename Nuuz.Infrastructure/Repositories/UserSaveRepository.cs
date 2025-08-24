using Google.Cloud.Firestore;
using Nuuz.Application.Abstraction;
using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Repositories;
public class UserSaveRepository : BaseRepository<UserSave>, IUserSaveRepository
{
    public UserSaveRepository(FirestoreDb db) : base(db, "UserSaves") { }

    public async Task<UserSave?> GetByUserAndArticleAsync(string userId, string articleId)
    {
        var q = _firestoreDb.Collection(_collectionName)
            .WhereEqualTo("userId", userId)
            .WhereEqualTo("articleId", articleId)
            .Limit(1);

        var list = await QueryRecordsAsync(q);
        return list.SingleOrDefault();
    }

    public async Task<List<UserSave>> GetByUserAsync(string userId, int limit, string? afterId = null)
    {
        var q = _firestoreDb.Collection(_collectionName)
            .WhereEqualTo("userId", userId)
            .OrderByDescending("savedAt")
            .Limit(limit);

        // optional cursor by doc ID if you store it; simple starting point skips
        return await QueryRecordsAsync(q);
    }

    // Upsert using Firestore merge
    public async Task AddOrUpdateAsync(string id, UserSave entity)
    {
        var docRef = _firestoreDb.Collection(_collectionName).Document(id);
        await docRef.SetAsync(entity, SetOptions.MergeAll);
    }
}
