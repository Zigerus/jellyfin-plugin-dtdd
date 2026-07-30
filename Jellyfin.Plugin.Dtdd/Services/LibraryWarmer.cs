using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Dtdd.Api.Models;
using Jellyfin.Plugin.Dtdd.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dtdd.Services;

/// <summary>
/// On-demand library warmer — called from <c>POST /DTDD/scan</c> after a user
/// saves their phobia picks. Same per-item logic as
/// <see cref="ScheduledTasks.PrefetchWarningsTask"/> (cache miss → fetch +
/// backfill ProviderId + politeness delay) but ignores the
/// <c>PrefetchEnabled</c> toggle: the user explicitly asked for it.
///
/// <para>
/// Runs once at a time per server (a static gate flag rejects re-entry while
/// a previous warm is still walking the library). Subsequent /scan requests
/// while a warm is in flight return immediately without queueing.
/// </para>
/// </summary>
public class LibraryWarmer
{
    // Same pacing rationale as PrefetchWarningsTask: up to two requests per
    // cache-miss item against the free tier's 30/min budget.
    private static readonly TimeSpan PerRequestDelay = TimeSpan.FromSeconds(4.5);
    private static int _running; // 0 = idle, 1 = warming

    private readonly DtddClient _dtdd;
    private readonly WarningCache _cache;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryWarmer> _logger;

    public LibraryWarmer(
        DtddClient dtdd,
        WarningCache cache,
        ILibraryManager libraryManager,
        ILogger<LibraryWarmer> logger)
    {
        _dtdd = dtdd;
        _cache = cache;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// True when a warm is currently running.
    /// </summary>
    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// Kick off a warm if none is in flight. Returns true if this call
    /// started one, false if a previous warm is still running.
    /// </summary>
    public bool TryStartBackground(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            _logger.LogInformation("Library warm already in progress; new /scan request ignored");
            return false;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Library warm failed");
                }
                finally
                {
                    Interlocked.Exchange(ref _running, 0);
                }
            },
            cancellationToken);

        return true;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            _logger.LogWarning("Library warm requested but API key not configured; nothing to do");
            return;
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            IsVirtualItem = false,
            Recursive = true,
        };

        var items = _libraryManager.GetItemList(query);
        if (items.Count == 0)
        {
            _logger.LogInformation("Library warm: no Movie or Series items in library");
            return;
        }

        _logger.LogInformation("Library warm starting: {Total} library items", items.Count);

        var fetched = 0;
        var cacheHits = 0;
        var noProviderId = 0;
        var notFound = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var outcome = await WarmOneAsync(item, cancellationToken).ConfigureAwait(false);
                switch (outcome)
                {
                    case WarmOutcome.Fetched:
                        fetched++;
                        await Task.Delay(PerRequestDelay, cancellationToken).ConfigureAwait(false);
                        break;
                    case WarmOutcome.CacheHit:
                        cacheHits++;
                        break;
                    case WarmOutcome.NoProviderId:
                        noProviderId++;
                        break;
                    case WarmOutcome.NotFound:
                        notFound++;
                        await Task.Delay(PerRequestDelay, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Library warm failed for {Title}", item.Name);
            }
        }

        _logger.LogInformation(
            "Library warm complete: {Fetched} fetched, {Hits} already-cached, {NoId} skipped (no TMDB), {Miss} not in DTDD",
            fetched, cacheHits, noProviderId, notFound);
    }

    private async Task<WarmOutcome> WarmOneAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var tmdbStr = item.GetProviderId(MetadataProvider.Tmdb);
        if (!int.TryParse(tmdbStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            return WarmOutcome.NoProviderId;
        }

        if (_cache.Get(tmdbId) is not null)
        {
            return WarmOutcome.CacheHit;
        }

        var imdbStr = item.GetProviderId(MetadataProvider.Imdb);
        var typeId = item is Series ? DtddConstants.ItemTypeSeries : DtddConstants.ItemTypeMovie;
        var details = await _dtdd.ResolveAsync(tmdbId, imdbStr, item.Name, item.ProductionYear, typeId, cancellationToken).ConfigureAwait(false);

        if (details is null)
        {
            return WarmOutcome.NotFound;
        }

        _cache.Put(tmdbId, details);
        await DtddProviderIdBackfill.TryBackfillAsync(item, details.Item.Id, _logger, cancellationToken).ConfigureAwait(false);
        return WarmOutcome.Fetched;
    }

    private enum WarmOutcome
    {
        Fetched,
        CacheHit,
        NoProviderId,
        NotFound,
    }
}
