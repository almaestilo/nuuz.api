using Google.Cloud.Firestore;
using Nuuz.Application.Abstraction;
using Nuuz.Domain.Entities;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Repositories;

public interface IConnectedAccountRepository : IBaseRepository<ConnectedAccount>
{
    Task<ConnectedAccount?> GetForUserAsync(string userId, string provider = "twitter");

    /// <summary>
    /// Deletes the connected account document for a specific user+provider.
    /// Returns true if a document existed and was deleted.
    /// </summary>
    Task<bool> DeleteForUserAsync(string userId, string provider, CancellationToken ct = default);

    /// <summary>
    /// Deletes all connected accounts for a user (optional helper).
    /// Returns the number of deleted documents.
    /// </summary>
    Task<int> DeleteAllForUserAsync(string userId, CancellationToken ct = default);
}

public sealed class ConnectedAccountRepository
    : BaseRepository<ConnectedAccount>, IConnectedAccountRepository
{
    public ConnectedAccountRepository(FirestoreDb db) : base(db, "ConnectedAccounts") { }

    public async Task<ConnectedAccount?> GetForUserAsync(string userId, string provider = "twitter")
    {
        var q = _firestoreDb.Collection(_collectionName)
            .WhereEqualTo("userId", userId)
            .WhereEqualTo("provider", provider)
            .Limit(1);

        var snap = await q.GetSnapshotAsync();
        var doc = snap.Documents.FirstOrDefault();
        return doc?.ConvertTo<ConnectedAccount>();
    }

    public async Task<bool> DeleteForUserAsync(string userId, string provider, CancellationToken ct = default)
    {
        var q = _firestoreDb.Collection(_collectionName)
            .WhereEqualTo("userId", userId)
            .WhereEqualTo("provider", provider)
            .Limit(1);

        var snap = await q.GetSnapshotAsync(ct);
        var doc = snap.Documents.FirstOrDefault();
        if (doc is null) return false;

        await doc.Reference.DeleteAsync(cancellationToken: ct);
        return true;
    }

    public async Task<int> DeleteAllForUserAsync(string userId, CancellationToken ct = default)
    {
        var q = _firestoreDb.Collection(_collectionName)
            .WhereEqualTo("userId", userId);

        var snap = await q.GetSnapshotAsync(ct);
        if (snap.Count == 0) return 0;

        // Best-effort sequential deletes (safe for small N; batch if you expect many)
        var count = 0;
        foreach (var doc in snap.Documents)
        {
            await doc.Reference.DeleteAsync(cancellationToken: ct);
            count++;
        }
        return count;
    }
}
