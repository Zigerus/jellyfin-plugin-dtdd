using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dtdd.Services;

/// <summary>
/// Per-user phobia preferences. Persisted as JSON next to the cache DB.
/// Schema on disk: { "&lt;userGuid&gt;": { "phobiaTopicIds": [int, ...] } }.
///
/// "Has no record yet" (Get returns null) is the not_configured state in
/// the safety state machine. An explicit save with an empty list is a
/// distinct state (record exists, list is empty) — not_configured does
/// not apply there. The picker UX in Phase 3 should never save an empty
/// list without prompting.
/// </summary>
public class UserPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly ILogger<UserPreferenceStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public UserPreferenceStore(IApplicationPaths applicationPaths, ILogger<UserPreferenceStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(applicationPaths.DataPath, "Jellyfin.Plugin.Dtdd");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "prefs.json");
    }

    /// <summary>
    /// Returns the prefs record for the given user, or null if no record exists yet
    /// (the "not_configured" state).
    /// </summary>
    public async Task<UserPrefs?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        all.TryGetValue(KeyFor(userId), out var prefs);
        return prefs;
    }

    /// <summary>
    /// Returns every user's prefs. Used by the prefetch task to decide which
    /// items deserve aggressive priority (e.g., items containing topics that
    /// matter to at least one user).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, UserPrefs>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Read-modify-write: load, replace the entry for this user, atomic-rename the file.
    /// </summary>
    public async Task PutAsync(Guid userId, UserPrefs prefs, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = new Dictionary<string, UserPrefs>(await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false));
            all[KeyFor(userId)] = prefs;
            await WriteUnlockedAsync(all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, UserPrefs>> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, UserPrefs>();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, UserPrefs>>(stream, JsonOpts, cancellationToken).ConfigureAwait(false);
            return loaded ?? new Dictionary<string, UserPrefs>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "User preferences file is corrupt at {Path}; treating as empty (will be overwritten on next save)", _path);
            return new Dictionary<string, UserPrefs>();
        }
    }

    private async Task WriteUnlockedAsync(Dictionary<string, UserPrefs> data, CancellationToken cancellationToken)
    {
        var tmp = _path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, data, JsonOpts, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tmp, _path, overwrite: true);
    }

    private static string KeyFor(Guid userId) => userId.ToString("N");
}

/// <summary>
/// The persisted prefs record for one Jellyfin user.
/// </summary>
public class UserPrefs
{
    [JsonPropertyName("phobiaTopicIds")]
    public List<int> PhobiaTopicIds { get; set; } = new();
}
