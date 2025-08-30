using CodeHollow.FeedReader;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nuuz.Application.Abstraction;
using Nuuz.Application.Services;
using Nuuz.Domain.Entities;
using Nuuz.Infrastructure.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Services
{
    public sealed class NewsIngestionService : BackgroundService
    {
        private readonly ILogger<NewsIngestionService> _log;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeSpan _interval;
        private readonly string[] _sources;
        private readonly int _horizonDays;

        public NewsIngestionService(
            ILogger<NewsIngestionService> log,
            IServiceScopeFactory scopeFactory,
            IConfiguration config)
        {
            _log = log;
            _scopeFactory = scopeFactory;

            var minutes = Math.Max(1, config.GetValue<int?>("Ingestion:IntervalMinutes") ?? 10);
            _interval = TimeSpan.FromMinutes(minutes);

            _sources = config.GetSection("Ingestion:Sources").Get<string[]>() ?? Array.Empty<string>();
            _horizonDays = Math.Max(1, config.GetValue<int?>("Ingestion:HorizonDays") ?? 5);
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _log.LogInformation("Ingestion starting every {m} minutes", _interval.TotalMinutes);
            using var timer = new PeriodicTimer(_interval);

            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();

                    var db = scope.ServiceProvider.GetRequiredService<FirestoreDb>();
                    var articles = scope.ServiceProvider.GetRequiredService<IArticleRepository>();
                    var matcher = scope.ServiceProvider.GetRequiredService<IInterestMatcher>();
                    var summarizer = scope.ServiceProvider.GetRequiredService<IAISummarizer>();
                    var spark = scope.ServiceProvider.GetRequiredService<ISparkNotesService>();
                    var unified = scope.ServiceProvider.GetRequiredService<IUnifiedNotesService>();
                    var extractor = scope.ServiceProvider.GetRequiredService<IContentExtractor>();

                    foreach (var feedUrl in _sources)
                    {
                        if (ct.IsCancellationRequested) break;
                        await IngestFeed(feedUrl, db, articles, matcher, summarizer, spark, unified, extractor, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Ingestion cycle failed");
                }
            }

            _log.LogInformation("Ingestion stopped.");
        }

        private async Task IngestFeed(
            string feedUrl,
            FirestoreDb db,
            IArticleRepository articles,
            IInterestMatcher matcher,
            IAISummarizer summarizer,
            ISparkNotesService spark,
            IUnifiedNotesService unified,
            IContentExtractor extractor,
            CancellationToken ct)
        {
            try
            {
                _log.LogInformation("Fetching feed {feed}", feedUrl);
                var feed = await FeedReader.ReadAsync(feedUrl);

                using var sem = new SemaphoreSlim(4);
                var tasks = new List<Task>();

                foreach (var item in feed.Items.Take(30))
                {
                    await sem.WaitAsync(ct);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var url = item.Link?.Trim();
                            if (string.IsNullOrWhiteSpace(url)) return;

                            var published = item.PublishingDate ?? DateTime.UtcNow;
                            if (published < DateTime.UtcNow.AddDays(-_horizonDays)) return;

                            var id = HashUtil.UrlHash(url);
                            var existing = await articles.GetAsync(id);
                            if (existing is not null) return;

                            var title = item.Title?.Trim() ?? "";
                            var rssHtml = (item.Content ?? item.Description ?? "").Trim();

                            // Extract main content (text + lead image)
                            var extracted = await extractor.ExtractAsync(url, rssHtml, ct);
                            var text = string.IsNullOrWhiteSpace(extracted.Text) ? title : extracted.Text;

                            // Summaries + fine-grained signals + SparkNotes in a single call (primary path)
                            string summary, vibe; string[] tags;
                            double? sent = null, sentVar = null, arousal = null;

                            // Defaults for extended signals (in case of fallback)
                            double depth = 0.35, conflict = 0.2, practicality = 0.2, optimism = 0.3,
                                   novelty = 0.3, humanInterest = 0.25, hype = 0.2, explainer = 0.25,
                                   analysis = 0.25, wholesome = 0.25;
                            int readMinutes = Math.Clamp((int)Math.Round((text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length) / 220.0), 1, 60);
                            string genre = "Report", eventStage = "Feature", format = readMinutes <= 4 ? "Short" : (readMinutes >= 14 ? "Longform" : "Standard");

                            SparkNotesResult sparkNotes;

                            try
                            {
                                var unifiedRes = await unified.BuildAsync(url, title, text, ct);
                                var rich = unifiedRes.Rich;
                                sparkNotes = unifiedRes.Spark;

                                summary = string.IsNullOrWhiteSpace(rich.Summary) ? title : rich.Summary;
                                vibe = string.IsNullOrWhiteSpace(rich.Vibe) ? "Neutral" : rich.Vibe;
                                tags = (rich.Tags?.Length ?? 0) > 0 ? rich.Tags : Array.Empty<string>();

                                sent = rich.Sentiment;
                                sentVar = rich.SentimentVar;
                                arousal = rich.Arousal;

                                var f = rich.Features;
                                depth = f.Depth;
                                readMinutes = f.ReadMinutes;
                                conflict = f.Conflict;
                                practicality = f.Practicality;
                                optimism = f.Optimism;
                                novelty = f.Novelty;
                                humanInterest = f.HumanInterest;
                                hype = f.Hype;
                                explainer = f.Explainer;
                                analysis = f.Analysis;
                                wholesome = f.Wholesome;
                                genre = f.Genre;
                                eventStage = f.EventStage;
                                format = f.Format;
                            }
                            catch
                            {
                                // Fallback path: try independent services
                                try
                                {
                                    sparkNotes = await spark.BuildAsync(url, title, text, ct);
                                }
                                catch
                                {
                                    sparkNotes = new SparkNotesResult(
$@"<h2>Nuuz SparkNotes</h2>
<p class=""kicker"">{System.Net.WebUtility.HtmlEncode(title)}</p>
<p>We couldn't generate a brief yet. Tap Read original.</p>
<p class=""cta""><a href=""{url}"" target=""_blank"" rel=""noopener nofollow"">Read the original</a></p>",
                                        title);
                                }

                                // Fallback: simple heuristics already implied by defaults above
                                summary = string.IsNullOrWhiteSpace(rssHtml) ? title : title;
                                var s = HeuristicVibeEstimator.EstimateSentiment(text);
                                var a = HeuristicVibeEstimator.EstimateArousal(title, text);
                                var vt = HeuristicVibeEstimator.GuessVibeTags(title, text);
                                vibe = vt.vibe; tags = vt.tags; sent = s.overall; sentVar = s.variance; arousal = a;
                            }

                            // Interest match
                            var interestMatches = await matcher.MatchAsync(title, text);

                            // Persist
                            var art = new Article
                            {
                                Id = id,
                                Url = url,
                                SourceId = feed.Title ?? "rss",
                                Title = title,
                                Author = item.Author,
                                PublishedAt = Timestamp.FromDateTime(published.ToUniversalTime()),
                                ImageUrl = extracted.LeadImageUrl,
                                Summary = summary,
                                Vibe = vibe,
                                Tags = tags.ToList(),
                                Sentiment = sent,
                                SentimentVar = sentVar,
                                Arousal = arousal,
                                InterestMatches = interestMatches,
                                CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),

                                SparkNotesHtml = sparkNotes.Html,
                                SparkNotesText = sparkNotes.PlainText,

                                // NEW: extended mood features
                                Depth = depth,
                                ReadMinutes = readMinutes,
                                Conflict = conflict,
                                Practicality = practicality,
                                Optimism = optimism,
                                Novelty = novelty,
                                HumanInterest = humanInterest,
                                Hype = hype,
                                Explainer = explainer,
                                Analysis = analysis,
                                Wholesome = wholesome,
                                Genre = genre,
                                EventStage = eventStage,
                                Format = format

                                // TopicEmbedding: OPTIONAL — if you compute it elsewhere, set it here.
                            };

                            await articles.AddAsync(art);
                        }
                        catch (Exception exOne)
                        {
                            _log.LogWarning(exOne, "Failed to ingest one item from {feed}", feedUrl);
                        }
                        finally
                        {
                            sem.Release();
                        }
                    }, ct));
                }

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "IngestFeed failed for {feed}", feedUrl);
            }
        }
    }
}
