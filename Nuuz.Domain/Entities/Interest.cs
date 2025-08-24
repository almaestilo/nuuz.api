using Google.Cloud.Firestore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Domain.Entities;
/// <summary>
/// A topic or category that users can subscribe to.
/// </summary>
[FirestoreData]
public class Interest
{
    [FirestoreDocumentId]           // preset doc ID in Firestore
    public string Id { get; set; } = null!;

    [FirestoreProperty("name")]
    public string Name { get; set; } = null!;

    [FirestoreProperty("slug")]
    public string? Slug { get; set; }

    [FirestoreProperty("emoji")]
    public string? Emoji { get; set; }

    [FirestoreProperty("isSystem")]
    public bool IsSystem { get; set; } = true;
}
