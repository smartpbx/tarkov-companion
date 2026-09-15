using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TarkovCompanion.Infrastructure.Persistence;

public sealed record SqliteMaintenanceInspection(
    long DatabaseBytes,
    long PageBytes,
    long FreePageBytes,
    int CachedResponses,
    long CachedCompressedBytes,
    int CompletedOutboxRows,
    int PublicationRows,
    int SyncRunRows,
    int ItemMetricRows,
    int UnresolvedPriceRows,
    int AggregateSequenceRows,
    int MaintenanceRunRows);

public sealed record SqliteMaintenanceResult(
    Guid RunId,
    string Operation,
    bool DryRun,
    long EstimatedOrReclaimedBytes,
    int AffectedRows,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc);

public sealed record SqliteQueryPlanEvidence(string Path, string Sql, ImmutableArray<string> Steps)
{
    public bool UsesIndex => Steps.Any(step => step.Contains("USING", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Inspectable, schedulable database care; dry-run is the default API, never a side effect.</summary>
public sealed class SqliteDataPlatformMaintenance(
    SqliteConnectionFactory connectionFactory,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<SqliteMaintenanceInspection> InspectAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var pageSize = await ScalarLongAsync(connection, "PRAGMA page_size;", cancellationToken).ConfigureAwait(false);
        var pages = await ScalarLongAsync(connection, "PRAGMA page_count;", cancellationToken).ConfigureAwait(false);
        var free = await ScalarLongAsync(connection, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false);
        return new(
            File.Exists(connectionFactory.DatabasePath) ? new FileInfo(connectionFactory.DatabasePath).Length : pages * pageSize,
            pages * pageSize,
            free * pageSize,
            await ScalarIntAsync(connection, "SELECT COUNT(*) FROM http_response_cache;", cancellationToken).ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COALESCE(SUM(compressed_bytes), 0) FROM raw_endpoint_bodies;", cancellationToken).ConfigureAwait(false),
            await ScalarIntAsync(connection, "SELECT COUNT(*) FROM durable_outbox WHERE delivery_state = 5;", cancellationToken).ConfigureAwait(false),
            await ScalarIntAsync(connection, "SELECT COUNT(*) FROM dataset_publications;", cancellationToken).ConfigureAwait(false),
            await ScalarIntAsync(connection, "SELECT COUNT(*) FROM dataset_sync_runs;", cancellationToken).ConfigureAwait(false),
            await ScalarIntAsync(connection, "SELECT COUNT(*) FROM item_metrics_v2;", cancellationToken).ConfigureAwait(false),
            await ScalarIntAsync(connection, "SELECT COUNT(*) FROM price_history_unresolved_time;", cancellationToken).ConfigureAwait(false),
            await ScalarIntAsync(connection, "SELECT COUNT(*) FROM outbox_aggregate_sequences;", cancellationToken).ConfigureAwait(false),
            await ScalarIntAsync(connection, "SELECT COUNT(*) FROM maintenance_history;", cancellationToken).ConfigureAwait(false));
    }

    public async Task<SqliteMaintenanceResult> RunAsync(
        string operation,
        bool dryRun,
        DateTimeOffset retainAfterUtc,
        CancellationToken cancellationToken)
    {
        operation = NormalizeOperation(operation);
        var started = _timeProvider.GetUtcNow();
        var before = await InspectAsync(cancellationToken).ConfigureAwait(false);
        var affected = operation == "prune"
            ? await CountPrunableAsync(retainAfterUtc, cancellationToken).ConfigureAwait(false)
            : 0;
        if (!dryRun)
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (operation == "prune")
            {
                affected = await PruneAsync(connection, retainAfterUtc, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var command = connection.CreateCommand();
                command.CommandText = operation == "vacuum" ? "VACUUM;" : "REINDEX;";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var completed = _timeProvider.GetUtcNow();
        var after = dryRun ? before : await InspectAsync(cancellationToken).ConfigureAwait(false);
        var bytes = dryRun && operation == "vacuum"
            ? before.FreePageBytes
            : Math.Max(0, before.DatabaseBytes - after.DatabaseBytes);
        var result = new SqliteMaintenanceResult(Guid.NewGuid(), operation, dryRun, bytes, affected, started, completed);
        await RecordResultAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task ScheduleAsync(string operation, bool enabled, int intervalHours, CancellationToken cancellationToken)
    {
        operation = NormalizeOperation(operation);
        if (intervalHours is < 1 or > 8760) throw new ArgumentOutOfRangeException(nameof(intervalHours));
        var now = _timeProvider.GetUtcNow();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO maintenance_schedules(operation, enabled, interval_hours, last_run_utc, next_run_utc, updated_utc)
            VALUES ($operation, $enabled, $hours, NULL, $next, $updated)
            ON CONFLICT(operation) DO UPDATE SET enabled = excluded.enabled, interval_hours = excluded.interval_hours,
                next_run_utc = excluded.next_run_utc, updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$enabled", enabled);
        command.Parameters.AddWithValue("$hours", intervalHours);
        command.Parameters.AddWithValue("$next", Format(now.AddHours(intervalHours)));
        command.Parameters.AddWithValue("$updated", Format(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<SqliteMaintenanceResult>> RunDueAsync(DateTimeOffset retainAfterUtc, CancellationToken cancellationToken)
    {
        var due = new List<(string Operation, int Hours)>();
        var now = _timeProvider.GetUtcNow();
        await using (var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT operation, interval_hours FROM maintenance_schedules WHERE enabled = 1 AND (next_run_utc IS NULL OR next_run_utc <= $now) ORDER BY operation;";
            command.Parameters.AddWithValue("$now", Format(now));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) due.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        var results = ImmutableArray.CreateBuilder<SqliteMaintenanceResult>(due.Count);
        foreach (var item in due)
        {
            results.Add(await RunAsync(item.Operation, false, retainAfterUtc, cancellationToken).ConfigureAwait(false));
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE maintenance_schedules SET last_run_utc = $now, next_run_utc = $next, updated_utc = $now WHERE operation = $operation;";
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$next", Format(now.AddHours(item.Hours)));
            command.Parameters.AddWithValue("$operation", item.Operation);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return results.ToImmutable();
    }

    private async Task<int> CountPrunableAsync(DateTimeOffset retainAfterUtc, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM durable_outbox WHERE delivery_state = 5 AND completed_utc < $cutoff) +
              (SELECT COUNT(*) FROM dataset_publications AS p
               WHERE attempted_utc < $cutoff
                 AND NOT EXISTS (SELECT 1 FROM dataset_heads AS h WHERE h.visible_publication_id = p.publication_id OR h.last_known_good_publication_id = p.publication_id));
            """;
        command.Parameters.AddWithValue("$cutoff", Format(retainAfterUtc));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<int> PruneAsync(SqliteConnection connection, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var affected = 0;
        affected += await ExecuteAsync(connection, transaction, "DELETE FROM durable_outbox WHERE delivery_state = 5 AND completed_utc < $cutoff;", cutoff, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            UPDATE dataset_publications
            SET previous_lkg_publication_id = NULL
            WHERE previous_lkg_publication_id IN (
                SELECT publication_id
                FROM dataset_publications AS previous
                WHERE previous.attempted_utc < $cutoff
                  AND NOT EXISTS (
                    SELECT 1 FROM dataset_heads AS h
                    WHERE h.visible_publication_id = previous.publication_id
                       OR h.last_known_good_publication_id = previous.publication_id));
            """, cutoff, cancellationToken).ConfigureAwait(false);
        affected += await ExecuteAsync(connection, transaction, """
            DELETE FROM dataset_publications
            WHERE attempted_utc < $cutoff
              AND NOT EXISTS (SELECT 1 FROM dataset_heads AS h WHERE h.visible_publication_id = publication_id OR h.last_known_good_publication_id = publication_id);
            """, cutoff, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            DELETE FROM raw_endpoint_bodies
            WHERE NOT EXISTS (SELECT 1 FROM http_response_cache AS c WHERE c.content_sha256 = raw_endpoint_bodies.content_sha256)
              AND NOT EXISTS (SELECT 1 FROM dataset_publications AS p WHERE p.content_sha256 = raw_endpoint_bodies.content_sha256);
            """, cutoff, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    private async Task RecordResultAsync(SqliteMaintenanceResult result, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO maintenance_history(run_id, operation, dry_run, started_utc, completed_utc,
                reclaimed_bytes, affected_rows, status, diagnostic_code)
            VALUES ($id, $operation, $dry, $started, $completed, $bytes, $rows, 'completed', NULL);
            """;
        command.Parameters.AddWithValue("$id", result.RunId.ToString("D"));
        command.Parameters.AddWithValue("$operation", result.Operation);
        command.Parameters.AddWithValue("$dry", result.DryRun);
        command.Parameters.AddWithValue("$started", Format(result.StartedUtc));
        command.Parameters.AddWithValue("$completed", Format(result.CompletedUtc));
        command.Parameters.AddWithValue("$bytes", result.EstimatedOrReclaimedBytes);
        command.Parameters.AddWithValue("$rows", result.AffectedRows);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$cutoff", Format(cutoff));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeOperation(string operation) => operation?.Trim().ToLowerInvariant() switch
    {
        "vacuum" => "vacuum",
        "reindex" => "reindex",
        "prune" => "prune",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken) =>
        checked((int)await ScalarLongAsync(connection, sql, cancellationToken).ConfigureAwait(false));

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>Captures SQLite's chosen access paths for each latency-critical V2 read.</summary>
public sealed class SqliteQueryPlanAuditor(SqliteConnectionFactory connectionFactory)
{
    private static readonly (string Name, string Sql)[] Queries =
    [
        ("item", "SELECT * FROM items WHERE id = 'item';"),
        ("quest", "SELECT * FROM quest_catalog_tasks WHERE source_key = 'json.tarkov.dev/tasks' AND source_mode = 'regular' AND language = 'en' AND id = 'task';"),
        ("price", "SELECT * FROM price_history WHERE item_id = 'item' ORDER BY timestamp_utc DESC LIMIT 100;"),
        ("map", "SELECT * FROM maps WHERE id = 'map';"),
        ("history", "SELECT * FROM raids WHERE profile_id = 'profile' ORDER BY start_utc DESC LIMIT 100;"),
        ("profile", "SELECT * FROM profile_contexts WHERE game_mode = 'Pvp' AND generation = 'wipe' AND lifecycle = 'Active';"),
        ("craft", "SELECT * FROM crafts WHERE station_id = 'station' AND level = 1;"),
    ];

    public async Task<ImmutableArray<SqliteQueryPlanEvidence>> CaptureAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var evidence = ImmutableArray.CreateBuilder<SqliteQueryPlanEvidence>(Queries.Length);
        foreach (var query in Queries)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"EXPLAIN QUERY PLAN {query.Sql}";
            var steps = ImmutableArray.CreateBuilder<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) steps.Add(reader.GetString(3));
            evidence.Add(new(query.Name, query.Sql, steps.ToImmutable()));
        }

        return evidence.ToImmutable();
    }
}

public sealed record SqliteUnreadSchemaMember(string Table, string Column);

/// <summary>Ratchets every V2 column against the persistence code's deliberate read/write map.</summary>
public sealed class SqliteV2UnreadSchemaAuditor(SqliteConnectionFactory connectionFactory)
{
    private static readonly ImmutableDictionary<string, ImmutableHashSet<string>> Declared =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["raw_endpoint_bodies"] = ["content_sha256", "compression", "compressed_body", "uncompressed_bytes", "compressed_bytes", "created_utc"],
            ["http_response_cache"] = ["cache_key", "content_sha256", "cached_utc", "last_accessed_utc", "etag", "last_modified"],
            ["item_metrics_v2"] = ["item_id", "weight_kg", "flea_price_roubles", "trader_value_roubles", "measured_utc", "source"],
            ["price_history_unresolved_time"] = ["item_id", "source_ordinal", "flea_price", "trader_value", "source", "raw_json"],
            ["dataset_sync_runs"] = ["run_id", "game_mode", "language", "started_utc", "completed_utc", "state", "endpoint_count", "successful_count", "stale_count", "refused_count", "failure_count", "detail"],
            ["dataset_publications"] = ["publication_id", "run_id", "source_key", "game_mode", "language", "state", "content_sha256", "record_count", "attempted_utc", "published_utc", "detail", "previous_lkg_publication_id"],
            ["dataset_heads"] = ["source_key", "game_mode", "language", "visible_publication_id", "last_known_good_publication_id", "state", "updated_utc"],
            ["profile_workspaces"] = ["workspace_key", "revision", "active_profile_id", "updated_utc"],
            ["profile_contexts"] = ["profile_id", "generation", "name", "game_mode", "wipe_season", "language", "region", "time_zone", "data_snapshot_id", "data_snapshot_published_utc", "level", "lifecycle", "updated_utc", "extension_json"],
            ["profile_trader_progress_v2"] = ["profile_id", "trader_id", "level"],
            ["profile_completed_tasks_v2"] = ["profile_id", "task_id"],
            ["profile_objective_progress_v2"] = ["profile_id", "objective_id", "progress_count"],
            ["profile_hideout_progress_v2"] = ["profile_id", "station_id", "level"],
            ["profile_wishlist_v2"] = ["profile_id", "item_id"],
            ["profile_owned_counts_v2"] = ["profile_id", "item_id", "item_count"],
            ["profile_event_states_v2"] = ["profile_id", "item_id", "state"],
            ["profile_item_overrides_v2"] = ["profile_id", "item_id", "value"],
            ["profile_pins_v2"] = ["profile_id", "target_kind", "target_id", "sort_order", "note"],
            ["durable_outbox"] = ["operation_id", "idempotency_key", "correlation_id", "feature_id", "command_kind", "version_major", "version_minor", "aggregate_id", "aggregate_sequence", "created_utc", "not_before_utc", "expires_utc", "payload", "max_attempts", "attempt_timeout_ticks", "initial_retry_delay_ticks", "max_retry_delay_ticks", "backoff_factor", "delivery_state", "attempt_count", "next_attempt_utc", "lease_token", "lease_expires_utc", "last_fault_json", "completed_utc", "dead_lettered_utc"],
            ["outbox_aggregate_sequences"] = ["aggregate_id", "next_sequence"],
            ["outbox_target_operations"] = ["operation_id", "command_kind", "target_id", "applied_utc"],
            ["observed_inventory_snapshots"] = ["snapshot_id", "profile_id", "generation", "game_mode", "data_snapshot_id", "observed_utc", "recorded_utc", "source", "producer_version", "coverage", "confidence", "is_current", "payload_json", "extension_json"],
            ["observed_inventory_nodes"] = ["snapshot_id", "node_id", "parent_node_id", "node_kind", "item_id", "grid_x", "grid_y", "width", "height", "item_count", "weight_kg", "confidence", "raw_json"],
            ["raid_field_history"] = ["id", "raid_id", "field_name", "value_json", "provenance_kind", "source", "observed_utc", "recorded_utc", "confidence"],
            ["craft_history"] = ["history_id", "craft_id", "station_id", "station_level", "observed_utc", "recorded_utc", "output_item_id", "output_count", "estimated_cost_roubles", "estimated_yield_roubles", "source", "payload_json"],
            ["loadout_plans"] = ["plan_id", "profile_id", "generation", "game_mode", "name", "revision", "updated_utc", "payload_json", "extension_json"],
            ["model_snapshots"] = ["model_snapshot_id", "profile_id", "generation", "game_mode", "model_kind", "presentation_kind", "source", "observed_utc", "data_through_utc", "generated_utc", "coverage", "confidence", "calibration", "model_version", "payload_json", "extension_json"],
            ["retention_policies"] = ["policy_key", "screenshot_retention_enabled", "screenshot_retention_hours", "debug_capture_enabled", "data_retention_days", "updated_utc", "extension_json"],
            ["local_json_recovery"] = ["document_key", "state", "detected_utc", "content_sha256", "diagnostic_code"],
            ["maintenance_schedules"] = ["operation", "enabled", "interval_hours", "last_run_utc", "next_run_utc", "updated_utc"],
            ["maintenance_history"] = ["run_id", "operation", "dry_run", "started_utc", "completed_utc", "reclaimed_bytes", "affected_rows", "status", "diagnostic_code"],
        }.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

    public async Task<ImmutableArray<SqliteUnreadSchemaMember>> AuditAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var unread = ImmutableArray.CreateBuilder<SqliteUnreadSchemaMember>();
        foreach (var table in Declared)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info([{table.Key}]);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                if (!table.Value.Contains(reader.GetString(1))) unread.Add(new(table.Key, reader.GetString(1)));
        }
        return unread.ToImmutable();
    }
}
