using Google.Cloud.Firestore;
using Microsoft.ML;
using Microsoft.ML.Data;
using Nuuz.Application.Abstraction;
using Nuuz.Application.Services;
using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Services
{
    /// <summary>
    /// Trains lightweight logistic models per (user,mood) from ArticleFeedback.
    /// Models are stored in Firestore collection 'MoodModels'.
    /// </summary>
    public sealed class MoodModelService : IMoodModelService
    {
        private readonly FirestoreDb _db;
        private readonly IArticleRepository _articles;
        private readonly MLContext _ml = new();

        public MoodModelService(FirestoreDb db, IArticleRepository articles)
        {
            _db = db;
            _articles = articles;
        }

        public async Task<MoodModel?> GetModelAsync(string userId, string mood, CancellationToken ct = default)
        {
            var docId = $"{userId}_{NormalizeMood(mood)}";
            var snap = await _db.Collection("MoodModels").Document(docId).GetSnapshotAsync(ct);
            if (!snap.Exists) return null;
            var weights = snap.TryGetValue<List<double>>("weights", out var w) ? w : null;
            var bias = snap.TryGetValue<double>("bias", out var b) ? b : double.NaN;
            if (weights is null || double.IsNaN(bias)) return null;
            return new MoodModel { Weights = weights.ToArray(), Bias = b };
        }

        public async Task TrainAsync(string userId, string mood, CancellationToken ct = default)
        {
            var normMood = NormalizeMood(mood);
            var q = _db.Collection("ArticleFeedback")
                       .WhereEqualTo("userId", userId)
                       .WhereEqualTo("mood", normMood)
                       .OrderByDescending("createdAt")
                       .Limit(500);
            var snap = await q.GetSnapshotAsync(ct);
            var data = new List<Input>();
            foreach (var d in snap.Documents)
            {
                var action = d.TryGetValue<string>("action", out var a) ? a : null;
                var articleId = d.TryGetValue<string>("articleId", out var aid) ? aid : null;
                if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(articleId)) continue;
                var article = await _articles.GetAsync(articleId);
                if (article is null) continue;
                var features = MoodModel.ExtractFeatures(article);
                data.Add(new Input
                {
                    Label = IsPositive(action) ? 1f : 0f,
                    Arousal = (float)features[0],
                    Sentiment = (float)features[1],
                    Depth = (float)features[2],
                    Conflict = (float)features[3],
                    Practicality = (float)features[4],
                    Optimism = (float)features[5],
                    Novelty = (float)features[6],
                    Human = (float)features[7],
                    Hype = (float)features[8],
                    Explainer = (float)features[9],
                    Analysis = (float)features[10],
                    Wholesome = (float)features[11],
                    ReadMinutes = (float)features[12]
                });
            }
            if (data.Count < 10) return; // not enough samples

            var trainData = _ml.Data.LoadFromEnumerable(data);
            var pipeline = _ml.Transforms.Concatenate("Features",
                                    nameof(Input.Arousal),
                                    nameof(Input.Sentiment),
                                    nameof(Input.Depth),
                                    nameof(Input.Conflict),
                                    nameof(Input.Practicality),
                                    nameof(Input.Optimism),
                                    nameof(Input.Novelty),
                                    nameof(Input.Human),
                                    nameof(Input.Hype),
                                    nameof(Input.Explainer),
                                    nameof(Input.Analysis),
                                    nameof(Input.Wholesome),
                                    nameof(Input.ReadMinutes))
                            .Append(_ml.BinaryClassification.Trainers.SdcaLogisticRegression());
            var model = pipeline.Fit(trainData);
            var calibrated = model.LastTransformer.Model as Microsoft.ML.Calibrators.CalibratedModelParametersBase<Microsoft.ML.Trainers.LinearBinaryModelParameters, Microsoft.ML.Calibrators.PlattCalibrator>;
            if (calibrated is null) return;
            var linear = calibrated.SubModel;
            var weights = linear.Weights.ToArray().Select(v => (double)v).ToArray();
            var bias = linear.Bias;
            var docRef = _db.Collection("MoodModels").Document($"{userId}_{normMood}");
            await docRef.SetAsync(new
            {
                userId,
                mood = normMood,
                weights = weights.ToList(),
                bias,
                updatedAt = Timestamp.FromDateTime(DateTime.UtcNow)
            }, SetOptions.MergeAll, ct);
        }

        public Task RecordEvaluationAsync(string userId, string mood, string articleId, double heuristicScore, double? modelScore, CancellationToken ct = default)
        {
            var docRef = _db.Collection("MoodModelEval").Document();
            return docRef.SetAsync(new
            {
                userId,
                mood = NormalizeMood(mood),
                articleId,
                heuristic = heuristicScore,
                model = modelScore,
                createdAt = Timestamp.FromDateTime(DateTime.UtcNow)
            }, cancellationToken: ct);
        }

        private static bool IsPositive(string action)
            => action is "MoreLikeThis" or "GreatExplainer" or "MoreLaunches";

        private static string NormalizeMood(string? m)
        {
            var k = (m ?? "").Trim().ToLowerInvariant();
            return k switch
            {
                "calm" => "Calm",
                "focused" => "Focused",
                "curious" => "Curious",
                "hyped" => "Hyped",
                "meh" => "Meh",
                "stressed" => "Stressed",
                "sad" => "Sad",
                _ => "Calm"
            };
        }

        private sealed class Input
        {
            [ColumnName("Label")] public float Label { get; set; }
            public float Arousal { get; set; }
            public float Sentiment { get; set; }
            public float Depth { get; set; }
            public float Conflict { get; set; }
            public float Practicality { get; set; }
            public float Optimism { get; set; }
            public float Novelty { get; set; }
            public float Human { get; set; }
            public float Hype { get; set; }
            public float Explainer { get; set; }
            public float Analysis { get; set; }
            public float Wholesome { get; set; }
            public float ReadMinutes { get; set; }
        }
    }
}
