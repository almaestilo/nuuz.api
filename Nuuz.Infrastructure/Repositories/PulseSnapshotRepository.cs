using Google.Cloud.Firestore;
using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Repositories
{
    /// <summary>
    /// Pulse snapshots live under:
    ///   PulseSnapshots/{YYYY-MM-DD}/Hours/{HH}
    /// This repo follows the BaseRepository pattern by inheriting from it,
    /// and adds subcollection helpers for the hourly docs.
    /// </summary>
    public interface IPulseSnapshotRepository
    {
        Task<PulseSnapshotHour?> GetAsync(string dateYmd, int hour);
        Task SetAsync(string dateYmd, int hour, PulseSnapshotHour data);
        Task<List<(int Hour, PulseSnapshotHour Doc)>> ListHoursAsync(string dateYmd, int maxHours = 24);
        Task<bool> ExistsAsync(string dateYmd, int hour);
        Task DeleteDayAsync(string dateYmd);
    }

    /// <inheritdoc/>
    public sealed class PulseSnapshotRepository
        : BaseRepository<PulseSnapshotHour>, IPulseSnapshotRepository
    {
        public PulseSnapshotRepository(FirestoreDb firestoreDb)
            : base(firestoreDb, "PulseSnapshots") { }

        private DocumentReference DayDoc(string ymd)
            => _firestoreDb.Collection(_collectionName).Document(ymd);

        private CollectionReference Hours(string ymd)
            => DayDoc(ymd).Collection("Hours");

        public async Task<PulseSnapshotHour?> GetAsync(string dateYmd, int hour)
        {
            var id = hour.ToString("D2");
            var snap = await Hours(dateYmd).Document(id).GetSnapshotAsync();
            return snap.Exists ? snap.ConvertTo<PulseSnapshotHour>() : null;
        }

        public async Task SetAsync(string dateYmd, int hour, PulseSnapshotHour data)
        {
            data.Id = hour.ToString("D2"); // keep Hour doc id
            await Hours(dateYmd).Document(data.Id).SetAsync(data, SetOptions.Overwrite);
        }

        public async Task<List<(int Hour, PulseSnapshotHour Doc)>> ListHoursAsync(string dateYmd, int maxHours = 24)
        {
            var snap = await Hours(dateYmd)
                .OrderBy(FieldPath.DocumentId) // "00".."23"
                .Limit(maxHours)
                .GetSnapshotAsync();

            return snap.Documents
                       .Select(d => (int.Parse(d.Id), d.ConvertTo<PulseSnapshotHour>()))
                       .ToList();
        }

        public async Task<bool> ExistsAsync(string dateYmd, int hour)
        {
            var snap = await Hours(dateYmd).Document(hour.ToString("D2")).GetSnapshotAsync();
            return snap.Exists;
        }

        public async Task DeleteDayAsync(string dateYmd)
        {
            var hours = Hours(dateYmd);
            var snap = await hours.GetSnapshotAsync();
            var batch = _firestoreDb.StartBatch();
            foreach (var d in snap.Documents)
                batch.Delete(d.Reference);
            await batch.CommitAsync();
        }
    }
}
