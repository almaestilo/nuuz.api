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
public class UserRepository : BaseRepository<User>, IUserRepository
{
    public UserRepository(FirestoreDb firestoreDb)
        : base(firestoreDb, "Users")
    {
    }

    public async Task<User?> GetByFirebaseUidAsync(string firebaseUid)
    {
        // Query the Firestore “Users” collection for the document
        // where the “FirebaseUid” field equals the given UID
        var query = _firestoreDb
                        .Collection(_collectionName)
                        .WhereEqualTo("firebaseUid", firebaseUid);

        var results = await QueryRecordsAsync(query);
        return results.SingleOrDefault();
    }
}
