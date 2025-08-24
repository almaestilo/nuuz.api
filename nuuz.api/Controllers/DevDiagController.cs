// Nuuz.Api/Controllers/DevDiagController.cs
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace Nuuz.Api.Controllers;

[ApiController]
[Route("api/dev/diag")]
public class DevDiagController : ControllerBase
{
    private readonly FirestoreDb _db;
    public DevDiagController(FirestoreDb db) => _db = db;

    [HttpGet("articles")]
    public async Task<IActionResult> Articles([FromQuery] int limit = 10)
    {
        var snap = await _db.Collection("Articles").OrderByDescending("PublishedAt")
            .Limit(Math.Clamp(limit, 1, 50))
            .GetSnapshotAsync();

        var data = snap.Documents.Select(d => new
        {
            id = d.Id,
            title = d.GetValue<string>("Title"),
            publishedAt = d.GetValue<Timestamp>("PublishedAt").ToDateTime(),
            interestMatches = d.TryGetValue<List<string>>("InterestMatches", out var x) ? x : new List<string>()
        });

        return Ok(data);
    }

    [HttpGet("firestore")]
    public async Task<IActionResult> FirestoreCheck([FromQuery] int take = 3)
    {
        var list = new List<object>();
        var snap = await _db.Collection("Articles").Limit(Math.Max(1, Math.Min(10, take))).GetSnapshotAsync();

        foreach (var doc in snap.Documents)
        {
            var hasPub = doc.TryGetValue<Timestamp>("publishedAt", out var pubTs);
            var hasPubString = doc.TryGetValue<string>("publishedAt", out var pubStr);
            var hasCreated = doc.TryGetValue<Timestamp>("createdAt", out var createdTs);

            list.Add(new
            {
                id = doc.Id,
                hasPublishedAt = hasPub,
                publishedAtType = hasPub ? "timestamp" : (hasPubString ? "string" : "missing"),
                publishedAtTimestamp = hasPub ? pubTs.ToDateTimeOffset().ToString("O") : null,
                publishedAtString = hasPubString ? pubStr : null,
                hasCreatedAt = hasCreated,
                createdAt = hasCreated ? createdTs.ToDateTimeOffset().ToString("O") : null,
                // quick peek at a few fields
                hasVibe = doc.ContainsField("vibe"),
                hasTags = doc.ContainsField("tags"),
            });
        }

        return Ok(new
        {
            projectId = _db.ProjectId,
            sampleCount = snap.Count,
            sample = list
        });
    }
}
