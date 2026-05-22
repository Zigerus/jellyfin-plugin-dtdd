using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dtdd.Services;

/// <summary>
/// Orchestrates the DoesTheDogDie topic-catalog seed. Hits a deliberately broad
/// set of <c>/dddsearch?q=&lt;term&gt;</c> queries and accumulates the topics each
/// response returns. Idempotent (delegates to <see cref="WarningCache.SeedTopics"/>
/// which uses <c>INSERT … ON CONFLICT DO NOTHING</c>).
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
/// <b>Seed query rationale</b> — the queries below were chosen to span DTDD's
/// topic categories (animal harm, violence, phobia/horror, body horror, gore).
/// Each query returns a <c>topics[]</c> field in the search response containing
/// the topics that match — five generic terms cover most of the catalog without
/// pulling per-title /media/{id} payloads. Honors the same DtddClient retry
/// policy (5 attempts, exponential backoff, negative-cache on exhaustion).
/// </para>
/// </summary>
public class TopicSeeder
{
    /// <summary>
    /// Canonical seed queries. Each is a single broad term that DTDD's search
    /// will match against many titles and topics. The intersection of all five
    /// covers most of the topic catalog.
    /// </summary>
    private static readonly string[] SeedQueries =
    {
        "death",     // covers death/dying topic family
        "violence",  // covers assault, weapons, fighting topics
        "fear",      // covers phobias / horror tropes
        "spider",    // covers specific phobia surface — different from "fear"
        "blood",     // covers body horror / gore topics
    };

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
        _logger.LogInformation("DTDD topic-seed: starting refresh ({Count} seed queries)", SeedQueries.Length);

        var totalObserved = 0;
        var succeeded = 0;

        foreach (var query in SeedQueries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await _dtdd.SearchByQueryAsync(query, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                _logger.LogDebug("DTDD topic-seed: query {Query} returned no response (retries exhausted or empty result)", query);
                continue;
            }

            _cache.SeedTopics(response.Topics);
            totalObserved += response.Topics.Count;
            succeeded++;
        }

        _logger.LogInformation(
            "DTDD topic-seed complete: {Succeeded}/{Total} queries returned data, {Observed} topic mentions inserted (duplicates ignored)",
            succeeded, SeedQueries.Length, totalObserved);
    }
}
