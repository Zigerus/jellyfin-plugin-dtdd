using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Dtdd.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dtdd.ScheduledTasks;

/// <summary>
/// Scheduled task that re-seeds the local DTDD topic catalog so the phobia
/// picker stays current with newly-added topics. Idempotent: duplicate
/// topic IDs are silently ignored at the storage layer.
///
/// <para>
/// Default cadence: weekly, Sunday 03:00. Sits one hour before
/// <see cref="PrefetchWarningsTask"/> (04:00) so seed completes before prefetch
/// fires — though they're independent and either can run on its own.
/// </para>
///
/// <para>
/// Always runs regardless of the prefetch toggle (the catalog refresh is
/// cheap — five search calls — and the picker needs it whether or not
/// bulk warming is enabled). Admins can disable via the Jellyfin task UI.
/// </para>
/// </summary>
public class SeedTopicsRefreshTask : IScheduledTask
{
    private readonly TopicSeeder _seeder;
    private readonly ILogger<SeedTopicsRefreshTask> _logger;

    public SeedTopicsRefreshTask(TopicSeeder seeder, ILogger<SeedTopicsRefreshTask> logger)
    {
        _seeder = seeder;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Refresh DoesTheDogDie topics catalog";

    /// <inheritdoc />
    public string Description => "Re-seeds the local topic catalog from DTDD so the phobia picker stays up to date. Idempotent; safe to run as often as you like.";

    /// <inheritdoc />
    public string Key => "DtddSeedTopics";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
            },
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            _logger.LogInformation("DTDD topic-seed task: API key not configured; skipping run");
            progress.Report(100);
            return;
        }

        try
        {
            await _seeder.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DTDD topic-seed task failed; weekly retry will pick this up");
        }

        progress.Report(100);
    }
}
