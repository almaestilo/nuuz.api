// Nuuz.Api/Controllers/DevSeedController.cs
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;
using Nuuz.Application.Abstraction;
using Nuuz.Domain.Entities;
using Nuuz.Infrastructure.Utils;

namespace Nuuz.Api.Controllers;

[ApiController]
[Route("api/dev")]
public class DevSeedController : ControllerBase
{
    private readonly IArticleRepository _articles;
    private readonly IInterestRepository _interests;

    public DevSeedController(IArticleRepository articles, IInterestRepository interests)
    {
        _articles = articles;
        _interests = interests;
    }

    [HttpPost("seed")]
    public async Task<IActionResult> Seed()
    {
        // Load system interests so we can map names -> IDs
        var all = await _interests.GetAllOrderedAsync();
        var byName = all.ToDictionary(i => i.Name.Trim().ToLowerInvariant(), i => i.Id);

        string? IdOf(string name)
        {
            byName.TryGetValue(name.Trim().ToLowerInvariant(), out var id);
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        var now = DateTimeOffset.UtcNow;

        // Helper to build an article quickly
        Article A(string url, string title, string source, string[] names, string summary, string vibe, int minutesAgo)
        {
            var ids = names.Select(IdOf).Where(x => x != null)!.Cast<string>().Distinct().ToList();
            return new Article
            {
                Id = HashUtil.UrlHash(url),
                Url = url,
                SourceId = source,
                Title = title,
                Author = null,
                PublishedAt = Timestamp.FromDateTimeOffset(now.AddMinutes(-minutesAgo)),
                ImageUrl = null,
                Summary = summary,
                Vibe = vibe,
                Tags = names.ToList(),
                Topics = new(),
                InterestMatches = ids, // IMPORTANT: these are the actual interest IDs
                CreatedAt = Timestamp.FromDateTimeOffset(now)
            };
        }

        var samples = new List<Article>
        {
            A("https://example.com/tech-1",
              "Tiny GPU wins big on efficient AI inferencing",
              "SampleTech",
              new[] { "Technology", "AI" },
              "A new open-source kernel makes small GPUs punch above their weight.",
              "Analytical", 5),

            A("https://example.com/climate-1",
              "Cities test cool pavement to beat heat waves",
              "ClimateDaily",
              new[] { "Climate" },
              "Reflective coatings reduced peak surface temps by up to 9°C in pilots.",
              "Optimistic", 12),

            A("https://example.com/sports-1",
              "Late winner lifts the underdogs in cup thriller",
              "SportWire",
              new[] { "Sports" },
              "An 89th-minute strike sealed a shock upset in the quarterfinals.",
              "Excited", 25),

            A("https://example.com/good-1",
              "Local library clears all late fees—visits surge 40%",
              "GoodNews",
              new[] { "Good News Only" },
              "Removing fines brought families back and boosted literacy programs.",
              "Wholesome", 30),

            A("https://example.com/politics-1",
              "New transparency rules target AI-generated political ads",
              "PolicyWatch",
              new[] { "Politics", "AI" },
              "Platforms must label synthetic content and retain disclosure records.",
              "Cautionary", 45),

            A("https://example.com/art-1",
              "Generative tools spark a renaissance in indie game art",
              "Art & Code",
              new[] { "Art & Design", "Pop Culture", "Technology" },
              "Small teams ship striking visuals by blending paintovers with models.",
              "Creative", 60),

            A("https://example.com/space-1",
              "Reusable upper stage completes third successful hop",
              "OrbitalTimes",
              new[] { "Space", "Technology" },
              "The prototype aims to cut launch costs by 30% after refly milestone.",
              "Awe", 75),

            A("https://example.com/health-1",
              "Sleep consistency linked to better glucose control",
              "HealthLab",
              new[] { "Health" },
              "New cohort data suggests regular bedtimes may rival step counts.",
              "Informative", 90),
        };

        int inserted = 0;
        foreach (var a in samples)
        {
            // idempotent: if already exists, skip
            var existing = await _articles.GetAsync(a.Id);
            if (existing is null)
            {
                await _articles.AddAsync(a);
                inserted++;
            }
        }

        return Ok(new { inserted, total = samples.Count });
    }
}
