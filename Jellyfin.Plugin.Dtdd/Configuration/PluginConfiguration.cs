using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Dtdd.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string ApiKey { get; set; } = string.Empty;

    public int CacheTtlDays { get; set; } = 14;

    public bool PrefetchEnabled { get; set; }

    public string DtddBaseUrl { get; set; } = "https://www.doesthedogdie.com";
}
