using System.Globalization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.Infrastructure.Diagnostics;

/// <summary>One game-data endpoint as the database itself last recorded it.</summary>
public sealed record SelfTestEndpointRow(
    string SourceKey,
    DateTimeOffset? LastSuccessUtc,
    string Status,
    string? ErrorSummary,
    int? RecordCount,
    long Bytes);

public sealed record SelfTestTableRow(string Name, long Rows);

public sealed record SelfTestDatabaseRow(
    string Path,
    long Bytes,
    IReadOnlyList<string> Applied,
    IReadOnlyList<SelfTestTableRow> Tables);

/// <summary>
/// The read-only queries behind Setup's self-test: what each endpoint holds, and what the
/// database holds.
/// </summary>
/// <remarks>
/// Its own class rather than more methods on the repositories, because this reads across their
/// tables on purpose — an endpoint's answer is spread over its sync state, its published record
/// count, and the size of the body still in the response cache, and no one repository owns all
/// three. Nothing here writes, opens a transaction, or starts a refresh: the self-test is
/// pressed mid-raid and must cost a few reads and nothing else.
/// </remarks>
public sealed class SqliteSelfTestReader(SqliteConnectionFactory connectionFactory)
{
    /// <summary>
    /// The tables worth counting, in the order a person would ask about them.
    /// </summary>
    /// <remarks>
    /// A fixed list rather than every table in the schema. "Which tables matter" is a judgement,
    /// and a hundred-row dump of internal bookkeeping is the sort of output that gets skimmed
    /// instead of read. Each of these answers a question somebody has actually asked: is the
    /// catalog there, are my raids being recorded, did the scans land.
    /// </remarks>
    public static IReadOnlyList<string> CountedTables { get; } =
    [
        "items",
        "tasks",
        "maps",
        "traders",
        "hideout_stations",
        "barters",
        "crafts",
        "raids",
        "raid_events",
        "scan_history",
    ];

    public async Task<IReadOnlyList<SelfTestEndpointRow>> ReadEndpointsAsync(
        string gameMode,
        string language,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // The record count comes from the publication the head currently points at, not from the
        // newest attempt: a failed refresh writes a publication too, and reporting its count
        // would say nothing landed when yesterday's rows are still being served.
        command.CommandText = """
            SELECT
                s.source_key,
                s.last_success_utc,
                s.status,
                s.error_summary,
                (SELECT p.record_count
                   FROM dataset_publications p
                   JOIN dataset_heads h
                     ON h.source_key = s.source_key
                    AND h.game_mode = s.game_mode
                    AND h.language = s.language
                  WHERE p.publication_id = COALESCE(h.visible_publication_id, h.last_known_good_publication_id)
                    AND p.source_key = s.source_key) AS record_count,
                (SELECT COALESCE(SUM(b.uncompressed_bytes), 0)
                   FROM http_response_cache c
                   JOIN raw_endpoint_bodies b ON b.content_sha256 = c.content_sha256
                  WHERE c.cache_key = s.game_mode || '/' || s.source_key
                     OR c.cache_key LIKE s.game_mode || '/' || s.source_key || '\_%' ESCAPE '\') AS bytes
            FROM sync_state s
            WHERE s.game_mode = $mode AND s.language = $language
            ORDER BY s.source_key;
            """;
        command.Parameters.AddWithValue("$mode", gameMode);
        command.Parameters.AddWithValue("$language", language);

        var rows = new List<SelfTestEndpointRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : ParseUtc(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? 0 : reader.GetInt64(5)));
        }

        return rows;
    }

    public async Task<SelfTestDatabaseRow> ReadDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var applied = new List<string>();
        await using (var migrations = connection.CreateCommand())
        {
            migrations.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
            await using var reader = await migrations.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                applied.Add(reader.GetString(0));
            }
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        await using (var names = connection.CreateCommand())
        {
            names.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            await using var reader = await names.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                present.Add(reader.GetString(0));
            }
        }

        var tables = new List<SelfTestTableRow>(CountedTables.Count);
        foreach (var table in CountedTables)
        {
            if (!present.Contains(table))
            {
                continue;
            }

            await using var count = connection.CreateCommand();
            // The name is one of CountedTables and never reaches here from outside, which is
            // what makes the interpolation safe; SQLite cannot parameterize a table name.
            count.CommandText = $"SELECT COUNT(*) FROM {table};";
            var value = await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            tables.Add(new(table, value is long rows ? rows : 0));
        }

        return new(
            connectionFactory.DatabasePath,
            File.Exists(connectionFactory.DatabasePath) ? new FileInfo(connectionFactory.DatabasePath).Length : 0,
            applied,
            tables);
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
