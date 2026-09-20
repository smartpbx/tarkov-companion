using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

public sealed record ObservedInventorySnapshot(
    Guid SnapshotId,
    Guid ProfileId,
    string Generation,
    string GameMode,
    DateTimeOffset RecordedUtc,
    bool IsCurrent,
    RecognitionResultEnvelope<StashRecognition> Recognition,
    string? DataSnapshotId = null);

public sealed record RaidFieldHistoryRecord(
    long Id,
    Guid RaidId,
    string FieldName,
    string ValueJson,
    string ProvenanceKind,
    string Source,
    DateTimeOffset? ObservedUtc,
    DateTimeOffset RecordedUtc,
    double? Confidence);

public sealed record CraftHistoryRecord(
    Guid HistoryId,
    string CraftId,
    string? StationId,
    int? StationLevel,
    DateTimeOffset? ObservedUtc,
    DateTimeOffset RecordedUtc,
    string? OutputItemId,
    double? OutputCount,
    long? EstimatedCostRoubles,
    long? EstimatedYieldRoubles,
    string Source,
    string PayloadJson);

public sealed record LoadoutPlanRecord(
    Guid PlanId,
    Guid ProfileId,
    string Generation,
    string GameMode,
    string Name,
    long Revision,
    DateTimeOffset UpdatedUtc,
    string PayloadJson,
    string ExtensionJson);

public sealed record HistoricalIntelligenceSnapshot<T>(
    Guid ModelSnapshotId,
    Guid? ProfileId,
    string? Generation,
    string? GameMode,
    HistoricalIntelligence<T> Intelligence)
    where T : class, IIntelligencePayload;

public sealed record ModelledIntelligenceSnapshot<T>(
    Guid ModelSnapshotId,
    Guid? ProfileId,
    string? Generation,
    string? GameMode,
    ModelledIntelligence<T> Intelligence)
    where T : class, IIntelligencePayload;

public sealed record DataRetentionPolicy(
    string PolicyKey,
    bool ScreenshotRetentionEnabled,
    int? ScreenshotRetentionHours,
    bool DebugCaptureEnabled,
    int? DataRetentionDays,
    DateTimeOffset UpdatedUtc,
    string ExtensionJson);

public sealed record LocalJsonRecoveryRecord(
    string DocumentKey,
    string State,
    DateTimeOffset DetectedUtc,
    string? ContentSha256,
    string DiagnosticCode);

/// <summary>
/// Inventory and intelligence persistence accepts frozen V2 contract roots. Query projections
/// and flattened metadata are derived from those roots and checked against them again when read.
/// </summary>
public sealed class SqliteV2DataStore(SqliteConnectionFactory connectionFactory)
{
    public const int MaximumContractJsonBytes = 8 * 1024 * 1024;
    public const int MaximumContractStringUtf8Bytes = 1024;
    public const int MaximumRaidFieldReadCount = 1000;
    public const int MaximumCraftHistoryReadCount = 100;
    public const int MaximumInventoryRegions = 256;
    public const int MaximumInventoryContainers = 256;
    public const int MaximumInventoryNodes = 4096;

    public async Task SaveInventorySnapshotAsync(ObservedInventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateInventoryScope(snapshot);
        var recognition = snapshot.Recognition ?? throw new ArgumentNullException(nameof(snapshot.Recognition));
        ValidateInventoryRecognition(recognition);
        var stash = recognition.Result.Value!;
        var provenance = recognition.Result.Provenance;
        var dataSnapshotId = ResolveInventoryDataSnapshotId(snapshot, stash);
        var nodes = ProjectInventoryNodes(stash);
        var payloadJson = SerializeContract(recognition, nameof(snapshot.Recognition));

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.IsCurrent)
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE observed_inventory_snapshots SET is_current = 0 WHERE profile_id = $profile AND generation = $generation AND game_mode = $mode;",
                cancellationToken, ("$profile", Id(snapshot.ProfileId)), ("$generation", snapshot.Generation), ("$mode", snapshot.GameMode)).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, """
            INSERT INTO observed_inventory_snapshots(
                snapshot_id, profile_id, generation, game_mode, data_snapshot_id, observed_utc,
                recorded_utc, source, producer_version, coverage, confidence, is_current,
                payload_json, extension_json)
            VALUES ($id, $profile, $generation, $mode, $dataSnapshot, $observed, $recorded,
                    $source, $producer, $coverage, $confidence, $current, $payload, $extension);
            """, cancellationToken,
            ("$id", Id(snapshot.SnapshotId)), ("$profile", Id(snapshot.ProfileId)),
            ("$generation", snapshot.Generation), ("$mode", snapshot.GameMode),
            ("$dataSnapshot", dataSnapshotId), ("$observed", Format(provenance.ObservedUtc)),
            ("$recorded", Format(snapshot.RecordedUtc)), ("$source", provenance.SourceIdentifier),
            ("$producer", provenance.Producer.Version), ("$coverage", provenance.Coverage?.Fraction),
            ("$confidence", provenance.Confidence.Score), ("$current", snapshot.IsCurrent),
            ("$payload", payloadJson), ("$extension", "{}")).ConfigureAwait(false);

        foreach (var node in nodes)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO observed_inventory_nodes(
                    snapshot_id, node_id, parent_node_id, node_kind, item_id, grid_x, grid_y,
                    width, height, item_count, weight_kg, confidence, raw_json)
                VALUES ($snapshot, $node, $parent, $kind, $item, $x, $y, $width, $height,
                        $count, $weight, $confidence, $raw);
                """, cancellationToken,
                ("$snapshot", Id(snapshot.SnapshotId)), ("$node", node.NodeId),
                ("$parent", node.ParentNodeId), ("$kind", node.NodeKind), ("$item", node.ItemId),
                ("$x", node.GridX), ("$y", node.GridY), ("$width", node.Width), ("$height", node.Height),
                ("$count", node.ItemCount), ("$weight", null),
                ("$confidence", node.Confidence), ("$raw", "{}")).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ObservedInventorySnapshot?> ReadCurrentInventoryAsync(
        Guid profileId,
        string generation,
        string gameMode,
        CancellationToken cancellationToken)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A profile id is required.", nameof(profileId));
        }

        ValidateRequiredString(generation, nameof(generation));
        ValidateRequiredString(gameMode, nameof(gameMode));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // A read of one snapshot, not a write: BEGIN DEFERRED gives the same consistent view under WAL
        // without taking the write lock that the default BEGIN IMMEDIATE does, so a page's read is not
        // queued behind a background refresh's write.
        await using var transaction = connection.BeginTransaction(deferred: true);
        RawInventorySnapshot row;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT snapshot_id, data_snapshot_id, observed_utc, recorded_utc, source,
                       producer_version, coverage, confidence, is_current,
                       CASE WHEN typeof(snapshot_id) = 'text'
                            THEN length(CAST(snapshot_id AS BLOB)) ELSE -1 END,
                       CASE WHEN data_snapshot_id IS NULL THEN NULL
                            WHEN typeof(data_snapshot_id) = 'text'
                            THEN length(CAST(data_snapshot_id AS BLOB)) ELSE -1 END,
                       CASE WHEN observed_utc IS NULL THEN NULL
                            WHEN typeof(observed_utc) = 'text'
                            THEN length(CAST(observed_utc AS BLOB)) ELSE -1 END,
                       CASE WHEN typeof(recorded_utc) = 'text'
                            THEN length(CAST(recorded_utc AS BLOB)) ELSE -1 END,
                       CASE WHEN typeof(source) = 'text'
                            THEN length(CAST(source AS BLOB)) ELSE -1 END,
                       CASE WHEN typeof(producer_version) = 'text'
                            THEN length(CAST(producer_version AS BLOB)) ELSE -1 END,
                       CASE WHEN typeof(payload_json) = 'text'
                            THEN length(CAST(payload_json AS BLOB)) ELSE -1 END,
                       payload_json,
                       CASE WHEN typeof(extension_json) = 'text' AND extension_json = '{}'
                            THEN 1 ELSE 0 END
                FROM observed_inventory_snapshots
                WHERE profile_id = $profile AND generation = $generation AND game_mode = $mode AND is_current = 1
                ORDER BY recorded_utc DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$profile", Id(profileId));
            command.Parameters.AddWithValue("$generation", generation);
            command.Parameters.AddWithValue("$mode", gameMode);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            try
            {
                long totalJsonBytes = 0;
                var coverage = ReadOptionalDouble(reader, 6, "inventory coverage");
                var confidence = ReadOptionalDouble(reader, 7, "inventory confidence");
                if (coverage is < 0 or > 1 || confidence is < 0 or > 1)
                {
                    throw new InvalidDataException("Persisted inventory confidence metadata is outside its unit range.");
                }

                var isCurrent = ReadBoolean(reader, 8, "inventory current state");
                var extensionIsCanonical = ReadBoolean(reader, 17, "inventory extension state");
                row = new(
                    ReadRequiredGuid(reader, 0, 9, "inventory snapshot id"),
                    ReadOptionalText(reader, 1, 10, "inventory data snapshot id"),
                    ReadOptionalTime(reader, 2, 11, "inventory observation time"),
                    ReadRequiredTime(reader, 3, 12, "inventory recording time"),
                    ReadRequiredText(reader, 4, 13, "inventory source"),
                    ReadRequiredText(reader, 5, 14, "inventory producer version"),
                    coverage,
                    confidence,
                    ReadJson(reader, 16, 15, ref totalJsonBytes, "inventory payload"),
                    isCurrent && extensionIsCanonical);
            }
            catch (Exception exception) when (IsPersistedValueFailure(exception))
            {
                throw new InvalidDataException("Persisted inventory metadata is invalid.", exception);
            }
        }

        var recognition = DeserializeContract<RecognitionResultEnvelope<StashRecognition>>(
            row.PayloadJson,
            "persisted inventory recognition");
        ValidateInventoryRecognition(recognition);
        ValidateInventoryMetadata(row, recognition);
        var expectedNodes = ProjectInventoryNodes(recognition.Result.Value!)
            .ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var storedNodes = new Dictionary<string, InventoryNodeProjection>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT node_id, parent_node_id, node_kind, item_id, grid_x, grid_y, width, height,
                       item_count, weight_kg, confidence,
                       CASE WHEN typeof(node_id) = 'text'
                            THEN length(CAST(node_id AS BLOB)) ELSE -1 END,
                       CASE WHEN parent_node_id IS NULL THEN NULL
                            WHEN typeof(parent_node_id) = 'text'
                            THEN length(CAST(parent_node_id AS BLOB)) ELSE -1 END,
                       CASE WHEN typeof(node_kind) = 'text'
                            THEN length(CAST(node_kind AS BLOB)) ELSE -1 END,
                       CASE WHEN item_id IS NULL THEN NULL
                            WHEN typeof(item_id) = 'text'
                            THEN length(CAST(item_id AS BLOB)) ELSE -1 END,
                       CASE WHEN typeof(raw_json) = 'text' AND raw_json = '{}'
                            THEN 1 ELSE 0 END
                FROM observed_inventory_nodes
                WHERE snapshot_id = $snapshot
                ORDER BY node_id
                LIMIT $maximum;
                """;
            command.Parameters.AddWithValue("$snapshot", Id(row.SnapshotId));
            command.Parameters.AddWithValue("$maximum", MaximumInventoryNodes + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (storedNodes.Count == MaximumInventoryNodes)
                {
                    throw new InvalidDataException("Persisted inventory nodes exceed the bounded stash projection.");
                }

                var nodeId = ReadRequiredText(reader, 0, 11, "inventory node id");
                var parentNodeId = ReadOptionalText(reader, 1, 12, "inventory parent node id");
                var nodeKind = ReadRequiredText(reader, 2, 13, "inventory node kind");
                var itemId = ReadOptionalText(reader, 3, 14, "inventory item id");
                if (Encoding.UTF8.GetByteCount(nodeId) > 64 ||
                    parentNodeId is not null && Encoding.UTF8.GetByteCount(parentNodeId) > 64 ||
                    Encoding.UTF8.GetByteCount(nodeKind) > 16 ||
                    nodeKind is not ("stash" or "container" or "item" or "unknown") ||
                    !reader.IsDBNull(9) ||
                    !ReadBoolean(reader, 15, "inventory node extension state"))
                {
                    throw new InvalidDataException("Persisted inventory nodes contain data outside the typed stash projection.");
                }

                var confidence = ReadOptionalDouble(reader, 10, "inventory node confidence");
                if (confidence is < 0 or > 1)
                {
                    throw new InvalidDataException("Persisted inventory node confidence is outside its unit range.");
                }

                var node = new InventoryNodeProjection(
                    nodeId,
                    parentNodeId,
                    nodeKind,
                    itemId,
                    ReadOptionalInt32(reader, 4, "inventory grid x"),
                    ReadOptionalInt32(reader, 5, "inventory grid y"),
                    ReadOptionalInt32(reader, 6, "inventory width"),
                    ReadOptionalInt32(reader, 7, "inventory height"),
                    ReadOptionalInt32(reader, 8, "inventory item count"),
                    confidence);
                if (!storedNodes.TryAdd(node.NodeId, node))
                {
                    throw new InvalidDataException("Persisted inventory nodes contain a duplicate identity.");
                }
            }
        }

        if (storedNodes.Count != expectedNodes.Count ||
            expectedNodes.Any(pair => !storedNodes.TryGetValue(pair.Key, out var stored) || stored != pair.Value))
        {
            throw new InvalidDataException("Persisted inventory nodes do not match the typed stash recognition.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            row.SnapshotId,
            profileId,
            generation,
            gameMode,
            row.RecordedUtc,
            true,
            recognition,
            row.DataSnapshotId);
    }

    public async Task AppendRaidFieldAsync(RaidFieldHistoryRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.RaidId == Guid.Empty || string.IsNullOrWhiteSpace(record.FieldName) ||
            record.ProvenanceKind is not ("manual" or "observed") || string.IsNullOrWhiteSpace(record.Source))
            throw new ArgumentException("Raid field provenance is incomplete.", nameof(record));
        ValidateRequiredString(record.FieldName, nameof(record.FieldName));
        ValidateRequiredString(record.ProvenanceKind, nameof(record.ProvenanceKind));
        ValidateRequiredString(record.Source, nameof(record.Source));
        ValidateRequiredTime(record.RecordedUtc, nameof(record.RecordedUtc));
        ValidateOptionalTime(record.ObservedUtc, nameof(record.ObservedUtc));
        ValidateJson(record.ValueJson, nameof(record.ValueJson));
        ValidateUnit(record.Confidence, nameof(record.Confidence));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            INSERT INTO raid_field_history(
                raid_id, field_name, value_json, provenance_kind, source, observed_utc, recorded_utc, confidence)
            VALUES ($raid, $field, $value, $kind, $source, $observed, $recorded, $confidence);
            """, cancellationToken, ("$raid", Id(record.RaidId)), ("$field", record.FieldName),
            ("$value", record.ValueJson), ("$kind", record.ProvenanceKind), ("$source", record.Source),
            ("$observed", Format(record.ObservedUtc)), ("$recorded", Format(record.RecordedUtc)),
            ("$confidence", record.Confidence)).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<RaidFieldHistoryRecord>> ListRaidFieldsAsync(
        Guid raidId,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (raidId == Guid.Empty)
        {
            throw new ArgumentException("A raid id is required.", nameof(raidId));
        }

        if (maximumCount is < 1 or > MaximumRaidFieldReadCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH newest AS (
                SELECT id, field_name, value_json, provenance_kind, source, observed_utc,
                       recorded_utc, confidence
                FROM raid_field_history
                WHERE raid_id = $raid
                ORDER BY recorded_utc DESC, id DESC
                LIMIT $maximum
            )
            SELECT id, field_name, value_json, provenance_kind, source, observed_utc,
                   recorded_utc, confidence,
                   CASE WHEN typeof(field_name) = 'text' THEN length(CAST(field_name AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(value_json) = 'text' THEN length(CAST(value_json AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(provenance_kind) = 'text' THEN length(CAST(provenance_kind AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(source) = 'text' THEN length(CAST(source AS BLOB)) ELSE -1 END,
                   CASE WHEN observed_utc IS NULL THEN NULL
                        WHEN typeof(observed_utc) = 'text' THEN length(CAST(observed_utc AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(recorded_utc) = 'text' THEN length(CAST(recorded_utc AS BLOB)) ELSE -1 END
            FROM newest
            ORDER BY recorded_utc, id;
            """;
        command.Parameters.AddWithValue("$raid", Id(raidId));
        command.Parameters.AddWithValue("$maximum", maximumCount);
        var result = ImmutableArray.CreateBuilder<RaidFieldHistoryRecord>();
        long totalJsonBytes = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetValue(0) is not long id)
                {
                    throw new InvalidDataException("Persisted raid field identity is not an integer.");
                }
                var fieldName = ReadRequiredText(reader, 1, 8, "raid field name");
                var valueJson = ReadJson(reader, 2, 9, ref totalJsonBytes, "raid field value");
                var provenanceKind = ReadRequiredText(reader, 3, 10, "raid field provenance kind");
                var source = ReadRequiredText(reader, 4, 11, "raid field source");
                var observedUtc = ReadOptionalTime(reader, 5, 12, "raid field observation time");
                var recordedUtc = ReadRequiredTime(reader, 6, 13, "raid field recording time");
                var confidence = ReadOptionalDouble(reader, 7, "raid field confidence");
                if (id <= 0 || provenanceKind is not ("manual" or "observed") ||
                    confidence is < 0 or > 1)
                {
                    throw new InvalidDataException("Persisted raid field history contains invalid state or numerics.");
                }

                result.Add(new(id, raidId, fieldName, valueJson, provenanceKind, source,
                    observedUtc, recordedUtc, confidence));
            }
        }
        catch (Exception exception) when (IsPersistedValueFailure(exception))
        {
            throw new InvalidDataException("Persisted raid field history is invalid.", exception);
        }

        return result.ToImmutable();
    }

    public async Task AppendCraftHistoryAsync(CraftHistoryRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.HistoryId == Guid.Empty || string.IsNullOrWhiteSpace(record.CraftId) || string.IsNullOrWhiteSpace(record.Source))
            throw new ArgumentException("Craft history identity and source are required.", nameof(record));
        ValidateRequiredString(record.CraftId, nameof(record.CraftId));
        ValidateOptionalString(record.StationId, nameof(record.StationId));
        ValidateOptionalString(record.OutputItemId, nameof(record.OutputItemId));
        ValidateRequiredString(record.Source, nameof(record.Source));
        if (record.StationLevel is < 0 || record.EstimatedCostRoubles is < 0 || record.EstimatedYieldRoubles is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "Craft history facts cannot be negative.");
        }

        ValidateRequiredTime(record.RecordedUtc, nameof(record.RecordedUtc));
        ValidateOptionalTime(record.ObservedUtc, nameof(record.ObservedUtc));
        ValidateOptionalFinite(record.OutputCount, nameof(record.OutputCount), 0, double.MaxValue);
        ValidateJson(record.PayloadJson, nameof(record.PayloadJson));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            INSERT INTO craft_history(
                history_id, craft_id, station_id, station_level, observed_utc, recorded_utc,
                output_item_id, output_count, estimated_cost_roubles, estimated_yield_roubles, source, payload_json)
            VALUES ($id, $craft, $station, $level, $observed, $recorded, $item, $count, $cost, $yield, $source, $payload);
            """, cancellationToken, ("$id", Id(record.HistoryId)), ("$craft", record.CraftId),
            ("$station", record.StationId), ("$level", record.StationLevel), ("$observed", Format(record.ObservedUtc)),
            ("$recorded", Format(record.RecordedUtc)), ("$item", record.OutputItemId), ("$count", record.OutputCount),
            ("$cost", record.EstimatedCostRoubles), ("$yield", record.EstimatedYieldRoubles),
            ("$source", record.Source), ("$payload", record.PayloadJson)).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<CraftHistoryRecord>> ListCraftHistoryAsync(
        string craftId,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ValidateRequiredString(craftId, nameof(craftId));
        if (maximumCount is < 1 or > MaximumCraftHistoryReadCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT history_id, station_id, station_level, observed_utc, recorded_utc, output_item_id,
                   output_count, estimated_cost_roubles, estimated_yield_roubles, source, payload_json,
                   CASE WHEN typeof(history_id) = 'text' THEN length(CAST(history_id AS BLOB)) ELSE -1 END,
                   CASE WHEN station_id IS NULL THEN NULL
                        WHEN typeof(station_id) = 'text' THEN length(CAST(station_id AS BLOB)) ELSE -1 END,
                   CASE WHEN observed_utc IS NULL THEN NULL
                        WHEN typeof(observed_utc) = 'text' THEN length(CAST(observed_utc AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(recorded_utc) = 'text' THEN length(CAST(recorded_utc AS BLOB)) ELSE -1 END,
                   CASE WHEN output_item_id IS NULL THEN NULL
                        WHEN typeof(output_item_id) = 'text' THEN length(CAST(output_item_id AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(source) = 'text' THEN length(CAST(source AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(payload_json) = 'text' THEN length(CAST(payload_json AS BLOB)) ELSE -1 END
            FROM craft_history
            WHERE craft_id = $craft
            ORDER BY observed_utc DESC, recorded_utc DESC
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$craft", craftId);
        command.Parameters.AddWithValue("$maximum", maximumCount);
        var result = ImmutableArray.CreateBuilder<CraftHistoryRecord>();
        long totalJsonBytes = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var historyId = ReadRequiredGuid(reader, 0, 11, "craft history id");
                var stationId = ReadOptionalText(reader, 1, 12, "craft station id");
                var stationLevel = ReadOptionalInt32(reader, 2, "craft station level");
                var observedUtc = ReadOptionalTime(reader, 3, 13, "craft observation time");
                var recordedUtc = ReadRequiredTime(reader, 4, 14, "craft recording time");
                var outputItemId = ReadOptionalText(reader, 5, 15, "craft output item id");
                var outputCount = ReadOptionalDouble(reader, 6, "craft output count");
                var cost = ReadOptionalInt64(reader, 7, "craft estimated cost");
                var yield = ReadOptionalInt64(reader, 8, "craft estimated yield");
                var source = ReadRequiredText(reader, 9, 16, "craft source");
                var payloadJson = ReadJson(reader, 10, 17, ref totalJsonBytes, "craft history payload");
                if (stationLevel is < 0 || outputCount is < 0 || cost is < 0 || yield is < 0)
                {
                    throw new InvalidDataException("Persisted craft history contains negative facts.");
                }

                result.Add(new(historyId, craftId, stationId, stationLevel, observedUtc, recordedUtc,
                    outputItemId, outputCount, cost, yield, source, payloadJson));
            }
        }
        catch (Exception exception) when (IsPersistedValueFailure(exception))
        {
            throw new InvalidDataException("Persisted craft history is invalid.", exception);
        }

        return result.ToImmutable();
    }

    public async Task<bool> TrySaveLoadoutPlanAsync(long expectedRevision, LoadoutPlanRecord plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.PlanId == Guid.Empty || plan.ProfileId == Guid.Empty || expectedRevision < 0 ||
            expectedRevision == long.MaxValue || plan.Revision != expectedRevision + 1 ||
            string.IsNullOrWhiteSpace(plan.Name))
            throw new ArgumentException("Loadout plan identity, name, and the next monotonic revision are required.", nameof(plan));
        ValidateRequiredString(plan.Generation, nameof(plan.Generation));
        ValidateRequiredString(plan.GameMode, nameof(plan.GameMode));
        ValidateRequiredString(plan.Name, nameof(plan.Name));
        ValidateRequiredTime(plan.UpdatedUtc, nameof(plan.UpdatedUtc));
        ValidateJson(plan.PayloadJson, nameof(plan.PayloadJson));
        ValidateJson(plan.ExtensionJson, nameof(plan.ExtensionJson));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO loadout_plans(plan_id, profile_id, generation, game_mode, name, revision, updated_utc, payload_json, extension_json)
            SELECT $id, $profile, $generation, $mode, $name, $revision, $updated, $payload, $extension
            WHERE ($expected = 0 AND NOT EXISTS (
                       SELECT 1 FROM loadout_plans WHERE plan_id = $id
                   ))
               OR ($expected > 0 AND EXISTS (
                       SELECT 1 FROM loadout_plans
                       WHERE plan_id = $id AND revision = $expected
                         AND profile_id = $profile AND generation = $generation AND game_mode = $mode
                   ))
            ON CONFLICT(plan_id) DO UPDATE SET
                name = excluded.name, revision = excluded.revision, updated_utc = excluded.updated_utc,
                payload_json = excluded.payload_json, extension_json = excluded.extension_json
            WHERE loadout_plans.revision = $expected
              AND loadout_plans.profile_id = $profile
              AND loadout_plans.generation = $generation
              AND loadout_plans.game_mode = $mode;
            """;
        command.Parameters.AddWithValue("$id", Id(plan.PlanId));
        command.Parameters.AddWithValue("$profile", Id(plan.ProfileId));
        command.Parameters.AddWithValue("$generation", plan.Generation);
        command.Parameters.AddWithValue("$mode", plan.GameMode);
        command.Parameters.AddWithValue("$name", plan.Name);
        command.Parameters.AddWithValue("$revision", plan.Revision);
        command.Parameters.AddWithValue("$updated", Format(plan.UpdatedUtc));
        command.Parameters.AddWithValue("$payload", plan.PayloadJson);
        command.Parameters.AddWithValue("$extension", plan.ExtensionJson);
        command.Parameters.AddWithValue("$expected", expectedRevision);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<LoadoutPlanRecord?> ReadLoadoutPlanAsync(
        Guid planId,
        Guid profileId,
        string generation,
        string gameMode,
        CancellationToken cancellationToken)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A profile id is required.", nameof(profileId));
        }

        ValidateRequiredString(generation, nameof(generation));
        ValidateRequiredString(gameMode, nameof(gameMode));
        return await ReadLoadoutPlanCoreAsync(
            planId, profileId, generation, gameMode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LoadoutPlanRecord?> ReadLoadoutPlanCoreAsync(
        Guid planId,
        Guid profileId,
        string generation,
        string gameMode,
        CancellationToken cancellationToken)
    {
        if (planId == Guid.Empty)
        {
            throw new ArgumentException("A plan id is required.", nameof(planId));
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, generation, game_mode, name, revision, updated_utc, payload_json, extension_json,
                   CASE WHEN typeof(profile_id) = 'text' THEN length(CAST(profile_id AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(generation) = 'text' THEN length(CAST(generation AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(game_mode) = 'text' THEN length(CAST(game_mode AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(name) = 'text' THEN length(CAST(name AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(updated_utc) = 'text' THEN length(CAST(updated_utc AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(payload_json) = 'text' THEN length(CAST(payload_json AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(extension_json) = 'text' THEN length(CAST(extension_json AS BLOB)) ELSE -1 END
            FROM loadout_plans
            WHERE plan_id = $id
              AND profile_id = $profile AND generation = $generation AND game_mode = $mode;
            """;
        command.Parameters.AddWithValue("$id", Id(planId));
        command.Parameters.AddWithValue("$profile", Id(profileId));
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$mode", gameMode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            long totalJsonBytes = 0;
            var storedProfileId = ReadRequiredGuid(reader, 0, 8, "loadout profile id");
            var storedGeneration = ReadRequiredText(reader, 1, 9, "loadout generation");
            var storedGameMode = ReadRequiredText(reader, 2, 10, "loadout game mode");
            var name = ReadRequiredText(reader, 3, 11, "loadout name");
            if (reader.GetValue(4) is not long revision)
            {
                throw new InvalidDataException("Persisted loadout revision is not an integer.");
            }
            var updatedUtc = ReadRequiredTime(reader, 5, 12, "loadout update time");
            var payloadJson = ReadJson(reader, 6, 13, ref totalJsonBytes, "loadout payload");
            var extensionJson = ReadJson(reader, 7, 14, ref totalJsonBytes, "loadout extension");
            if (revision < 0)
            {
                throw new InvalidDataException("Persisted loadout revision is negative.");
            }

            return new(planId, storedProfileId, storedGeneration, storedGameMode, name, revision,
                updatedUtc, payloadJson, extensionJson);
        }
        catch (Exception exception) when (IsPersistedValueFailure(exception))
        {
            throw new InvalidDataException("Persisted loadout plan is invalid.", exception);
        }
    }

    public async Task SaveModelSnapshotAsync<T>(
        HistoricalIntelligenceSnapshot<T> snapshot,
        CancellationToken cancellationToken)
        where T : class, IIntelligencePayload
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateIntelligenceScope(snapshot.ModelSnapshotId, snapshot.ProfileId, snapshot.Generation, snapshot.GameMode);
        RequireIntelligencePayload<T>();
        var intelligence = snapshot.Intelligence ?? throw new ArgumentNullException(nameof(snapshot.Intelligence));
        RejectLiveClaims(intelligence.IntelligenceId, intelligence.Value);
        var payloadJson = SerializeContract(intelligence, nameof(snapshot.Intelligence));
        await SaveIntelligenceAsync(
            snapshot.ModelSnapshotId,
            snapshot.ProfileId,
            snapshot.Generation,
            snapshot.GameMode,
            IntelligenceKind<T>(),
            "historical",
            intelligence.Value.Provenance,
            payloadJson,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveModelSnapshotAsync<T>(
        ModelledIntelligenceSnapshot<T> snapshot,
        CancellationToken cancellationToken)
        where T : class, IIntelligencePayload
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateIntelligenceScope(snapshot.ModelSnapshotId, snapshot.ProfileId, snapshot.Generation, snapshot.GameMode);
        RequireIntelligencePayload<T>();
        var intelligence = snapshot.Intelligence ?? throw new ArgumentNullException(nameof(snapshot.Intelligence));
        RejectLiveClaims(intelligence.IntelligenceId, intelligence.Estimate, intelligence.Explanation);
        var payloadJson = SerializeContract(intelligence, nameof(snapshot.Intelligence));
        await SaveIntelligenceAsync(
            snapshot.ModelSnapshotId,
            snapshot.ProfileId,
            snapshot.Generation,
            snapshot.GameMode,
            IntelligenceKind<T>(),
            "modelled",
            intelligence.Estimate.Provenance,
            payloadJson,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<HistoricalIntelligenceSnapshot<T>>> ListHistoricalModelSnapshotsAsync<T>(
        Guid? profileId,
        string? generation,
        string? gameMode,
        int maximumCount,
        CancellationToken cancellationToken)
        where T : class, IIntelligencePayload
    {
        RequireIntelligencePayload<T>();
        var kind = IntelligenceKind<T>();
        var rows = await ListIntelligenceRowsAsync(
            profileId, generation, gameMode, kind, "historical", maximumCount, cancellationToken).ConfigureAwait(false);
        var result = ImmutableArray.CreateBuilder<HistoricalIntelligenceSnapshot<T>>(rows.Length);
        foreach (var row in rows)
        {
            var intelligence = DeserializeContract<HistoricalIntelligence<T>>(row.PayloadJson, "persisted historical intelligence");
            RejectLiveClaims(intelligence.IntelligenceId, intelligence.Value);
            ValidateIntelligenceMetadata(row, kind, "historical", intelligence.Value.Provenance);
            result.Add(new(row.ModelSnapshotId, row.ProfileId, row.Generation, row.GameMode, intelligence));
        }

        return result.ToImmutable();
    }

    public async Task<ImmutableArray<ModelledIntelligenceSnapshot<T>>> ListModelledModelSnapshotsAsync<T>(
        Guid? profileId,
        string? generation,
        string? gameMode,
        int maximumCount,
        CancellationToken cancellationToken)
        where T : class, IIntelligencePayload
    {
        RequireIntelligencePayload<T>();
        var kind = IntelligenceKind<T>();
        var rows = await ListIntelligenceRowsAsync(
            profileId, generation, gameMode, kind, "modelled", maximumCount, cancellationToken).ConfigureAwait(false);
        var result = ImmutableArray.CreateBuilder<ModelledIntelligenceSnapshot<T>>(rows.Length);
        foreach (var row in rows)
        {
            var intelligence = DeserializeContract<ModelledIntelligence<T>>(row.PayloadJson, "persisted modelled intelligence");
            RejectLiveClaims(intelligence.IntelligenceId, intelligence.Estimate, intelligence.Explanation);
            ValidateIntelligenceMetadata(row, kind, "modelled", intelligence.Estimate.Provenance);
            result.Add(new(row.ModelSnapshotId, row.ProfileId, row.Generation, row.GameMode, intelligence));
        }

        return result.ToImmutable();
    }

    public async Task SaveRetentionPolicyAsync(DataRetentionPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.PolicyKey) || policy.ScreenshotRetentionHours is < 1 or > 720 ||
            policy.DataRetentionDays is < 1 or > 365_000)
            throw new ArgumentException("Retention limits are outside their safe bounds.", nameof(policy));
        ValidateRequiredString(policy.PolicyKey, nameof(policy.PolicyKey));
        ValidateRequiredTime(policy.UpdatedUtc, nameof(policy.UpdatedUtc));
        ValidateJson(policy.ExtensionJson, nameof(policy.ExtensionJson));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            INSERT INTO retention_policies(
                policy_key, screenshot_retention_enabled, screenshot_retention_hours,
                debug_capture_enabled, data_retention_days, updated_utc, extension_json)
            VALUES ($key, $screenshots, $hours, $debug, $days, $updated, $extension)
            ON CONFLICT(policy_key) DO UPDATE SET
                screenshot_retention_enabled = excluded.screenshot_retention_enabled,
                screenshot_retention_hours = excluded.screenshot_retention_hours,
                debug_capture_enabled = excluded.debug_capture_enabled,
                data_retention_days = excluded.data_retention_days,
                updated_utc = excluded.updated_utc,
                extension_json = excluded.extension_json;
            """, cancellationToken, ("$key", policy.PolicyKey), ("$screenshots", policy.ScreenshotRetentionEnabled),
            ("$hours", policy.ScreenshotRetentionHours), ("$debug", policy.DebugCaptureEnabled),
            ("$days", policy.DataRetentionDays), ("$updated", Format(policy.UpdatedUtc)),
            ("$extension", policy.ExtensionJson)).ConfigureAwait(false);
    }

    public async Task<DataRetentionPolicy?> ReadRetentionPolicyAsync(string policyKey, CancellationToken cancellationToken)
    {
        ValidateRequiredString(policyKey, nameof(policyKey));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT screenshot_retention_enabled, screenshot_retention_hours, debug_capture_enabled,
                   data_retention_days, updated_utc, extension_json,
                   CASE WHEN typeof(updated_utc) = 'text' THEN length(CAST(updated_utc AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(extension_json) = 'text' THEN length(CAST(extension_json AS BLOB)) ELSE -1 END
            FROM retention_policies WHERE policy_key = $key;
            """;
        command.Parameters.AddWithValue("$key", policyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var screenshotsEnabled = ReadBoolean(reader, 0, "screenshot retention enabled");
            var screenshotHours = ReadOptionalInt32(reader, 1, "screenshot retention hours");
            var debugEnabled = ReadBoolean(reader, 2, "debug capture enabled");
            var dataDays = ReadOptionalInt32(reader, 3, "data retention days");
            var updatedUtc = ReadRequiredTime(reader, 4, 6, "retention update time");
            long totalJsonBytes = 0;
            var extensionJson = ReadJson(reader, 5, 7, ref totalJsonBytes, "retention extension");
            if (screenshotHours is < 1 or > 720 || dataDays is < 1 or > 365_000)
            {
                throw new InvalidDataException("Persisted retention limits are outside their safe bounds.");
            }

            return new(policyKey, screenshotsEnabled, screenshotHours, debugEnabled, dataDays,
                updatedUtc, extensionJson);
        }
        catch (Exception exception) when (IsPersistedValueFailure(exception))
        {
            throw new InvalidDataException("Persisted retention policy is invalid.", exception);
        }
    }

    public async Task RecordLocalJsonRecoveryAsync(LocalJsonRecoveryRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.DocumentKey) || record.State is not ("current" or "malformed" or "quarantined" or "missing") || string.IsNullOrWhiteSpace(record.DiagnosticCode))
            throw new ArgumentException("Local JSON recovery state is invalid.", nameof(record));
        ValidateRequiredString(record.DocumentKey, nameof(record.DocumentKey));
        ValidateRequiredString(record.State, nameof(record.State));
        ValidateRequiredString(record.DiagnosticCode, nameof(record.DiagnosticCode));
        ValidateRequiredTime(record.DetectedUtc, nameof(record.DetectedUtc));
        ValidateContentHash(record.ContentSha256, nameof(record.ContentSha256));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            INSERT INTO local_json_recovery(document_key, state, detected_utc, content_sha256, diagnostic_code)
            VALUES ($key, $state, $detected, $hash, $code)
            ON CONFLICT(document_key) DO UPDATE SET state = excluded.state, detected_utc = excluded.detected_utc,
                content_sha256 = excluded.content_sha256, diagnostic_code = excluded.diagnostic_code;
            """, cancellationToken, ("$key", record.DocumentKey), ("$state", record.State),
            ("$detected", Format(record.DetectedUtc)), ("$hash", record.ContentSha256),
            ("$code", record.DiagnosticCode)).ConfigureAwait(false);
    }

    public async Task<LocalJsonRecoveryRecord?> ReadLocalJsonRecoveryAsync(string documentKey, CancellationToken cancellationToken)
    {
        ValidateRequiredString(documentKey, nameof(documentKey));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state, detected_utc, content_sha256, diagnostic_code,
                   CASE WHEN typeof(state) = 'text' THEN length(CAST(state AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(detected_utc) = 'text' THEN length(CAST(detected_utc AS BLOB)) ELSE -1 END,
                   CASE WHEN content_sha256 IS NULL THEN NULL
                        WHEN typeof(content_sha256) = 'text' THEN length(CAST(content_sha256 AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(diagnostic_code) = 'text' THEN length(CAST(diagnostic_code AS BLOB)) ELSE -1 END
            FROM local_json_recovery WHERE document_key = $key;
            """;
        command.Parameters.AddWithValue("$key", documentKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var state = ReadRequiredText(reader, 0, 4, "local JSON recovery state");
            var detectedUtc = ReadRequiredTime(reader, 1, 5, "local JSON recovery time");
            var contentHash = ReadOptionalText(reader, 2, 6, "local JSON recovery content hash");
            var diagnostic = ReadRequiredText(reader, 3, 7, "local JSON recovery diagnostic");
            if (state is not ("current" or "malformed" or "quarantined" or "missing"))
            {
                throw new InvalidDataException("Persisted local JSON recovery state is invalid.");
            }

            try
            {
                ValidateContentHash(contentHash, "local JSON recovery content hash");
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Persisted local JSON recovery hash is invalid.", exception);
            }

            return new(documentKey, state, detectedUtc, contentHash, diagnostic);
        }
        catch (Exception exception) when (IsPersistedValueFailure(exception))
        {
            throw new InvalidDataException("Persisted local JSON recovery record is invalid.", exception);
        }
    }

    private static void ValidateInventoryScope(ObservedInventorySnapshot snapshot)
    {
        if (snapshot.SnapshotId == Guid.Empty || snapshot.ProfileId == Guid.Empty || snapshot.RecordedUtc == default)
        {
            throw new ArgumentException("Inventory persistence identity and recording time are required.", nameof(snapshot));
        }

        ValidateRequiredString(snapshot.Generation, nameof(snapshot.Generation));
        ValidateRequiredString(snapshot.GameMode, nameof(snapshot.GameMode));
    }

    internal static void ValidateInventoryRecognition(RecognitionResultEnvelope<StashRecognition> recognition)
    {
        ArgumentNullException.ThrowIfNull(recognition);
        if (recognition.Header.ContractVersion != V2ContractVersion.Current)
        {
            throw new ArgumentException(
                $"Inventory persistence accepts exactly contract {V2ContractVersion.Current}.",
                nameof(recognition));
        }

        if (recognition.Result.Value is null)
        {
            throw new ArgumentException("An observed inventory snapshot must carry a stash result.", nameof(recognition));
        }

        var regionCount = 0;
        var containerCount = 0;
        var nodeCount = 0;
        ValidateInventorySource(recognition.Result.Provenance, nameof(recognition));
        ValidateInventoryClaim(recognition.Result.Value, ref regionCount, ref containerCount, ref nodeCount);
        foreach (var candidate in recognition.Result.Candidates)
        {
            ValidateInventorySource(candidate.Provenance, nameof(recognition));
            ValidateInventoryClaim(candidate.Value, ref regionCount, ref containerCount, ref nodeCount);
        }
    }

    private static void ValidateInventoryClaim(
        StashRecognition stash,
        ref int regionCount,
        ref int containerCount,
        ref int nodeCount)
    {
        AddBounded(stash.CapturedRegions.Count, ref regionCount, MaximumInventoryRegions, "capture regions");
        AddBounded(stash.Coverage.Count, ref containerCount, MaximumInventoryContainers, "containers");
        AddBounded(stash.Coverage.Count, ref nodeCount, MaximumInventoryNodes, "inventory nodes");
        foreach (var region in stash.CapturedRegions)
        {
            AddBounded(region.Grid.Cells.Count, ref nodeCount, MaximumInventoryNodes, "inventory nodes");
        }
    }

    private static void ValidateInventorySource(EvidenceProvenance provenance, string parameterName)
    {
        if (provenance.SourceClass is
            EvidenceSourceClass.GameWrittenScreenshot or EvidenceSourceClass.ExternalVisiblePixels)
        {
            return;
        }

        // A whole-stash result combines several captures, so its honest root is a calculation.
        // Permit that root only while every branch remains a calculation and every leaf remains
        // user-triggered visible-pixel evidence. This rejects a mixed log, user, unknown, public,
        // historical, or modelled lineage instead of laundering it through a derived wrapper.
        if (provenance.SourceClass != EvidenceSourceClass.DerivedCalculation ||
            provenance.Inputs.Count == 0 ||
            provenance.Inputs.Any(input => !HasOnlyVisibleCaptureLeaves(input)))
        {
            throw new ArgumentException(
                "Observed stash recognition must be visible-capture evidence or a calculation whose entire lineage has only visible-capture leaves.",
                parameterName);
        }
    }

    private static bool HasOnlyVisibleCaptureLeaves(EvidenceProvenance provenance)
    {
        if (provenance.SourceClass is
            EvidenceSourceClass.GameWrittenScreenshot or EvidenceSourceClass.ExternalVisiblePixels)
        {
            return provenance.Inputs.Count == 0;
        }

        return provenance.SourceClass == EvidenceSourceClass.DerivedCalculation &&
               provenance.Inputs.Count > 0 &&
               provenance.Inputs.All(HasOnlyVisibleCaptureLeaves);
    }

    private static void AddBounded(int count, ref int total, int maximum, string description)
    {
        if (count < 0 || count > maximum - total)
        {
            throw new ArgumentOutOfRangeException(description, $"A persisted stash carries at most {maximum} {description}.");
        }

        total += count;
    }

    private static ImmutableArray<InventoryNodeProjection> ProjectInventoryNodes(StashRecognition stash)
    {
        var result = ImmutableArray.CreateBuilder<InventoryNodeProjection>();
        var containerIds = new Dictionary<string, string>(stash.Coverage.Count, StringComparer.Ordinal);
        for (var index = 0; index < stash.Coverage.Count; index++)
        {
            containerIds.Add(stash.Coverage[index].ContainerPath, $"container:{index:D3}");
        }

        for (var index = 0; index < stash.Coverage.Count; index++)
        {
            var path = stash.Coverage[index].ContainerPath;
            var parentPath = ContainerPaths.Parent(path);
            result.Add(new(
                containerIds[path],
                parentPath is null ? null : containerIds[parentPath],
                parentPath is null ? "stash" : "container",
                null,
                null,
                null,
                null,
                null,
                null,
                stash.Coverage[index].ObservedCells.Provenance.Confidence.Score));
        }

        for (var regionIndex = 0; regionIndex < stash.CapturedRegions.Count; regionIndex++)
        {
            var region = stash.CapturedRegions[regionIndex];
            var parentId = containerIds[region.ContainerPath];
            for (var cellIndex = 0; cellIndex < region.Grid.Cells.Count; cellIndex++)
            {
                var cell = region.Grid.Cells[cellIndex];
                var item = cell.Item.Value;
                result.Add(new(
                    $"item:{regionIndex:D3}:{cellIndex:D5}",
                    parentId,
                    item is null ? "unknown" : "item",
                    item?.CanonicalId.Value,
                    cell.Anchor.Column,
                    cell.Anchor.Row,
                    item?.WidthCells.Value,
                    item?.HeightCells.Value,
                    item?.Quantity.Value,
                    cell.Item.Provenance.Confidence.Score));
            }
        }

        return result.ToImmutable();
    }

    private static void ValidateInventoryMetadata(
        RawInventorySnapshot row,
        RecognitionResultEnvelope<StashRecognition> recognition)
    {
        var provenance = recognition.Result.Provenance;
        if (row.DataSnapshotId is null ||
            row.ObservedUtc != provenance.ObservedUtc ||
            !string.Equals(row.Source, provenance.SourceIdentifier, StringComparison.Ordinal) ||
            !string.Equals(row.ProducerVersion, provenance.Producer.Version, StringComparison.Ordinal) ||
            row.Coverage != provenance.Coverage?.Fraction ||
            row.Confidence != provenance.Confidence.Score ||
            !row.ExtensionIsCanonical)
        {
            throw new InvalidDataException("Persisted inventory metadata does not match its typed recognition evidence.");
        }
    }

    private static string ResolveInventoryDataSnapshotId(
        ObservedInventorySnapshot snapshot,
        StashRecognition stash)
    {
        // Compatibility: callers predating the explicit field used StashRecognition.SnapshotId
        // as both identities. New stash scans always provide the catalog/economics snapshot id.
        var value = snapshot.DataSnapshotId ?? stash.SnapshotId;
        ValidateRequiredString(value, nameof(snapshot.DataSnapshotId));
        return value.Trim();
    }

    private async Task SaveIntelligenceAsync(
        Guid modelSnapshotId,
        Guid? profileId,
        string? generation,
        string? gameMode,
        string modelKind,
        string presentationKind,
        EvidenceProvenance provenance,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        if (provenance.DataThroughUtc is not { } dataThroughUtc ||
            provenance.GeneratedUtc is not { } generatedUtc ||
            provenance.Coverage is null || provenance.Confidence.Score is null ||
            string.IsNullOrWhiteSpace(provenance.Producer.ModelVersion))
        {
            throw new ArgumentException("Historical and modelled persistence requires complete typed provenance.", nameof(provenance));
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            INSERT INTO model_snapshots(
                model_snapshot_id, profile_id, generation, game_mode, model_kind, presentation_kind,
                source, observed_utc, data_through_utc, generated_utc, coverage, confidence,
                calibration, model_version, payload_json, extension_json)
            VALUES ($id, $profile, $generation, $mode, $kind, $presentation, $source, $observed,
                    $through, $generated, $coverage, $confidence, $calibration, $version, $payload, '{}');
            """, cancellationToken,
            ("$id", Id(modelSnapshotId)), ("$profile", profileId is { } profile ? Id(profile) : null),
            ("$generation", generation), ("$mode", gameMode), ("$kind", modelKind),
            ("$presentation", presentationKind), ("$source", provenance.SourceIdentifier),
            ("$observed", Format(provenance.ObservedUtc)), ("$through", Format(dataThroughUtc)),
            ("$generated", Format(generatedUtc)), ("$coverage", provenance.Coverage.Fraction),
            // The frozen contract carries a named calibration reference. The legacy projection is
            // numeric, so leave it empty instead of mislabelling confidence as calibration.
            ("$confidence", provenance.Confidence.Score), ("$calibration", null),
            ("$version", provenance.Producer.ModelVersion), ("$payload", payloadJson)).ConfigureAwait(false);
    }

    private async Task<ImmutableArray<RawIntelligenceSnapshot>> ListIntelligenceRowsAsync(
        Guid? profileId,
        string? generation,
        string? gameMode,
        string modelKind,
        string presentationKind,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A nullable profile id cannot contain the empty identifier.", nameof(profileId));
        }

        ValidateOptionalString(generation, nameof(generation));
        ValidateOptionalString(gameMode, nameof(gameMode));
        if (maximumCount is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT model_snapshot_id, profile_id, generation, game_mode, model_kind,
                   presentation_kind, source, observed_utc, data_through_utc, generated_utc,
                   coverage, confidence, calibration, model_version, payload_json,
                   CASE WHEN typeof(extension_json) = 'text' AND extension_json = '{}'
                        THEN 1 ELSE 0 END,
                   CASE WHEN typeof(model_snapshot_id) = 'text'
                        THEN length(CAST(model_snapshot_id AS BLOB)) ELSE -1 END,
                   CASE WHEN profile_id IS NULL THEN NULL
                        WHEN typeof(profile_id) = 'text'
                        THEN length(CAST(profile_id AS BLOB)) ELSE -1 END,
                   CASE WHEN generation IS NULL THEN NULL
                        WHEN typeof(generation) = 'text'
                        THEN length(CAST(generation AS BLOB)) ELSE -1 END,
                   CASE WHEN game_mode IS NULL THEN NULL
                        WHEN typeof(game_mode) = 'text'
                        THEN length(CAST(game_mode AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(model_kind) = 'text'
                        THEN length(CAST(model_kind AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(presentation_kind) = 'text'
                        THEN length(CAST(presentation_kind AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(source) = 'text'
                        THEN length(CAST(source AS BLOB)) ELSE -1 END,
                   CASE WHEN observed_utc IS NULL THEN NULL
                        WHEN typeof(observed_utc) = 'text'
                        THEN length(CAST(observed_utc AS BLOB)) ELSE -1 END,
                   CASE WHEN data_through_utc IS NULL THEN NULL
                        WHEN typeof(data_through_utc) = 'text'
                        THEN length(CAST(data_through_utc AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(generated_utc) = 'text'
                        THEN length(CAST(generated_utc AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(model_version) = 'text'
                        THEN length(CAST(model_version AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(payload_json) = 'text'
                        THEN length(CAST(payload_json AS BLOB)) ELSE -1 END
            FROM model_snapshots
            WHERE model_kind = $kind AND presentation_kind = $presentation
              AND (($profile IS NULL AND profile_id IS NULL) OR profile_id = $profile)
              AND (($generation IS NULL AND generation IS NULL) OR generation = $generation)
              AND (($mode IS NULL AND game_mode IS NULL) OR game_mode = $mode)
            ORDER BY generated_utc DESC LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$kind", modelKind);
        command.Parameters.AddWithValue("$presentation", presentationKind);
        command.Parameters.AddWithValue("$profile", profileId is { } profile ? Id(profile) : DBNull.Value);
        command.Parameters.AddWithValue("$generation", (object?)generation ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", (object?)gameMode ?? DBNull.Value);
        command.Parameters.AddWithValue("$maximum", maximumCount);
        var result = ImmutableArray.CreateBuilder<RawIntelligenceSnapshot>();
        long totalPayloadBytes = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var coverage = ReadOptionalDouble(reader, 10, "intelligence coverage");
                var confidence = ReadOptionalDouble(reader, 11, "intelligence confidence");
                var calibration = ReadOptionalDouble(reader, 12, "intelligence calibration");
                if (coverage is < 0 or > 1 || confidence is < 0 or > 1 || calibration is < 0 or > 1)
                {
                    throw new InvalidDataException("Persisted intelligence confidence metadata is outside its unit range.");
                }

                result.Add(new(
                    ReadRequiredGuid(reader, 0, 16, "model snapshot id"),
                    ReadOptionalGuid(reader, 1, 17, "model profile id"),
                    ReadOptionalText(reader, 2, 18, "model generation"),
                    ReadOptionalText(reader, 3, 19, "model game mode"),
                    ReadRequiredText(reader, 4, 20, "model kind"),
                    ReadRequiredText(reader, 5, 21, "model presentation kind"),
                    ReadRequiredText(reader, 6, 22, "model source"),
                    ReadOptionalTime(reader, 7, 23, "model observation time"),
                    ReadOptionalTime(reader, 8, 24, "model data-through time"),
                    ReadRequiredTime(reader, 9, 25, "model generation time"),
                    coverage,
                    confidence,
                    calibration,
                    ReadRequiredText(reader, 13, 26, "model version"),
                    ReadJson(reader, 14, 27, ref totalPayloadBytes, "model payload"),
                    ReadBoolean(reader, 15, "model extension state")));
            }
            catch (Exception exception) when (IsPersistedValueFailure(exception))
            {
                throw new InvalidDataException("Persisted intelligence snapshot is invalid.", exception);
            }
        }

        return result.ToImmutable();
    }

    private static void ValidateIntelligenceScope(
        Guid snapshotId,
        Guid? profileId,
        string? generation,
        string? gameMode)
    {
        if (snapshotId == Guid.Empty || profileId == Guid.Empty)
        {
            throw new ArgumentException("Intelligence persistence identifiers must be non-empty.", nameof(snapshotId));
        }

        ValidateOptionalString(generation, nameof(generation));
        ValidateOptionalString(gameMode, nameof(gameMode));
    }

    private static void ValidateIntelligenceMetadata(
        RawIntelligenceSnapshot row,
        string modelKind,
        string presentationKind,
        EvidenceProvenance provenance)
    {
        if (!string.Equals(row.ModelKind, modelKind, StringComparison.Ordinal) ||
            !string.Equals(row.PresentationKind, presentationKind, StringComparison.Ordinal) ||
            !string.Equals(row.Source, provenance.SourceIdentifier, StringComparison.Ordinal) ||
            row.ObservedUtc != provenance.ObservedUtc || row.DataThroughUtc != provenance.DataThroughUtc ||
            row.GeneratedUtc != provenance.GeneratedUtc || row.Coverage != provenance.Coverage?.Fraction ||
            row.Confidence != provenance.Confidence.Score || row.Calibration is not null ||
            !string.Equals(row.ModelVersion, provenance.Producer.ModelVersion, StringComparison.Ordinal) ||
            !row.ExtensionIsCanonical)
        {
            throw new InvalidDataException("Persisted intelligence metadata does not match its typed provenance.");
        }
    }

    private static void RequireIntelligencePayload<T>()
        where T : class, IIntelligencePayload
    {
        if (!V2WirePayloads.Intelligence.Contains(typeof(T)))
        {
            throw new ArgumentException($"{typeof(T).Name} is not an allowlisted V2 intelligence payload.");
        }
    }

    private static string IntelligenceKind<T>()
        where T : class, IIntelligencePayload => typeof(T) switch
        {
            var type when type == typeof(ZoneTrafficIntensity) => "zone-traffic-intensity",
            var type when type == typeof(RouteCorridorPressure) => "route-corridor-pressure",
            var type when type == typeof(EncounterLikelihood) => "encounter-likelihood",
            _ => throw new ArgumentException($"{typeof(T).Name} is not an allowlisted V2 intelligence payload."),
        };

    private static void RejectLiveClaims<T>(
        string intelligenceId,
        EvidencedValue<T> value,
        string? explanation = null)
    {
        if (ContainsWord(intelligenceId, "live") || ContainsWord(explanation, "live"))
        {
            throw new ArgumentException("Historical and modelled intelligence cannot claim live detection.");
        }

        var pending = new Stack<EvidenceProvenance>();
        pending.Push(value.Provenance);
        foreach (var candidate in value.Candidates)
        {
            pending.Push(candidate.Provenance);
        }

        while (pending.TryPop(out var provenance))
        {
            if (ContainsWord(provenance.SourceIdentifier, "live") || ContainsWord(provenance.Reference, "live"))
            {
                throw new ArgumentException("Historical and modelled intelligence cannot claim live detection.");
            }

            foreach (var input in provenance.Inputs)
            {
                pending.Push(input);
            }
        }
    }

    private static bool ContainsWord(string? value, string word)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        for (var index = 0; index <= value.Length - word.Length; index++)
        {
            if (!value.AsSpan(index, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var before = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
            var after = index + word.Length == value.Length || !char.IsLetterOrDigit(value[index + word.Length]);
            if (before && after)
            {
                return true;
            }
        }

        return false;
    }

    private static string SerializeContract<T>(T value, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        using var stream = new BoundedContractStream(parameterName);
        JsonSerializer.Serialize(stream, value, V2ContractJson.Options);

        var utf8 = stream.GetBuffer().AsSpan(0, checked((int)stream.Length));
        ValidateContractJson(utf8, parameterName);
        return Encoding.UTF8.GetString(utf8);
    }

    private static T DeserializeContract<T>(string json, string description)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException($"The {description} is empty.");
        }

        if (Encoding.UTF8.GetByteCount(json) > MaximumContractJsonBytes)
        {
            throw new ArgumentOutOfRangeException(description, $"The {description} exceeds the JSON byte budget.");
        }

        var utf8 = Encoding.UTF8.GetBytes(json);
        ValidateContractJson(utf8, description);
        return JsonSerializer.Deserialize<T>(utf8, V2ContractJson.Options)
            ?? throw new InvalidDataException($"The {description} did not contain a contract object.");
    }

    private static void ValidateContractJson(ReadOnlySpan<byte> utf8, string parameterName)
    {
        if (utf8.Length == 0 || utf8.Length > MaximumContractJsonBytes)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Contract JSON must be 1-{MaximumContractJsonBytes} UTF-8 bytes.");
        }

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = V2ContractJson.MaxDepth,
        });
        var first = true;
        while (reader.Read())
        {
            if (first)
            {
                first = false;
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    throw new ArgumentException("A persisted contract must be a JSON object.", parameterName);
                }
            }

            if ((reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String) &&
                StringUtf8Length(ref reader) > MaximumContractStringUtf8Bytes)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"A persisted contract string exceeds {MaximumContractStringUtf8Bytes} UTF-8 bytes.");
            }
        }

        if (first)
        {
            throw new ArgumentException("A persisted contract must be a JSON object.", parameterName);
        }
    }

    private static int StringUtf8Length(ref Utf8JsonReader reader) => reader.ValueIsEscaped
        ? Encoding.UTF8.GetByteCount(reader.GetString() ?? string.Empty)
        : reader.ValueSpan.Length;

    private static void ValidateRequiredString(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > MaximumContractStringUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateOptionalString(string? value, string parameterName)
    {
        if (value is not null)
        {
            ValidateRequiredString(value, parameterName);
        }
    }

    private static void ValidateJson(string json, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        var utf8 = Encoding.UTF8.GetBytes(json);
        if (utf8.Length > MaximumContractJsonBytes)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new ArgumentException("Persisted JSON must be an object.", parameterName);
        }

        do
        {
            if ((reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String) &&
                StringUtf8Length(ref reader) > MaximumContractStringUtf8Bytes)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"A persisted JSON string exceeds {MaximumContractStringUtf8Bytes} UTF-8 bytes.");
            }
        }
        while (reader.Read());
    }

    private static void ValidateUnit(double? value, string name) => ValidateOptionalFinite(value, name, 0, 1);
    private static void ValidateOptionalFinite(double? value, string name, double minimum, double maximum)
    {
        if (value is { } number && (!double.IsFinite(number) || number < minimum || number > maximum)) throw new ArgumentOutOfRangeException(name);
    }

    private static void ValidateRequiredTime(DateTimeOffset value, string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A required timestamp cannot be the default value.");
        }
    }

    private static void ValidateOptionalTime(DateTimeOffset? value, string parameterName)
    {
        if (value == default(DateTimeOffset))
        {
            throw new ArgumentOutOfRangeException(parameterName, "An observed timestamp cannot be the default value.");
        }
    }

    private static void ValidateContentHash(string? value, string parameterName)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A content hash must be 64 lowercase hexadecimal characters.", parameterName);
        }
    }

    private static string ReadRequiredText(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description)
    {
        var value = ReadBoundedText(reader, valueOrdinal, lengthOrdinal, description, optional: false)!;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"The persisted {description} is empty.");
        }

        return value;
    }

    private static string? ReadOptionalText(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description) =>
        ReadBoundedText(reader, valueOrdinal, lengthOrdinal, description, optional: true);

    private static string? ReadBoundedText(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description,
        bool optional)
    {
        if (reader.IsDBNull(valueOrdinal))
        {
            if (!optional)
            {
                throw new InvalidDataException($"The persisted {description} is missing.");
            }

            if (!reader.IsDBNull(lengthOrdinal))
            {
                throw new InvalidDataException($"The persisted {description} has inconsistent length metadata.");
            }

            return null;
        }

        if (reader.IsDBNull(lengthOrdinal))
        {
            throw new InvalidDataException($"The persisted {description} has no byte length.");
        }

        var byteLength = reader.GetInt64(lengthOrdinal);
        if (byteLength is < 1 or > MaximumContractStringUtf8Bytes)
        {
            throw new InvalidDataException($"The persisted {description} exceeds its string byte budget.");
        }

        var value = reader.GetString(valueOrdinal);
        if (Encoding.UTF8.GetByteCount(value) != byteLength)
        {
            throw new InvalidDataException($"The persisted {description} has inconsistent UTF-8 length.");
        }

        return value;
    }

    private static string ReadJson(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        ref long aggregateBytes,
        string description)
    {
        if (reader.IsDBNull(valueOrdinal) || reader.IsDBNull(lengthOrdinal))
        {
            throw new InvalidDataException($"The persisted {description} is missing.");
        }

        var byteLength = reader.GetInt64(lengthOrdinal);
        if (byteLength is < 1 or > MaximumContractJsonBytes ||
            byteLength > MaximumContractJsonBytes - aggregateBytes)
        {
            throw new InvalidDataException($"The persisted {description} exceeds the aggregate JSON byte budget.");
        }

        aggregateBytes += byteLength;
        var json = reader.GetString(valueOrdinal);
        if (Encoding.UTF8.GetByteCount(json) != byteLength)
        {
            throw new InvalidDataException($"The persisted {description} has inconsistent UTF-8 length.");
        }

        ValidateJson(json, description);
        return json;
    }

    private static Guid ReadRequiredGuid(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description)
    {
        if (reader.IsDBNull(valueOrdinal) || reader.IsDBNull(lengthOrdinal) ||
            reader.GetInt64(lengthOrdinal) != 36)
        {
            throw new InvalidDataException($"The persisted {description} is not a canonical UUID.");
        }

        var value = reader.GetString(valueOrdinal);
        if (!Guid.TryParseExact(value, "D", out var result) || result == Guid.Empty)
        {
            throw new InvalidDataException($"The persisted {description} is not a non-empty canonical UUID.");
        }

        return result;
    }

    private static Guid? ReadOptionalGuid(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description)
    {
        if (reader.IsDBNull(valueOrdinal))
        {
            if (!reader.IsDBNull(lengthOrdinal))
            {
                throw new InvalidDataException($"The persisted {description} has inconsistent length metadata.");
            }

            return null;
        }

        return ReadRequiredGuid(reader, valueOrdinal, lengthOrdinal, description);
    }

    private static DateTimeOffset ReadRequiredTime(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description)
    {
        var value = ReadRequiredText(reader, valueOrdinal, lengthOrdinal, description);
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var result) || result == default || result.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"The persisted {description} is not a canonical UTC timestamp.");
        }

        return result;
    }

    private static DateTimeOffset? ReadOptionalTime(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description)
    {
        var value = ReadOptionalText(reader, valueOrdinal, lengthOrdinal, description);
        if (value is null)
        {
            return null;
        }

        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var result) || result == default || result.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"The persisted {description} is not a canonical UTC timestamp.");
        }

        return result;
    }

    private static int? ReadOptionalInt32(SqliteDataReader reader, int ordinal, string description)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        if (reader.GetValue(ordinal) is not long value)
        {
            throw new InvalidDataException($"The persisted {description} is not an integer.");
        }
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidDataException($"The persisted {description} is outside the Int32 range.");
        }

        return (int)value;
    }

    private static long? ReadOptionalInt64(SqliteDataReader reader, int ordinal, string description)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            if (reader.GetValue(ordinal) is not long value)
            {
                throw new InvalidDataException($"The persisted {description} is not an integer.");
            }

            return value;
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException or FormatException)
        {
            throw new InvalidDataException($"The persisted {description} is not an integer.", exception);
        }
    }

    private static double? ReadOptionalDouble(SqliteDataReader reader, int ordinal, string description)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal) switch
        {
            long integer => integer,
            double real => real,
            _ => throw new InvalidDataException($"The persisted {description} is not numeric."),
        };
        if (!double.IsFinite(value))
        {
            throw new InvalidDataException($"The persisted {description} is not finite.");
        }

        return value;
    }

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal, string description)
    {
        if (reader.GetValue(ordinal) is not long value)
        {
            throw new InvalidDataException($"The persisted {description} is not an integer boolean.");
        }
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException($"The persisted {description} is not a boolean."),
        };
    }

    private static bool IsPersistedValueFailure(Exception exception) =>
        exception is not InvalidDataException and
        (ArgumentException or FormatException or OverflowException or InvalidCastException or JsonException);

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Id(Guid value) => value.ToString("D");
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string? Format(DateTimeOffset? value) => value is { } timestamp ? Format(timestamp) : null;

    private sealed record InventoryNodeProjection(
        string NodeId,
        string? ParentNodeId,
        string NodeKind,
        string? ItemId,
        int? GridX,
        int? GridY,
        int? Width,
        int? Height,
        int? ItemCount,
        double? Confidence);

    private sealed record RawInventorySnapshot(
        Guid SnapshotId,
        string? DataSnapshotId,
        DateTimeOffset? ObservedUtc,
        DateTimeOffset RecordedUtc,
        string Source,
        string ProducerVersion,
        double? Coverage,
        double? Confidence,
        string PayloadJson,
        bool ExtensionIsCanonical);

    private sealed record RawIntelligenceSnapshot(
        Guid ModelSnapshotId,
        Guid? ProfileId,
        string? Generation,
        string? GameMode,
        string ModelKind,
        string PresentationKind,
        string Source,
        DateTimeOffset? ObservedUtc,
        DateTimeOffset? DataThroughUtc,
        DateTimeOffset GeneratedUtc,
        double? Coverage,
        double? Confidence,
        double? Calibration,
        string ModelVersion,
        string PayloadJson,
        bool ExtensionIsCanonical);

    private sealed class BoundedContractStream : MemoryStream
    {
        private readonly string _parameterName;

        public BoundedContractStream(string parameterName)
        {
            _parameterName = parameterName;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureRemaining(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureRemaining(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureRemaining(1);
            base.WriteByte(value);
        }

        private void EnsureRemaining(int count)
        {
            if (count < 0 || count > MaximumContractJsonBytes - Position)
            {
                throw new ArgumentOutOfRangeException(
                    _parameterName,
                    $"Contract JSON must be at most {MaximumContractJsonBytes} UTF-8 bytes.");
            }
        }
    }
}
