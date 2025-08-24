// Nuuz.Domain/Entities/User.cs
using Google.Cloud.Firestore;
using System.Collections.Generic;


namespace Nuuz.Domain.Entities
{
    /// <summary>
    /// Represents a user of the Nuuz application.
    /// </summary>
    [FirestoreData]                       // Enable Firestore serialization
    public class User
    {
        [FirestoreDocumentId]            // Maps this property to the document ID
        public string Id { get; set; } = null!;

        [FirestoreProperty("email")]
        public string Email { get; set; } = null!;

        [FirestoreProperty("displayName")]
        public string DisplayName { get; set; } = null!;

        [FirestoreProperty("firebaseUid")]
        public string FirebaseUid { get; set; } = null!;

        [FirestoreProperty("interestIds")]
        public List<string> InterestIds { get; set; } = new();
        // NEW: user-only, free-form interests
        [FirestoreProperty("customInterests")]
        public List<CustomInterest> CustomInterests { get; set; } = new();
    }
}
