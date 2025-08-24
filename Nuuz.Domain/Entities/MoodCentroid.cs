using Google.Cloud.Firestore;
using System;
using System.Collections.Generic;

namespace Nuuz.Domain.Entities
{
    [FirestoreData]
    public sealed class MoodCentroid
    {
        // Doc ID = Mood (Calm, Focused, ...)
        [FirestoreDocumentId] public string Id { get; set; } = default!;
        [FirestoreProperty] public string Mood { get; set; } = default!;
        [FirestoreProperty] public List<double> Vec { get; set; } = new(); // L2-normalized
        [FirestoreProperty] public int Count { get; set; } = 0;
        [FirestoreProperty] public Timestamp UpdatedAt { get; set; } = Timestamp.FromDateTime(DateTime.UtcNow);
    }
}
