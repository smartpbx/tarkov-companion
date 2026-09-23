using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.LootScan;

namespace TarkovCompanion.Infrastructure.Persistence.LootScan;

/// <summary>Saved Loot Scan results (0019), bounded on every save by <see cref="LootScanHistoryRetention"/>.</summary>
public sealed class SqliteLootScanHistoryStore(SqliteConnectionFactory connectionFactory) : ILootScanHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task SaveAsync(SavedLootScan scan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentException.ThrowIfNullOrWhiteSpace(scan.ScanId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scan.RulesetVersion);
        if (scan.EvaluatedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A saved loot scan's time must be UTC.", nameof(scan));
        }

        var items = scan.Items.Take(LootScanHistoryRetention.MaximumItemsPerScan).ToArray();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var write = connection.CreateCommand())
        {
            write.Transaction = transaction;
            // The same frame decided again (a pin, a change of raid phase) replaces its earlier save.
            write.CommandText = """
                INSERT INTO loot_scans(scan_id, raid_id, map_id, evaluated_utc, ruleset_version, is_complete, items_json)
                VALUES ($scan, $raid, $map, $evaluated, $ruleset, $complete, $items)
                ON CONFLICT(scan_id) DO UPDATE SET
                    raid_id = excluded.raid_id,
                    map_id = excluded.map_id,
                    evaluated_utc = excluded.evaluated_utc,
                    ruleset_version = excluded.ruleset_version,
                    is_complete = excluded.is_complete,
                    items_json = excluded.items_json;
                """;
            write.Parameters.AddWithValue("$scan", scan.ScanId.Trim());
            write.Parameters.AddWithValue("$raid", scan.RaidId is { } raidId ? raidId.ToString("D") : DBNull.Value);
            write.Parameters.AddWithValue("$map", string.IsNullOrWhiteSpace(scan.MapId) ? DBNull.Value : scan.MapId.Trim());
            write.Parameters.AddWithValue("$evaluated", Format(scan.EvaluatedUtc));
            write.Parameters.AddWithValue("$ruleset", scan.RulesetVersion.Trim());
            write.Parameters.AddWithValue("$complete", scan.IsComplete ? 1 : 0);
            write.Parameters.AddWithValue("$items", JsonSerializer.Serialize(items, JsonOptions));
            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await PruneAsync(connection, transaction, scan.EvaluatedUtc - LootScanHistoryRetention.MaximumAge, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Keeps the newest <see cref="LootScanHistoryRetention.MaximumScans"/> scans, none older than the cutoff.</summary>
    internal static async Task<int> PruneAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DateTimeOffset olderThanUtc,
        CancellationToken cancellationToken)
    {
        await using var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = """
            DELETE FROM loot_scans
            WHERE evaluated_utc < $cutoff
               OR scan_id NOT IN (
                   SELECT scan_id FROM loot_scans
                   ORDER BY evaluated_utc DESC, scan_id DESC
                   LIMIT $maximum);
            """;
        prune.Parameters.AddWithValue("$cutoff", Format(olderThanUtc));
        prune.Parameters.AddWithValue("$maximum", LootScanHistoryRetention.MaximumScans);
        return await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SavedLootScan>> ListForRaidAsync(Guid raidId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT scan_id, raid_id, map_id, evaluated_utc, ruleset_version, is_complete, items_json
            FROM loot_scans
            WHERE raid_id = $raid
            ORDER BY evaluated_utc, scan_id;
            """;
        read.Parameters.AddWithValue("$raid", raidId.ToString("D"));
        return await ReadAsync(read, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SavedLootScan>> ListRecentAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT scan_id, raid_id, map_id, evaluated_utc, ruleset_version, is_complete, items_json
            FROM loot_scans
            ORDER BY evaluated_utc DESC, scan_id DESC
            LIMIT $limit;
            """;
        read.Parameters.AddWithValue("$limit", Math.Min(limit, LootScanHistoryRetention.MaximumScans));
        return await ReadAsync(read, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<SavedLootScan>> ReadAsync(SqliteCommand read, CancellationToken cancellationToken)
    {
        var scans = new List<SavedLootScan>();
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            IReadOnlyList<SavedLootScanItem> items;
            try
            {
                items = JsonSerializer.Deserialize<SavedLootScanItem[]>(reader.GetString(6), JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                // One unreadable row must not hide the rest of the history.
                continue;
            }

            if (!DateTimeOffset.TryParseExact(
                    reader.GetString(3),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var evaluated))
            {
                continue;
            }

            scans.Add(new(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                evaluated.ToUniversalTime(),
                reader.GetString(4),
                reader.GetInt32(5) == 1,
                items.Where(item => item is not null && item.Name is not null).ToArray()));
        }

        return scans;
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
