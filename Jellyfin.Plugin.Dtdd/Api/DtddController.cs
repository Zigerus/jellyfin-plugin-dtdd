using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Dtdd.Api.Models;
using Jellyfin.Plugin.Dtdd.Services;
using Jellyfin.Plugin.Dtdd.Providers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dtdd.Api;

/// <summary>
/// REST surface for the plugin. Mounted under <c>/DTDD/</c>. All endpoints require
/// an authenticated Jellyfin user; the prefs endpoints resolve the calling user from
/// the request's "Jellyfin-UserId" claim.
/// </summary>
[Authorize]
[ApiController]
[Route("DTDD")]
[Produces(MediaTypeNames.Application.Json)]
public class DtddController : ControllerBase
{
    // Mirrors Jellyfin.Api.Constants.InternalClaimTypes.UserId (not in MediaBrowser.Controller package).
    private const string UserIdClaimType = "Jellyfin-UserId";

    /// <summary>Cap on how long an inline /topics seed is allowed to run.</summary>
    private static readonly TimeSpan InlineSeedTimeout = TimeSpan.FromSeconds(20);

    private readonly DtddClient _dtdd;
    private readonly WarningCache _cache;
    private readonly UserPreferenceStore _prefsStore;
    private readonly TopicSeeder _seeder;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DtddController> _logger;

    public DtddController(
        DtddClient dtdd,
        WarningCache cache,
        UserPreferenceStore prefsStore,
        TopicSeeder seeder,
        ILibraryManager libraryManager,
        ILogger<DtddController> logger)
    {
        _dtdd = dtdd;
        _cache = cache;
        _prefsStore = prefsStore;
        _seeder = seeder;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Compute the per-user Safe / Not Safe verdict for a single Jellyfin item.
    /// </summary>
    [HttpGet("safety/{itemId}")]
    public async Task<ActionResult<SafetyResponse>> GetSafety(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCallingUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var prefs = await _prefsStore.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        if (prefs is null)
        {
            // User has never opened the picker → CTA state.
            return new SafetyResponse { State = SafetyStates.NotConfigured };
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogDebug("Safety lookup: item {ItemId} not found", itemId);
            return new SafetyResponse
            {
                State = SafetyStates.Unknown,
                ConfiguredPhobiaCount = prefs.PhobiaTopicIds.Count,
            };
        }

        var tmdbStr = item.ProviderIds.TryGetValue("Tmdb", out var tv) ? tv : null;
        var imdbStr = item.ProviderIds.TryGetValue("Imdb", out var iv) ? iv : null;

        int? tmdbId = int.TryParse(tmdbStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        // Cache lookup is keyed by tmdbId only (mission spec). Items without TMDB
        // metadata still get a live fetch but skip caching.
        var details = tmdbId.HasValue ? _cache.Get(tmdbId.Value) : null;

        if (details is null)
        {
            if (!string.IsNullOrWhiteSpace(imdbStr))
            {
                details = await _dtdd.GetByImdbAsync(imdbStr, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(item.Name))
            {
                var typeId = item is Series ? DtddConstants.ItemTypeSeries : DtddConstants.ItemTypeMovie;
                details = await _dtdd.GetByTitleAsync(item.Name, item.ProductionYear, typeId, cancellationToken).ConfigureAwait(false);
            }

            if (details is not null && tmdbId.HasValue)
            {
                _cache.Put(tmdbId.Value, details);
            }
        }

        if (details is not null)
        {
            // Side-effect: surface the badge by writing the Dtdd ProviderId.
            // See DtddProviderIdBackfill for the v1 shortcut rationale and the
            // v1.x migration to a true IRemoteMetadataProvider.
            await DtddProviderIdBackfill.TryBackfillAsync(item, details.Item.Id, _logger, cancellationToken).ConfigureAwait(false);
        }

        if (details is null)
        {
            return new SafetyResponse
            {
                State = SafetyStates.Unknown,
                ConfiguredPhobiaCount = prefs.PhobiaTopicIds.Count,
            };
        }

        var phobiaSet = new HashSet<int>(prefs.PhobiaTopicIds);
        var matches = details.TopicItemStats
            .Where(s => phobiaSet.Contains(s.TopicId) && s.YesSum >= 1)
            .Select(s => new MatchedPhobia
            {
                TopicId = s.TopicId,
                Name = s.Topic?.Name ?? string.Empty,
                YesSum = s.YesSum,
                NoSum = s.NoSum,
                TopComment = s.Comment,
                NumComments = s.NumComments,
            })
            .ToList();

        return new SafetyResponse
        {
            State = matches.Count > 0 ? SafetyStates.NotSafe : SafetyStates.Safe,
            MatchedPhobias = matches,
            ConfiguredPhobiaCount = prefs.PhobiaTopicIds.Count,
            DtddItemId = details.Item.Id == 0 ? null : details.Item.Id,
        };
    }

    /// <summary>
    /// Returns the cumulative topic catalog. When the catalog is empty and an
    /// API key IS configured, this endpoint runs the seeder inline (bounded by
    /// <see cref="InlineSeedTimeout"/>) so the very first picker open populates
    /// without the user having to manually run the scheduled task.
    ///
    /// <para>
    /// Why inline here: the startup hosted service runs once at boot, and on
    /// fresh installs the API key is set AFTER startup — so the boot seed
    /// no-ops. Lazy seed on first /topics request handles that case.
    /// </para>
    /// </summary>
    [HttpGet("topics")]
    public async Task<ActionResult<List<DtddTopic>>> GetTopics(CancellationToken cancellationToken = default)
    {
        var topics = _cache.GetTopics();
        if (topics.Count > 0)
        {
            return topics;
        }

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            return topics; // empty — picker will show its empty-state hint
        }

        _logger.LogInformation("Topic catalog empty on /topics request; running inline seed (bounded at {Seconds}s)", InlineSeedTimeout.TotalSeconds);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(InlineSeedTimeout);

        try
        {
            await _seeder.RunAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Inline seed hit the {Seconds}s budget; returning partial cache state", InlineSeedTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inline topic seed during /topics request failed");
        }

        return _cache.GetTopics();
    }

    /// <summary>
    /// Returns the calling user's prefs, or an empty list if no record exists.
    /// Use <c>GET /DTDD/safety/{id}</c>'s <c>state == "not_configured"</c> for the
    /// has-a-record? signal — this endpoint always returns a shape.
    /// </summary>
    [HttpGet("prefs")]
    public async Task<ActionResult<UserPrefs>> GetPrefs(CancellationToken cancellationToken = default)
    {
        var userId = GetCallingUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var prefs = await _prefsStore.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        return prefs ?? new UserPrefs();
    }

    /// <summary>
    /// Overwrite the calling user's phobia list. Empty list is allowed but the picker
    /// in Phase 3 confirms before saving zero.
    ///
    /// <para>
    /// We deliberately do NOT validate phobiaTopicIds against the cumulative topics
    /// table. The picker UI sources its IDs from <c>GET /DTDD/topics</c>, so the only
    /// way to send a "bogus" ID is direct API access — and even then, an unrecognised
    /// ID just never matches anything in the safety lookup (harmless, not exploitable).
    /// Validating against the topics table would create a chicken-and-egg problem:
    /// a topic that's valid at DTDD but hasn't yet been observed in any cached
    /// /media/{id} response would be rejected. A length cap below bounds memory
    /// from a malicious or buggy client without false-negatives on legitimate IDs.
    /// </para>
    /// </summary>
    [HttpPut("prefs")]
    public async Task<ActionResult> PutPrefs(
        [FromBody] UserPrefs prefs,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCallingUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (prefs is null)
        {
            return BadRequest();
        }

        prefs.PhobiaTopicIds ??= new List<int>();

        // Length cap — DTDD has ~300 topics across all categories; 500 is generous.
        const int MaxPhobiaTopicIds = 500;
        if (prefs.PhobiaTopicIds.Count > MaxPhobiaTopicIds)
        {
            return BadRequest($"phobiaTopicIds exceeds maximum of {MaxPhobiaTopicIds}.");
        }

        await _prefsStore.PutAsync(userId, prefs, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private Guid GetCallingUserId()
    {
        var claim = User.FindFirst(UserIdClaimType)?.Value;
        return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
    }
}
