using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Dtdd.Api.Models;

/// <summary>
/// One entry of <c>GET /api/v3/topics</c>. Only the fields the plugin uses are
/// mapped — v3 also serves notName / keywords / listName / minimalName /
/// altTopicCategoryId, none of which the picker or safety path needs. Note v3
/// uses camelCase <c>topicCategoryId</c> (v1 media payloads used PascalCase)
/// and carries no isSpoiler / isSensitive flags.
/// </summary>
public class DtddV3Topic
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("doesName")]
    public string? DoesName { get; set; }

    [JsonPropertyName("topicCategoryId")]
    public int? TopicCategoryId { get; set; }
}
