// Nuuz.Domain/Entities/Feedback.cs
using Google.Cloud.Firestore;
using System;

namespace Nuuz.Domain.Entities
{
    /// <summary>
    /// User-submitted feedback. Stored in Firestore.
    /// </summary>
    [FirestoreData]
    public class Feedback
    {
        // Firestore document id
        [FirestoreDocumentId]
        public string Id { get; set; } = null!;

        [FirestoreProperty("userFirebaseUid")]
        public string UserFirebaseUid { get; set; } = string.Empty;

        // Stored as strings for forward compatibility & easy querying
        [FirestoreProperty("category")]
        public string Category { get; set; } = "Other";

        [FirestoreProperty("severity")]
        public string Severity { get; set; } = "Normal";

        [FirestoreProperty("subject")]
        public string Subject { get; set; } = string.Empty;

        [FirestoreProperty("message")]
        public string Message { get; set; } = string.Empty;

        [FirestoreProperty("rating")]
        public int? Rating { get; set; }

        // Screenshot metadata + reference (URI). Do NOT store big base64 in the doc.
        [FirestoreProperty("screenshotMediaType")]
        public string? ScreenshotMediaType { get; set; }

        [FirestoreProperty("screenshotBytes")]
        public long? ScreenshotBytes { get; set; }

        /// <summary>
        /// Where the screenshot is stored (e.g., gs://bucket/path or https URL).
        /// </summary>
        [FirestoreProperty("screenshotUri")]
        public string? ScreenshotUri { get; set; }

        // Request context (useful for triage)
        [FirestoreProperty("userAgent")]
        public string? UserAgent { get; set; }

        [FirestoreProperty("clientIp")]
        public string? ClientIp { get; set; }

        [FirestoreProperty("appVersion")]
        public string? AppVersion { get; set; }

        [FirestoreProperty("device")]
        public string? Device { get; set; }

        // Timestamps
        [FirestoreProperty("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
