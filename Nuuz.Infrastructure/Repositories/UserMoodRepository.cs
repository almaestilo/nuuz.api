using System.Threading.Tasks;
using Google.Cloud.Firestore;
using Nuuz.Application.Abstraction;
using Nuuz.Domain.Entities;

namespace Nuuz.Infrastructure.Repositories
{
    public sealed class UserMoodRepository : BaseRepository<UserMood>, IUserMoodRepository
    {
        public UserMoodRepository(FirestoreDb db) : base(db, "UserMoods") { }

        // We treat firebaseUid as the document ID (mood.Id)
        public async Task<UserMood> UpsertAsync(UserMood mood)
        {
            if (string.IsNullOrWhiteSpace(mood.Id))
                throw new ArgumentException("UserMood.Id (firebaseUid) is required.", nameof(mood));

            // If it already exists, Update (MergeAll via BaseRepository); else Add
            var existing = await GetAsync(mood.Id);
            if (existing is null)
            {
                await AddAsync(mood);
                return mood;
            }

            // Preserve the same doc id
            await UpdateAsync(mood);
            return mood;
        }
    }
}
