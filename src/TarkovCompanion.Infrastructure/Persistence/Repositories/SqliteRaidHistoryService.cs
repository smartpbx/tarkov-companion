using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

public sealed class SqliteRaidHistoryService(
    SqliteConnectionFactory connectionFactory,
    TimeProvider? timeProvider = null) : IRaidHistoryService, IRaidHistoryOperationStore
{
    internal const string RaidHistoryListSql = """
        SELECT id, profile_id, map_id, mode, start_utc, end_utc, outcome, notes
        FROM raids
        WHERE deleted_utc IS NULL
        ORDER BY COALESCE(start_utc, end_utc) DESC, id;
        """;
    internal const string MapTrailsSql = """
        SELECT raid.id, raid.start_utc, event.payload_json
        FROM raid_events AS event
        JOIN raids AS raid ON raid.id = event.raid_id
        WHERE event.type = 'position'
          AND raid.id IN (
              SELECT id FROM raids
              WHERE map_id IS NOT NULL AND lower(map_id) = lower($mapId)
              ORDER BY start_utc DESC
              LIMIT $limit
          )
        ORDER BY raid.start_utc DESC, event.timestamp_utc, event.id;
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly AsyncLocal<OperationTransaction?> _operation = new();

    public async Task ApplyOnceAsync(
        OperationId operationId,
        OutboxCommandKind commandKind,
        Guid raidId,
        Func<CancellationToken, Task> apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (!operationId.IsDefined) throw new ArgumentException("An operation id is required.", nameof(operationId));
        if (!Enum.IsDefined(commandKind)) throw new ArgumentOutOfRangeException(nameof(commandKind));
        if (raidId == Guid.Empty) throw new ArgumentException("A raid id is required.", nameof(raidId));
        if (_operation.Value is not null) throw new InvalidOperationException("Nested raid-history operations are not supported.");
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT command_kind, target_id FROM outbox_target_operations WHERE operation_id = $id;";
            exists.Parameters.AddWithValue("$id", operationId.ToString());
            await using var reader = await exists.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetValue(0) is not long storedKind ||
                    reader.GetValue(1) is not string storedTarget ||
                    !Guid.TryParseExact(storedTarget, "D", out var storedRaidId))
                {
                    throw new InvalidDataException("The raid-history operation ledger contains an invalid ownership tuple.");
                }

                if (storedKind != (int)commandKind || storedRaidId != raidId)
                {
                    throw new InvalidOperationException(
                        "The outbox operation id is already bound to a different raid-history command.");
                }

                return;
            }
        }

        _operation.Value = new(connection, transaction);
        try
        {
            await apply(cancellationToken).ConfigureAwait(false);
            await using var applied = connection.CreateCommand();
            applied.Transaction = transaction;
            applied.CommandText = """
                INSERT INTO outbox_target_operations(operation_id, command_kind, target_id, applied_utc)
                VALUES ($operation, $kind, $target, $applied);
                """;
            applied.Parameters.AddWithValue("$operation", operationId.ToString());
            applied.Parameters.AddWithValue("$kind", (int)commandKind);
            applied.Parameters.AddWithValue("$target", raidId.ToString("D"));
            applied.Parameters.AddWithValue("$applied", Format(_timeProvider.GetUtcNow()));
            await applied.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Value = null;
        }
    }

    public async Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raid);
        if (raid.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(raid.Mode))
        {
            throw new ArgumentException("Raid history requires a profile id and mode.", nameof(raid));
        }

        var raidId = raid.Id == Guid.Empty ? Guid.NewGuid() : raid.Id;
        var now = _timeProvider.GetUtcNow();
        if (_operation.Value is { } operation)
        {
            await StartCoreAsync(operation.Connection, operation.Transaction, raid with { Id = raidId }, now, cancellationToken).ConfigureAwait(false);
            return raidId;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await StartCoreAsync(connection, transaction, raid with { Id = raidId }, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return raidId;
    }

    private static async Task StartCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RaidHistoryEntry raid,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var profile = connection.CreateCommand())
        {
            profile.Transaction = transaction;
            profile.CommandText = """
                INSERT INTO player_profiles(id, name, game_mode, faction, level, created_utc, updated_utc)
                VALUES ($id, 'Local profile', $mode, 'Unknown', 1, $now, $now)
                ON CONFLICT(id) DO NOTHING;
                """;
            profile.Parameters.AddWithValue("$id", raid.ProfileId.ToString("D"));
            profile.Parameters.AddWithValue("$mode", raid.Mode);
            profile.Parameters.AddWithValue("$now", Format(now));
            await profile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO raids(id, profile_id, map_id, mode, start_utc, end_utc, outcome, notes)
                VALUES ($id, $profileId, $mapId, $mode, $startUtc, $endUtc, $outcome, $notes);
                """;
            BindRaid(command, raid);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RecordEventAsync(
        Guid raidId,
        string type,
        DateTimeOffset timestampUtc,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        using var payload = JsonDocument.Parse(payloadJson);
        if (_operation.Value is { } operation)
        {
            await RecordEventCoreAsync(operation.Connection, operation.Transaction, raidId, type, timestampUtc, payloadJson, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await RecordEventCoreAsync(connection, null, raidId, type, timestampUtc, payloadJson, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecordEventCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid raidId,
        string type,
        DateTimeOffset timestampUtc,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO raid_events(raid_id, timestamp_utc, type, payload_json)
            VALUES ($raidId, $timestampUtc, $type, $payloadJson);
            """;
        command.Parameters.AddWithValue("$raidId", raidId.ToString("D"));
        command.Parameters.AddWithValue("$timestampUtc", Format(timestampUtc));
        command.Parameters.AddWithValue("$type", type.Trim());
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one raid's path back out of the events it recorded while it was running.
    /// </summary>
    /// <remarks>
    /// Stored as ordinary raid events with the type "position", not in the raid_positions
    /// table beside them, which has been empty since the schema was written. The events are
    /// where the data actually is, so that is where this reads it from; filling a second table
    /// to say the same thing twice would be work in the service of tidiness.
    ///
    /// A payload that will not parse is skipped rather than failing the read. One unreadable
    /// screenshot costs one point on a trail, and a trail missing a point is still a trail.
    /// </remarks>
    /// <summary>
    /// Every raid's trail on one map, newest first.
    /// </summary>
    /// <remarks>
    /// The same rows <see cref="ListPositionsAsync"/> reads, asked for by map rather than by
    /// raid — one query joined to raids for the map and the start time, rather than one query
    /// per raid, because a player who has run Customs two hundred times would otherwise cost
    /// two hundred round trips to draw one layer.
    ///
    /// Grouped rather than flattened. The points of one raid are a path and the points of two
    /// are not: joining the last position of Tuesday to the first of Wednesday would draw a
    /// line across the map that nobody walked.
    ///
    /// The limit counts raids, not points, because that is what the caller is choosing between.
    /// </remarks>
    /// <summary>
    /// One raid's events of one kind, oldest first, as they were stored.
    /// </summary>
    /// <remarks>
    /// The same rows every other read here uses, asked for by kind. Nothing could read these
    /// back at all, so a raid's own record was reachable only by the application that happened
    /// to be running when it was written.
    ///
    /// A null payload is skipped rather than returned. Every caller parses what comes out of
    /// here, and handing them a null to check is handing every one of them the same check.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ListEventPayloadsAsync(
        Guid raidId,
        string type,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM raid_events
            WHERE raid_id = $raidId AND type = $type
            ORDER BY timestamp_utc, id;
            """;
        command.Parameters.AddWithValue("$raidId", raidId.ToString("D"));
        command.Parameters.AddWithValue("$type", type);

        var payloads = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                payloads.Add(reader.GetString(0));
            }
        }

        return payloads;
    }

    public async Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(
        string mapId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        if (limit <= 0)
        {
            return [];
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // The inner select picks the raids; the join then takes every position belonging to
        // them. Ordering by start descending and then by event time ascending gives newest
        // raid first with each raid's own points in the order they were taken.
        command.CommandText = MapTrailsSql;
        command.Parameters.AddWithValue("$mapId", mapId.Trim());
        command.Parameters.AddWithValue("$limit", limit);

        var trails = new List<RaidTrail>();
        var points = new List<ScreenshotPosition>();
        var currentId = Guid.Empty;
        DateTimeOffset? currentStart = null;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParse(reader.GetString(0), out var raidId) || reader.IsDBNull(2))
            {
                continue;
            }

            if (raidId != currentId)
            {
                Flush();
                currentId = raidId;
                currentStart = reader.IsDBNull(1) ? null : ReadTime(reader.GetString(1));
            }

            // The same refusal the single-raid read makes: a payload that is valid JSON but is
            // not one of these deserialises to a record of defaults, which draws a point at the
            // origin that nobody stood on. Every real one came from a screenshot and has its
            // name.
            try
            {
                if (JsonSerializer.Deserialize<ScreenshotPosition>(reader.GetString(2), JsonOptions)
                    is { Filename.Length: > 0 } position)
                {
                    points.Add(position);
                }
            }
            catch (JsonException)
            {
                // One unreadable screenshot costs one point on one trail.
            }
        }

        Flush();
        return trails;

        void Flush()
        {
            // A raid whose every position was unreadable is not an empty trail, it is no trail.
            if (currentId != Guid.Empty && points.Count > 0)
            {
                trails.Add(new(currentId, currentStart, [.. points]));
            }

            points.Clear();
        }
    }

    /// <summary>Reads a stored instant, or nothing rather than a wrong one.</summary>
    private static DateTimeOffset? ReadTime(string stored) =>
        DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    public async Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(
        Guid raidId,
        CancellationToken cancellationToken)
    {
        var positions = new List<ScreenshotPosition>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM raid_events
            WHERE raid_id = $raidId AND type = 'position'
            ORDER BY timestamp_utc, id;
            """;
        command.Parameters.AddWithValue("$raidId", raidId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            try
            {
                // A payload that is valid JSON but is not one of these deserialises to a
                // record of defaults rather than throwing: no filename, a zero timestamp and
                // a position at the origin. Drawn on a map that is a point somebody never
                // stood on, which is the failure this codebase keeps having to refuse. Every
                // real one came from a screenshot and therefore has its name.
                if (JsonSerializer.Deserialize<ScreenshotPosition>(reader.GetString(0), JsonOptions)
                    is { Filename.Length: > 0 } position)
                {
                    positions.Add(position);
                }
            }
            catch (JsonException)
            {
                // One unreadable screenshot costs one point on a trail.
            }
        }

        return positions;
    }

    public async Task EndAsync(
        Guid raidId,
        DateTimeOffset endUtc,
        string? outcome,
        string? notes,
        CancellationToken cancellationToken)
    {
        if (_operation.Value is { } operation)
        {
            await EndCoreAsync(operation.Connection, operation.Transaction, raidId, endUtc, outcome, notes, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EndCoreAsync(connection, null, raidId, endUtc, outcome, notes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a player's correction, and keeps the fact that it was theirs.
    /// </summary>
    /// <remarks>
    /// The update and a <c>correction</c> event go in one transaction, so a field never says it was
    /// corrected without the record of what it said before, nor the reverse. Saving what is already
    /// there records nothing. Debrief and the export read that event to label the field manual.
    /// </remarks>
    public async Task CorrectAsync(
        Guid raidId,
        string? outcome,
        string? notes,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        RaidHistoryEntry before;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT id, profile_id, map_id, mode, start_utc, end_utc, outcome, notes
                FROM raids
                WHERE id = $id;
                """;
            read.Parameters.AddWithValue("$id", raidId.ToString("D"));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new KeyNotFoundException($"Raid '{raidId:D}' does not exist.");
            }

            before = ReadRaid(reader);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE raids
                SET outcome = $outcome, notes = $notes
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", raidId.ToString("D"));
            command.Parameters.AddWithValue("$outcome", (object?)outcome ?? DBNull.Value);
            command.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var now = _timeProvider.GetUtcNow();
        if (RaidCorrection.Between(before, outcome, notes, now) is { } correction)
        {
            await RecordEventCoreAsync(
                connection,
                transaction,
                raidId,
                RaidCorrection.EventType,
                now,
                correction.ToPayload(),
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EndCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid raidId,
        DateTimeOffset endUtc,
        string? outcome,
        string? notes,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE raids
            SET end_utc = $endUtc, outcome = $outcome, notes = $notes
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", raidId.ToString("D"));
        command.Parameters.AddWithValue("$endUtc", Format(endUtc));
        command.Parameters.AddWithValue("$outcome", (object?)outcome ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Raid '{raidId:D}' does not exist.");
        }
    }

    public async Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = RaidHistoryListSql;
        var entries = new List<RaidHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(ReadRaid(reader));
        }

        return entries;
    }

    /// <summary>
    /// Marks raids deleted rather than removing them: <see cref="ListAsync"/> already excludes a
    /// marked row, and its own per-map stats along with it, which is what lets Debrief show a
    /// delete as done immediately while still being able to undo it.
    /// </summary>
    public async Task SoftDeleteAsync(IReadOnlyCollection<Guid> raidIds, DateTimeOffset deletedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raidIds);
        if (raidIds.Count == 0)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE raids SET deleted_utc = $deletedUtc WHERE id = $id AND deleted_utc IS NULL;";
            command.Parameters.Add("$deletedUtc", SqliteType.Text);
            command.Parameters.Add("$id", SqliteType.Text);
            foreach (var raidId in raidIds)
            {
                command.Parameters["$deletedUtc"].Value = Format(deletedUtc);
                command.Parameters["$id"].Value = raidId.ToString("D");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Undoes a soft delete: only a raid still marked deleted is cleared, so an undo pressed twice is harmless.</summary>
    public async Task RestoreDeletedAsync(IReadOnlyCollection<Guid> raidIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raidIds);
        if (raidIds.Count == 0)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE raids SET deleted_utc = NULL WHERE id = $id AND deleted_utc IS NOT NULL;";
            command.Parameters.Add("$id", SqliteType.Text);
            foreach (var raidId in raidIds)
            {
                command.Parameters["$id"].Value = raidId.ToString("D");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hard-deletes every raid still marked deleted, except the ones named — the batch a workspace's
    /// one-press undo still covers. Called at the top of a load, so a raid stays soft-deleted (and
    /// so recoverable) for exactly as long as the undo that covers it could still be pressed: until
    /// another delete starts, or the app is restarted and no undo for it exists any more.
    /// </summary>
    public async Task PurgeDeletedAsync(IReadOnlyCollection<Guid> exceptRaidIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exceptRaidIds);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var toPurge = new List<Guid>();
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT id FROM raids WHERE deleted_utc IS NOT NULL;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = Guid.Parse(reader.GetString(0));
                if (!exceptRaidIds.Contains(id))
                {
                    toPurge.Add(id);
                }
            }
        }

        if (toPurge.Count == 0)
        {
            return;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // raid_events, raid_positions and raid_extracts all cascade on delete (0001_initial.sql).
            command.CommandText = "DELETE FROM raids WHERE id = $id;";
            command.Parameters.Add("$id", SqliteType.Text);
            foreach (var raidId in toPurge)
            {
                command.Parameters["$id"].Value = raidId.ToString("D");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RaidManualMetadata?> GetManualMetadataAsync(Guid raidId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT manual_metadata_json FROM raids WHERE id = $id;";
        command.Parameters.AddWithValue("$id", raidId.ToString("D"));
        return ParseManualMetadata(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reads a stored payload, treating one that will not parse the same as none at all.</summary>
    private static RaidManualMetadata? ParseManualMetadata(object? stored)
    {
        if (stored is not string json || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RaidManualMetadata>(json, JsonOptions);
            return parsed is null || parsed.IsEmpty ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SetManualMetadataAsync(Guid raidId, RaidManualMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE raids SET manual_metadata_json = $json WHERE id = $id;";
        command.Parameters.AddWithValue("$id", raidId.ToString("D"));
        command.Parameters.AddWithValue(
            "$json",
            metadata.IsEmpty ? DBNull.Value : (object)JsonSerializer.Serialize(metadata, JsonOptions));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Raid '{raidId:D}' does not exist.");
        }
    }

    public async Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var records = await LoadExportRecordsAsync(cancellationToken).ConfigureAwait(false);
        await RaidHistoryExport.WriteCsvAsync(destination, records, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var records = await LoadExportRecordsAsync(cancellationToken).ConfigureAwait(false);
        await RaidHistoryExport.WriteJsonAsync(destination, records, _timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Every raid with the scans and corrections recorded against it, from one events query rather
    /// than one per raid, because an export covers the whole history.
    /// </summary>
    private async Task<IReadOnlyList<RaidExportRecord>> LoadExportRecordsAsync(CancellationToken cancellationToken)
    {
        var raids = await ListAsync(cancellationToken).ConfigureAwait(false);
        var scans = new Dictionary<Guid, List<RaidScanFact>>();
        var corrections = new Dictionary<Guid, List<string>>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raid_id, type, payload_json
            FROM raid_events
            WHERE type IN ('scan', 'correction') AND payload_json IS NOT NULL
            ORDER BY timestamp_utc, id;
            """;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader.GetString(0), out var raidId))
                {
                    continue;
                }

                var payload = reader.GetString(2);
                if (reader.GetString(1) == RaidCorrection.EventType)
                {
                    (corrections.TryGetValue(raidId, out var list) ? list : corrections[raidId] = []).Add(payload);
                }
                else if (RaidScanFact.TryParse(payload) is { } scan)
                {
                    (scans.TryGetValue(raidId, out var list) ? list : scans[raidId] = []).Add(scan);
                }
            }
        }

        var manual = new Dictionary<Guid, RaidManualMetadata>();
        await using (var manualCommand = connection.CreateCommand())
        {
            manualCommand.CommandText = "SELECT id, manual_metadata_json FROM raids WHERE manual_metadata_json IS NOT NULL;";
            await using var reader = await manualCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Guid.TryParse(reader.GetString(0), out var raidId)
                    && ParseManualMetadata(reader.GetString(1)) is { } metadata)
                {
                    manual[raidId] = metadata;
                }
            }
        }

        return
        [
            .. raids.Select(raid => new RaidExportRecord(
                raid,
                RaidFactRules.Classify(raid, RaidCorrection.ParseAll(corrections.GetValueOrDefault(raid.Id, []))),
                scans.GetValueOrDefault(raid.Id, []),
                manual.GetValueOrDefault(raid.Id))),
        ];
    }

    private static void BindRaid(SqliteCommand command, RaidHistoryEntry raid)
    {
        command.Parameters.AddWithValue("$id", raid.Id.ToString("D"));
        command.Parameters.AddWithValue("$profileId", raid.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$mapId", (object?)raid.MapId ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", raid.Mode);
        command.Parameters.AddWithValue("$startUtc", raid.StartedUtc is null ? DBNull.Value : Format(raid.StartedUtc.Value));
        command.Parameters.AddWithValue("$endUtc", raid.EndedUtc is null ? DBNull.Value : Format(raid.EndedUtc.Value));
        command.Parameters.AddWithValue("$outcome", (object?)raid.Outcome ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)raid.Notes ?? DBNull.Value);
    }

    private static RaidHistoryEntry ReadRaid(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        ParseNullable(reader, 4),
        ParseNullable(reader, 5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private static DateTimeOffset? ParseNullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record OperationTransaction(SqliteConnection Connection, SqliteTransaction Transaction);
}
