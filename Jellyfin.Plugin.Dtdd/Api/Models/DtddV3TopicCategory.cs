using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Dtdd.Api.Models;

/// <summary>
/// One entry of <c>GET /api/v3/topiccategories</c>. The seeder joins these onto
/// topics by <c>topicCategoryId</c> so the picker can group by category name.
/// (v3 also serves topicSuperCategoryId — unused.)
/// </summary>
public class DtddV3TopicCategory
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
