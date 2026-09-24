using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

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

public sealed record SqliteMaintenanceHistoryEntry(
    Guid RunId,
    string Operation,
    bool DryRun,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    long? ReclaimedBytes,
    int? AffectedRows,
    string Status,
    string? DiagnosticCode);

public sealed record SqliteMaintenanceScheduleEntry(
    string Operation,
    bool Enabled,
    int IntervalHours,
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? NextRunUtc,
    DateTimeOffset UpdatedUtc);

public sealed record SqliteQueryPlanStatementEvidence(
    string Name,
    string Sql,
    bool IndexRequired,
    ImmutableArray<string> Steps)
{
    public bool UsesIndex => Steps.Any(step => step.Contains("USING", StringComparison.OrdinalIgnoreCase));

    public bool MeetsExpectation => !IndexRequired || UsesIndex;
}

public sealed record SqliteQueryPlanEvidence(
    string Path,
    string ReaderPath,
    ImmutableArray<SqliteQueryPlanStatementEvidence> Statements)
{
    public string Sql => string.Join(Environment.NewLine, Statements.Select(statement => statement.Sql));

    public ImmutableArray<string> Steps => Statements.SelectMany(statement => statement.Steps).ToImmutableArray();

    public bool UsesIndex => Statements.Any(statement => statement.UsesIndex);
}

/// <summary>Inspectable, schedulable database care; dry-run is the default API, never a side effect.</summary>
public sealed class SqliteDataPlatformMaintenance(
    SqliteConnectionFactory connectionFactory,
    TimeProvider? timeProvider = null)
{
    private const int MaximumHistoryRows = 256;
    private const int MaximumScheduleRows = 3;
    private const int MaximumOperationUtf8Bytes = 16;
    private const int MaximumStatusUtf8Bytes = 32;
    private const int MaximumDiagnosticCodeUtf8Bytes = 128;
    private const int MaximumTimestampUtf8Bytes = 64;
    private const long MaximumRetentionDays = 365_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
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

    public Task<SqliteMaintenanceResult> RunAsync(
        string operation,
        bool dryRun,
        DateTimeOffset retainAfterUtc,
        CancellationToken cancellationToken) =>
        RunCoreAsync(operation, dryRun, retainAfterUtc, null, cancellationToken);

    private async Task<SqliteMaintenanceResult> RunCoreAsync(
        string operation,
        bool dryRun,
        DateTimeOffset retainAfterUtc,
        ScheduledPruneGuard? scheduledPruneGuard,
        CancellationToken cancellationToken)
    {
        operation = NormalizeOperation(operation);
        var started = _timeProvider.GetUtcNow();
        var before = await InspectAsync(cancellationToken).ConfigureAwait(false);
        var affected = operation == "prune"
            ? await CountPrunableAsync(retainAfterUtc, cancellationToken).ConfigureAwait(false)
            : 0;
        try
        {
            if (!dryRun)
            {
                await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (operation == "prune")
                {
                    affected = await PruneAsync(
                            connection,
                            retainAfterUtc,
                            scheduledPruneGuard,
                            cancellationToken)
                        .ConfigureAwait(false);
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
            if (!dryRun)
            {
                await RecordResultAsync(result, "completed", null, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch (Exception exception)
        {
            if (!dryRun)
            {
                try
                {
                    var failed = new SqliteMaintenanceResult(
                        Guid.NewGuid(),
                        operation,
                        false,
                        0,
                        affected,
                        started,
                        _timeProvider.GetUtcNow());
                    await RecordResultAsync(
                        failed,
                        "failed",
                        DiagnosticCode(exception),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Preserve the operation failure. Recording diagnostics must never replace
                    // the exception that explains why maintenance itself failed.
                }
            }

            throw;
        }
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

    public async Task<ImmutableArray<SqliteMaintenanceScheduleEntry>> ListSchedulesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation, enabled, interval_hours, last_run_utc, next_run_utc, updated_utc,
                   CASE WHEN typeof(operation) = 'text'
                        THEN length(CAST(operation AS BLOB)) ELSE -1 END,
                   typeof(enabled), typeof(interval_hours),
                   CASE WHEN last_run_utc IS NULL THEN NULL
                        WHEN typeof(last_run_utc) = 'text'
                        THEN length(CAST(last_run_utc AS BLOB)) ELSE -1 END,
                   CASE WHEN next_run_utc IS NULL THEN NULL
                        WHEN typeof(next_run_utc) = 'text'
                        THEN length(CAST(next_run_utc AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(updated_utc) = 'text'
                        THEN length(CAST(updated_utc AS BLOB)) ELSE -1 END
            FROM maintenance_schedules
            ORDER BY operation
            LIMIT 4;
            """;
        var schedules = ImmutableArray.CreateBuilder<SqliteMaintenanceScheduleEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (schedules.Count == MaximumScheduleRows)
            {
                throw new InvalidDataException(
                    $"Persisted maintenance schedules exceed the {MaximumScheduleRows}-row boundary.");
            }

            try
            {
                var operation = ReadOperation(reader, 0, 6);
                schedules.Add(new(
                    operation,
                    ReadBoolean(reader, 1, 7, "maintenance schedule enabled state"),
                    ReadRequiredInt32(reader, 2, 8, 1, 8760, "maintenance schedule interval"),
                    ReadOptionalTimestamp(reader, 3, 9, "maintenance schedule last-run time"),
                    ReadOptionalTimestamp(reader, 4, 10, "maintenance schedule next-run time"),
                    ReadRequiredTimestamp(reader, 5, 11, "maintenance schedule update time")));
            }
            catch (Exception exception) when (IsPersistedValueFailure(exception))
            {
                throw new InvalidDataException("Persisted maintenance schedule metadata is invalid.", exception);
            }
        }

        return schedules.ToImmutable();
    }

    public async Task<ImmutableArray<SqliteMaintenanceHistoryEntry>> ListHistoryAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > MaximumHistoryRows)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, operation, dry_run, started_utc, completed_utc, reclaimed_bytes,
                   affected_rows, status, diagnostic_code,
                   CASE WHEN typeof(run_id) = 'text'
                        THEN length(CAST(run_id AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(operation) = 'text'
                        THEN length(CAST(operation AS BLOB)) ELSE -1 END,
                   typeof(dry_run),
                   CASE WHEN typeof(started_utc) = 'text'
                        THEN length(CAST(started_utc AS BLOB)) ELSE -1 END,
                   CASE WHEN completed_utc IS NULL THEN NULL
                        WHEN typeof(completed_utc) = 'text'
                        THEN length(CAST(completed_utc AS BLOB)) ELSE -1 END,
                   typeof(reclaimed_bytes), typeof(affected_rows),
                   CASE WHEN typeof(status) = 'text'
                        THEN length(CAST(status AS BLOB)) ELSE -1 END,
                   CASE WHEN diagnostic_code IS NULL THEN NULL
                        WHEN typeof(diagnostic_code) = 'text'
                        THEN length(CAST(diagnostic_code AS BLOB)) ELSE -1 END
            FROM maintenance_history
            ORDER BY started_utc DESC, run_id DESC
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$maximum", maximumCount);
        var history = ImmutableArray.CreateBuilder<SqliteMaintenanceHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var status = ReadRequiredText(
                    reader, 7, 16, MaximumStatusUtf8Bytes, "maintenance history status");
                if (status is not ("planned" or "completed" or "failed"))
                {
                    throw new InvalidDataException("Persisted maintenance history has an invalid status.");
                }

                history.Add(new(
                    ReadRequiredGuid(reader, 0, 9, "maintenance run id"),
                    ReadOperation(reader, 1, 10),
                    ReadBoolean(reader, 2, 11, "maintenance dry-run state"),
                    ReadRequiredTimestamp(reader, 3, 12, "maintenance start time"),
                    ReadOptionalTimestamp(reader, 4, 13, "maintenance completion time"),
                    ReadOptionalInt64(reader, 5, 14, 0, long.MaxValue, "maintenance reclaimed bytes"),
                    ReadOptionalInt32(reader, 6, 15, 0, int.MaxValue, "maintenance affected rows"),
                    status,
                    ReadOptionalText(
                        reader,
                        8,
                        17,
                        MaximumDiagnosticCodeUtf8Bytes,
                        "maintenance diagnostic code")));
            }
            catch (Exception exception) when (IsPersistedValueFailure(exception))
            {
                throw new InvalidDataException("Persisted maintenance history metadata is invalid.", exception);
            }
        }

        return history.ToImmutable();
    }

    public async Task<ImmutableArray<SqliteMaintenanceResult>> RunDueAsync(CancellationToken cancellationToken)
    {
        var due = new List<(string Operation, int Hours, long? RetentionDays, DateTimeOffset DueNextUtc)>();
        var now = _timeProvider.GetUtcNow();
        await using (var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT operation, enabled, interval_hours, next_run_utc,
                       CASE WHEN operation = 'prune' THEN (
                           SELECT CASE
                               WHEN typeof(data_retention_days) = 'integer' THEN data_retention_days
                               ELSE NULL
                           END
                           FROM retention_policies
                           WHERE policy_key = 'local'
                       ) END,
                       CASE WHEN typeof(operation) = 'text'
                            THEN length(CAST(operation AS BLOB)) ELSE -1 END,
                       typeof(enabled), typeof(interval_hours),
                       CASE WHEN next_run_utc IS NULL THEN NULL
                            WHEN typeof(next_run_utc) = 'text'
                            THEN length(CAST(next_run_utc AS BLOB)) ELSE -1 END
                FROM maintenance_schedules
                WHERE operation IN ('prune', 'reindex', 'vacuum')
                  AND enabled = 1
                ORDER BY operation
                LIMIT 4;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var operation = ReadOperation(reader, 0, 5);
                    if (!ReadBoolean(reader, 1, 6, "maintenance schedule enabled state"))
                    {
                        continue;
                    }

                    var hours = ReadRequiredInt32(
                        reader, 2, 7, 1, 8760, "maintenance schedule interval");
                    var nextRunUtc = ReadOptionalTimestamp(
                        reader, 3, 8, "maintenance schedule next-run time");
                    if (nextRunUtc is not { } dueNextUtc || dueNextUtc > now)
                    {
                        continue;
                    }

                    due.Add((operation, hours, ReadRetentionDays(reader, 4), dueNextUtc));
                }
                catch (InvalidDataException)
                {
                    // Maintenance is optional care. A malformed local schedule is never authority
                    // to run work, and must not prevent another independently valid task at startup.
                }
            }
        }

        var results = ImmutableArray.CreateBuilder<SqliteMaintenanceResult>(due.Count);
        foreach (var item in due)
        {
            // A schedule switch is not consent to delete local history. The explicit local
            // retention value is the authority for both whether pruning may run and its cutoff.
            if (item.Operation == "prune" && !IsSupportedRetentionDays(item.RetentionDays))
            {
                continue;
            }

            var claimedNextUtc = now.AddHours(item.Hours);
            if (!await TryClaimAsync(
                    item.Operation,
                    item.DueNextUtc,
                    item.Hours,
                    claimedNextUtc,
                    now,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                var cutoff = item.Operation == "prune"
                    ? RetentionCutoff(now, item.RetentionDays!.Value)
                    : now;
                var pruneGuard = item.Operation == "prune"
                    ? new ScheduledPruneGuard(item.RetentionDays!.Value, claimedNextUtc)
                    : null;
                results.Add(await RunCoreAsync(
                        item.Operation,
                        false,
                        cutoff,
                        pruneGuard,
                        cancellationToken)
                    .ConfigureAwait(false));
                await CompleteScheduleAsync(item.Operation, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ReleaseScheduleAsync(item.Operation, claimedNextUtc).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                // Maintenance is care for an otherwise usable database. A failed optional task is
                // recorded by RunAsync and retried on the next startup; it must not take startup
                // or another due operation down with it.
                await ReleaseScheduleAsync(item.Operation, claimedNextUtc).ConfigureAwait(false);
            }
        }

        return results.ToImmutable();
    }

    private static long? ReadRetentionDays(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        try
        {
            // SQLite integers are signed 64-bit values. Reading as Int32 before validation made
            // a directly poisoned local row throw during startup, outside the fail-safe optional
            // maintenance loop.
            return reader.GetInt64(ordinal);
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException or FormatException)
        {
            return long.MinValue;
        }
    }

    private static bool IsSupportedRetentionDays(long? retentionDays) =>
        retentionDays is { } days && days >= 1 && days <= MaximumRetentionDays;

    private static DateTimeOffset RetentionCutoff(DateTimeOffset now, long retentionDays)
    {
        try
        {
            return now.AddDays(-retentionDays);
        }
        catch (ArgumentOutOfRangeException)
        {
            // An excessively long forward-compatible policy retains everything instead of
            // turning a local metadata edit into startup failure or unexpected deletion.
            return DateTimeOffset.MinValue;
        }
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
                 AND NOT EXISTS (SELECT 1 FROM dataset_heads AS h WHERE h.visible_publication_id = p.publication_id OR h.last_known_good_publication_id = p.publication_id)) +
              (SELECT COUNT(*) FROM dataset_sync_runs AS r
               WHERE completed_utc < $cutoff
                 AND NOT EXISTS (
                     SELECT 1 FROM dataset_publications AS p
                     WHERE p.run_id = r.run_id
                       AND (p.attempted_utc >= $cutoff OR EXISTS (
                           SELECT 1 FROM dataset_heads AS h
                           WHERE h.visible_publication_id = p.publication_id
                              OR h.last_known_good_publication_id = p.publication_id)))) +
              (SELECT COUNT(*) FROM observed_inventory_snapshots WHERE is_current = 0 AND recorded_utc < $cutoff) +
              (SELECT COUNT(*) FROM raid_field_history WHERE recorded_utc < $cutoff) +
              (SELECT COUNT(*) FROM craft_history WHERE recorded_utc < $cutoff) +
              (SELECT COUNT(*) FROM model_snapshots WHERE generated_utc < $cutoff) +
              (SELECT COUNT(*) FROM loot_scans WHERE evaluated_utc < $cutoff);
            """;
        command.Parameters.AddWithValue("$cutoff", Format(retainAfterUtc));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<int> PruneAsync(
        SqliteConnection connection,
        DateTimeOffset cutoff,
        ScheduledPruneGuard? scheduledGuard,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (scheduledGuard is not null)
        {
            // The due-list read and schedule claim happen on earlier short-lived connections.
            // Acquire SQLite's writer slot and re-check both the exact claim and the user's
            // current retention choice before deleting anything. A settings update that wins
            // this race therefore cancels the old authorization; once this UPDATE succeeds, the
            // writer lock keeps that authorization stable through the pruning transaction.
            await using var authorization = connection.CreateCommand();
            authorization.Transaction = transaction;
            authorization.CommandText = """
                UPDATE maintenance_schedules
                SET updated_utc = updated_utc
                WHERE operation = 'prune'
                  AND typeof(enabled) = 'integer'
                  AND enabled = 1
                  AND typeof(next_run_utc) = 'text'
                  AND next_run_utc = $claimedNext
                  AND EXISTS (
                      SELECT 1
                      FROM retention_policies
                      WHERE policy_key = 'local'
                        AND typeof(data_retention_days) = 'integer'
                        AND data_retention_days = $retentionDays);
                """;
            authorization.Parameters.AddWithValue("$claimedNext", Format(scheduledGuard.ClaimedNextUtc));
            authorization.Parameters.AddWithValue("$retentionDays", scheduledGuard.RetentionDays);
            if (await authorization.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ScheduledPruneAuthorizationChangedException();
            }
        }

        var affected = 0;
        affected += await ExecuteAsync(connection, transaction, "DELETE FROM durable_outbox WHERE delivery_state = 5 AND completed_utc < $cutoff;", cutoff, cancellationToken).ConfigureAwait(false);
        affected += await ExecuteAsync(connection, transaction, "DELETE FROM observed_inventory_snapshots WHERE is_current = 0 AND recorded_utc < $cutoff;", cutoff, cancellationToken).ConfigureAwait(false);
        affected += await ExecuteAsync(connection, transaction, "DELETE FROM raid_field_history WHERE recorded_utc < $cutoff;", cutoff, cancellationToken).ConfigureAwait(false);
        affected += await ExecuteAsync(connection, transaction, "DELETE FROM craft_history WHERE recorded_utc < $cutoff;", cutoff, cancellationToken).ConfigureAwait(false);
        affected += await ExecuteAsync(connection, transaction, "DELETE FROM model_snapshots WHERE generated_utc < $cutoff;", cutoff, cancellationToken).ConfigureAwait(false);
        // #274: saved loot scans also keep their own 500-scan / 90-day bound on every save.
        affected += await ExecuteAsync(connection, transaction, "DELETE FROM loot_scans WHERE evaluated_utc < $cutoff;", cutoff, cancellationToken).ConfigureAwait(false);
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
        affected += await ExecuteAsync(connection, transaction, """
            DELETE FROM dataset_sync_runs
            WHERE completed_utc < $cutoff
              AND NOT EXISTS (SELECT 1 FROM dataset_publications AS p WHERE p.run_id = dataset_sync_runs.run_id);
            """, cutoff, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            DELETE FROM raw_endpoint_bodies
            WHERE NOT EXISTS (SELECT 1 FROM http_response_cache AS c WHERE c.content_sha256 = raw_endpoint_bodies.content_sha256);
            """, cutoff, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    private async Task RecordResultAsync(
        SqliteMaintenanceResult result,
        string status,
        string? diagnosticCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO maintenance_history(run_id, operation, dry_run, started_utc, completed_utc,
                reclaimed_bytes, affected_rows, status, diagnostic_code)
            VALUES ($id, $operation, $dry, $started, $completed, $bytes, $rows, $status, $diagnostic);

            DELETE FROM maintenance_history
            WHERE run_id NOT IN (
                SELECT run_id FROM maintenance_history
                ORDER BY started_utc DESC, run_id DESC
                LIMIT $maximumHistory);
            """;
        command.Parameters.AddWithValue("$id", result.RunId.ToString("D"));
        command.Parameters.AddWithValue("$operation", result.Operation);
        command.Parameters.AddWithValue("$dry", result.DryRun);
        command.Parameters.AddWithValue("$started", Format(result.StartedUtc));
        command.Parameters.AddWithValue("$completed", Format(result.CompletedUtc));
        command.Parameters.AddWithValue("$bytes", result.EstimatedOrReclaimedBytes);
        command.Parameters.AddWithValue("$rows", result.AffectedRows);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$diagnostic", (object?)diagnosticCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$maximumHistory", MaximumHistoryRows);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryClaimAsync(
        string operation,
        DateTimeOffset dueNextUtc,
        int intervalHours,
        DateTimeOffset claimedNextUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE maintenance_schedules
            SET next_run_utc = $next, updated_utc = $now
            WHERE operation = $operation
              AND typeof(enabled) = 'integer'
              AND enabled = 1
              AND typeof(interval_hours) = 'integer'
              AND interval_hours = $hours
              AND typeof(next_run_utc) = 'text'
              AND next_run_utc = $dueNext
              AND next_run_utc <= $now;
            """;
        command.Parameters.AddWithValue("$next", Format(claimedNextUtc));
        command.Parameters.AddWithValue("$dueNext", Format(dueNextUtc));
        command.Parameters.AddWithValue("$hours", intervalHours);
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$operation", operation);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private sealed record ScheduledPruneGuard(long RetentionDays, DateTimeOffset ClaimedNextUtc);

    private sealed class ScheduledPruneAuthorizationChangedException : InvalidOperationException
    {
        public ScheduledPruneAuthorizationChangedException()
            : base("The scheduled prune authorization changed before the delete transaction began.")
        {
        }
    }

    private async Task CompleteScheduleAsync(
        string operation,
        DateTimeOffset completedUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE maintenance_schedules
            SET last_run_utc = $completed, updated_utc = $completed
            WHERE operation = $operation;
            """;
        command.Parameters.AddWithValue("$completed", Format(completedUtc));
        command.Parameters.AddWithValue("$operation", operation);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReleaseScheduleAsync(string operation, DateTimeOffset claimedNextUtc)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            await using var connection = await connectionFactory.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE maintenance_schedules
                SET next_run_utc = $retry, updated_utc = $now
                WHERE operation = $operation
                  AND next_run_utc = $claimedNext;
                """;
            command.Parameters.AddWithValue("$retry", Format(now.AddHours(1)));
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$operation", operation);
            command.Parameters.AddWithValue("$claimedNext", Format(claimedNextUtc));
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // RunAsync already recorded the operation failure where the database remained
            // writable. A best-effort retry timestamp must not replace that original failure or
            // turn cancellation into a shutdown error.
        }
    }

    private static string DiagnosticCode(Exception exception)
    {
        var value = exception switch
        {
            SqliteException sqlite => $"sqlite-{sqlite.SqliteErrorCode}",
            IOException => "io-failure",
            UnauthorizedAccessException => "access-denied",
            OperationCanceledException => "cancelled",
            _ => exception.GetType().Name,
        };
        return value.Length <= 128 ? value : value[..128];
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

    private static string ReadOperation(SqliteDataReader reader, int valueOrdinal, int lengthOrdinal)
    {
        var operation = ReadRequiredText(
            reader,
            valueOrdinal,
            lengthOrdinal,
            MaximumOperationUtf8Bytes,
            "maintenance operation");
        if (operation is not ("vacuum" or "reindex" or "prune"))
        {
            throw new InvalidDataException("Persisted maintenance operation is invalid.");
        }

        return operation;
    }

    private static Guid ReadRequiredGuid(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description)
    {
        var value = ReadRequiredText(reader, valueOrdinal, lengthOrdinal, 36, description);
        if (value.Length != 36 ||
            !Guid.TryParseExact(value, "D", out var parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Persisted {description} is not a canonical non-empty UUID.");
        }

        return parsed;
    }

    private static DateTimeOffset ReadRequiredTimestamp(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description)
    {
        var value = ReadRequiredText(
            reader,
            valueOrdinal,
            lengthOrdinal,
            MaximumTimestampUtf8Bytes,
            description);
        return ParseCanonicalTimestamp(value, description);
    }

    private static DateTimeOffset? ReadOptionalTimestamp(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description)
    {
        var value = ReadOptionalText(
            reader,
            valueOrdinal,
            lengthOrdinal,
            MaximumTimestampUtf8Bytes,
            description);
        return value is null ? null : ParseCanonicalTimestamp(value, description);
    }

    private static DateTimeOffset ParseCanonicalTimestamp(string value, string description)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed) ||
            parsed == default ||
            parsed.Offset != TimeSpan.Zero ||
            !string.Equals(value, Format(parsed), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Persisted {description} is not a canonical UTC timestamp.");
        }

        return parsed;
    }

    private static string ReadRequiredText(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        int maximumUtf8Bytes,
        string description)
    {
        var value = ReadBoundedText(
            reader,
            valueOrdinal,
            lengthOrdinal,
            maximumUtf8Bytes,
            description,
            optional: false)!;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Persisted {description} is empty.");
        }

        return value;
    }

    private static string? ReadOptionalText(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        int maximumUtf8Bytes,
        string description)
    {
        var value = ReadBoundedText(
            reader,
            valueOrdinal,
            lengthOrdinal,
            maximumUtf8Bytes,
            description,
            optional: true);
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Persisted {description} is empty.");
        }

        return value;
    }

    private static string? ReadBoundedText(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        int maximumUtf8Bytes,
        string description,
        bool optional)
    {
        if (reader.IsDBNull(valueOrdinal))
        {
            if (!optional || !reader.IsDBNull(lengthOrdinal))
            {
                throw new InvalidDataException($"Persisted {description} is missing or has inconsistent metadata.");
            }

            return null;
        }

        if (reader.GetValue(lengthOrdinal) is not long byteLength ||
            byteLength is < 1 ||
            byteLength > maximumUtf8Bytes ||
            reader.GetValue(valueOrdinal) is not string value)
        {
            throw new InvalidDataException($"Persisted {description} is not bounded UTF-8 text.");
        }

        int actualByteLength;
        try
        {
            actualByteLength = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException($"Persisted {description} contains invalid Unicode.", exception);
        }

        if (actualByteLength != byteLength)
        {
            throw new InvalidDataException($"Persisted {description} has inconsistent UTF-8 length.");
        }

        return value;
    }

    private static bool ReadBoolean(
        SqliteDataReader reader,
        int valueOrdinal,
        int typeOrdinal,
        string description)
    {
        var value = ReadRequiredInt64(reader, valueOrdinal, typeOrdinal, 0, 1, description);
        return value == 1;
    }

    private static int ReadRequiredInt32(
        SqliteDataReader reader,
        int valueOrdinal,
        int typeOrdinal,
        int minimum,
        int maximum,
        string description) =>
        checked((int)ReadRequiredInt64(reader, valueOrdinal, typeOrdinal, minimum, maximum, description));

    private static long ReadRequiredInt64(
        SqliteDataReader reader,
        int valueOrdinal,
        int typeOrdinal,
        long minimum,
        long maximum,
        string description)
    {
        var value = ReadOptionalInteger(reader, valueOrdinal, typeOrdinal, description);
        if (value is not { } integer || integer < minimum || integer > maximum)
        {
            throw new InvalidDataException($"Persisted {description} is outside its integer bounds.");
        }

        return integer;
    }

    private static int? ReadOptionalInt32(
        SqliteDataReader reader,
        int valueOrdinal,
        int typeOrdinal,
        int minimum,
        int maximum,
        string description)
    {
        var value = ReadOptionalInt64(
            reader,
            valueOrdinal,
            typeOrdinal,
            minimum,
            maximum,
            description);
        return value is null ? null : checked((int)value.Value);
    }

    private static long? ReadOptionalInt64(
        SqliteDataReader reader,
        int valueOrdinal,
        int typeOrdinal,
        long minimum,
        long maximum,
        string description)
    {
        var value = ReadOptionalInteger(reader, valueOrdinal, typeOrdinal, description);
        if (value is { } integer && (integer < minimum || integer > maximum))
        {
            throw new InvalidDataException($"Persisted {description} is outside its integer bounds.");
        }

        return value;
    }

    private static long? ReadOptionalInteger(
        SqliteDataReader reader,
        int valueOrdinal,
        int typeOrdinal,
        string description)
    {
        if (reader.GetValue(typeOrdinal) is not string storageClass)
        {
            throw new InvalidDataException($"Persisted {description} has invalid storage metadata.");
        }

        if (string.Equals(storageClass, "null", StringComparison.Ordinal))
        {
            if (!reader.IsDBNull(valueOrdinal))
            {
                throw new InvalidDataException($"Persisted {description} has inconsistent storage metadata.");
            }

            return null;
        }

        if (!string.Equals(storageClass, "integer", StringComparison.Ordinal) ||
            reader.GetValue(valueOrdinal) is not long value)
        {
            throw new InvalidDataException($"Persisted {description} is not an integer.");
        }

        return value;
    }

    private static bool IsPersistedValueFailure(Exception exception) =>
        exception is not InvalidDataException and
        (ArgumentException or FormatException or OverflowException or InvalidCastException);
}

/// <summary>Captures SQLite's chosen access paths for each latency-critical V2 read.</summary>
public sealed class SqliteQueryPlanAuditor(SqliteConnectionFactory connectionFactory)
{
    // Declared before Queries: static initializers run in textual order.
    private static readonly ImmutableArray<(string Name, object Value)> QuestProgressScope =
        [("$profileId", "00000000-0000-0000-0000-000000000001"), ("$gameMode", "Regular"), ("$generation", "1")];

    private static readonly ImmutableArray<QueryFamily> Queries =
    [
        new(
            "item",
            typeof(SqliteItemRepository),
            nameof(SqliteItemRepository.GetAsync),
            [new("exact-item", SqliteItemRepository.ExactItemSql, true, [("$itemId", "item")])]),
        new(
            "quest",
            typeof(SqliteQuestCatalog),
            nameof(SqliteQuestCatalog.GetAsync),
            [new("catalog-tasks", SqliteQuestCatalog.TaskCatalogSql, true,
                [("$sourceKey", "json.tarkov.dev/tasks"), ("$sourceMode", "regular"), ("$language", "en")])]),
        new(
            "price",
            typeof(SqlitePriceHistoryRepository),
            nameof(SqlitePriceHistoryRepository.GetAsync),
            [new("item-history", SqlitePriceHistoryRepository.PriceHistorySql, true,
                [("$itemId", "item"), ("$sinceUtc", "2026-01-01T00:00:00.0000000+00:00")])]),
        new(
            "map",
            typeof(SqliteMapDefinitionCache),
            nameof(SqliteMapDefinitionCache.GetAsync),
            [
                new("catalog-scan", SqliteMapDefinitionCache.MapCatalogSql, false, []),
                new("extracts-by-map", SqliteMapDefinitionCache.MapExtractsSql, true, [("$mapId", "map")]),
            ]),
        new(
            "history",
            typeof(SqliteRaidHistoryService),
            nameof(SqliteRaidHistoryService.ListAsync),
            [
                new("history-list", SqliteRaidHistoryService.RaidHistoryListSql, false, []),
                new("map-trails", SqliteRaidHistoryService.MapTrailsSql, true,
                    [("$mapId", "map"), ("$limit", 100)]),
            ]),
        new(
            "profile",
            typeof(SqliteProfileWorkspaceStore),
            nameof(SqliteProfileWorkspaceStore.ReadAsync),
            [
                new("workspace-head", SqliteProfileWorkspaceStore.ProfileWorkspaceSql, true, []),
                new("profile-contexts", SqliteProfileWorkspaceStore.ProfileContextsSql, false, []),
            ]),
        new(
            "craft",
            typeof(SqliteCraftPlanningCatalog),
            nameof(SqliteCraftPlanningCatalog.GetByStationAsync),
            [
                new("station-headers", SqliteCraftPlanningCatalog.StationCraftHeadersSql, true, [("$scope", "station")]),
                new("station-requirements", SqliteCraftPlanningCatalog.StationCraftRequirementsSql, true, [("$scope", "station")]),
                new("station-outputs", SqliteCraftPlanningCatalog.StationCraftOutputsSql, true, [("$scope", "station")]),
                new("station-history", SqliteCraftPlanningCatalog.StationCraftHistorySql, true,
                    [("$scope", "station"), ("$limit", 100)]),
            ]),
        new(
            "quest-progress",
            typeof(SqliteQuestProgressStore),
            nameof(SqliteQuestProgressStore.GetAsync),
            [
                new("profile-revision", SqliteQuestProgressStore.ProfileRevisionSql, true, QuestProgressScope),
                new("task-states", SqliteQuestProgressStore.TaskStatesSql, true, QuestProgressScope),
                new("objective-states", SqliteQuestProgressStore.ObjectiveStatesSql, true, QuestProgressScope),
                new("item-holdings", SqliteQuestProgressStore.ItemHoldingsSql, true, QuestProgressScope),
                new("pins", SqliteQuestProgressStore.PinsSql, true, QuestProgressScope),
            ]),
        new(
            "requirements",
            typeof(SqliteRequirementCatalog),
            nameof(SqliteRequirementCatalog.GetQuestRequirementsAsync),
            [
                // Both are loaded whole once per sync and cached: the projection is every requirement.
                new("quest-items", SqliteRequirementCatalog.QuestItemRequirementsSql, false, []),
                new("hideout-items", SqliteRequirementCatalog.HideoutItemRequirementsSql, false, []),
            ]),
        new(
            "item-search",
            typeof(SqliteItemRepository),
            nameof(SqliteItemRepository.SearchAsync),
            [
                new("exact-name", SqliteItemRepository.ExactNameCandidatesSql, true, [("$query", "salewa")]),
                new("full-text", SqliteItemRepository.FullTextCandidatesSql, false, [("$query", "\"salewa\"*")]),
                // Typo tolerance scores every name in memory, so this one reads the table by design.
                new("fuzzy-names", SqliteItemRepository.FuzzyCandidatesSql, false, []),
            ]),
    ];

    public async Task<ImmutableArray<SqliteQueryPlanEvidence>> CaptureAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var evidence = ImmutableArray.CreateBuilder<SqliteQueryPlanEvidence>(Queries.Length);
        foreach (var family in Queries)
        {
            EnsureReaderExists(family.ReaderType, family.ReaderMethod);
            var statements = ImmutableArray.CreateBuilder<SqliteQueryPlanStatementEvidence>(family.Statements.Length);
            foreach (var statement in family.Statements)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"EXPLAIN QUERY PLAN {statement.Sql}";
                foreach (var (name, value) in statement.Parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                var steps = ImmutableArray.CreateBuilder<string>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    steps.Add(reader.GetString(3));
                }

                statements.Add(new(statement.Name, statement.Sql, statement.IndexRequired, steps.ToImmutable()));
            }

            evidence.Add(new(
                family.Name,
                $"{family.ReaderType.FullName}.{family.ReaderMethod}",
                statements.ToImmutable()));
        }

        return evidence.ToImmutable();
    }

    private static void EnsureReaderExists(Type readerType, string readerMethod)
    {
        if (!SqliteProductionReadPath.ReaderExists(readerType, readerMethod))
        {
            throw new InvalidOperationException(
                $"Query-plan evidence names missing production reader '{readerType.FullName}.{readerMethod}'.");
        }
    }

    private sealed record QueryFamily(
        string Name,
        Type ReaderType,
        string ReaderMethod,
        ImmutableArray<QueryStatement> Statements);

    private sealed record QueryStatement(
        string Name,
        string Sql,
        bool IndexRequired,
        ImmutableArray<(string Name, object Value)> Parameters);
}

public sealed record SqliteUnreadSchemaMember(
    string Table,
    string Column,
    string Reason = "no-production-read-path",
    string? ReaderPath = null);

/// <summary>A schema-read claim anchored to a method on a compiled production repository.</summary>
public sealed record SqliteProductionReadPath(
    string Table,
    Type ReaderType,
    string ReaderMethod,
    ImmutableArray<string> Columns)
{
    public string DisplayName => $"{ReaderType.FullName}.{ReaderMethod}";

    internal static bool ReaderExists(Type readerType, string readerMethod) =>
        readerType.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
            .Any(method => string.Equals(method.Name, readerMethod, StringComparison.Ordinal));
}

/// <summary>
/// Ratchets every V2 column against the columns returned by a deliberate production read path.
/// Writes do not count: a persisted value with no way back out is reported as unread.
/// </summary>
public sealed class SqliteV2UnreadSchemaAuditor
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ImmutableArray<SqliteProductionReadPath> _readPaths;

    private static readonly ImmutableArray<string> V2Tables = Regex.Matches(
            SqliteMigrationRunner.ReadFixture("0011_v2_data_platform").UpgradeSql,
            @"(?im)^\s*CREATE TABLE\s+([a-z][a-z0-9_]*)\s*\(")
        .Select(match => match.Groups[1].Value)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToImmutableArray();

    public static ImmutableArray<SqliteProductionReadPath> DefaultReadPaths { get; } =
    [
        new("raw_endpoint_bodies", typeof(SqliteTarkovDevResponseCache), nameof(SqliteTarkovDevResponseCache.GetAsync),
            ["content_sha256", "compression", "compressed_body", "uncompressed_bytes", "created_utc"]),
        new("raw_endpoint_bodies", typeof(SqliteTarkovDevResponseCache), nameof(SqliteTarkovDevResponseCache.InspectAsync),
            ["compressed_bytes"]),
        new("http_response_cache", typeof(SqliteTarkovDevResponseCache), nameof(SqliteTarkovDevResponseCache.GetAsync),
            ["cache_key", "content_sha256", "cached_utc", "etag", "last_modified"]),
        new("http_response_cache", typeof(SqliteTarkovDevResponseCache), nameof(SqliteTarkovDevResponseCache.InspectAsync),
            ["last_accessed_utc"]),
        new("item_metrics_v2", typeof(SqliteItemFactCatalog), nameof(SqliteItemFactCatalog.GetLoadoutFactsAsync),
            ["item_id", "weight_kg", "flea_price_roubles", "trader_value_roubles"]),
        new("price_history_unresolved_time", typeof(SqlitePriceHistoryRepository), nameof(SqlitePriceHistoryRepository.GetUnresolvedAsync),
            ["item_id", "source_ordinal", "flea_price", "trader_value", "source", "raw_json"]),
        new("dataset_sync_runs", typeof(SqliteSyncStateRepository), nameof(SqliteSyncStateRepository.ListDatasetSyncRunsAsync),
            ["publication_order", "run_id", "game_mode", "language", "started_utc", "completed_utc", "state", "endpoint_count", "successful_count", "stale_count", "refused_count", "failure_count", "detail"]),
        new("dataset_publications", typeof(SqliteSyncStateRepository), nameof(SqliteSyncStateRepository.ListDatasetPublicationsAsync),
            ["publication_id", "run_id", "source_key", "game_mode", "language", "state", "content_sha256", "record_count", "attempted_utc", "published_utc", "detail", "previous_lkg_publication_id"]),
        new("dataset_heads", typeof(SqliteSyncStateRepository), nameof(SqliteSyncStateRepository.GetDatasetHeadAsync),
            ["source_key", "game_mode", "language", "visible_publication_id", "last_known_good_publication_id", "state", "updated_utc"]),
        new("dataset_endpoint_materializations", typeof(SqliteSyncStateRepository), nameof(SqliteSyncStateRepository.GetEndpointMaterializationAsync),
            ["source_key", "claimed_publication_order", "claimed_run_id", "materialized_publication_order", "materialized_run_id", "materialized_game_mode", "materialized_language", "materialized_publication_id"]),
        new("profile_workspaces", typeof(SqliteProfileWorkspaceStore), nameof(SqliteProfileWorkspaceStore.ReadAsync),
            ["workspace_key", "revision", "active_profile_id"]),
        new("profile_contexts", typeof(SqliteProfileWorkspaceStore), nameof(SqliteProfileWorkspaceStore.ReadAsync),
            ["profile_id", "generation", "name", "game_mode", "wipe_season", "language", "region", "time_zone", "data_snapshot_id", "data_snapshot_published_utc", "level", "lifecycle", "updated_utc", "extension_json"]),
        new("profile_trader_progress_v2", typeof(SqliteProfileWorkspaceStore), nameof(SqliteProfileWorkspaceStore.ReadAsync), ["profile_id", "trader_id", "level"]),
        new("profile_completed_tasks_v2", typeof(SqliteProfileWorkspaceStore), nameof(SqliteProfileWorkspaceStore.ReadAsync), ["profile_id", "task_id"]),
        new("profile_objective_progress_v2", typeof(SqliteProfileWorkspaceStore), nameof(SqliteProfileWorkspaceStore.ReadAsync), ["profile_id", "objective_id", "progress_count"]),
        new("profile_hideout_progress_v2", typeof(SqliteProfileWorkspaceStore), nameof(SqliteProfileWorkspaceStore.ReadAsync), ["profile_id", "station_id", "level"]),
        new("profile_wishlist_v2", typeof(SqliteProfileWorkspaceStore), nameof(SqliteProfileWorkspaceStore.ReadAsync), ["profile_id", "item_id"]),
        new("profile_owned_counts_v2", typeof(SqliteProfileWorkspaceStore), nameof(SqliteProfileWorkspaceStore.ReadAsync), ["profile_id", "item_id", "item_count"]),
        new("profile_event_states_v2", typeof(SqliteProfileWorkspaceStore), nameof(SqliteProfileWorkspaceStore.ReadAsync), ["profile_id", "item_id", "state"]),
        new("profile_item_overrides_v2", typeof(SqliteProfileWorkspaceStore), nameof(SqliteProfileWorkspaceStore.ReadAsync), ["profile_id", "item_id", "value"]),
        new("profile_pins_v2", typeof(SqliteProfileWorkspaceStore), nameof(SqliteProfileWorkspaceStore.ReadAsync), ["profile_id", "target_kind", "target_id", "sort_order", "note"]),
        new("durable_outbox", typeof(SqliteOutboxStore), nameof(SqliteOutboxStore.ListAsync),
            ["operation_id", "idempotency_key", "correlation_id", "feature_id", "command_kind", "version_major", "version_minor", "aggregate_id", "aggregate_sequence", "created_utc", "not_before_utc", "expires_utc", "payload", "max_attempts", "attempt_timeout_ticks", "initial_retry_delay_ticks", "max_retry_delay_ticks", "backoff_factor", "delivery_state", "attempt_count", "next_attempt_utc", "lease_token", "lease_expires_utc", "last_fault_json", "completed_utc", "dead_lettered_utc"]),
        new("outbox_aggregate_sequences", typeof(SqliteOutboxStore), nameof(SqliteOutboxStore.ReadAggregateSequenceHeadsAsync), ["aggregate_id", "next_sequence"]),
        new("outbox_target_operations", typeof(SqliteRaidHistoryService), nameof(SqliteRaidHistoryService.ApplyOnceAsync), ["operation_id", "command_kind", "target_id"]),
        new("observed_inventory_snapshots", typeof(SqliteV2DataStore), nameof(SqliteV2DataStore.ReadCurrentInventoryAsync),
            ["snapshot_id", "profile_id", "generation", "game_mode", "data_snapshot_id", "observed_utc", "recorded_utc", "source", "producer_version", "coverage", "confidence", "is_current", "payload_json", "extension_json"]),
        new("observed_inventory_nodes", typeof(SqliteV2DataStore), nameof(SqliteV2DataStore.ReadCurrentInventoryAsync),
            ["snapshot_id", "node_id", "parent_node_id", "node_kind", "item_id", "grid_x", "grid_y", "width", "height", "item_count", "weight_kg", "confidence", "raw_json"]),
        new("raid_field_history", typeof(SqliteV2DataStore), nameof(SqliteV2DataStore.ListRaidFieldsAsync),
            ["id", "raid_id", "field_name", "value_json", "provenance_kind", "source", "observed_utc", "recorded_utc", "confidence"]),
        new("craft_history", typeof(SqliteCraftPlanningCatalog), nameof(SqliteCraftPlanningCatalog.GetByStationAsync),
            ["history_id", "craft_id", "station_id", "station_level", "observed_utc", "recorded_utc", "output_item_id", "output_count", "estimated_cost_roubles", "estimated_yield_roubles", "source", "payload_json"]),
        new("loadout_plans", typeof(SqliteV2DataStore), nameof(SqliteV2DataStore.ReadLoadoutPlanAsync),
            ["plan_id", "profile_id", "generation", "game_mode", "name", "revision", "updated_utc", "payload_json", "extension_json"]),
        new("model_snapshots", typeof(SqliteV2DataStore), nameof(SqliteV2DataStore.ListHistoricalModelSnapshotsAsync),
            ["model_snapshot_id", "profile_id", "generation", "game_mode", "model_kind", "presentation_kind", "source", "observed_utc", "data_through_utc", "generated_utc", "coverage", "confidence", "calibration", "model_version", "payload_json", "extension_json"]),
        new("retention_policies", typeof(SqliteV2DataStore), nameof(SqliteV2DataStore.ReadRetentionPolicyAsync),
            ["policy_key", "screenshot_retention_enabled", "screenshot_retention_hours", "debug_capture_enabled", "data_retention_days", "updated_utc", "extension_json"]),
        new("local_json_recovery", typeof(SqliteV2DataStore), nameof(SqliteV2DataStore.ReadLocalJsonRecoveryAsync),
            ["document_key", "state", "detected_utc", "content_sha256", "diagnostic_code"]),
        new("maintenance_schedules", typeof(SqliteDataPlatformMaintenance), nameof(SqliteDataPlatformMaintenance.ListSchedulesAsync),
            ["operation", "enabled", "interval_hours", "last_run_utc", "next_run_utc", "updated_utc"]),
        new("maintenance_history", typeof(SqliteDataPlatformMaintenance), nameof(SqliteDataPlatformMaintenance.ListHistoryAsync),
            ["run_id", "operation", "dry_run", "started_utc", "completed_utc", "reclaimed_bytes", "affected_rows", "status", "diagnostic_code"]),
    ];

    public SqliteV2UnreadSchemaAuditor(
        SqliteConnectionFactory connectionFactory,
        IEnumerable<SqliteProductionReadPath>? readPaths = null)
    {
        _connectionFactory = connectionFactory;
        _readPaths = readPaths?.ToImmutableArray() ?? DefaultReadPaths;
    }

    public async Task<ImmutableArray<SqliteUnreadSchemaMember>> AuditAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var unread = ImmutableArray.CreateBuilder<SqliteUnreadSchemaMember>();
        var validPaths = new List<SqliteProductionReadPath>();
        foreach (var path in _readPaths)
        {
            if (!SqliteProductionReadPath.ReaderExists(path.ReaderType, path.ReaderMethod))
            {
                unread.Add(new(path.Table, "*", "declared-reader-not-found", path.DisplayName));
                continue;
            }

            validPaths.Add(path);
        }

        var pathsByTable = validPaths
            .GroupBy(path => path.Table, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(path => path.Columns).ToImmutableHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        foreach (var tableName in V2Tables)
        {
            if (!pathsByTable.TryGetValue(tableName, out var readColumns))
            {
                unread.Add(new(tableName, "*"));
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info([{tableName}]);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var schemaColumns = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var column = reader.GetString(1);
                schemaColumns.Add(column);
                if (!readColumns.Contains(column)) unread.Add(new(tableName, column));
            }

            if (schemaColumns.Count == 0)
            {
                unread.Add(new(tableName, "<missing-table>"));
                continue;
            }

            foreach (var declaredColumn in readColumns.Except(schemaColumns, StringComparer.Ordinal))
            {
                unread.Add(new(tableName, declaredColumn, "declared-column-not-in-schema"));
            }
        }

        foreach (var staleDeclaration in pathsByTable.Keys.Except(V2Tables, StringComparer.Ordinal))
        {
            unread.Add(new(staleDeclaration, "<not-in-v2-migration>", "declared-table-not-in-schema"));
        }

        return unread
            .OrderBy(member => member.Table, StringComparer.Ordinal)
            .ThenBy(member => member.Column, StringComparer.Ordinal)
            .ThenBy(member => member.Reason, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}
