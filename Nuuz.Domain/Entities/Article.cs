using Google.Cloud.Firestore;

namespace Nuuz.Domain.Entities;

[FirestoreData]
public sealed class Article
{
    [FirestoreDocumentId] public string Id { get; set; } = default!;
    [FirestoreProperty] public string Url { get; set; } = default!;
    [FirestoreProperty] public string? SourceId { get; set; }
    [FirestoreProperty] public string? Title { get; set; }
    [FirestoreProperty] public string? Author { get; set; }
    [FirestoreProperty] public Timestamp PublishedAt { get; set; }
    [FirestoreProperty] public string? ImageUrl { get; set; }
    [FirestoreProperty] public string? Summary { get; set; }

    // Existing vibe/tags/signals
    [FirestoreProperty] public string? Vibe { get; set; }
    [FirestoreProperty] public List<string>? Tags { get; set; }
    [FirestoreProperty] public double? Sentiment { get; set; }
    [FirestoreProperty] public double? SentimentVar { get; set; }
    [FirestoreProperty] public double? Arousal { get; set; }
    [FirestoreProperty] public List<double>? TopicEmbedding { get; set; }
    [FirestoreProperty] public List<string>? Topics { get; set; }
    [FirestoreProperty] public List<string>? InterestMatches { get; set; }
    [FirestoreProperty] public Timestamp CreatedAt { get; set; }

    // Reader-ready content
    [FirestoreProperty] public string? SparkNotesHtml { get; set; }
    [FirestoreProperty] public string? SparkNotesText { get; set; }

    // ---------- NEW: fine-grained mood features ----------
    // Numeric (0..1 unless noted)
    [FirestoreProperty] public double? Depth { get; set; }               // comprehensiveness
    [FirestoreProperty] public int? ReadMinutes { get; set; }          // 1..60
    [FirestoreProperty] public double? Conflict { get; set; }
    [FirestoreProperty] public double? Practicality { get; set; }         // tips/steps/templates
    [FirestoreProperty] public double? Optimism { get; set; }
    [FirestoreProperty] public double? Novelty { get; set; }              // new finding/launch vs recap
    [FirestoreProperty] public double? HumanInterest { get; set; }
    [FirestoreProperty] public double? Hype { get; set; }                 // wins/launch/records
    [FirestoreProperty] public double? Explainer { get; set; }
    [FirestoreProperty] public double? Analysis { get; set; }
    [FirestoreProperty] public double? Wholesome { get; set; }

    // Discrete labels
    [FirestoreProperty] public string? Genre { get; set; }                // Explainer|Analysis|Report|Profile|HowTo|Q&A|List|Recap
    [FirestoreProperty] public string? EventStage { get; set; }           // Breaking|Update|Launch|Aftermath|Feature
    [FirestoreProperty] public string? Format { get; set; }               // Short|Standard|Longform|Visual
}
