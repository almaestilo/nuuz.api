using Google.Cloud.Firestore;

namespace Nuuz.Domain.Entities;

[FirestoreData]
public sealed class ShareEvent
{
    [FirestoreDocumentId] public string Id { get; set; } = default!;
    [FirestoreProperty("shareId")] public string ShareId { get; set; } = default!;
    [FirestoreProperty("userId")] public string UserId { get; set; } = default!;
    [FirestoreProperty("provider")] public string Provider { get; set; } = "twitter";
    [FirestoreProperty("mode")] public string Mode { get; set; } = "api";       // "api" | "intent"
    [FirestoreProperty("status")] public string Status { get; set; } = "posted"; // "posted" | "error" | "cancelled"
    [FirestoreProperty("providerPostId")] public string? ProviderPostId { get; set; }
    [FirestoreProperty("providerPermalink")] public string? ProviderPermalink { get; set; }
    [FirestoreProperty("error")] public string? Error { get; set; }
    [FirestoreProperty("createdAt")] public Timestamp CreatedAt { get; set; } = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
}
