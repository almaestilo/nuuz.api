// Nuuz.Infrastructure/Services/SimpleContentExtractor.cs
using System.Net;
using System.Net.Http;
using HtmlAgilityPack;
using Nuuz.Application.Services;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace Nuuz.Infrastructure.Services;

public sealed class SimpleContentExtractor : IContentExtractor
{
    private readonly HttpClient _http;
    private readonly ILogger<SimpleContentExtractor>? _logger;
    private static readonly SemaphoreSlim s_seleniumGate = new(2, 2);

    public SimpleContentExtractor(HttpClient http, ILogger<SimpleContentExtractor>? logger = null)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ExtractedContent> ExtractAsync(string url, string? rssHtmlOrSummary, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);

            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger?.LogInformation("HTTP 403 for {Url}. Trying Selenium fallback.", url);
                var html403 = await TryLoadHtmlWithSeleniumAsync(url, ct);
                if (!string.IsNullOrWhiteSpace(html403))
                {
                    return ExtractFromHtml(html403!, rssHtmlOrSummary);
                }

                _logger?.LogWarning("Selenium fallback failed for {Url}. Using RSS summary.", url);
                var textFallback = HtmlEntity.DeEntitize(StripTags(rssHtmlOrSummary ?? ""));
                return new ExtractedContent { Text = textFallback, LeadImageUrl = null };
            }

            resp.EnsureSuccessStatusCode();
            var html = await resp.Content.ReadAsStringAsync(ct);
            return ExtractFromHtml(html, rssHtmlOrSummary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Content extraction failed for {Url}. Using RSS summary if available.", url);
            var text = HtmlEntity.DeEntitize(StripTags(rssHtmlOrSummary ?? ""));
            return new ExtractedContent { Text = text, LeadImageUrl = null };
        }
    }

    private ExtractedContent ExtractFromHtml(string html, string? rssHtmlOrSummary)
    {
        // crude readability: remove script/style/nav/footer/aside, pick longest text block
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        RemoveNodes(doc, new[] { "//script", "//style", "//nav", "//footer", "//aside", "//form" });

        var paragraphs = doc.DocumentNode.SelectNodes("//p") ?? new HtmlNodeCollection(null);
        var texts = paragraphs.Select(p => p.InnerText.Trim())
                              .Where(t => t.Length > 0)
                              .ToList();

        string mainText = string.Join("\n\n", texts);
        if (string.IsNullOrWhiteSpace(mainText) && !string.IsNullOrWhiteSpace(rssHtmlOrSummary))
        {
            // fallback to RSS content/summary stripped
            mainText = HtmlEntity.DeEntitize(StripTags(rssHtmlOrSummary));
        }

        string? leadImg = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", null)
                        ?? doc.DocumentNode.SelectSingleNode("//img[@src]")?.GetAttributeValue("src", null);

        return new ExtractedContent { Text = mainText ?? "", LeadImageUrl = leadImg };
    }

    private static void RemoveNodes(HtmlDocument doc, IEnumerable<string> xpaths)
    {
        foreach (var xp in xpaths)
        {
            var nodes = doc.DocumentNode.SelectNodes(xp);
            if (nodes == null) continue;
            foreach (var n in nodes) n.Remove();
        }
    }

    private static string StripTags(string html)
    {
        var d = new HtmlDocument();
        d.LoadHtml(html);
        return d.DocumentNode.InnerText;
    }

    private Task<string?> TryLoadHtmlWithSeleniumAsync(string url, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ChromeDriver? driver = null;
            try
            {
                // Throttle concurrent Selenium sessions (heavyweight)
                if (!s_seleniumGate.Wait(TimeSpan.FromSeconds(30), ct))
                {
                    _logger?.LogWarning("Selenium gate timeout for {Url}", url);
                    return null;
                }

                var options = new ChromeOptions();
                // Run headless to avoid UI requirements
                options.AddArgument("--headless=new");
                options.AddArgument("--disable-gpu");
                options.AddArgument("--no-sandbox");
                options.AddArgument("--disable-dev-shm-usage");
                options.AddArgument("--disable-blink-features=AutomationControlled");
                options.AddArgument("--window-size=1200,900");

                // If a specific Chrome binary is provided via env var, use it
                var chromeBinary = Environment.GetEnvironmentVariable("NUUZ_CHROME_PATH");
                if (!string.IsNullOrWhiteSpace(chromeBinary))
                {
                    options.BinaryLocation = chromeBinary;
                }

                var service = ChromeDriverService.CreateDefaultService();
                service.HideCommandPromptWindow = true;

                driver = new ChromeDriver(service, options, TimeSpan.FromSeconds(30));
                driver.Navigate().GoToUrl(url);

                // Simple readiness wait loop (avoid bringing in Selenium.Support)
                var waitUntil = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while (DateTime.UtcNow < waitUntil)
                {
                    if (ct.IsCancellationRequested) return null;
                    try
                    {
                        var ready = (driver as IJavaScriptExecutor)?.ExecuteScript("return document.readyState")?.ToString();
                        if (ready == "complete" || ready == "interactive") break;
                    }
                    catch
                    {
                        // ignore transient JS errors during load
                    }
                    Thread.Sleep(250);
                }

                // Optionally, give the page a brief moment for JS-rendered content
                Thread.Sleep(250);

                var html = driver.PageSource;
                return string.IsNullOrWhiteSpace(html) ? null : html;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Selenium load failed for {Url}", url);
                return null;
            }
            finally
            {
                try { driver?.Quit(); } catch { }
                try { driver?.Dispose(); } catch { }
                try { s_seleniumGate.Release(); } catch { }
            }
        }, ct);
    }
}
