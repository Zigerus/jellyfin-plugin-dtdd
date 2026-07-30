using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Dtdd.Api.Models;

/// <summary>
/// One entry of the bare JSON array returned by <c>GET /api/v3/items</c>
/// (<c>?tmdb=</c> / <c>?imdb=</c> / <c>?name=&amp;releaseYear=</c> / <c>?q=</c>).
/// Only the fields the plugin uses are mapped. Unlike v1's /dddsearch items,
/// v3 entries carry no <c>cleanName</c>.
/// </summary>
public class DtddV3Item
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <remarks>Number on ?tmdb= lookups, string on ?q= searches — see <see cref="FlexibleStringConverter"/>.</remarks>
    [JsonPropertyName("releaseYear")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ReleaseYear { get; set; }

    [JsonPropertyName("itemTypeId")]
    public int ItemTypeId { get; set; }

    [JsonPropertyName("tmdbId")]
    public int? TmdbId { get; set; }

    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }
}
