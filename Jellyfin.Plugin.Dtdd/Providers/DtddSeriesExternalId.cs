using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Dtdd.Providers;

/// <summary>
/// Surfaces the DoesTheDogDie badge in Jellyfin's external-IDs row for Series items.
/// Counterpart to <see cref="DtddMovieExternalId"/> — separate provider per media type
/// is the convention (matches IntroSkipper / theflanman's plugin).
/// </summary>
public class DtddSeriesExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => DtddConstants.ProviderName;

    /// <inheritdoc />
    public string Key => DtddConstants.ProviderId;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Series;

    /// <inheritdoc />
    public string? UrlFormatString => "https://www.doesthedogdie.com/media/{0}";

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Series;
}
