using System.Threading;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Configuration;
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

    private record Interest(string Id, string Slug, string Name, string Emoji);

    public async Task StartAsync(CancellationToken ct)
    {
        var shouldRun = _config.GetValue<bool>("Seed:Interests");
        if (!_env.IsDevelopment() || !shouldRun)
        {
            _log.LogInformation("Skipping Interest seeding (IsDevelopment={IsDev}, Seed:Interests={Flag})",
                _env.IsDevelopment(), shouldRun);
            return;
        }

        _log.LogInformation("Seeding system Interests…");

        // Stable GUIDs for each interest (doc IDs). Keep these constant.
        var data = new Interest[]
        {
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1001","top","Top Stories","🗞️"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1002","world","World","🌍"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1003","us","U.S.","🇺🇸"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1004","local","Local","📍"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1005","politics","Politics & Policy","🏛️"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1006","elections","Elections","🗳️"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1007","business","Business & Industry","🏢"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1008","markets","Markets & Investing","📈"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1009","personal-finance","Personal Finance","💸"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1010","tech","Technology","💻"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1011","ai","AI & Machine Learning","🤖"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1012","cyber","Cybersecurity & Privacy","🔐"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1013","science","Science","🔬"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1014","space","Space","🚀"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1015","health","Health & Medicine","🩺"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1016","climate","Climate & Environment","🌎"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1017","energy","Energy","⚡"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1018","transport","Transportation & Mobility","🚗"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1019","sports","Sports","🏟️"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1020","entertainment","Entertainment","🎬"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1021","music","Music","🎵"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1022","gaming","Gaming & Esports","🎮"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1023","pop-culture","Pop Culture & Celebrities","🌟"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1024","arts-design","Arts & Design","🎨"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1025","books-ideas","Books & Ideas","📚"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1026","food-drink","Food & Drink","🍽️"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1027","travel","Travel","✈️"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1028","lifestyle","Lifestyle & Wellness","🧘"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1029","fashion","Fashion & Beauty","👗"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1030","education","Education","🎓"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1031","parenting","Parenting & Family","👪"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1032","real-estate","Real Estate & Housing","🏠"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1033","careers","Jobs & Careers","💼"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1034","law-justice","Law, Courts & Justice","⚖️"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1035","crime","Crime & Public Safety","🚔"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1036","defense","Military & Defense","🪖"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1037","crypto","Crypto & Web3","🪙"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1038","social-impact","Philanthropy & Social Impact","❤️"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1039","agriculture","Agriculture & Food Systems","🌾"),
            new("b2b8a5a8-6f7c-4e1b-bf9c-9b4d2d1f1040","tech-policy","Tech Policy & Regulation","📜"),
        };

        var col = _db.Collection("Interests");
        int created = 0, updated = 0;

        for (int i = 0; i < data.Length; i++)
        {
            var x = data[i];
            var doc = col.Document(x.Id); // <-- Use GUID as Firestore doc ID
            var snap = await doc.GetSnapshotAsync(ct);

            var payload = new
            {
                id = x.Id,            // optional: store the id in the document too
                name = x.Name,
                slug = x.Slug,
                emoji = x.Emoji,
                isSystem = true,
                order = i
            };

            await doc.SetAsync(payload, SetOptions.MergeAll, ct);

            if (snap.Exists)
            {
                updated++;
                _log.LogInformation("Updated interest {Name} ({Id})", x.Name, x.Id);
            }
            else
            {
                created++;
                _log.LogInformation("Created interest {Name} ({Id})", x.Name, x.Id);
            }
        }

        _log.LogInformation("Interests seeding complete. Created={Created}, Updated={Updated}, Total={Total}",
            created, updated, data.Length);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
