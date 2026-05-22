using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Dtdd.Api.Models;
using Jellyfin.Plugin.Dtdd.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dtdd.ScheduledTasks;

/// <summary>
/// Scheduled task that prefetches DTDD warnings for library items so the first
/// per-item /safety call is a cache hit.
///
/// Default OFF: Plugin.Configuration.PrefetchEnabled must be true for this task
/// to do anything beyond logging "disabled, skipping". Politer to DTDD's API
/// while cache stability is being verified — flip it on once you trust v1.
///
/// Default schedule: weekly, Sunday at 04:00 local time. Jellyfin's task UI
/// lets admins reschedule.
/// </summary>
public class PrefetchWarningsTask : IScheduledTask
{
    private static readonly TimeSpan PerRequestDelay = TimeSpan.FromSeconds(1);

    private readonly DtddClient _dtdd;
    private readonly WarningCache _cache;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PrefetchWarningsTask> _logger;

    public PrefetchWarningsTask(
        DtddClient dtdd,
        WarningCache cache,
        ILibraryManager libraryManager,
        ILogger<PrefetchWarningsTask> logger)
    {
        _dtdd = dtdd;
        _cache = cache;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Prefetch DoesTheDogDie warnings";

    /// <inheritdoc />
    public string Description => "Iterates Movie and Series items, fetching DTDD content warnings into the local cache. Off by default — enable on the DoesTheDogDie plugin config page.";

    /// <inheritdoc />
    public string Key => "DtddPrefetchWarnings";

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
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
            },
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.PrefetchEnabled)
        {
            _logger.LogInformation("DTDD prefetch is disabled in plugin configuration; skipping run");
            progress.Report(100);
            return;
        }

        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            _logger.LogWarning("DTDD prefetch enabled but API key is not configured; aborting");
            progress.Report(100);
            return;
        }

        var expired = _cache.ExpireOlderThan(cfg.CacheTtlDays);
        if (expired > 0)
        {
            _logger.LogInformation("DTDD prefetch: expired {Count} stale cache rows before fetching", expired);
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            IsVirtualItem = false,
            Recursive = true,
        };

        var items = _libraryManager.GetItemList(query);
        var total = items.Count;
        if (total == 0)
        {
            _logger.LogInformation("DTDD prefetch: no Movie or Series items in library");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("DTDD prefetch: scanning {Total} library items", total);

        var done = 0;
        var fetched = 0;
        var skipped = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await TryPrefetchAsync(item, cancellationToken).ConfigureAwait(false))
                {
                    fetched++;
                    await Task.Delay(PerRequestDelay, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    skipped++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DTDD prefetch failed for {Title}", item.Name);
            }

            done++;
            progress.Report(100d * done / total);
        }

        _logger.LogInformation("DTDD prefetch complete: {Fetched} fetched, {Skipped} skipped of {Total}", fetched, skipped, total);
        progress.Report(100);
    }

    /// <summary>
    /// Returns true if the task issued a DTDD network call for the item (so the
    /// caller knows to insert the inter-request delay). Skips when the item lacks
    /// a TMDB ID or when the cache is already warm for it.
    /// </summary>
    private async Task<bool> TryPrefetchAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var tmdbStr = item.GetProviderId(MetadataProvider.Tmdb);
        if (!int.TryParse(tmdbStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            return false;
        }

        if (_cache.Get(tmdbId) is not null)
        {
            return false;
        }

        DtddMediaDetails? details = null;
        var imdbStr = item.GetProviderId(MetadataProvider.Imdb);
        if (!string.IsNullOrWhiteSpace(imdbStr))
        {
            details = await _dtdd.GetByImdbAsync(imdbStr, cancellationToken).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(item.Name))
        {
            var typeId = item is Series ? DtddConstants.ItemTypeSeries : DtddConstants.ItemTypeMovie;
            details = await _dtdd.GetByTitleAsync(item.Name, item.ProductionYear, typeId, cancellationToken).ConfigureAwait(false);
        }

        if (details is not null)
        {
            _cache.Put(tmdbId, details);
        }

        return true;
    }
}
