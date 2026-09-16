using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.Infrastructure.Persistence.Stash;

/// <summary>
/// Snapshot lifecycle over the existing observed_inventory tables. Deletion promotes the newest
/// surviving snapshot in the same profile scope; retention can remove only non-current history.
/// </summary>
public sealed class SqliteStashSnapshotStore(
    SqliteConnectionFactory connectionFactory,
    SqliteV2DataStore dataStore) : IStashSnapshotStore
{
    public const int MaximumListCount = 256;

    public Task SaveAsync(StashSnapshotRecord snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return dataStore.SaveInventorySnapshotAsync(
            new ObservedInventorySnapshot(
                snapshot.SnapshotId,
                snapshot.ProfileScope.ProfileId,
                snapshot.ProfileScope.Generation,
                snapshot.ProfileScope.GameMode,
                snapshot.RecordedUtc,
                snapshot.IsCurrent,
                snapshot.Recognition,
                snapshot.DataSnapshotId),
            cancellationToken);
    }

    public async Task<StashSnapshotRecord?> ReadCurrentAsync(
        InventoryProfileScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var stored = await dataStore.ReadCurrentInventoryAsync(
            scope.ProfileId,
            scope.Generation,
            scope.GameMode,
            cancellationToken).ConfigureAwait(false);
        return stored is null
            ? null
            : new StashSnapshotRecord(
                stored.SnapshotId,
                scope,
                stored.DataSnapshotId ?? throw new InvalidDataException("The persisted data snapshot id is missing."),
                stored.RecordedUtc,
                stored.IsCurrent,
                stored.Recognition);
    }

    public async Task<StashSnapshotRecord?> ReadAsync(
        InventoryProfileScope scope,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateSnapshotId(snapshotId);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_id, data_snapshot_id, observed_utc, recorded_utc, source,
                   producer_version, coverage, confidence, is_current, payload_json
            FROM observed_inventory_snapshots
            WHERE snapshot_id = $snapshot AND profile_id = $profile
              AND generation = $generation AND game_mode = $mode
            LIMIT 1;
            """;
        BindScope(command, scope);
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRecord(reader, scope)
            : null;
    }

    public async Task<IReadOnlyList<StashSnapshotSummary>> ListAsync(
        InventoryProfileScope scope,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (maximumCount is < 1 or > MaximumListCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_id, data_snapshot_id, observed_utc, recorded_utc, source,
                   producer_version, coverage, confidence, is_current, payload_json
            FROM observed_inventory_snapshots
            WHERE profile_id = $profile AND generation = $generation AND game_mode = $mode
            ORDER BY recorded_utc DESC, snapshot_id DESC
            LIMIT $maximum;
            """;
        BindScope(command, scope);
        command.Parameters.AddWithValue("$maximum", maximumCount);
        var result = new List<StashSnapshotSummary>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = ReadRecord(reader, scope);
            var root = record.Recognition.Result;
            result.Add(new StashSnapshotSummary(
                record.SnapshotId,
                root.Value!.SnapshotId,
                record.DataSnapshotId,
                record.RecordedUtc,
                record.IsCurrent,
                root.Status,
                root.Provenance.Coverage ?? new EvidenceCoverage(
                    description: "The persisted recognition root did not quantify aggregate coverage.")));
        }

        return result.AsReadOnly();
    }

    public async Task<StashSnapshotDeleteResult> DeleteAsync(
        InventoryProfileScope scope,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateSnapshotId(snapshotId);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var wasCurrent = await ReadCurrentFlagAsync(
            connection,
            transaction,
            scope,
            snapshotId,
            cancellationToken).ConfigureAwait(false);
        if (wasCurrent is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new StashSnapshotDeleteResult(false, null);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM observed_inventory_snapshots
                WHERE snapshot_id = $snapshot AND profile_id = $profile
                  AND generation = $generation AND game_mode = $mode;
                """;
            BindScope(delete, scope);
            delete.Parameters.AddWithValue("$snapshot", snapshotId.ToString("D"));
            if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException("The selected stash snapshot changed during deletion.");
            }
        }

        Guid? promoted = null;
        if (wasCurrent.Value)
        {
            promoted = await PromoteNewestAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StashSnapshotDeleteResult(true, promoted);
    }

    public async Task<StashSnapshotRetentionResult> ApplyRetentionAsync(
        InventoryProfileScope scope,
        DateTimeOffset retainFromUtc,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (retainFromUtc.Offset != TimeSpan.Zero || retainFromUtc == default)
        {
            throw new ArgumentException("Retention cutoff must be a defined UTC instant.", nameof(retainFromUtc));
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        int matched;
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = """
                SELECT COUNT(*) FROM observed_inventory_snapshots
                WHERE profile_id = $profile AND generation = $generation AND game_mode = $mode
                  AND is_current = 0 AND recorded_utc < $cutoff;
                """;
            BindScope(count, scope);
            count.Parameters.AddWithValue("$cutoff", Format(retainFromUtc));
            matched = checked((int)(long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L));
        }

        var deleted = 0;
        if (!dryRun && matched > 0)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM observed_inventory_snapshots
                WHERE profile_id = $profile AND generation = $generation AND game_mode = $mode
                  AND is_current = 0 AND recorded_utc < $cutoff;
                """;
            BindScope(delete, scope);
            delete.Parameters.AddWithValue("$cutoff", Format(retainFromUtc));
            deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (deleted != matched)
            {
                throw new InvalidDataException("Stash retention changed after its bounded preview.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StashSnapshotRetentionResult(matched, deleted, dryRun);
    }

    private static StashSnapshotRecord ReadRecord(SqliteDataReader reader, InventoryProfileScope scope)
    {
        var snapshotId = ReadGuid(reader, 0, "stash snapshot id");
        var dataSnapshotId = ReadText(reader, 1, "stash data snapshot id");
        var observedUtc = ReadOptionalUtc(reader, 2, "stash observation time");
        var recordedUtc = ReadUtc(reader, 3, "stash recording time");
        var source = ReadText(reader, 4, "stash source");
        var producer = ReadText(reader, 5, "stash producer version");
        var coverage = ReadOptionalUnit(reader, 6, "stash coverage");
        var confidence = ReadOptionalUnit(reader, 7, "stash confidence");
        var isCurrent = ReadBoolean(reader, 8, "stash current state");
        var payload = ReadText(reader, 9, "stash payload", SqliteV2DataStore.MaximumContractJsonBytes);
        RecognitionResultEnvelope<StashRecognition> recognition;
        try
        {
            recognition = JsonSerializer.Deserialize<RecognitionResultEnvelope<StashRecognition>>(
                payload,
                V2ContractJson.Options) ?? throw new JsonException("The stash payload was null.");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException("The persisted stash payload is invalid.", exception);
        }

        try
        {
            SqliteV2DataStore.ValidateInventoryRecognition(recognition);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The persisted stash recognition violates the bounded inventory contract.", exception);
        }

        var root = recognition.Result;
        if (root.Value is null ||
            observedUtc != root.Provenance.ObservedUtc ||
            !string.Equals(source, root.Provenance.SourceIdentifier, StringComparison.Ordinal) ||
            !string.Equals(producer, root.Provenance.Producer.Version, StringComparison.Ordinal) ||
            coverage != root.Provenance.Coverage?.Fraction ||
            confidence != root.Provenance.Confidence.Score)
        {
            throw new InvalidDataException("Persisted stash metadata does not match its typed evidence root.");
        }

        return new StashSnapshotRecord(
            snapshotId,
            scope,
            dataSnapshotId,
            recordedUtc,
            isCurrent,
            recognition);
    }

    private static async Task<bool?> ReadCurrentFlagAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        InventoryProfileScope scope,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT is_current FROM observed_inventory_snapshots
            WHERE snapshot_id = $snapshot AND profile_id = $profile
              AND generation = $generation AND game_mode = $mode
            LIMIT 1;
            """;
        BindScope(command, scope);
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : ReadBoolean(value, "stash current state");
    }

    private static async Task<Guid?> PromoteNewestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        InventoryProfileScope scope,
        CancellationToken cancellationToken)
    {
        string? promotedId;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT snapshot_id FROM observed_inventory_snapshots
                WHERE profile_id = $profile AND generation = $generation AND game_mode = $mode
                ORDER BY recorded_utc DESC, snapshot_id DESC
                LIMIT 1;
                """;
            BindScope(select, scope);
            promotedId = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        if (promotedId is null)
        {
            return null;
        }

        if (!Guid.TryParseExact(promotedId, "D", out var promoted))
        {
            throw new InvalidDataException("The replacement stash snapshot id is invalid.");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE observed_inventory_snapshots SET is_current = 1 WHERE snapshot_id = $snapshot;";
        update.Parameters.AddWithValue("$snapshot", promotedId);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("The replacement stash snapshot could not be promoted.");
        }

        return promoted;
    }

    private static void BindScope(SqliteCommand command, InventoryProfileScope scope)
    {
        command.Parameters.AddWithValue("$profile", scope.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$generation", scope.Generation);
        command.Parameters.AddWithValue("$mode", scope.GameMode);
    }

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal, string description)
    {
        var value = ReadText(reader, ordinal, description, 64);
        return Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new InvalidDataException($"Persisted {description} is invalid.");
    }

    private static string ReadText(
        SqliteDataReader reader,
        int ordinal,
        string description,
        int maximumUtf8Bytes = SqliteV2DataStore.MaximumContractStringUtf8Bytes)
    {
        if (reader.GetValue(ordinal) is not string value || string.IsNullOrWhiteSpace(value) ||
            Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new InvalidDataException($"Persisted {description} is invalid.");
        }

        return value;
    }

    private static string? ReadOptionalText(SqliteDataReader reader, int ordinal, string description) =>
        reader.IsDBNull(ordinal) ? null : ReadText(reader, ordinal, description);

    private static DateTimeOffset ReadUtc(SqliteDataReader reader, int ordinal, string description)
    {
        var text = ReadText(reader, ordinal, description, 64);
        if (!DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed) || parsed == default || parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"Persisted {description} is not canonical UTC.");
        }

        return parsed;
    }

    private static DateTimeOffset? ReadOptionalUtc(SqliteDataReader reader, int ordinal, string description) =>
        reader.IsDBNull(ordinal) ? null : ReadUtc(reader, ordinal, description);

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal, string description) =>
        ReadBoolean(reader.GetValue(ordinal), description);

    private static bool ReadBoolean(object value, string description) => value switch
    {
        0L => false,
        1L => true,
        _ => throw new InvalidDataException($"Persisted {description} is not Boolean."),
    };

    private static double? ReadOptionalUnit(SqliteDataReader reader, int ordinal, string description)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal) switch
        {
            double number => number,
            long number => number,
            _ => throw new InvalidDataException($"Persisted {description} is not numeric."),
        };
        return double.IsFinite(value) && value is >= 0 and <= 1
            ? value
            : throw new InvalidDataException($"Persisted {description} is outside the unit range.");
    }

    private static void ValidateSnapshotId(Guid snapshotId)
    {
        if (snapshotId == Guid.Empty)
        {
            throw new ArgumentException("A snapshot id is required.", nameof(snapshotId));
        }
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
