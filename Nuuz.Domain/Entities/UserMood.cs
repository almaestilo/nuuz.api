using Google.Cloud.Firestore;

namespace Nuuz.Domain.Entities;

[FirestoreData]
public sealed class UserMood
{
    // We’ll use firebaseUid as the document ID for fast lookups.
    [FirestoreDocumentId] public string Id { get; set; } = null!;

    // "Calm","Focused","Curious","Hyped","Meh","Stressed","Sad"
    [FirestoreProperty("mood")] public string Mood { get; set; } = "Curious";

    // 0..1  (0 = Comfort, 1 = Challenge)
    [FirestoreProperty("blend")] public double Blend { get; set; } = 0.3;

    [FirestoreProperty("setAt")]
    public Timestamp SetAt { get; set; } =
        Timestamp.FromDateTime(DateTime.UtcNow.ToUniversalTime());

    // For future analytics/UX (e.g., quickdock | onboarding)
    [FirestoreProperty("source")] public string? Source { get; set; }
}
