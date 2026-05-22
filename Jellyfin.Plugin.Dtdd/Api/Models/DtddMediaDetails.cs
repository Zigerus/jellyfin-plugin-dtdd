using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Dtdd.Api.Models;

/// <summary>
/// Response shape from /media/{dtddId}: the item plus all topic vote stats.
/// This is what we cache in WarningCache as the JSON blob for tmdbId.
/// </summary>
public class DtddMediaDetails
{
    [JsonPropertyName("item")]
    public DtddMediaItem Item { get; set; } = new();

    [JsonPropertyName("topicItemStats")]
    public List<DtddTopicItemStat> TopicItemStats { get; set; } = new();
}
