using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nuuz.Application.Abstraction;
using Nuuz.Application.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Services
{
    public sealed class MoodModelTrainingService : BackgroundService
    {
        private readonly ILogger<MoodModelTrainingService> _log;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeSpan _interval;
        private static readonly string[] Moods = new[]
        {
            "Calm", "Focused", "Curious", "Hyped", "Meh", "Stressed", "Sad"
        };

        public MoodModelTrainingService(
            ILogger<MoodModelTrainingService> log,
            IServiceScopeFactory scopeFactory,
            IConfiguration cfg)
        {
            _log = log;
            _scopeFactory = scopeFactory;
            var minutes = Math.Max(1, cfg.GetValue<int?>("ModelTraining:IntervalMinutes") ?? 60);
            _interval = TimeSpan.FromMinutes(minutes);
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _log.LogInformation("MoodModelTrainingService running every {m} minutes", _interval.TotalMinutes);
            using var timer = new PeriodicTimer(_interval);

            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var model = scope.ServiceProvider.GetRequiredService<IMoodModelService>();
                    var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                    var all = await users.GetAllAsync();

                    foreach (var u in all)
                    {
                        if (ct.IsCancellationRequested) break;
                        foreach (var mood in Moods)
                        {
                            await model.TrainAsync(u.FirebaseUid, mood, ct);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Mood model training cycle failed");
                }
            }

            _log.LogInformation("MoodModelTrainingService stopped.");
        }
    }
}
