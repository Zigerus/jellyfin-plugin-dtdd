using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Dtdd.Api.Models;

/// <summary>
/// A DoesTheDogDie topic (a.k.a. trigger). The phobia picker selects from these by ID.
/// </summary>
public class DtddTopic
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <remarks>DTDD returns int 0/1 here in /dddsearch but real bool in /media/{id}.</remarks>
    [JsonPropertyName("isSpoiler")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool IsSpoiler { get; set; }

    /// <remarks>Same int-or-bool inconsistency as IsSpoiler.</remarks>
    [JsonPropertyName("isSensitive")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool IsSensitive { get; set; }

    /// <summary>Category ID — JSON key uses PascalCase in DTDD's response.</summary>
    [JsonPropertyName("TopicCategoryId")]
    public int? TopicCategoryId { get; set; }

    [JsonPropertyName("TopicCategory")]
    public DtddTopicCategory? TopicCategory { get; set; }
}
