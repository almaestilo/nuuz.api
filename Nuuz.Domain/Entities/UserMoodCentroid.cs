using Google.Cloud.Firestore;
using System;
using System.Collections.Generic;

namespace Nuuz.Domain.Entities
{
    [FirestoreData]
    public sealed class UserMoodCentroid
    {
        // Doc ID convention: $"{UserId}_{Mood}"
        [FirestoreDocumentId] public string Id { get; set; } = default!;
        [FirestoreProperty] public string UserId { get; set; } = default!; // firebaseUid
        [FirestoreProperty] public string Mood { get; set; } = default!;   // Calm|Focused|...
        [FirestoreProperty] public List<double> Vec { get; set; } = new(); // L2-normalized
        [FirestoreProperty] public int Count { get; set; } = 0;
        [FirestoreProperty] public Timestamp UpdatedAt { get; set; } = Timestamp.FromDateTime(DateTime.UtcNow);
    }
}
