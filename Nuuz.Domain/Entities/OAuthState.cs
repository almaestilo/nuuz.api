using Google.Cloud.Firestore;

namespace Nuuz.Domain.Entities;

[FirestoreData]
public sealed class OAuthState
{
    [FirestoreDocumentId] public string Id { get; set; } = default!; // equals State
    [FirestoreProperty("userId")] public string UserId { get; set; } = default!;
    [FirestoreProperty("provider")] public string Provider { get; set; } = "twitter";
    [FirestoreProperty("codeVerifier")] public string CodeVerifier { get; set; } = default!;
    [FirestoreProperty("redirectTo")] public string? RedirectTo { get; set; }
    [FirestoreProperty("createdAt")] public Timestamp CreatedAt { get; set; } = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
    [FirestoreProperty] public string? DpopPrivateJwk { get; set; }

}
