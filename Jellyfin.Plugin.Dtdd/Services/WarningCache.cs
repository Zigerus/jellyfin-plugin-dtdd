using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.Dtdd.Api.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dtdd.Services;

/// <summary>
/// SQLite-backed cache for DTDD /media/{id} payloads and the cumulative topic catalog.
/// Two tables: <c>warnings</c> (tmdb_id PK, full JSON, fetched_at) and <c>topics</c>
/// (topic_id PK, JSON, last_seen_at).
///
/// <para>
/// <b>Topic-catalog trigger points</b> (idempotent inserts — ON CONFLICT DO NOTHING):
/// </para>
/// <list type="number">
///   <item><see cref="Put"/> — every successful /media/{id} cache write also accumulates
///         the topics referenced in its TopicItemStats (organic growth as items are
///         viewed).</item>
///   <item><see cref="SeedTopics"/> — called by TopicSeeder from two deliberate
///         triggers: first-load startup (when topics table is empty) and the
///         weekly SeedTopicsRefreshTask scheduled task. Both seed from a curated
///         set of /dddsearch queries; see Services/TopicSeeder.cs for the list.</item>
/// </list>
/// <para>
/// Seeded rows are refreshed on every seeder run (v3 catalog is authoritative
/// since v0.2); rows observed opportunistically from media payloads never
/// overwrite existing data. See <c>SeedTopicInner</c> for the split.
/// </para>
/// </summary>
public class WarningCache
{
    private const string SchemaSql = @"
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS warnings (
            tmdb_id    INTEGER NOT NULL PRIMARY KEY,
            json       TEXT    NOT NULL,
            fetched_at INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_warnings_fetched_at ON warnings(fetched_at);
        CREATE TABLE IF NOT EXISTS topics (
            topic_id     INTEGER NOT NULL PRIMARY KEY,
            json         TEXT    NOT NULL,
            last_seen_at INTEGER NOT NULL
        ) WITHOUT ROWID;";

    private readonly string _connectionString;
    private readonly ILogger<WarningCache> _logger;

    public WarningCache(IApplicationPaths applicationPaths, ILogger<WarningCache> logger)
    {
        _logger = logger;

        var dir = Path.Combine(applicationPaths.DataPath, "Jellyfin.Plugin.Dtdd");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "cache.db");

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        InitializeSchema();
        _logger.LogInformation("DTDD warning cache initialized at {DbPath}", dbPath);
    }

    /// <summary>
    /// Returns the cached DTDD details for the given TMDB ID, or null if absent
    /// or older than the configured CacheTtlDays. Callers treat null as cache-miss
    /// and call <see cref="Put"/> after fetching fresh data.
    /// </summary>
    public DtddMediaDetails? Get(int tmdbId)
    {
        var ttlDays = Plugin.Instance?.Configuration.CacheTtlDays ?? 14;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-ttlDays).ToUnixTimeSeconds();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM warnings WHERE tmdb_id = $id AND fetched_at >= $cutoff;";
        cmd.Parameters.AddWithValue("$id", tmdbId);
        cmd.Parameters.AddWithValue("$cutoff", cutoff);

        if (cmd.ExecuteScalar() is not string json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DtddMediaDetails>(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Cached DTDD JSON corrupt for tmdb {TmdbId}; treating as miss", tmdbId);
            return null;
        }
    }

    /// <summary>
    /// Upserts the cache row and accumulates any topics referenced by the payload.
    /// </summary>
    public void Put(int tmdbId, DtddMediaDetails details)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var json = JsonSerializer.Serialize(details);

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var upsert = conn.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = @"
                INSERT INTO warnings (tmdb_id, json, fetched_at) VALUES ($id, $json, $t)
                ON CONFLICT(tmdb_id) DO UPDATE SET json = excluded.json, fetched_at = excluded.fetched_at;";
            upsert.Parameters.AddWithValue("$id", tmdbId);
            upsert.Parameters.AddWithValue("$json", json);
            upsert.Parameters.AddWithValue("$t", now);
            upsert.ExecuteNonQuery();
        }

        foreach (var stat in details.TopicItemStats)
        {
            if (stat.Topic is not null)
            {
                SeedTopicInner(conn, tx, stat.Topic, now, authoritative: false);
            }
        }

        tx.Commit();
    }

    /// <summary>
    /// Accumulate topics observed in a search response (where <c>topics[]</c> is
    /// returned alongside <c>items[]</c>). Called by the controller's /topics endpoint
    /// path when bootstrapping or refreshing the catalog.
    /// </summary>
    public void SeedTopics(IEnumerable<DtddTopic> topics)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        foreach (var topic in topics)
        {
            SeedTopicInner(conn, tx, topic, now, authoritative: true);
        }

        tx.Commit();
    }

    /// <summary>
    /// Returns every topic ever observed, ordered by topic_id.
    /// </summary>
    public List<DtddTopic> GetTopics()
    {
        var result = new List<DtddTopic>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM topics ORDER BY topic_id;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                var topic = JsonSerializer.Deserialize<DtddTopic>(reader.GetString(0));
                if (topic is not null)
                {
                    result.Add(topic);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Skipping corrupt topic row");
            }
        }

        return result;
    }

    /// <summary>
    /// Deletes warnings older than the given age. Returns the row count removed.
    /// Called by the prefetch task; topics rows are never expired (catalog grows monotonically).
    /// </summary>
    public int ExpireOlderThan(int days)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM warnings WHERE fetched_at < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }

    private void InitializeSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void SeedTopicInner(SqliteConnection conn, SqliteTransaction tx, DtddTopic topic, long now, bool authoritative)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Two conflict behaviors by source:
        //  - authoritative (TopicSeeder, v3 full-catalog endpoint since v0.2):
        //    ON CONFLICT DO UPDATE, so re-seeding refreshes names/descriptions
        //    and (crucially) category data that v1-era rows often lacked —
        //    under v1's DO NOTHING those rows stayed uncategorized in the
        //    picker forever.
        //  - opportunistic (Put's accumulation from /media/{id} payloads):
        //    ON CONFLICT DO NOTHING, because media-payload topic objects can
        //    carry less data (missing category) than the seeded row they'd
        //    overwrite.
        cmd.CommandText = authoritative
            ? @"INSERT INTO topics (topic_id, json, last_seen_at) VALUES ($id, $j, $t)
                ON CONFLICT(topic_id) DO UPDATE SET json = excluded.json, last_seen_at = excluded.last_seen_at;"
            : @"INSERT INTO topics (topic_id, json, last_seen_at) VALUES ($id, $j, $t)
                ON CONFLICT(topic_id) DO NOTHING;";
        cmd.Parameters.AddWithValue("$id", topic.Id);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(topic));
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }
}
