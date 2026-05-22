using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Dtdd.Providers;

/// <summary>
/// Surfaces the DoesTheDogDie badge in Jellyfin's external-IDs row for Movie items.
/// Jellyfin auto-discovers IExternalId implementations via assembly scanning; no DI
/// registration is required.
///
/// URL pattern verified against production (2026-05-21):
///   curl -I https://www.doesthedogdie.com/media/10752 → 200
///   curl -I https://www.doesthedogdie.com/media/123   → 404
/// No slug-redirect on valid IDs.
/// </summary>
public class DtddMovieExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => DtddConstants.ProviderName;

    /// <inheritdoc />
    public string Key => DtddConstants.ProviderId;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Movie;

    /// <inheritdoc />
    public string? UrlFormatString => "https://www.doesthedogdie.com/media/{0}";

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Movie;
}
