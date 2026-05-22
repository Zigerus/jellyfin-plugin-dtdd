namespace Jellyfin.Plugin.Dtdd.Services;

public class WarningCache
{
    // Phase 2: SQLite-backed cache stored in IApplicationPaths plugin data dir.
    // Schema: tmdbId PK, json blob, fetched_at timestamp.
    // Methods: Get(tmdbId), Put(tmdbId, data), ExpireOlderThan(days).
    // Concurrent reads/writes safe (Microsoft.Data.Sqlite + WAL).
    // Also seeds/maintains a topics table (cumulative from /media/{id} responses).
}
