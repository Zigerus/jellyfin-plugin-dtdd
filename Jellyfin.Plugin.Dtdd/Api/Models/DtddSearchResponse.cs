using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Dtdd.Api.Models;

/// <summary>
/// Response shape from /dddsearch?imdb=&lt;ttId&gt; or /dddsearch?q=&lt;title&gt;.
/// </summary>
public class DtddSearchResponse
{
    [JsonPropertyName("items")]
    public List<DtddMediaItem> Items { get; set; } = new();

    [JsonPropertyName("topics")]
    public List<DtddTopic> Topics { get; set; } = new();
}
