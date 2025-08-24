using Google.Cloud.Firestore;
using Nuuz.Application.Abstraction;
using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Repositories;
public class InterestRepository : BaseRepository<Interest>, IInterestRepository
{
    public InterestRepository(FirestoreDb firestoreDb)
        : base(firestoreDb, "Interests")
    {


    }

    public async Task<List<Interest>> GetAllOrderedAsync()
    {
        var query = _firestoreDb.Collection(_collectionName).OrderBy("name");
        return await QueryRecordsAsync(query);
    }

    public async Task<Interest?> GetByNameAsync(string name)
    {
        var query = _firestoreDb.Collection(_collectionName)
                                .WhereEqualTo("name", name);
        var results = await QueryRecordsAsync(query);
        return results.SingleOrDefault();
    }
}
