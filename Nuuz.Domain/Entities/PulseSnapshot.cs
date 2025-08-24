using Google.Cloud.Firestore;
using System.Collections.Generic;

namespace Nuuz.Domain.Entities
{
    [FirestoreData]
    public sealed class PulseSnapshotHour
    {
        [FirestoreDocumentId] public string Id { get; set; } = null!; // "HH"
        [FirestoreProperty("updatedAt")] public Timestamp UpdatedAt { get; set; }
        [FirestoreProperty("items")] public List<PulseItem> Items { get; set; } = new();
    }

    [FirestoreData]
    public sealed class PulseItem
    {
        [FirestoreProperty("articleId")] public string ArticleId { get; set; } = "";
        [FirestoreProperty("scoreGlobal")] public double ScoreGlobal { get; set; }  // raw score
        [FirestoreProperty("heat")] public double Heat { get; set; }                // 0..1 normalized
        [FirestoreProperty("trend")] public string Trend { get; set; } = "STEADY";  // NEW|UP|DOWN|STEADY|FADE
        [FirestoreProperty("reasons")] public List<string> Reasons { get; set; } = new();
        [FirestoreProperty("clusterId")] public string? ClusterId { get; set; }     // dedupe/cluster key
        [FirestoreProperty("title")] public string Title { get; set; } = "";
        [FirestoreProperty("sourceId")] public string? SourceId { get; set; }
        [FirestoreProperty("publishedAt")] public Timestamp PublishedAt { get; set; }
        [FirestoreProperty("summary")] public string? Summary { get; set; }
        [FirestoreProperty("imageUrl")] public string? ImageUrl { get; set; }
        [FirestoreProperty("topics")] public List<string> Topics { get; set; } = new();
    }
}
