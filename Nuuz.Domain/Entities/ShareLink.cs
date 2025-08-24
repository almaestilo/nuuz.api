using Google.Cloud.Firestore;

namespace Nuuz.Domain.Entities;

[FirestoreData]
public sealed class ShareLink
{
    [FirestoreDocumentId] public string Id { get; set; } = default!;
    [FirestoreProperty("userId")] public string UserId { get; set; } = default!;
    [FirestoreProperty("articleId")] public string ArticleId { get; set; } = default!;
    [FirestoreProperty("provider")] public string Provider { get; set; } = "twitter";
    [FirestoreProperty("shortPath")] public string ShortPath { get; set; } = default!; // equals Id for simplicity
    [FirestoreProperty("targetUrl")] public string TargetUrl { get; set; } = default!;
    [FirestoreProperty("clicks")] public long Clicks { get; set; }
    [FirestoreProperty("createdAt")] public Timestamp CreatedAt { get; set; } = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
    [FirestoreProperty("updatedAt")] public Timestamp UpdatedAt { get; set; } = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
}
