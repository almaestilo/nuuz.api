using Google.Cloud.Firestore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Domain.Entities;
[FirestoreData]
public class UserSave
{
    [FirestoreDocumentId] public string Id { get; set; } = null!;
    [FirestoreProperty("userId")] public string UserId { get; set; } = null!;   // firebase uid
    [FirestoreProperty("articleId")] public string ArticleId { get; set; } = null!;
    [FirestoreProperty("savedAt")] public Timestamp SavedAt { get; set; }
    [FirestoreProperty("collections")] public List<string> Collections { get; set; } = new();
    [FirestoreProperty("note")] public string? Note { get; set; }
}
