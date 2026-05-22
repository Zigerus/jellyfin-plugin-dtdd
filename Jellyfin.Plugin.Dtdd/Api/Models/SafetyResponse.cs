using System.Collections.Generic;

namespace Jellyfin.Plugin.Dtdd.Api.Models;

/// <summary>
/// Verdict returned by <c>GET /DTDD/safety/{jellyfinItemId}</c>. The v1 badge
/// renders the <see cref="State"/> field; v2's "Why?" modal will read
/// <see cref="MatchedPhobias"/> for the topComment / numComments preview without
/// any change to this contract.
/// </summary>
public class SafetyResponse
{
    /// <summary>
    /// One of: <c>"safe"</c>, <c>"not_safe"</c>, <c>"unknown"</c>, <c>"not_configured"</c>.
    /// </summary>
    public string State { get; set; } = SafetyStates.Unknown;

    /// <summary>
    /// The phobia topics that matched (TopicId &#8712; user's phobiaTopicIds AND yesSum &#8805; 1).
    /// Empty when <see cref="State"/> is anything other than <c>not_safe</c>.
    /// </summary>
    public List<MatchedPhobia> MatchedPhobias { get; set; } = new();

    /// <summary>
    /// How many phobia topics the calling user has configured. Useful for the picker UI
    /// (e.g., showing "7 topics tracked").
    /// </summary>
    public int ConfiguredPhobiaCount { get; set; }

    /// <summary>
    /// DTDD's own media ID for the resolved title, when known. Lets the client deep-link
    /// to the DoesTheDogDie page. Null when state is <c>unknown</c> or <c>not_configured</c>.
    /// </summary>
    public int? DtddItemId { get; set; }
}

/// <summary>
/// One matched phobia topic for a media item.
/// </summary>
public class MatchedPhobia
{
    public int TopicId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int YesSum { get; set; }

    public int NoSum { get; set; }

    public string? TopComment { get; set; }

    public int NumComments { get; set; }
}

/// <summary>
/// Canonical string values for <see cref="SafetyResponse.State"/>.
/// </summary>
public static class SafetyStates
{
    public const string Safe = "safe";
    public const string NotSafe = "not_safe";
    public const string Unknown = "unknown";
    public const string NotConfigured = "not_configured";
}
