using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Dtdd.Api.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dtdd.Services;

/// <summary>
/// Seeds the DoesTheDogDie topic catalog from API v3: one
/// <c>/api/v3/topiccategories</c> call for the category-name map and one
/// <c>/api/v3/topics</c> call for the full catalog, joined by
/// <c>topicCategoryId</c> into the <see cref="DtddTopic"/> shape the picker
/// expects (nested <c>TopicCategory.name</c> drives the picker's grouping).
///
/// <para>
/// This replaces the v1 approach of accumulating topics from five fuzzy
/// <c>/dddsearch</c> queries — v1 had no topics endpoint, so the catalog could
/// only grow organically. v3 serves the complete catalog in one call, so a
/// single seed is authoritative.
/// </para>
///
/// <para>
/// <b>Trigger points</b> (both call the same <see cref="RunAsync"/>):
/// </para>
/// <list type="number">
///   <item>First-load startup — fired by TopicSeederStartupService (IHostedService)
///         when the topics table is empty on plugin load.</item>
///   <item>Scheduled refresh — fired by SeedTopicsRefreshTask, default cadence
///         weekly Sunday 03:00. Admin can reschedule or disable in the Jellyfin
///         task UI.</item>
/// </list>
///
/// <para>
/// Idempotent: delegates to <see cref="WarningCache.SeedTopics"/> which uses
/// <c>INSERT … ON CONFLICT DO NOTHING</c>. Existing rows (including v1-era
/// ones) are never rewritten; only newly-published topics are added.
/// </para>
/// </summary>
public class TopicSeeder
{
    private readonly DtddClient _dtdd;
    private readonly WarningCache _cache;
    private readonly ILogger<TopicSeeder> _logger;

    public TopicSeeder(DtddClient dtdd, WarningCache cache, ILogger<TopicSeeder> logger)
    {
        _dtdd = dtdd;
        _cache = cache;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("DTDD topic-seed: fetching v3 topic catalog");

        var categories = await _dtdd.GetTopicCategoriesAsync(cancellationToken).ConfigureAwait(false);
        var categoryNames = (categories ?? new List<DtddV3TopicCategory>())
            .ToDictionary(c => c.Id, c => c.Name);

        cancellationToken.ThrowIfCancellationRequested();

        var topics = await _dtdd.GetTopicsAsync(cancellationToken).ConfigureAwait(false);
        if (topics is null || topics.Count == 0)
        {
            _logger.LogWarning("DTDD topic-seed: /api/v3/topics returned nothing (retries exhausted or empty); catalog unchanged");
            return;
        }

        var mapped = topics.Select(t => new DtddTopic
        {
            Id = t.Id,
            Name = t.Name,
            Description = t.Description,
            TopicCategoryId = t.TopicCategoryId,
            TopicCategory = t.TopicCategoryId is int catId && categoryNames.TryGetValue(catId, out var catName)
                ? new DtddTopicCategory { Id = catId, Name = catName }
                : null,
        });

        _cache.SeedTopics(mapped);

        _logger.LogInformation(
            "DTDD topic-seed complete: {Topics} topics across {Categories} categories observed (existing rows untouched)",
            topics.Count, categoryNames.Count);
    }
}
