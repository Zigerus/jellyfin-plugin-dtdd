namespace Jellyfin.Plugin.Dtdd;

/// <summary>
/// Constants shared across the plugin. Kept out of PluginConfiguration since
/// these don't vary per install.
/// </summary>
internal static class DtddConstants
{
    /// <summary>Named HttpClient registered in PluginServiceRegistrator.</summary>
    public const string HttpClientName = "Dtdd";

    /// <summary>DoesTheDogDie item type ID for Movie (per /dddsearch responses).</summary>
    public const int ItemTypeMovie = 15;

    /// <summary>DoesTheDogDie item type ID for TV Show (per /dddsearch responses).</summary>
    public const int ItemTypeSeries = 16;

    /// <summary>Provider key used in BaseItem.ProviderIds for the DTDD media ID.</summary>
    public const string ProviderId = "Dtdd";

    /// <summary>Display name surfaced in Jellyfin's external-IDs badge row.</summary>
    public const string ProviderName = "DoesTheDogDie";
}
