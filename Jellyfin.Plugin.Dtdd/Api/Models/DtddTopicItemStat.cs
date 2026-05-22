using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Dtdd.Api.Models;

/// <summary>
/// Vote and comment statistics for one topic on one media item.
/// Cached in full so the v2 "Why?" modal can be built without changing the cache shape.
/// </summary>
public class DtddTopicItemStat
{
    [JsonPropertyName("topicItemId")]
    public int TopicItemId { get; set; }

    /// <summary>Topic ID — JSON key uses PascalCase in DTDD's response.</summary>
    [JsonPropertyName("TopicId")]
    public int TopicId { get; set; }

    [JsonPropertyName("yesSum")]
    public int YesSum { get; set; }

    [JsonPropertyName("noSum")]
    public int NoSum { get; set; }

    [JsonPropertyName("numComments")]
    public int NumComments { get; set; }

    /// <summary>Top-voted user comment (may be null when no comments exist).</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("topic")]
    public DtddTopic? Topic { get; set; }
}
