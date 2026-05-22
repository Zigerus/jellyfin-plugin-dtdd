using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Dtdd.Configuration;

/// <summary>
/// Persisted plugin configuration. Serialized to/from XML by Jellyfin in the plugin's data directory.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the DoesTheDogDie API key. Set per-instance via the admin config page. Never committed.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cache TTL in days. Entries older than this are refetched on next access.
    /// </summary>
    public int CacheTtlDays { get; set; } = 14;

    /// <summary>
    /// Gets or sets a value indicating whether the prefetch scheduled task is enabled.
    /// Default off — politer to DTDD until the cache is verified stable.
    /// </summary>
    public bool PrefetchEnabled { get; set; }

    /// <summary>
    /// Gets or sets the DoesTheDogDie base URL. Configurable for testing / future API host changes.
    /// </summary>
    public string DtddBaseUrl { get; set; } = "https://www.doesthedogdie.com";
}
