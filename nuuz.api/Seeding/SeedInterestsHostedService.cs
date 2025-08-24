using Google.Cloud.Firestore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nuuz.Api.Seeding;

public class SeedInterestsHostedService : IHostedService
{
    private readonly ILogger<SeedInterestsHostedService> _log;
    private readonly FirestoreDb _db;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;

    public SeedInterestsHostedService(
        ILogger<SeedInterestsHostedService> log,
        FirestoreDb db,
        IConfiguration config,
        IHostEnvironment env)
    {
        _log = log;
        _db = db;
        _config = config;
        _env = env;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var shouldRun = _config.GetValue<bool>("Seed:Interests");
        if (!_env.IsDevelopment() || !shouldRun) return;

        _log.LogInformation("Seeding Interests…");

        var data = new (string Id, string Name, string Slug, string Emoji)[]
        {
            ("6b0e9a3f-6c34-4a8b-9c2e-9f4a4d2d7a11","Technology","technology","💻"),
            ("8f14e45f-ceea-4a0a-8c3a-1234567890ab","Climate","climate","🌍"),
            ("a1b2c3d4-e5f6-47a8-9012-abcdefabcdef","Politics","politics","🏛️"),
            ("11111111-2222-3333-4444-555555555555","Health","health","🩺"),
            ("22222222-3333-4444-5555-666666666666","Finance","finance","💸"),
            ("33333333-4444-5555-6666-777777777777","Space","space","🚀"),
            ("44444444-5555-6666-7777-888888888888","Pop Culture","pop-culture","🎬"),
            ("55555555-6666-7777-8888-999999999999","Sports","sports","🏟️"),
            ("66666666-7777-8888-9999-000000000000","Art & Design","art-design","🎨"),
            ("77777777-8888-9999-0000-111111111111","Good News Only","good-news","😊"),
        };

        var col = _db.Collection("Interests");
        foreach (var x in data)
        {
            var doc = col.Document(x.Id);
            var snap = await doc.GetSnapshotAsync(ct);
            if (snap.Exists)
            {
                _log.LogInformation("Interest {Name} already exists ({Id})", x.Name, x.Id);
                continue;
            }

            await doc.SetAsync(new
            {
                name = x.Name,
                slug = x.Slug,
                emoji = x.Emoji,
                isSystem = true
            }, cancellationToken: ct);

            _log.LogInformation("Created interest {Name} ({Id})", x.Name, x.Id);
        }

        _log.LogInformation("Interests seeding complete.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
