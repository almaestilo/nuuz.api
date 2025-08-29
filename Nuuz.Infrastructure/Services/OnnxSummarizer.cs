using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics.Tensors;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Nuuz.Application.Services;
using Tokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer;

namespace Nuuz.Infrastructure.Services
{
    /// <summary>
    /// Simple extractive summarizer backed by a local ONNX sentence encoder.
    /// Uses a sentence-transformer style model to embed text and selects the
    /// sentences most similar to the whole document. Falls back to heuristics
    /// if the model files are missing.
    /// </summary>
    public sealed class OnnxSummarizer : IAISummarizer, IDisposable
    {
        private readonly Tokenizer? _tokenizer;
        private readonly InferenceSession? _session;
        private readonly IReadOnlyDictionary<string, string> _tagLookup;
        private readonly string[] _positive = new[]
        {
            "great","good","positive","growth","success","improve","win","benefit"
        };
        private readonly string[] _negative = new[]
        {
            "bad","decline","risk","loss","fail","drop","negative","warn"
        };

        public OnnxSummarizer(IConfiguration cfg)
        {
            var modelPath = cfg["LocalSummarizer:ModelPath"] ?? "onnx/model.onnx";
            var tokenizerPath = cfg["LocalSummarizer:TokenizerPath"] ?? "onnx/tokenizer.json";
            try
            {
                if (System.IO.File.Exists(modelPath) && System.IO.File.Exists(tokenizerPath))
                {
                    _tokenizer = Tokenizer.FromFile(tokenizerPath);
                    _session = new InferenceSession(modelPath);
                }
            }
            catch
            {
                // ignore and fallback to heuristics
            }

            // tiny keyword to tag map
            _tagLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["finance"] = "Finance",
                ["market"] = "Finance",
                ["stock"] = "Finance",
                ["economy"] = "Finance",
                ["technology"] = "Tech",
                ["tech"] = "Tech",
                ["ai"] = "Tech",
                ["science"] = "Science",
                ["research"] = "Science",
                ["politics"] = "Politics",
                ["election"] = "Politics",
                ["government"] = "Politics",
                ["sports"] = "Sports",
                ["health"] = "Health",
                ["entertainment"] = "Entertainment"
            };
        }

        public void Dispose()
        {
            _session?.Dispose();
        }

        public Task<(string summary, string vibe, string[] tags)> SummarizeAsync(string title, string text, CancellationToken ct = default)
        {
            string summary = _session != null && _tokenizer != null
                ? ExtractiveSummary(text)
                : SimpleSummary(text);

            string vibe = GuessVibe(text);
            string[] tags = GuessTags(text);
            return Task.FromResult((summary, vibe, tags));
        }

        public async Task<(string summary, string vibe, string[] tags, double? sentiment, double? sentimentVar, double? arousal)> SummarizeRichAsync(string title, string text, CancellationToken ct = default)
        {
            var (summary, vibe, tags) = await SummarizeAsync(title, text, ct);
            double? sentiment = vibe == "Upbeat" ? 0.5 : vibe == "Cautionary" ? -0.5 : 0;
            return (summary, vibe, tags, sentiment, null, null);
        }

        public async Task<RichSignals> SummarizeSignalsAsync(string title, string text, CancellationToken ct = default)
        {
            var (summary, vibe, tags, sentiment, sentimentVar, arousal) = await SummarizeRichAsync(title, text, ct);
            var sig = FallbackSignals(title, text);
            return new RichSignals(summary, vibe, tags, sentiment, sentimentVar, arousal, sig);
        }

        private string ExtractiveSummary(string text)
        {
            try
            {
                var sentences = Regex.Split(text, @"(?<=[.!?])\s+")
                .Where(s => s.Length > 20)
                .Take(20)
                .ToArray();
                if (sentences.Length == 0) return text;

                var docEmb = Embed(text);
                var ranked = sentences
                    .Select(s => (s, score: TensorPrimitives.CosineSimilarity(Embed(s), docEmb)))
                    .OrderByDescending(x => x.score)
                    .Take(3)
                    .Select(x => x.s.Trim());
                var summary = string.Join(" ", ranked);
                return summary.Length > 480 ? summary[..480] + "…" : summary;
            } catch (Exception ex) {
                throw ex;
            }
            return "";
        }

        private string SimpleSummary(string text)
        {
            var sentences = Regex.Split(text, @"(?<=[.!?])\s+")
                .Where(s => s.Length > 20)
                .Take(3);
            var summary = string.Join(" ", sentences);
            return summary.Length > 480 ? summary[..480] + "…" : summary;
        }

        private float[] Embed(string text)
        {
            if (_tokenizer == null || _session == null) return Array.Empty<float>();
            var enc = _tokenizer.Encode(text, true, include_type_ids: true, include_attention_mask: true).Encodings[0];
            var seqLen = enc.Ids.Count;
            var inputIds = new DenseTensor<long>(enc.Ids.Select(i => (long)i).ToArray(), new[] { 1, seqLen });
            var typeIds = new DenseTensor<long>(enc.TypeIds.Select(i => (long)i).ToArray(), new[] { 1, seqLen });
            var mask = new DenseTensor<long>(enc.AttentionMask.Select(i => (long)i).ToArray(), new[] { 1, seqLen });
            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("token_type_ids", typeIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask)
            };
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);
            var output = results.First().AsEnumerable<float>().ToArray();
            int dim = output.Length / seqLen;
            float[] pooled = new float[dim];
            for (int i = 0; i < seqLen; i++)
            {
                var span = new ReadOnlySpan<float>(output, i * dim, dim);
                TensorPrimitives.Add(span, pooled, pooled);
            }
            TensorPrimitives.Divide(pooled, seqLen, pooled);
            return pooled;
        }

        private string GuessVibe(string text)
        {
            var lower = text.ToLowerInvariant();
            if (_positive.Any(p => lower.Contains(p))) return "Upbeat";
            if (_negative.Any(p => lower.Contains(p))) return "Cautionary";
            return "Neutral";
        }

        private string[] GuessTags(string text)
        {
            var lower = text.ToLowerInvariant();
            var tags = new List<string>();
            foreach (var kv in _tagLookup)
            {
                if (lower.Contains(kv.Key) && !tags.Contains(kv.Value))
                    tags.Add(kv.Value);
                if (tags.Count >= 6) break;
            }
            return tags.ToArray();
        }

        private static Signals FallbackSignals(string title, string text)
        {
            var words = Math.Max(50, text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length);
            var readMin = Math.Clamp((int)Math.Round(words / 220.0), 1, 60);
            return new Signals(
                Depth: 0.2,
                ReadMinutes: readMin,
                Conflict: 0.2,
                Practicality: 0.2,
                Optimism: 0.3,
                Novelty: 0.3,
                HumanInterest: 0.2,
                Hype: 0.2,
                Explainer: 0.2,
                Analysis: 0.2,
                Wholesome: 0.2,
                Genre: string.Empty,
                EventStage: string.Empty,
                Format: readMin <= 4 ? "Short" : (readMin >= 14 ? "Longform" : "Standard")
            );
        }
    }
}
