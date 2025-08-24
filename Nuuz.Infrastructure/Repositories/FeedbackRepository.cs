// Nuuz.Infrastructure.Repositories/FirestoreFeedbackRepository.cs
using Google.Cloud.Firestore;
using Nuuz.Application.Abstraction;
using Nuuz.Domain.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Repositories
{
    public class FirestoreFeedbackRepository : IFeedbackRepository
    {
        private readonly CollectionReference _col;

        public FirestoreFeedbackRepository(FirestoreDb db)
        {
            _col = db.Collection("feedback");
        }

        public async Task<Feedback> AddAsync(Feedback feedback)
        {
            // Id may already be set; if not, Firestore will assign one.
            if (string.IsNullOrWhiteSpace(feedback.Id))
            {
                var added = await _col.AddAsync(feedback);
                feedback.Id = added.Id;
                return feedback;
            }

            await _col.Document(feedback.Id).SetAsync(feedback, SetOptions.Overwrite);
            return feedback;
        }

        public async Task<Feedback?> GetAsync(string id)
        {
            var snap = await _col.Document(id).GetSnapshotAsync();
            if (!snap.Exists) return null;
            return snap.ConvertTo<Feedback>();
        }

        public async Task<IReadOnlyList<Feedback>> ListByUserAsync(string firebaseUid, int take = 50)
        {
            var q = _col
                .WhereEqualTo("userFirebaseUid", firebaseUid)
                .OrderByDescending("createdAtUtc")
                .Limit(take);

            var snapshot = await q.GetSnapshotAsync();
            return snapshot.Documents.Select(d => d.ConvertTo<Feedback>()).ToList();
        }
    }
}
