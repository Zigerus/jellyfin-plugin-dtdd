using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dtdd.Services;

/// <summary>
/// IHostedService that fires the first-load topic seed. This is the equivalent
/// of an OnInstall lifecycle hook in current Jellyfin — BasePlugin in 10.11.x
/// exposes a constructor (runs on every plugin load) and OnUninstalling, but
/// no dedicated install/first-load hook. An IHostedService runs once when the
/// Jellyfin server starts the DI container with our plugin loaded, which IS
/// the "first load" moment from Jellyfin's perspective.
///
/// <para>
/// <b>Guard</b> — only seeds when the topics table is empty. A normal server
/// restart finds rows already there and skips. The weekly
/// <see cref="ScheduledTasks.SeedTopicsRefreshTask"/> handles subsequent
/// refreshes. This avoids hammering DTDD's API on every Jellyfin restart.
/// </para>
///
/// <para>
/// <b>Fire-and-forget</b> — runs the seed on a background task so Jellyfin's
/// startup is not blocked. If seed fails the weekly task will retry; in the
/// meantime backend safety calls still work, just with an empty picker UI
/// until a /media/{id} response accumulates topics organically via
/// WarningCache.Put.
/// </para>
/// </summary>
public class TopicSeederStartupService : IHostedService
{
    private static readonly TimeSpan SeedTimeout = TimeSpan.FromMinutes(5);

    private readonly TopicSeeder _seeder;
    private readonly WarningCache _cache;
    private readonly ILogger<TopicSeederStartupService> _logger;

    public TopicSeederStartupService(
        TopicSeeder seeder,
        WarningCache cache,
        ILogger<TopicSeederStartupService> logger)
    {
        _seeder = seeder;
        _cache = cache;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var existing = _cache.GetTopics();
            if (existing.Count > 0)
            {
                _logger.LogDebug("DTDD topic-seed startup: {Count} topics already present; skipping first-load seed", existing.Count);
                return Task.CompletedTask;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DTDD topic-seed startup: failed to inspect topics table; skipping first-load seed");
            return Task.CompletedTask;
        }

        _logger.LogInformation("DTDD topic-seed startup: empty catalog detected; running first-load seed in background");

        _ = Task.Run(
            async () =>
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(SeedTimeout);
                    await _seeder.RunAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DTDD topic-seed startup: first-load seed failed; weekly task will retry");
                }
            },
            cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
