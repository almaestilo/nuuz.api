using Google.Cloud.Firestore;
using Nuuz.Application.Abstraction;
using Nuuz.Domain.Entities;

namespace Nuuz.Infrastructure.Repositories;

public interface IShareLinkRepository : IBaseRepository<ShareLink>
{
    Task IncrementClicksAsync(string id);
}
public sealed class ShareLinkRepository : BaseRepository<ShareLink>, IShareLinkRepository
{
    public ShareLinkRepository(FirestoreDb db) : base(db, "ShareLinks") { }

    public async Task IncrementClicksAsync(string id)
    {
        var doc = _firestoreDb.Collection(_collectionName).Document(id);
        await doc.UpdateAsync(new Dictionary<string, object>
        {
            ["clicks"] = FieldValue.Increment(1),
            ["updatedAt"] = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        });
    }
}
