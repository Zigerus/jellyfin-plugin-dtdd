using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Dtdd.Api.Models;

/// <summary>
/// Lightweight media item record returned by /dddsearch and embedded in /media/{id} responses.
/// Only the fields used by v1 are mapped.
/// </summary>
public class DtddMediaItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("cleanName")]
    public string? CleanName { get; set; }

    [JsonPropertyName("releaseYear")]
    public string? ReleaseYear { get; set; }

    [JsonPropertyName("itemTypeId")]
    public int ItemTypeId { get; set; }
}
