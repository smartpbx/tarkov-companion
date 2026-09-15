using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

public sealed record ObservedInventoryNode(
    string NodeId,
    string? ParentNodeId,
    string NodeKind,
    string? ItemId,
    int? GridX,
    int? GridY,
    int? Width,
    int? Height,
    int? ItemCount,
    double? WeightKg,
    double? Confidence,
    string RawJson);

public sealed record ObservedInventorySnapshot(
    Guid SnapshotId,
    Guid ProfileId,
    string Generation,
    string GameMode,
    string? DataSnapshotId,
    DateTimeOffset? ObservedUtc,
    DateTimeOffset RecordedUtc,
    string Source,
    string ProducerVersion,
    double? Coverage,
    double? Confidence,
    bool IsCurrent,
    string PayloadJson,
    string ExtensionJson,
    ImmutableArray<ObservedInventoryNode> Nodes);

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

public sealed record ModelSnapshotRecord(
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
    string ExtensionJson);

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
/// V2 evidence stores keep nullable observations and their provenance separate from user-entered
/// facts. JSON is retained byte-for-byte so fields from a newer producer survive an old reader.
/// </summary>
public sealed class SqliteV2DataStore(SqliteConnectionFactory connectionFactory)
{
    private const int MaximumJsonBytes = 8 * 1024 * 1024;
    private static readonly string[] InventoryKinds = ["stash", "container", "item", "unknown"];
    private static readonly string[] ModelPresentations = ["historical", "modelled", "predicted"];

    public async Task SaveInventorySnapshotAsync(ObservedInventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        ValidateInventory(snapshot);
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
            ("$dataSnapshot", snapshot.DataSnapshotId), ("$observed", Format(snapshot.ObservedUtc)),
            ("$recorded", Format(snapshot.RecordedUtc)), ("$source", snapshot.Source),
            ("$producer", snapshot.ProducerVersion), ("$coverage", snapshot.Coverage),
            ("$confidence", snapshot.Confidence), ("$current", snapshot.IsCurrent),
            ("$payload", snapshot.PayloadJson), ("$extension", snapshot.ExtensionJson)).ConfigureAwait(false);

        foreach (var node in snapshot.Nodes)
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
                ("$count", node.ItemCount), ("$weight", node.WeightKg),
                ("$confidence", node.Confidence), ("$raw", node.RawJson)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ObservedInventorySnapshot?> ReadCurrentInventoryAsync(
        Guid profileId,
        string generation,
        string gameMode,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ObservedInventorySnapshot? snapshot;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT snapshot_id, data_snapshot_id, observed_utc, recorded_utc, source,
                       producer_version, coverage, confidence, payload_json, extension_json
                FROM observed_inventory_snapshots
                WHERE profile_id = $profile AND generation = $generation AND game_mode = $mode AND is_current = 1
                ORDER BY recorded_utc DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$profile", Id(profileId));
            command.Parameters.AddWithValue("$generation", generation);
            command.Parameters.AddWithValue("$mode", gameMode);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            snapshot = new(
                Guid.Parse(reader.GetString(0)), profileId, generation, gameMode,
                Text(reader, 1), Time(reader, 2), RequiredTime(reader, 3), reader.GetString(4), reader.GetString(5),
                Number(reader, 6), Number(reader, 7), true, reader.GetString(8), reader.GetString(9), []);
        }

        var nodes = ImmutableArray.CreateBuilder<ObservedInventoryNode>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT node_id, parent_node_id, node_kind, item_id, grid_x, grid_y, width, height,
                       item_count, weight_kg, confidence, raw_json
                FROM observed_inventory_nodes WHERE snapshot_id = $snapshot ORDER BY node_id;
                """;
            command.Parameters.AddWithValue("$snapshot", Id(snapshot.SnapshotId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                nodes.Add(new(reader.GetString(0), Text(reader, 1), reader.GetString(2), Text(reader, 3),
                    Integer(reader, 4), Integer(reader, 5), Integer(reader, 6), Integer(reader, 7),
                    Integer(reader, 8), Number(reader, 9), Number(reader, 10), reader.GetString(11)));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot with { Nodes = nodes.ToImmutable() };
    }

    public async Task AppendRaidFieldAsync(RaidFieldHistoryRecord record, CancellationToken cancellationToken)
    {
        if (record.RaidId == Guid.Empty || string.IsNullOrWhiteSpace(record.FieldName) ||
            record.ProvenanceKind is not ("manual" or "observed") || string.IsNullOrWhiteSpace(record.Source))
            throw new ArgumentException("Raid field provenance is incomplete.", nameof(record));
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

    public async Task<ImmutableArray<RaidFieldHistoryRecord>> ListRaidFieldsAsync(Guid raidId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, field_name, value_json, provenance_kind, source, observed_utc, recorded_utc, confidence
            FROM raid_field_history WHERE raid_id = $raid ORDER BY recorded_utc, id;
            """;
        command.Parameters.AddWithValue("$raid", Id(raidId));
        var result = ImmutableArray.CreateBuilder<RaidFieldHistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(reader.GetInt64(0), raidId, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), Time(reader, 5), RequiredTime(reader, 6), Number(reader, 7)));
        return result.ToImmutable();
    }

    public async Task AppendCraftHistoryAsync(CraftHistoryRecord record, CancellationToken cancellationToken)
    {
        if (record.HistoryId == Guid.Empty || string.IsNullOrWhiteSpace(record.CraftId) || string.IsNullOrWhiteSpace(record.Source))
            throw new ArgumentException("Craft history identity and source are required.", nameof(record));
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

    public async Task<ImmutableArray<CraftHistoryRecord>> ListCraftHistoryAsync(string craftId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(craftId);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT history_id, station_id, station_level, observed_utc, recorded_utc, output_item_id,
                   output_count, estimated_cost_roubles, estimated_yield_roubles, source, payload_json
            FROM craft_history WHERE craft_id = $craft ORDER BY observed_utc DESC, recorded_utc DESC;
            """;
        command.Parameters.AddWithValue("$craft", craftId);
        var result = ImmutableArray.CreateBuilder<CraftHistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(Guid.Parse(reader.GetString(0)), craftId, Text(reader, 1), Integer(reader, 2),
                Time(reader, 3), RequiredTime(reader, 4), Text(reader, 5), Number(reader, 6),
                Long(reader, 7), Long(reader, 8), reader.GetString(9), reader.GetString(10)));
        return result.ToImmutable();
    }

    public async Task<bool> TrySaveLoadoutPlanAsync(long expectedRevision, LoadoutPlanRecord plan, CancellationToken cancellationToken)
    {
        if (plan.PlanId == Guid.Empty || plan.ProfileId == Guid.Empty || expectedRevision < 0 ||
            expectedRevision == long.MaxValue || plan.Revision != expectedRevision + 1 ||
            string.IsNullOrWhiteSpace(plan.Name))
            throw new ArgumentException("Loadout plan identity, name, and the next monotonic revision are required.", nameof(plan));
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
                       SELECT 1 FROM loadout_plans WHERE plan_id = $id AND revision = $expected
                   ))
            ON CONFLICT(plan_id) DO UPDATE SET
                profile_id = excluded.profile_id, generation = excluded.generation, game_mode = excluded.game_mode,
                name = excluded.name, revision = excluded.revision, updated_utc = excluded.updated_utc,
                payload_json = excluded.payload_json, extension_json = excluded.extension_json
            WHERE loadout_plans.revision = $expected;
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

    public async Task<LoadoutPlanRecord?> ReadLoadoutPlanAsync(Guid planId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, generation, game_mode, name, revision, updated_utc, payload_json, extension_json
            FROM loadout_plans WHERE plan_id = $id;
            """;
        command.Parameters.AddWithValue("$id", Id(planId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(planId, Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), RequiredTime(reader, 5), reader.GetString(6), reader.GetString(7))
            : null;
    }

    public async Task SaveModelSnapshotAsync(ModelSnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.ModelSnapshotId == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.ModelKind) ||
            !ModelPresentations.Contains(snapshot.PresentationKind, StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(snapshot.Source) || string.IsNullOrWhiteSpace(snapshot.ModelVersion) ||
            snapshot.Source.Contains("live", StringComparison.OrdinalIgnoreCase) ||
            snapshot.ModelKind.Contains("live", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Models are historical evidence and must never be presented as live detection.", nameof(snapshot));
        ValidateUnit(snapshot.Coverage, nameof(snapshot.Coverage));
        ValidateUnit(snapshot.Confidence, nameof(snapshot.Confidence));
        ValidateUnit(snapshot.Calibration, nameof(snapshot.Calibration));
        ValidateJson(snapshot.PayloadJson, nameof(snapshot.PayloadJson));
        ValidateJson(snapshot.ExtensionJson, nameof(snapshot.ExtensionJson));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            INSERT INTO model_snapshots(
                model_snapshot_id, profile_id, generation, game_mode, model_kind, presentation_kind,
                source, observed_utc, data_through_utc, generated_utc, coverage, confidence,
                calibration, model_version, payload_json, extension_json)
            VALUES ($id, $profile, $generation, $mode, $kind, $presentation, $source, $observed,
                    $through, $generated, $coverage, $confidence, $calibration, $version, $payload, $extension);
            """, cancellationToken, ("$id", Id(snapshot.ModelSnapshotId)),
            ("$profile", snapshot.ProfileId is { } profile ? Id(profile) : null),
            ("$generation", snapshot.Generation), ("$mode", snapshot.GameMode), ("$kind", snapshot.ModelKind),
            ("$presentation", snapshot.PresentationKind), ("$source", snapshot.Source),
            ("$observed", Format(snapshot.ObservedUtc)), ("$through", Format(snapshot.DataThroughUtc)),
            ("$generated", Format(snapshot.GeneratedUtc)), ("$coverage", snapshot.Coverage),
            ("$confidence", snapshot.Confidence), ("$calibration", snapshot.Calibration),
            ("$version", snapshot.ModelVersion), ("$payload", snapshot.PayloadJson),
            ("$extension", snapshot.ExtensionJson)).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<ModelSnapshotRecord>> ListModelSnapshotsAsync(
        Guid? profileId,
        string? generation,
        string? gameMode,
        string modelKind,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKind);
        if (maximumCount is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT model_snapshot_id, profile_id, generation, game_mode, presentation_kind, source,
                   observed_utc, data_through_utc, generated_utc, coverage, confidence, calibration,
                   model_version, payload_json, extension_json
            FROM model_snapshots
            WHERE model_kind = $kind
              AND (($profile IS NULL AND profile_id IS NULL) OR profile_id = $profile)
              AND (($generation IS NULL AND generation IS NULL) OR generation = $generation)
              AND (($mode IS NULL AND game_mode IS NULL) OR game_mode = $mode)
            ORDER BY generated_utc DESC LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$kind", modelKind);
        command.Parameters.AddWithValue("$profile", profileId is { } profile ? Id(profile) : DBNull.Value);
        command.Parameters.AddWithValue("$generation", (object?)generation ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", (object?)gameMode ?? DBNull.Value);
        command.Parameters.AddWithValue("$maximum", maximumCount);
        var result = ImmutableArray.CreateBuilder<ModelSnapshotRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                Text(reader, 2), Text(reader, 3), modelKind, reader.GetString(4), reader.GetString(5),
                Time(reader, 6), Time(reader, 7), RequiredTime(reader, 8), Number(reader, 9), Number(reader, 10),
                Number(reader, 11), reader.GetString(12), reader.GetString(13), reader.GetString(14)));
        return result.ToImmutable();
    }

    public async Task SaveRetentionPolicyAsync(DataRetentionPolicy policy, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(policy.PolicyKey) || policy.ScreenshotRetentionHours is < 1 or > 720 || policy.DataRetentionDays is < 1)
            throw new ArgumentException("Retention limits are outside their safe bounds.", nameof(policy));
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
        ArgumentException.ThrowIfNullOrWhiteSpace(policyKey);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT screenshot_retention_enabled, screenshot_retention_hours, debug_capture_enabled,
                   data_retention_days, updated_utc, extension_json
            FROM retention_policies WHERE policy_key = $key;
            """;
        command.Parameters.AddWithValue("$key", policyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(policyKey, reader.GetBoolean(0), Integer(reader, 1), reader.GetBoolean(2), Integer(reader, 3),
                RequiredTime(reader, 4), reader.GetString(5))
            : null;
    }

    public async Task RecordLocalJsonRecoveryAsync(LocalJsonRecoveryRecord record, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(record.DocumentKey) || record.State is not ("current" or "malformed" or "quarantined" or "missing") || string.IsNullOrWhiteSpace(record.DiagnosticCode))
            throw new ArgumentException("Local JSON recovery state is invalid.", nameof(record));
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
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state, detected_utc, content_sha256, diagnostic_code FROM local_json_recovery WHERE document_key = $key;";
        command.Parameters.AddWithValue("$key", documentKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(documentKey, reader.GetString(0), RequiredTime(reader, 1), Text(reader, 2), reader.GetString(3))
            : null;
    }

    private static void ValidateInventory(ObservedInventorySnapshot snapshot)
    {
        if (snapshot.SnapshotId == Guid.Empty || snapshot.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.Generation) ||
            string.IsNullOrWhiteSpace(snapshot.GameMode) || string.IsNullOrWhiteSpace(snapshot.Source) || string.IsNullOrWhiteSpace(snapshot.ProducerVersion))
            throw new ArgumentException("Inventory snapshot identity and provenance are required.", nameof(snapshot));
        ValidateUnit(snapshot.Coverage, nameof(snapshot.Coverage));
        ValidateUnit(snapshot.Confidence, nameof(snapshot.Confidence));
        ValidateJson(snapshot.PayloadJson, nameof(snapshot.PayloadJson));
        ValidateJson(snapshot.ExtensionJson, nameof(snapshot.ExtensionJson));
        var nodes = snapshot.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        if (nodes.Count != snapshot.Nodes.Length || nodes.ContainsKey(string.Empty)) throw new ArgumentException("Inventory node ids must be non-empty and unique.", nameof(snapshot));
        foreach (var node in snapshot.Nodes)
        {
            if (!InventoryKinds.Contains(node.NodeKind, StringComparer.Ordinal) ||
                node.ParentNodeId is { } parent && !nodes.ContainsKey(parent) || node.ParentNodeId == node.NodeId)
                throw new ArgumentException("Inventory hierarchy contains an invalid node or parent.", nameof(snapshot));
            if (node.ItemCount < 0 || node.Width < 0 || node.Height < 0) throw new ArgumentOutOfRangeException(nameof(snapshot));
            ValidateOptionalFinite(node.WeightKg, nameof(node.WeightKg), 0, 1_000_000);
            ValidateUnit(node.Confidence, nameof(node.Confidence));
            ValidateJson(node.RawJson, nameof(node.RawJson));
            var seen = new HashSet<string>(StringComparer.Ordinal) { node.NodeId };
            for (var current = node.ParentNodeId; current is not null; current = nodes[current].ParentNodeId)
                if (!seen.Add(current)) throw new ArgumentException("Inventory hierarchy contains a cycle.", nameof(snapshot));
        }
    }

    private static void ValidateJson(string json, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new ArgumentOutOfRangeException(parameterName);
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Persisted JSON must be an object.", parameterName);
    }

    private static void ValidateUnit(double? value, string name) => ValidateOptionalFinite(value, name, 0, 1);
    private static void ValidateOptionalFinite(double? value, string name, double minimum, double maximum)
    {
        if (value is { } number && (!double.IsFinite(number) || number < minimum || number > maximum)) throw new ArgumentOutOfRangeException(name);
    }

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
    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static int? Integer(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static double? Number(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    private static long? Long(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static DateTimeOffset? Time(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : RequiredTime(reader, ordinal);
    private static DateTimeOffset RequiredTime(SqliteDataReader reader, int ordinal) => DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
