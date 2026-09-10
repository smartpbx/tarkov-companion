using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

public sealed class SqliteQuestProgressImportStore(
    SqliteConnectionFactory connectionFactory,
    TimeProvider? timeProvider = null) : IQuestProgressImportStore
{
    private const string UndoSource = "Undo reviewed quest progress import";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<QuestImportApplyResult> ApplyImportAsync(
        QuestProgressImportPreview preview,
        string expectedPreviewSha256,
        IReadOnlyDictionary<string, QuestImportResolution> resolutions,
        CancellationToken cancellationToken)
    {
        ValidatePreview(preview, expectedPreviewSha256, resolutions);
        var recordedUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureProfileAsync(connection, transaction, preview, recordedUtc, cancellationToken).ConfigureAwait(false);

        var existing = await GetExistingImportAsync(
            connection, transaction, preview.Scope, preview.PayloadSha256, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing with { AlreadyApplied = true };
        }

        var currentRevision = await GetRevisionAsync(
            connection, transaction, preview.Scope, cancellationToken).ConfigureAwait(false);
        if (currentRevision != preview.BaseRevision)
        {
            throw new InvalidOperationException(
                $"Quest progress changed after preview (expected revision {preview.BaseRevision}, current {currentRevision}); preview again.");
        }

        var importId = Guid.NewGuid();
        var source = preview.Source switch
        {
            QuestProgressImportSource.ProjectJsonV2 => "Project JSON v2 import",
            QuestProgressImportSource.LegacyProfileJsonV1 => "Project profile JSON v1 compatibility import",
            QuestProgressImportSource.TarkovTracker => "TarkovTracker read-only snapshot import",
            _ => throw new InvalidOperationException("The quest progress import source is unsupported."),
        };
        var changes = preview.Proposals.Where(proposal =>
            proposal.Classification == QuestImportClassification.SafeMonotonic ||
            proposal.Classification == QuestImportClassification.Conflict &&
            resolutions[proposal.Key] == QuestImportResolution.UseIncoming).ToArray();
        var nextRevision = changes.Length == 0 ? currentRevision : checked(currentRevision + 1);
        var keptLocal = preview.Conflicts.Count(proposal =>
            resolutions[proposal.Key] == QuestImportResolution.KeepLocal);
        await InsertImportAsync(
            connection,
            transaction,
            importId,
            preview,
            nextRevision,
            recordedUtc,
            changes.Length,
            keptLocal,
            preview.Unresolved.Count,
            cancellationToken).ConfigureAwait(false);

        foreach (var proposal in changes)
        {
            await ApplyValueAsync(
                connection,
                transaction,
                preview.Scope,
                proposal.EntityKind,
                proposal.EntityId,
                proposal.IncomingValue,
                nextRevision,
                source,
                recordedUtc,
                cancellationToken).ConfigureAwait(false);
            await AppendJournalAsync(
                connection,
                transaction,
                preview.Scope,
                importId,
                proposal,
                nextRevision,
                source,
                recordedUtc,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var conflict in preview.Conflicts)
        {
            await InsertConflictAsync(
                connection,
                transaction,
                importId,
                conflict,
                resolutions[conflict.Key],
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var unresolved in preview.Unresolved)
        {
            await InsertUnresolvedAsync(
                connection,
                transaction,
                importId,
                unresolved,
                cancellationToken).ConfigureAwait(false);
        }

        if (changes.Length > 0)
        {
            await UpdateProfileAsync(
                connection,
                transaction,
                preview.Scope,
                preview.ProfileName,
                nextRevision,
                recordedUtc,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            importId,
            nextRevision,
            changes.Length > 0,
            false,
            changes.Length,
            keptLocal,
            preview.Unresolved.Count);
    }

    public async Task<QuestImportUndoResult> UndoImportAsync(
        QuestProfileScope scope,
        Guid importId,
        CancellationToken cancellationToken)
    {
        ValidateScope(scope);
        if (importId == Guid.Empty)
        {
            throw new ArgumentException("An import id is required.", nameof(importId));
        }

        var recordedUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var import = await GetImportAsync(connection, transaction, scope, importId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected project import does not exist in this exact profile scope.");
        var existingUndo = await GetExistingUndoAsync(
            connection, transaction, importId, cancellationToken).ConfigureAwait(false);
        if (existingUndo is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existingUndo with { AlreadyUndone = true };
        }

        var journal = await LoadImportJournalAsync(
            connection, transaction, scope, importId, cancellationToken).ConfigureAwait(false);
        if (journal.Count != import.AppliedChangeCount)
        {
            throw new InvalidOperationException("The import journal is incomplete; no undo changes were made.");
        }

        var currentRevision = await GetRevisionAsync(connection, transaction, scope, cancellationToken)
            .ConfigureAwait(false);
        if (journal.Count > 0 && currentRevision != import.AppliedRevision)
        {
            throw new InvalidOperationException(
                "Quest progress changed after this import; undo would overwrite later work and was refused.");
        }

        var undoId = Guid.NewGuid();
        var nextRevision = journal.Count == 0 ? currentRevision : checked(currentRevision + 1);
        foreach (var change in journal)
        {
            var currentValue = DeserializeValue(change.NewValueJson)
                ?? throw new InvalidOperationException("An applied import journal entry has no current value.");
            var inverseValue = DeserializeValue(change.InverseValueJson);
            await RestoreValueAsync(
                connection,
                transaction,
                scope,
                change.EntityKind,
                change.EntityId,
                currentValue,
                inverseValue,
                nextRevision,
                recordedUtc,
                cancellationToken).ConfigureAwait(false);
            await AppendUndoJournalAsync(
                connection,
                transaction,
                scope,
                undoId,
                change,
                nextRevision,
                recordedUtc,
                cancellationToken).ConfigureAwait(false);
        }

        if (journal.Count > 0)
        {
            await UpdateProfileAsync(
                connection,
                transaction,
                scope,
                import.ProfileName,
                nextRevision,
                recordedUtc,
                cancellationToken).ConfigureAwait(false);
        }

        await InsertUndoAsync(
            connection,
            transaction,
            importId,
            undoId,
            nextRevision,
            journal.Count,
            recordedUtc,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(importId, undoId, nextRevision, journal.Count > 0, false, journal.Count);
    }

    private static void ValidatePreview(
        QuestProgressImportPreview preview,
        string expectedPreviewSha256,
        IReadOnlyDictionary<string, QuestImportResolution> resolutions)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(resolutions);
        ValidateScope(preview.Scope);
        if (string.IsNullOrWhiteSpace(preview.ProfileName) || preview.ProfileName.Length > 256)
        {
            throw new InvalidOperationException("The import preview profile name is invalid.");
        }

        if (preview.BaseRevision < 0 ||
            !Enum.IsDefined(preview.Source) ||
            !IsSha256(preview.PayloadSha256) ||
            !IsSha256(preview.PreviewSha256) ||
            !IsSha256(expectedPreviewSha256))
        {
            throw new InvalidOperationException("The import preview identifiers or base revision are invalid.");
        }

        if (preview.Proposals.Count > 50_000 ||
            preview.Proposals.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() != preview.Proposals.Count)
        {
            throw new InvalidOperationException("The import preview contains too many proposals or duplicate proposal keys.");
        }

        var calculatedHash = QuestImportPreviewHash.Compute(preview);
        if (!calculatedHash.Equals(preview.PreviewSha256, StringComparison.Ordinal) ||
            !calculatedHash.Equals(expectedPreviewSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The import preview hash is stale or was modified.");
        }

        var conflicts = preview.Conflicts.Select(value => value.Key).ToHashSet(StringComparer.Ordinal);
        if (conflicts.Any(value => !resolutions.ContainsKey(value)) ||
            resolutions.Keys.Any(value => !conflicts.Contains(value)) ||
            resolutions.Values.Any(value => !Enum.IsDefined(value)))
        {
            throw new InvalidOperationException("Import conflict resolutions do not exactly match the preview.");
        }
    }

    private static async Task EnsureProfileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProgressImportPreview preview,
        DateTimeOffset recordedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quest_progress_profiles(
                profile_id, game_mode, generation, display_name, revision, created_utc, modified_utc)
            VALUES ($profileId, $gameMode, $generation, $displayName, 0, $recordedUtc, $recordedUtc)
            ON CONFLICT(profile_id, game_mode, generation) DO NOTHING;
            """;
        AddScope(command, preview.Scope);
        command.Parameters.AddWithValue("$displayName", preview.ProfileName);
        command.Parameters.AddWithValue("$recordedUtc", Timestamp(recordedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<QuestImportApplyResult?> GetExistingImportAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        string payloadSha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT import_id, applied_revision, applied_change_count, kept_local_count, unresolved_count
            FROM quest_progress_imports
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
              AND payload_sha256 = $payloadSha256;
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$payloadSha256", payloadSha256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetInt32(2) > 0,
                true,
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4))
            : null;
    }

    private static async Task InsertImportAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid importId,
        QuestProgressImportPreview preview,
        long appliedRevision,
        DateTimeOffset recordedUtc,
        int appliedChangeCount,
        int keptLocalCount,
        int unresolvedCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quest_progress_imports(
                import_id, profile_id, game_mode, generation, profile_name,
                payload_sha256, preview_sha256, base_revision, applied_revision,
                source_app_version, source_exported_utc, provenance_summary, imported_utc,
                applied_change_count, kept_local_count, unresolved_count)
            VALUES (
                $importId, $profileId, $gameMode, $generation, $profileName,
                $payloadSha256, $previewSha256, $baseRevision, $appliedRevision,
                $sourceAppVersion, $sourceExportedUtc, $provenanceSummary, $importedUtc,
                $appliedChangeCount, $keptLocalCount, $unresolvedCount);
            """;
        command.Parameters.AddWithValue("$importId", importId.ToString("D"));
        AddScope(command, preview.Scope);
        command.Parameters.AddWithValue("$profileName", preview.ProfileName);
        command.Parameters.AddWithValue("$payloadSha256", preview.PayloadSha256);
        command.Parameters.AddWithValue("$previewSha256", preview.PreviewSha256);
        command.Parameters.AddWithValue("$baseRevision", preview.BaseRevision);
        command.Parameters.AddWithValue("$appliedRevision", appliedRevision);
        command.Parameters.AddWithValue("$sourceAppVersion", preview.SourceAppVersion);
        command.Parameters.AddWithValue("$sourceExportedUtc", Timestamp(preview.ExportedUtc));
        command.Parameters.AddWithValue("$provenanceSummary", preview.ProvenanceSummary);
        command.Parameters.AddWithValue("$importedUtc", Timestamp(recordedUtc));
        command.Parameters.AddWithValue("$appliedChangeCount", appliedChangeCount);
        command.Parameters.AddWithValue("$keptLocalCount", keptLocalCount);
        command.Parameters.AddWithValue("$unresolvedCount", unresolvedCount);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyValueAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        QuestProgressEntityKind entityKind,
        string entityId,
        QuestImportValue value,
        long revision,
        string source,
        DateTimeOffset recordedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$recordedUtc", Timestamp(recordedUtc));
        switch (entityKind)
        {
            case QuestProgressEntityKind.Task when value.TaskState is { } state:
                command.CommandText = """
                    INSERT INTO quest_profile_task_states(
                        profile_id, game_mode, generation, task_id, state, assertion_source, revision, modified_utc)
                    VALUES ($profileId, $gameMode, $generation, $entityId, $state, $source, $revision, $recordedUtc)
                    ON CONFLICT(profile_id, game_mode, generation, task_id) DO UPDATE SET
                        state = excluded.state, assertion_source = excluded.assertion_source,
                        revision = excluded.revision, modified_utc = excluded.modified_utc;
                    """;
                command.Parameters.AddWithValue("$state", state.ToString());
                break;
            case QuestProgressEntityKind.Objective when value.ObjectiveState is { } state:
                if (value.ObjectiveCount is < 0)
                {
                    throw new InvalidOperationException("An import objective count cannot be negative.");
                }

                command.CommandText = """
                    INSERT INTO quest_profile_objective_states(
                        profile_id, game_mode, generation, objective_id, state, progress_count,
                        assertion_source, revision, modified_utc)
                    VALUES (
                        $profileId, $gameMode, $generation, $entityId, $state, $count,
                        $source, $revision, $recordedUtc)
                    ON CONFLICT(profile_id, game_mode, generation, objective_id) DO UPDATE SET
                        state = excluded.state, progress_count = excluded.progress_count,
                        assertion_source = excluded.assertion_source, revision = excluded.revision,
                        modified_utc = excluded.modified_utc;
                    """;
                command.Parameters.AddWithValue("$state", state.ToString());
                command.Parameters.AddWithValue("$count", value.ObjectiveCount is null
                    ? DBNull.Value
                    : value.ObjectiveCount.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case QuestProgressEntityKind.ItemHolding
                when value.HoldingFoundInRaid is { } foundInRaid && value.HoldingCount is { } count:
                if (count < 0)
                {
                    throw new InvalidOperationException("An import holding count cannot be negative.");
                }

                command.CommandText = """
                    INSERT INTO quest_profile_item_holdings(
                        profile_id, game_mode, generation, item_id, found_in_raid, item_count,
                        assertion_source, revision, modified_utc)
                    VALUES (
                        $profileId, $gameMode, $generation, $entityId, $foundInRaid, $count,
                        $source, $revision, $recordedUtc)
                    ON CONFLICT(profile_id, game_mode, generation, item_id, found_in_raid) DO UPDATE SET
                        item_count = excluded.item_count, assertion_source = excluded.assertion_source,
                        revision = excluded.revision, modified_utc = excluded.modified_utc;
                    """;
                command.Parameters.AddWithValue("$foundInRaid", foundInRaid);
                command.Parameters.AddWithValue("$count", count);
                break;
            case QuestProgressEntityKind.Pin
                when value.PinTargetKind is { } targetKind && value.PinSortOrder is { } sortOrder:
                command.CommandText = """
                    INSERT INTO quest_profile_pins(
                        profile_id, game_mode, generation, target_kind, target_id, sort_order, note,
                        assertion_source, revision, modified_utc)
                    VALUES (
                        $profileId, $gameMode, $generation, $targetKind, $entityId, $sortOrder, $note,
                        $source, $revision, $recordedUtc)
                    ON CONFLICT(profile_id, game_mode, generation, target_kind, target_id) DO UPDATE SET
                        sort_order = excluded.sort_order, note = excluded.note,
                        assertion_source = excluded.assertion_source, revision = excluded.revision,
                        modified_utc = excluded.modified_utc;
                    """;
                command.Parameters.AddWithValue("$targetKind", targetKind.ToString());
                command.Parameters.AddWithValue("$sortOrder", sortOrder);
                command.Parameters.AddWithValue("$note", value.PinNote is null ? DBNull.Value : value.PinNote);
                break;
            default:
                throw new InvalidOperationException($"Import proposal for {entityKind} is incomplete.");
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RestoreValueAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        QuestProgressEntityKind entityKind,
        string entityId,
        QuestImportValue currentValue,
        QuestImportValue? inverseValue,
        long revision,
        DateTimeOffset recordedUtc,
        CancellationToken cancellationToken)
    {
        if (inverseValue is not null)
        {
            await ApplyValueAsync(
                connection,
                transaction,
                scope,
                entityKind,
                entityId,
                inverseValue,
                revision,
                UndoSource,
                recordedUtc,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$entityId", entityId);
        command.CommandText = entityKind switch
        {
            QuestProgressEntityKind.Task => """
                DELETE FROM quest_profile_task_states
                WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
                  AND task_id = $entityId;
                """,
            QuestProgressEntityKind.Objective => """
                DELETE FROM quest_profile_objective_states
                WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
                  AND objective_id = $entityId;
                """,
            QuestProgressEntityKind.ItemHolding when currentValue.HoldingFoundInRaid is { } foundInRaid => """
                DELETE FROM quest_profile_item_holdings
                WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
                  AND item_id = $entityId AND found_in_raid = $foundInRaid;
                """,
            QuestProgressEntityKind.Pin when currentValue.PinTargetKind is { } targetKind => """
                DELETE FROM quest_profile_pins
                WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
                  AND target_kind = $targetKind AND target_id = $entityId;
                """,
            _ => throw new InvalidOperationException($"Import inverse for {entityKind} is incomplete."),
        };
        if (currentValue.HoldingFoundInRaid is { } holdingClass)
        {
            command.Parameters.AddWithValue("$foundInRaid", holdingClass);
        }

        if (currentValue.PinTargetKind is { } pinKind)
        {
            command.Parameters.AddWithValue("$targetKind", pinKind.ToString());
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AppendJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        Guid correlationId,
        QuestImportProposal proposal,
        long revision,
        string source,
        DateTimeOffset recordedUtc,
        CancellationToken cancellationToken) => await InsertJournalAsync(
            connection,
            transaction,
            scope,
            correlationId,
            proposal.EntityKind,
            proposal.EntityId,
            FieldName(proposal.EntityKind, proposal.IncomingValue),
            Serialize(proposal.LocalValue),
            Serialize(proposal.IncomingValue),
            Serialize(proposal.LocalValue),
            revision,
            source,
            recordedUtc,
            cancellationToken).ConfigureAwait(false);

    private static async Task AppendUndoJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        Guid correlationId,
        ImportJournalRow change,
        long revision,
        DateTimeOffset recordedUtc,
        CancellationToken cancellationToken) => await InsertJournalAsync(
            connection,
            transaction,
            scope,
            correlationId,
            change.EntityKind,
            change.EntityId,
            change.FieldName,
            change.NewValueJson,
            change.InverseValueJson,
            change.NewValueJson,
            revision,
            UndoSource,
            recordedUtc,
            cancellationToken).ConfigureAwait(false);

    private static async Task InsertJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        Guid correlationId,
        QuestProgressEntityKind entityKind,
        string entityId,
        string fieldName,
        string previousJson,
        string newJson,
        string inverseJson,
        long revision,
        string source,
        DateTimeOffset recordedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quest_progress_journal(
                profile_id, game_mode, generation, correlation_id, entity_kind, entity_id,
                field_name, previous_value_json, new_value_json, inverse_value_json,
                actor, assertion_source, revision, recorded_utc)
            VALUES (
                $profileId, $gameMode, $generation, $correlationId, $entityKind, $entityId,
                $fieldName, $previousJson, $newJson, $inverseJson,
                'Import', $source, $revision, $recordedUtc);
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$correlationId", correlationId.ToString("D"));
        command.Parameters.AddWithValue("$entityKind", entityKind.ToString());
        command.Parameters.AddWithValue("$entityId", entityId);
        command.Parameters.AddWithValue("$fieldName", fieldName);
        command.Parameters.AddWithValue("$previousJson", previousJson);
        command.Parameters.AddWithValue("$newJson", newJson);
        command.Parameters.AddWithValue("$inverseJson", inverseJson);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$recordedUtc", Timestamp(recordedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid importId,
        QuestImportProposal conflict,
        QuestImportResolution resolution,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quest_progress_import_conflicts(
                import_id, proposal_key, entity_kind, entity_id, local_value_json,
                incoming_value_json, reason, resolution)
            VALUES (
                $importId, $proposalKey, $entityKind, $entityId, $localValueJson,
                $incomingValueJson, $reason, $resolution);
            """;
        command.Parameters.AddWithValue("$importId", importId.ToString("D"));
        command.Parameters.AddWithValue("$proposalKey", conflict.Key);
        command.Parameters.AddWithValue("$entityKind", conflict.EntityKind.ToString());
        command.Parameters.AddWithValue("$entityId", conflict.EntityId);
        command.Parameters.AddWithValue("$localValueJson", Serialize(conflict.LocalValue));
        command.Parameters.AddWithValue("$incomingValueJson", Serialize(conflict.IncomingValue));
        command.Parameters.AddWithValue("$reason", conflict.Reason);
        command.Parameters.AddWithValue("$resolution", resolution.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertUnresolvedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid importId,
        QuestImportProposal unresolved,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quest_progress_import_unresolved(
                import_id, proposal_key, entity_kind, entity_id, incoming_value_json, reason)
            VALUES ($importId, $proposalKey, $entityKind, $entityId, $incomingValueJson, $reason);
            """;
        command.Parameters.AddWithValue("$importId", importId.ToString("D"));
        command.Parameters.AddWithValue("$proposalKey", unresolved.Key);
        command.Parameters.AddWithValue("$entityKind", unresolved.EntityKind.ToString());
        command.Parameters.AddWithValue("$entityId", unresolved.EntityId);
        command.Parameters.AddWithValue("$incomingValueJson", Serialize(unresolved.IncomingValue));
        command.Parameters.AddWithValue("$reason", unresolved.Reason);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateProfileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        string profileName,
        long revision,
        DateTimeOffset recordedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE quest_progress_profiles
            SET display_name = $profileName, revision = $revision, modified_utc = $recordedUtc
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation;
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$profileName", profileName);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$recordedUtc", Timestamp(recordedUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The quest progress import revision could not be advanced.");
        }
    }

    private static async Task<long> GetRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision FROM quest_progress_profiles
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation;
            """;
        AddScope(command, scope);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<ImportMetadata?> GetImportAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        Guid importId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT profile_name, applied_revision, applied_change_count
            FROM quest_progress_imports
            WHERE import_id = $importId AND profile_id = $profileId
              AND game_mode = $gameMode AND generation = $generation;
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$importId", importId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2))
            : null;
    }

    private static async Task<QuestImportUndoResult?> GetExistingUndoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid importId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT undo_correlation_id, restored_revision, restored_change_count
            FROM quest_progress_import_undos WHERE import_id = $importId;
            """;
        command.Parameters.AddWithValue("$importId", importId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(
                importId,
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetInt32(2) > 0,
                true,
                reader.GetInt32(2))
            : null;
    }

    private static async Task<IReadOnlyList<ImportJournalRow>> LoadImportJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        Guid importId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT entity_kind, entity_id, field_name, new_value_json, inverse_value_json
            FROM quest_progress_journal
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
              AND correlation_id = $importId AND actor = 'Import'
            ORDER BY id;
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$importId", importId.ToString("D"));
        var rows = new List<ImportJournalRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new(
                Enum.Parse<QuestProgressEntityKind>(reader.GetString(0), ignoreCase: false),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }

    private static async Task InsertUndoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid importId,
        Guid undoId,
        long revision,
        int restoredChangeCount,
        DateTimeOffset recordedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quest_progress_import_undos(
                import_id, undo_correlation_id, restored_revision, restored_change_count, recorded_utc)
            VALUES ($importId, $undoId, $revision, $restoredChangeCount, $recordedUtc);
            """;
        command.Parameters.AddWithValue("$importId", importId.ToString("D"));
        command.Parameters.AddWithValue("$undoId", undoId.ToString("D"));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$restoredChangeCount", restoredChangeCount);
        command.Parameters.AddWithValue("$recordedUtc", Timestamp(recordedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FieldName(QuestProgressEntityKind kind, QuestImportValue value) => kind switch
    {
        QuestProgressEntityKind.Task => "state",
        QuestProgressEntityKind.Objective => "progress",
        QuestProgressEntityKind.ItemHolding => value.HoldingFoundInRaid == true
            ? "foundInRaidCount"
            : "nonFoundInRaidCount",
        QuestProgressEntityKind.Pin => value.PinTargetKind == QuestPinTargetKind.Task
            ? "taskPin"
            : "objectivePin",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Serialize(QuestImportValue? value) =>
        JsonSerializer.Serialize(value, SerializerOptions);

    private static QuestImportValue? DeserializeValue(string json) =>
        JsonSerializer.Deserialize<QuestImportValue?>(json, SerializerOptions);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        serializerOptions.Converters.Add(new JsonStringEnumConverter());
        return serializerOptions;
    }

    private static void AddScope(SqliteCommand command, QuestProfileScope scope)
    {
        command.Parameters.AddWithValue("$profileId", scope.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$gameMode", scope.GameMode.ToString());
        command.Parameters.AddWithValue("$generation", scope.Generation);
    }

    private static void ValidateScope(QuestProfileScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ProfileId == Guid.Empty || !Enum.IsDefined(scope.GameMode) ||
            string.IsNullOrWhiteSpace(scope.Generation) || scope.Generation.Length > 128 ||
            !scope.Generation.Equals(scope.Generation.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The quest progress import scope is invalid.", nameof(scope));
        }
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed record ImportMetadata(string ProfileName, long AppliedRevision, int AppliedChangeCount);

    private sealed record ImportJournalRow(
        QuestProgressEntityKind EntityKind,
        string EntityId,
        string FieldName,
        string NewValueJson,
        string InverseValueJson);
}
