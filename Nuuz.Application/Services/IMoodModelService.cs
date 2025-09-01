using Nuuz.Domain.Entities;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Application.Services
{
    public interface IMoodModelService
    {
        Task<MoodModel?> GetModelAsync(string userId, string mood, CancellationToken ct = default);
        Task TrainAsync(string userId, string mood, CancellationToken ct = default);
        Task TrainGlobalAsync(string mood, CancellationToken ct = default);
        Task RecordEvaluationAsync(string userId, string mood, string articleId, double heuristicScore, double? modelScore, CancellationToken ct = default);
    }

    public sealed class MoodModel
    {
        public double[] Weights { get; init; } = Array.Empty<double>();
        public double Bias { get; init; }

        public double Predict(Article article)
        {
            var f = ExtractFeatures(article);
            double z = Bias;
            for (int i = 0; i < f.Length && i < Weights.Length; i++)
                z += Weights[i] * f[i];
            return 1.0 / (1.0 + Math.Exp(-z));
        }

        public static double[] ExtractFeatures(Article a)
        {
            return new double[]
            {
                a.Arousal ?? 0.5,
                a.Sentiment ?? 0.0,
                a.Depth ?? 0.35,
                a.Conflict ?? 0.25,
                a.Practicality ?? 0.25,
                a.Optimism ?? 0.3,
                a.Novelty ?? 0.3,
                a.HumanInterest ?? 0.25,
                a.Hype ?? 0.25,
                a.Explainer ?? 0.25,
                a.Analysis ?? 0.25,
                a.Wholesome ?? 0.25,
                a.ReadMinutes is > 0 and < 120 ? a.ReadMinutes.Value / 30.0 : 5.0 / 30.0
            };
        }
    }
}
