using Google.Cloud.Firestore;
using System;

namespace Nuuz.Domain.Entities
{
    [FirestoreData]
    public sealed class ArticleFeedbackEvent
    {
        [FirestoreDocumentId] public string Id { get; set; } = default!;
        [FirestoreProperty] public string UserId { get; set; } = default!;   // firebaseUid
        [FirestoreProperty] public string ArticleId { get; set; } = default!;
        [FirestoreProperty] public string Mood { get; set; } = default!;
        [FirestoreProperty] public string Action { get; set; } = default!;   // see enum below
        [FirestoreProperty] public Timestamp CreatedAt { get; set; } = Timestamp.FromDateTime(DateTime.UtcNow);
    }
}
