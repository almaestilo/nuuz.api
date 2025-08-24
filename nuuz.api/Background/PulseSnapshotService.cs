using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nuuz.Infrastructure.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

public sealed class PulseSnapshotService : BackgroundService
{
    private readonly ILogger<PulseSnapshotService> _log;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _interval;
    private readonly string _timezone;
    private readonly int _take;

    public PulseSnapshotService(
        ILogger<PulseSnapshotService> log,
        IServiceScopeFactory scopeFactory,
        IConfiguration cfg)
    {
        _log = log;
        _scopeFactory = scopeFactory;

        // Config
        _timezone = cfg["Pulse:Timezone"] ?? "America/New_York";
        var minutes = Math.Max(1, cfg.GetValue<int?>("Pulse:IntervalMinutes") ?? 60);
        _interval = TimeSpan.FromMinutes(minutes);
        _take = Math.Clamp(cfg.GetValue<int?>("Pulse:Take") ?? 12, 6, 20);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("PulseSnapshotService running every {m} minutes (tz={tz}, take={take})", _interval.TotalMinutes, _timezone, _take);
        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var pulse = scope.ServiceProvider.GetRequiredService<IPulseService>();
                await pulse.GenerateHourAsync(_timezone, take: _take, ct: ct);
                _log.LogInformation("Pulse snapshot written.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Pulse snapshot failed");
            }
        }

        _log.LogInformation("PulseSnapshotService stopped.");
    }
}
