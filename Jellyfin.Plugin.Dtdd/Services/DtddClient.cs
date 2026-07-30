using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Dtdd.Api.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dtdd.Services;

/// <summary>
/// HTTP client wrapper for doesthedogdie.com.
///
/// <para>
/// <b>Endpoint mix (v0.2)</b> — lookups and the topic catalog use API v3
/// (<c>/api/v3/items</c>, <c>/api/v3/topics</c>, <c>/api/v3/topiccategories</c>);
/// per-item detail deliberately stays on v1 <c>/media/{id}</c> because only v1
/// carries the per-topic top comment (see <see cref="GetByDtddIdAsync"/>).
/// Free-tier budget: 30 requests/min, 5,000/month — callers that loop
/// (prefetch, warmer) pace themselves accordingly.
/// </para>
///
/// <para>
/// <b>Retry policy</b> — bounded at <see cref="MaxAttempts"/> = 5 (initial + 4 retries).
/// Backoff is exponential with ±25% jitter. Two schedules:
/// </para>
/// <list type="bullet">
///   <item>5xx / network: base 0, 500ms, 1s, 2s, 4s</item>
///   <item>429: respects <c>Retry-After</c> header if present, otherwise 5s, 15s, 45s, 45s</item>
/// </list>
/// <para>
/// Retries fire on 5xx, network errors, timeouts, and 429. Permanent 4xx
/// (404 = not in DTDD, 401 = bad key, 403 = forbidden) return null
/// immediately. JsonException is permanent too — broken response, no point retrying.
/// </para>
///
/// <para>
/// <b>Negative cache</b> — after the retry budget is exhausted, the URL
/// is recorded in an in-memory negative cache for
/// <see cref="NegativeCacheTtlMinutes"/> minutes. Subsequent requests for
/// the same URL inside that window return null without contacting DTDD.
/// In-memory only; restarts forget. Keyed by absolute URL so the safety,
/// search, and media-id paths are tracked independently.
/// </para>
///
/// <para>
/// <b>Logging</b> — INFO on 429 (so rate-limit pressure is visible in
/// Jellyfin logs), WARN when retries exhaust (URL + last error), DEBUG
/// per attempt.
/// </para>
/// </summary>
public class DtddClient
{
    private const int MaxAttempts = 5;
    private const int NegativeCacheTtlMinutes = 5;

    private static readonly TimeSpan[] BackoffBase =
    {
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    };

    private static readonly TimeSpan[] Backoff429 =
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(45),
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DtddClient> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _negativeCache = new();

    public DtddClient(IHttpClientFactory httpClientFactory, ILogger<DtddClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Resolution ladder used by every lookup path (safety endpoint, prefetch
    /// task, library warmer): TMDB exact → IMDB exact → title+year → fuzzy
    /// title with score threshold. Each rung falls through on miss, so an item
    /// genuinely absent from DTDD costs up to three search calls — bounded in
    /// practice by the negative cache. Returns the v1 /media/{id} payload of
    /// the winner (see <see cref="GetByDtddIdAsync"/> for why detail stays v1).
    /// </summary>
    public async Task<DtddMediaDetails?> ResolveAsync(
        int? tmdbId,
        string? imdbId,
        string? title,
        int? year,
        int itemTypeId,
        CancellationToken cancellationToken = default)
    {
        if (tmdbId.HasValue)
        {
            var byTmdb = await GetByTmdbAsync(tmdbId.Value, cancellationToken).ConfigureAwait(false);
            if (byTmdb is not null)
            {
                return byTmdb;
            }
        }

        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            var byImdb = await GetByImdbAsync(imdbId, cancellationToken).ConfigureAwait(false);
            if (byImdb is not null)
            {
                return byImdb;
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            return await GetByTitleAsync(title, year, itemTypeId, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Exact lookup by TMDB ID via <c>/api/v3/items?tmdb=</c>. The strongest
    /// rung of the ladder: Jellyfin items are cache-keyed by TMDB ID, so this
    /// makes lookup key == cache key with no fuzzy matching at all.
    /// </summary>
    public async Task<DtddMediaDetails?> GetByTmdbAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        var results = await FetchJsonAsync<List<DtddV3Item>>(
            $"/api/v3/items?tmdb={tmdbId}",
            cancellationToken).ConfigureAwait(false);

        var first = results?.FirstOrDefault();
        if (first is null)
        {
            _logger.LogDebug("No DTDD match for TMDB {TmdbId}", tmdbId);
            return null;
        }

        return await GetByDtddIdAsync(first.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Exact lookup by IMDB ID (tt-prefixed) via <c>/api/v3/items?imdb=</c>.
    /// </summary>
    public async Task<DtddMediaDetails?> GetByImdbAsync(string imdbId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        var results = await FetchJsonAsync<List<DtddV3Item>>(
            $"/api/v3/items?imdb={Uri.EscapeDataString(imdbId)}",
            cancellationToken).ConfigureAwait(false);

        var first = results?.FirstOrDefault();
        if (first is null)
        {
            _logger.LogDebug("No DTDD match for IMDB {ImdbId}", imdbId);
            return null;
        }

        return await GetByDtddIdAsync(first.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Title fallback when no provider ID resolves: exact <c>?name=&amp;releaseYear=</c>
    /// first (when a year is known), then fuzzy <c>?q=</c> filtered through
    /// <see cref="FindBestMatch"/> with the same confidence threshold as v1.
    /// </summary>
    public async Task<DtddMediaDetails?> GetByTitleAsync(
        string title,
        int? year,
        int itemTypeId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        if (year.HasValue)
        {
            var exact = await FetchJsonAsync<List<DtddV3Item>>(
                $"/api/v3/items?name={Uri.EscapeDataString(title)}&releaseYear={year.Value}",
                cancellationToken).ConfigureAwait(false);

            var exactHit = exact?.FirstOrDefault(i => i.ItemTypeId == itemTypeId);
            if (exactHit is not null)
            {
                return await GetByDtddIdAsync(exactHit.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        var fuzzy = await FetchJsonAsync<List<DtddV3Item>>(
            $"/api/v3/items?q={Uri.EscapeDataString(title)}",
            cancellationToken).ConfigureAwait(false);

        var best = FindBestMatch(fuzzy, title, year, itemTypeId);
        if (best is null)
        {
            _logger.LogDebug("No DTDD match for title {Title} (year={Year} type={TypeId})", title, year, itemTypeId);
            return null;
        }

        return await GetByDtddIdAsync(best.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Direct /media/{id} fetch — used once a lookup rung has resolved the DTDD ID.
    ///
    /// <para>
    /// Detail deliberately stays on the v1 endpoint: v3's
    /// <c>/api/v3/items/{id}</c> topicItemStats carry only vote sums and
    /// counts, while v1's payload also carries the per-topic top comment and
    /// nested topic object that <c>SafetyResponse.topComment</c> and the topic
    /// catalog accumulate from (verified against live payloads 2026-07-30).
    /// Revisit if v3 ever adds the comment field.
    /// </para>
    /// </summary>
    public async Task<DtddMediaDetails?> GetByDtddIdAsync(int dtddId, CancellationToken cancellationToken = default)
    {
        return await FetchJsonAsync<DtddMediaDetails>($"/media/{dtddId}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Full topic catalog via <c>/api/v3/topics</c> — one call replaces the v1
    /// seeder's five fuzzy /dddsearch queries.
    /// </summary>
    public async Task<List<DtddV3Topic>?> GetTopicsAsync(CancellationToken cancellationToken = default)
    {
        return await FetchJsonAsync<List<DtddV3Topic>>("/api/v3/topics", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Topic categories via <c>/api/v3/topiccategories</c>, joined onto topics
    /// by the seeder so the picker can group by category name.
    /// </summary>
    public async Task<List<DtddV3TopicCategory>?> GetTopicCategoriesAsync(CancellationToken cancellationToken = default)
    {
        return await FetchJsonAsync<List<DtddV3TopicCategory>>("/api/v3/topiccategories", cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> FetchJsonAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            _logger.LogWarning("DoesTheDogDie API key is not configured; skipping fetch of {Path}", path);
            return null;
        }

        var url = $"{cfg.DtddBaseUrl.TrimEnd('/')}{path}";

        if (_negativeCache.TryGetValue(url, out var negativeUntil))
        {
            if (DateTimeOffset.UtcNow < negativeUntil)
            {
                _logger.LogDebug("DTDD negative-cache hit for {Path}; returning null without contacting DTDD", path);
                return null;
            }
            _negativeCache.TryRemove(url, out _);
        }

        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(DtddConstants.HttpClientName);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.TryAddWithoutValidation("X-API-KEY", cfg.ApiKey);
                req.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.Dtdd/0.2 (+https://github.com/Zigerus/jellyfin-plugin-dtdd)");

                using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);

                var status = (int)resp.StatusCode;

                // Permanent 4xx (excluding 429): no retry. 404 = not in DTDD,
                // 401 = bad API key, 403 = forbidden. All permanent.
                if (status >= 400 && status < 500 && resp.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    _logger.LogDebug("DTDD {Status} on {Path}; non-retryable", status, path);
                    return null;
                }

                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _logger.LogInformation("DTDD 429 rate-limit on {Path} (attempt {Attempt}/{Max})", path, attempt, MaxAttempts);
                    if (attempt < MaxAttempts)
                    {
                        var delay = resp.Headers.RetryAfter?.Delta ?? WithJitter(Backoff429[attempt]);
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    lastError = new HttpRequestException($"DTDD 429 exhausted after {attempt} attempts");
                    break;
                }

                if (status >= 500)
                {
                    _logger.LogDebug("DTDD {Status} on {Path} (attempt {Attempt}/{Max})", status, path, attempt, MaxAttempts);
                    if (attempt < MaxAttempts)
                    {
                        await Task.Delay(WithJitter(BackoffBase[attempt]), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    lastError = new HttpRequestException($"DTDD {status} exhausted after {attempt} attempts");
                    break;
                }

                // 2xx — proceed (3xx is not expected; treated as non-success)
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogDebug("DTDD non-success {Status} on {Path}", status, path);
                    return null;
                }

                var contentType = resp.Content.Headers.ContentType?.MediaType;
                if (contentType is not null && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("DTDD returned HTML on {Path} (likely invalid ID)", path);
                    return null;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                _logger.LogDebug(ex, "DTDD HTTP exception on {Path} (attempt {Attempt}/{Max})", path, attempt, MaxAttempts);
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(WithJitter(BackoffBase[attempt]), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                break;
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                // HttpClient timeout — treat as transient.
                lastError = ex;
                _logger.LogDebug("DTDD timeout on {Path} (attempt {Attempt}/{Max})", path, attempt, MaxAttempts);
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(WithJitter(BackoffBase[attempt]), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                break;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "DTDD JSON parse error on {Path} — not retrying", path);
                return null;
            }
        }

        _logger.LogWarning(
            "DTDD retries exhausted for {Path} after {Attempts} attempts; negative-cache for {Mins}min. Last error: {Error}",
            path, MaxAttempts, NegativeCacheTtlMinutes, lastError?.Message ?? "unknown");
        _negativeCache[url] = DateTimeOffset.UtcNow.AddMinutes(NegativeCacheTtlMinutes);
        return null;
    }

    private static TimeSpan WithJitter(TimeSpan baseDelay)
    {
        if (baseDelay <= TimeSpan.Zero)
        {
            return baseDelay;
        }

        // ±25% jitter — factor in [0.75, 1.25)
        var factor = 0.75 + Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
    }

    /// <summary>
    /// Score candidates by (exact-name &gt; normalized-name) + year-proximity + type filter.
    /// Returns null when no candidate clears the minimum confidence (70).
    /// (v3 entries carry no cleanName, so the v1 cleanName clause is gone; a
    /// fuzzy candidate now needs at least a normalized-name match to qualify.)
    /// </summary>
    internal static DtddV3Item? FindBestMatch(
        IReadOnlyList<DtddV3Item>? items,
        string title,
        int? year,
        int itemTypeId)
    {
        if (items is null || items.Count == 0)
        {
            return null;
        }

        var typeFiltered = items.Where(i => i.ItemTypeId == itemTypeId).ToList();
        if (typeFiltered.Count == 0)
        {
            return null;
        }

        var normalized = NormalizeTitle(title);

        DtddV3Item? best = null;
        var bestScore = 0;

        foreach (var item in typeFiltered)
        {
            var score = 0;

            if (string.Equals(item.Name, title, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }
            else if (string.Equals(NormalizeTitle(item.Name), normalized, StringComparison.OrdinalIgnoreCase))
            {
                score += 80;
            }

            if (year.HasValue && !string.IsNullOrEmpty(item.ReleaseYear) &&
                int.TryParse(item.ReleaseYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemYear))
            {
                if (itemYear == year.Value)
                {
                    score += 50;
                }
                else if (Math.Abs(itemYear - year.Value) == 1)
                {
                    score += 25;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        return bestScore >= 70 ? best : null;
    }

    private static string NormalizeTitle(string title)
    {
        var trimmed = title.Trim().ToLowerInvariant();
        foreach (var article in new[] { "the ", "a ", "an " })
        {
            if (trimmed.StartsWith(article, StringComparison.Ordinal))
            {
                return trimmed[article.Length..];
            }
        }

        return trimmed;
    }
}
