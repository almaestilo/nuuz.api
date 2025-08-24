using Google.Cloud.Firestore;

namespace Nuuz.Domain.Entities;

[FirestoreData]
public sealed class ConnectedAccount
{
    [FirestoreDocumentId] public string Id { get; set; } = default!;
    [FirestoreProperty("userId")] public string UserId { get; set; } = default!;
    [FirestoreProperty("provider")] public string Provider { get; set; } = "twitter"; // v1 only
    [FirestoreProperty("handle")] public string? Handle { get; set; }
    [FirestoreProperty("accessToken")] public string AccessToken { get; set; } = default!;
    [FirestoreProperty("refreshToken")] public string? RefreshToken { get; set; }
    [FirestoreProperty("expiresAt")] public Timestamp? ExpiresAt { get; set; }
    [FirestoreProperty("scopes")] public List<string> Scopes { get; set; } = new();
    [FirestoreProperty("createdAt")] public Timestamp CreatedAt { get; set; } = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
    [FirestoreProperty("updatedAt")] public Timestamp UpdatedAt { get; set; } = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
    [FirestoreProperty] public string? DpopPrivateJwk { get; set; }  // JSON string with kty, crv, x, y, d
    [FirestoreProperty] public string? Did { get; set; }

    // NEW: The user's Personal Data Server base URL (no /xrpc suffix), e.g. "https://bsky.social"
    // We use this to send XRPC calls to the correct host. Required for OAuth tokens to be valid.
    [FirestoreProperty("pdsBase")]
    public string? PdsBase { get; set; }

}
