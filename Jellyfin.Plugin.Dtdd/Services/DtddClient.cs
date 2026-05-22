using System;
using System.Collections.Generic;
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
/// HTTP client wrapper for doesthedogdie.com. Reads API key and base URL from
/// the live plugin configuration each call so config edits take effect without
/// restarting the Jellyfin server. Returns null on any failure (search miss,
/// HTML for invalid ID, exhausted retries) — callers treat that as "unknown".
/// </summary>
public class DtddClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(2);
    private const int MaxAttempts = 2;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DtddClient> _logger;

    public DtddClient(IHttpClientFactory httpClientFactory, ILogger<DtddClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Look up a media item by IMDB ID (tt-prefixed). Returns the full /media/{id}
    /// payload of the first match, or null if no match or on failure.
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
    /// Title+year+type fallback when IMDB ID isn't available on the Jellyfin item.
    /// Scores candidates by name match, year proximity, and type. Returns the best
    /// match's full details, or null.
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
    /// Direct fetch by known DTDD media ID. Useful for cache refresh when we
    /// already mapped tmdbId → dtddId in a prior call.
    /// </summary>
    public async Task<DtddMediaDetails?> GetByDtddIdAsync(int dtddId, CancellationToken cancellationToken = default)
    {
        return await FetchJsonAsync<DtddMediaDetails>($"/media/{dtddId}", cancellationToken).ConfigureAwait(false);
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

                if (IsTransient(resp.StatusCode) && attempt < MaxAttempts)
                {
                    var delay = resp.Headers.RetryAfter?.Delta ?? TransientRetryDelay;
                    _logger.LogDebug("DTDD transient {Status} on {Path}; retry in {Seconds}s", (int)resp.StatusCode, path, delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogDebug("DTDD non-success {Status} on {Path}", (int)resp.StatusCode, path);
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
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                _logger.LogDebug(ex, "DTDD HTTP exception on {Path}; will retry", path);
                await Task.Delay(TransientRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "DTDD HTTP error on {Path}", path);
                return null;
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogDebug(ex, "DTDD request timed out on {Path}", path);
                return null;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "DTDD JSON parse error on {Path}", path);
                return null;
            }
        }

        return null;
    }

    private static bool IsTransient(HttpStatusCode status)
    {
        if (status == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        var code = (int)status;
        return code >= 500 && code < 600;
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
                int.TryParse(item.ReleaseYear, out var itemYear))
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
