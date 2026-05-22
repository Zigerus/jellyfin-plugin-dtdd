using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Dtdd.Api.Models;

/// <summary>
/// A DoesTheDogDie topic category (e.g., "Phobias", "Violence"). Groups topics for display.
/// </summary>
public class DtddTopicCategory
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
