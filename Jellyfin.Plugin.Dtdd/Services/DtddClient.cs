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
    /// Look up by IMDB ID (tt-prefixed). Returns the /media/{id} payload of the
    /// first match, or null on miss / exhausted retries.
    /// </summary>
    public async Task<DtddMediaDetails?> GetByImdbAsync(string imdbId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        var search = await FetchJsonAsync<DtddSearchResponse>(
            $"/dddsearch?imdb={Uri.EscapeDataString(imdbId)}",
            cancellationToken).ConfigureAwait(false);

        var first = search?.Items.FirstOrDefault();
        if (first is null)
        {
            _logger.LogDebug("No DTDD match for IMDB {ImdbId}", imdbId);
            return null;
        }

        return await GetByDtddIdAsync(first.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Title+year+type fallback when IMDB ID isn't available. Scores candidates
    /// and returns the best match's full details, or null.
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

        var search = await FetchJsonAsync<DtddSearchResponse>(
            $"/dddsearch?q={Uri.EscapeDataString(title)}",
            cancellationToken).ConfigureAwait(false);

        var best = FindBestMatch(search?.Items, title, year, itemTypeId);
        if (best is null)
        {
            _logger.LogDebug("No DTDD match for title {Title} (year={Year} type={TypeId})", title, year, itemTypeId);
            return null;
        }

        return await GetByDtddIdAsync(best.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Direct /media/{id} fetch — used by cache refresh and the prefetch task
    /// when we already know the DTDD ID from a prior lookup.
    /// </summary>
    public async Task<DtddMediaDetails?> GetByDtddIdAsync(int dtddId, CancellationToken cancellationToken = default)
    {
        return await FetchJsonAsync<DtddMediaDetails>($"/media/{dtddId}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Raw search by free-text query, returning both <c>items</c> and the
    /// <c>topics</c> field. The TopicSeeder uses this to broaden the topic
    /// catalog via deliberate canonical queries.
    /// </summary>
    public async Task<DtddSearchResponse?> SearchByQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        return await FetchJsonAsync<DtddSearchResponse>(
            $"/dddsearch?q={Uri.EscapeDataString(query)}",
            cancellationToken).ConfigureAwait(false);
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
                req.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.Dtdd/0.1 (+https://github.com/Zigerus/jellyfin-plugin-dtdd)");

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
    /// </summary>
    internal static DtddMediaItem? FindBestMatch(
        IReadOnlyList<DtddMediaItem>? items,
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

        DtddMediaItem? best = null;
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
            else if (!string.IsNullOrEmpty(item.CleanName) &&
                     string.Equals(item.CleanName, normalized, StringComparison.OrdinalIgnoreCase))
            {
                score += 70;
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
