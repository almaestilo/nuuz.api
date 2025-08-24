using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Application.Services
{
    public interface IAISummarizer
    {
        // existing
        Task<(string summary, string vibe, string[] tags)>
            SummarizeAsync(string title, string text, CancellationToken ct = default);

        // existing
        Task<(string summary, string vibe, string[] tags, double? sentiment, double? sentimentVar, double? arousal)>
            SummarizeRichAsync(string title, string text, CancellationToken ct = default);

        // NEW: richer structured signals for vibe-scoring
        Task<RichSignals> SummarizeSignalsAsync(string title, string text, CancellationToken ct = default);
    }

    public sealed record RichSignals(
        string Summary,
        string Vibe,
        string[] Tags,
        double? Sentiment,
        double? SentimentVar,
        double? Arousal,
        Signals Features);

    public sealed record Signals(
        double Depth,
        int ReadMinutes,
        double Conflict,
        double Practicality,
        double Optimism,
        double Novelty,
        double HumanInterest,
        double Hype,
        double Explainer,
        double Analysis,
        double Wholesome,
        string Genre,
        string EventStage,
        string Format);
}
