using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

public sealed class SqliteQuestProgressStore(
    SqliteConnectionFactory connectionFactory) : IQuestProgressStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public async Task<QuestProgressSnapshot> GetAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        ValidateScope(scope);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var revision = await GetRevisionAsync(connection, null, scope, cancellationToken).ConfigureAwait(false);
        var tasks = await LoadTasksAsync(connection, scope, cancellationToken).ConfigureAwait(false);
        var objectives = await LoadObjectivesAsync(connection, scope, cancellationToken).ConfigureAwait(false);
        var holdings = await LoadHoldingsAsync(connection, scope, cancellationToken).ConfigureAwait(false);
        var pins = await LoadPinsAsync(connection, scope, cancellationToken).ConfigureAwait(false);
        return new(scope, revision, tasks, objectives, holdings, pins);
    }

    public async Task<QuestProgressCommandResult> ApplyAsync(
        QuestProgressMutation mutation,
        CancellationToken cancellationToken)
    {
        ValidateMutation(mutation);
        var scope = mutation.Scope;
        var recordedUtc = mutation.RecordedUtc.ToUniversalTime();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await EnsureProfileAsync(connection, transaction, mutation, recordedUtc, cancellationToken).ConfigureAwait(false);
        var currentRevision = await GetRevisionAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        var nextRevision = checked(currentRevision + 1);
        var change = mutation switch
        {
            SetTaskStateMutation task => await ApplyTaskAsync(
                connection, transaction, task, nextRevision, recordedUtc, cancellationToken).ConfigureAwait(false),
            SetObjectiveProgressMutation objective => await ApplyObjectiveAsync(
                connection, transaction, objective, nextRevision, recordedUtc, cancellationToken).ConfigureAwait(false),
            SetItemHoldingMutation holding => await ApplyHoldingAsync(
                connection, transaction, holding, nextRevision, recordedUtc, cancellationToken).ConfigureAwait(false),
            SetQuestPinMutation pin => await ApplyPinAsync(
                connection, transaction, pin, nextRevision, recordedUtc, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), "Unsupported quest progress mutation."),
        };

        if (change is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(mutation.CorrelationId, currentRevision, false);
        }

        await UpdateProfileRevisionAsync(
            connection, transaction, scope, mutation.ProfileName.Trim(), nextRevision, recordedUtc, cancellationToken)
            .ConfigureAwait(false);
        await AppendJournalAsync(
            connection, transaction, mutation, change, nextRevision, recordedUtc, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(mutation.CorrelationId, nextRevision, true);
    }

    public async Task<IReadOnlyList<QuestProgressChange>> GetJournalAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        ValidateScope(scope);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, correlation_id, entity_kind, entity_id, field_name,
                   previous_value_json, new_value_json, inverse_value_json,
                   actor, assertion_source, revision, recorded_utc
            FROM quest_progress_journal
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
            ORDER BY revision, id;
            """;
        AddScope(command, scope);
        var changes = new List<QuestProgressChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new(
                reader.GetInt64(0),
                scope,
                Guid.Parse(reader.GetString(1)),
                Enum.Parse<QuestProgressEntityKind>(reader.GetString(2), ignoreCase: false),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                Enum.Parse<QuestProgressActor>(reader.GetString(8), ignoreCase: false),
                reader.GetString(9),
                reader.GetInt64(10),
                ParseTimestamp(reader.GetString(11))));
        }

        return changes;
    }

    private static async Task EnsureProfileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProgressMutation mutation,
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
        AddScope(command, mutation.Scope);
        command.Parameters.AddWithValue("$displayName", mutation.ProfileName.Trim());
        command.Parameters.AddWithValue("$recordedUtc", FormatTimestamp(recordedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> GetRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision
            FROM quest_progress_profiles
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation;
            """;
        AddScope(command, scope);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<PendingChange?> ApplyTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SetTaskStateMutation mutation,
        long revision,
        DateTimeOffset recordedUtc,
        CancellationToken cancellationToken)
    {
        var taskId = RequiredId(mutation.TaskId, nameof(mutation.TaskId));
        var previous = await ReadTaskAsync(connection, transaction, mutation.Scope, taskId, cancellationToken)
            .ConfigureAwait(false);
        if (previous?.State == mutation.State)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quest_profile_task_states(
                profile_id, game_mode, generation, task_id, state, assertion_source, revision, modified_utc)
            VALUES ($profileId, $gameMode, $generation, $taskId, $state, $source, $revision, $modifiedUtc)
            ON CONFLICT(profile_id, game_mode, generation, task_id) DO UPDATE SET
                state = excluded.state,
                assertion_source = excluded.assertion_source,
                revision = excluded.revision,
                modified_utc = excluded.modified_utc;
            """;
        AddScope(command, mutation.Scope);
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$state", mutation.State.ToString());
        AddMutationValues(command, mutation, revision, recordedUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new(
            QuestProgressEntityKind.Task,
            taskId,
            "state",
            Serialize(previous is null ? null : new TaskValue(previous.State)),
            Serialize(new TaskValue(mutation.State)));
    }

    private static async Task<PendingChange?> ApplyObjectiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SetObjectiveProgressMutation mutation,
        long revision,
        DateTimeOffset recordedUtc,
        CancellationToken cancellationToken)
    {
        var objectiveId = RequiredId(mutation.ObjectiveId, nameof(mutation.ObjectiveId));
        if (mutation.Count is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mutation), "Objective count cannot be negative.");
        }

        var previous = await ReadObjectiveAsync(
            connection, transaction, mutation.Scope, objectiveId, cancellationToken).ConfigureAwait(false);
        if (previous?.State == mutation.State && previous.Count == mutation.Count)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quest_profile_objective_states(
                profile_id, game_mode, generation, objective_id, state, progress_count,
                assertion_source, revision, modified_utc)
            VALUES (
                $profileId, $gameMode, $generation, $objectiveId, $state, $count,
                $source, $revision, $modifiedUtc)
            ON CONFLICT(profile_id, game_mode, generation, objective_id) DO UPDATE SET
                state = excluded.state,
                progress_count = excluded.progress_count,
                assertion_source = excluded.assertion_source,
                revision = excluded.revision,
                modified_utc = excluded.modified_utc;
            """;
        AddScope(command, mutation.Scope);
        command.Parameters.AddWithValue("$objectiveId", objectiveId);
        command.Parameters.AddWithValue("$state", mutation.State.ToString());
        command.Parameters.AddWithValue("$count", mutation.Count is null
            ? DBNull.Value
            : mutation.Count.Value.ToString(CultureInfo.InvariantCulture));
        AddMutationValues(command, mutation, revision, recordedUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new(
            QuestProgressEntityKind.Objective,
            objectiveId,
            "progress",
            Serialize(previous is null ? null : new ObjectiveValue(previous.State, previous.Count)),
            Serialize(new ObjectiveValue(mutation.State, mutation.Count)));
    }

    private static async Task<PendingChange?> ApplyHoldingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SetItemHoldingMutation mutation,
        long revision,
        DateTimeOffset recordedUtc,
        CancellationToken cancellationToken)
    {
        var itemId = RequiredId(mutation.ItemId, nameof(mutation.ItemId));
        if (mutation.Count is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mutation), "Item holding count cannot be negative.");
        }

        var previous = await ReadHoldingAsync(
            connection, transaction, mutation.Scope, itemId, mutation.FoundInRaid, cancellationToken)
            .ConfigureAwait(false);
        if (previous?.Count == mutation.Count || previous is null && mutation.Count is null)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (mutation.Count is null)
        {
            command.CommandText = """
                DELETE FROM quest_profile_item_holdings
                WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
                  AND item_id = $itemId AND found_in_raid = $foundInRaid;
                """;
        }
        else
        {
            command.CommandText = """
                INSERT INTO quest_profile_item_holdings(
                    profile_id, game_mode, generation, item_id, found_in_raid, item_count,
                    assertion_source, revision, modified_utc)
                VALUES (
                    $profileId, $gameMode, $generation, $itemId, $foundInRaid, $count,
                    $source, $revision, $modifiedUtc)
                ON CONFLICT(profile_id, game_mode, generation, item_id, found_in_raid) DO UPDATE SET
                    item_count = excluded.item_count,
                    assertion_source = excluded.assertion_source,
                    revision = excluded.revision,
                    modified_utc = excluded.modified_utc;
                """;
            command.Parameters.AddWithValue("$count", mutation.Count.Value);
            AddMutationValues(command, mutation, revision, recordedUtc);
        }

        AddScope(command, mutation.Scope);
        command.Parameters.AddWithValue("$itemId", itemId);
        command.Parameters.AddWithValue("$foundInRaid", mutation.FoundInRaid);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new(
            QuestProgressEntityKind.ItemHolding,
            itemId,
            mutation.FoundInRaid ? "foundInRaidCount" : "nonFoundInRaidCount",
            Serialize(previous is null ? null : new HoldingValue(previous.Count, previous.FoundInRaid)),
            Serialize(mutation.Count is null ? null : new HoldingValue(mutation.Count.Value, mutation.FoundInRaid)));
    }

    private static async Task<PendingChange?> ApplyPinAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SetQuestPinMutation mutation,
        long revision,
        DateTimeOffset recordedUtc,
        CancellationToken cancellationToken)
    {
        var targetId = RequiredId(mutation.TargetId, nameof(mutation.TargetId));
        var note = string.IsNullOrWhiteSpace(mutation.Note) ? null : mutation.Note.Trim();
        var previous = await ReadPinAsync(
            connection, transaction, mutation.Scope, mutation.TargetKind, targetId, cancellationToken)
            .ConfigureAwait(false);
        if ((!mutation.IsPinned && previous is null) ||
            (mutation.IsPinned && previous?.SortOrder == mutation.SortOrder && previous.Note == note))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (mutation.IsPinned)
        {
            command.CommandText = """
                INSERT INTO quest_profile_pins(
                    profile_id, game_mode, generation, target_kind, target_id, sort_order, note,
                    assertion_source, revision, modified_utc)
                VALUES (
                    $profileId, $gameMode, $generation, $targetKind, $targetId, $sortOrder, $note,
                    $source, $revision, $modifiedUtc)
                ON CONFLICT(profile_id, game_mode, generation, target_kind, target_id) DO UPDATE SET
                    sort_order = excluded.sort_order,
                    note = excluded.note,
                    assertion_source = excluded.assertion_source,
                    revision = excluded.revision,
                    modified_utc = excluded.modified_utc;
                """;
            command.Parameters.AddWithValue("$sortOrder", mutation.SortOrder);
            command.Parameters.AddWithValue("$note", note is null ? DBNull.Value : note);
            AddMutationValues(command, mutation, revision, recordedUtc);
        }
        else
        {
            command.CommandText = """
                DELETE FROM quest_profile_pins
                WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
                  AND target_kind = $targetKind AND target_id = $targetId;
                """;
        }

        AddScope(command, mutation.Scope);
        command.Parameters.AddWithValue("$targetKind", mutation.TargetKind.ToString());
        command.Parameters.AddWithValue("$targetId", targetId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new(
            QuestProgressEntityKind.Pin,
            targetId,
            mutation.TargetKind == QuestPinTargetKind.Task ? "taskPin" : "objectivePin",
            Serialize(previous is null ? null : new PinValue(previous.TargetKind, previous.SortOrder, previous.Note)),
            Serialize(mutation.IsPinned ? new PinValue(mutation.TargetKind, mutation.SortOrder, note) : null));
    }

    private static async Task UpdateProfileRevisionAsync(
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
            SET display_name = $displayName, revision = $revision, modified_utc = $modifiedUtc
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation;
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$displayName", profileName);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$modifiedUtc", FormatTimestamp(recordedUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The quest progress profile revision could not be advanced.");
        }
    }

    private static async Task AppendJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProgressMutation mutation,
        PendingChange change,
        long revision,
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
                $actor, $source, $revision, $recordedUtc);
            """;
        AddScope(command, mutation.Scope);
        command.Parameters.AddWithValue("$correlationId", mutation.CorrelationId.ToString("D"));
        command.Parameters.AddWithValue("$entityKind", change.EntityKind.ToString());
        command.Parameters.AddWithValue("$entityId", change.EntityId);
        command.Parameters.AddWithValue("$fieldName", change.FieldName);
        command.Parameters.AddWithValue("$previousJson", change.PreviousJson);
        command.Parameters.AddWithValue("$newJson", change.NewJson);
        command.Parameters.AddWithValue("$inverseJson", change.PreviousJson);
        command.Parameters.AddWithValue("$actor", mutation.Actor.ToString());
        command.Parameters.AddWithValue("$source", mutation.Source.Trim());
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$recordedUtc", FormatTimestamp(recordedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, RecordedTaskProgress>> LoadTasksAsync(
        SqliteConnection connection,
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT task_id, state, assertion_source, revision, modified_utc
            FROM quest_profile_task_states
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
            ORDER BY task_id COLLATE BINARY;
            """;
        AddScope(command, scope);
        var values = new Dictionary<string, RecordedTaskProgress>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var value = TaskProgress(reader);
            values.Add(value.TaskId, value);
        }

        return values;
    }

    private static async Task<Dictionary<string, RecordedObjectiveProgress>> LoadObjectivesAsync(
        SqliteConnection connection,
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT objective_id, state, progress_count, assertion_source, revision, modified_utc
            FROM quest_profile_objective_states
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
            ORDER BY objective_id COLLATE BINARY;
            """;
        AddScope(command, scope);
        var values = new Dictionary<string, RecordedObjectiveProgress>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var value = ObjectiveProgress(reader);
            values.Add(value.ObjectiveId, value);
        }

        return values;
    }

    private static async Task<IReadOnlyList<RecordedItemHolding>> LoadHoldingsAsync(
        SqliteConnection connection,
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id, found_in_raid, item_count, assertion_source, revision, modified_utc
            FROM quest_profile_item_holdings
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
            ORDER BY item_id COLLATE BINARY, found_in_raid;
            """;
        AddScope(command, scope);
        var values = new List<RecordedItemHolding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(Holding(reader));
        }

        return values;
    }

    private static async Task<IReadOnlyList<RecordedQuestPin>> LoadPinsAsync(
        SqliteConnection connection,
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT target_kind, target_id, sort_order, note, assertion_source, revision, modified_utc
            FROM quest_profile_pins
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
            ORDER BY sort_order, target_kind, target_id COLLATE BINARY;
            """;
        AddScope(command, scope);
        var values = new List<RecordedQuestPin>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(Pin(reader));
        }

        return values;
    }

    private static async Task<RecordedTaskProgress?> ReadTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        string taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT task_id, state, assertion_source, revision, modified_utc
            FROM quest_profile_task_states
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
              AND task_id = $taskId;
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$taskId", taskId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? TaskProgress(reader) : null;
    }

    private static async Task<RecordedObjectiveProgress?> ReadObjectiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        string objectiveId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT objective_id, state, progress_count, assertion_source, revision, modified_utc
            FROM quest_profile_objective_states
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
              AND objective_id = $objectiveId;
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$objectiveId", objectiveId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ObjectiveProgress(reader) : null;
    }

    private static async Task<RecordedItemHolding?> ReadHoldingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        string itemId,
        bool foundInRaid,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT item_id, found_in_raid, item_count, assertion_source, revision, modified_utc
            FROM quest_profile_item_holdings
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
              AND item_id = $itemId AND found_in_raid = $foundInRaid;
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$itemId", itemId);
        command.Parameters.AddWithValue("$foundInRaid", foundInRaid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Holding(reader) : null;
    }

    private static async Task<RecordedQuestPin?> ReadPinAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QuestProfileScope scope,
        QuestPinTargetKind targetKind,
        string targetId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT target_kind, target_id, sort_order, note, assertion_source, revision, modified_utc
            FROM quest_profile_pins
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
              AND target_kind = $targetKind AND target_id = $targetId;
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$targetKind", targetKind.ToString());
        command.Parameters.AddWithValue("$targetId", targetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Pin(reader) : null;
    }

    private static RecordedTaskProgress TaskProgress(SqliteDataReader reader) => new(
        reader.GetString(0),
        Enum.Parse<RecordedTaskState>(reader.GetString(1), ignoreCase: false),
        reader.GetString(2),
        reader.GetInt64(3),
        ParseTimestamp(reader.GetString(4)));

    private static RecordedObjectiveProgress ObjectiveProgress(SqliteDataReader reader) => new(
        reader.GetString(0),
        Enum.Parse<RecordedObjectiveState>(reader.GetString(1), ignoreCase: false),
        reader.IsDBNull(2) ? null : decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
        reader.GetString(3),
        reader.GetInt64(4),
        ParseTimestamp(reader.GetString(5)));

    private static RecordedItemHolding Holding(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetBoolean(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetInt64(4),
        ParseTimestamp(reader.GetString(5)));

    private static RecordedQuestPin Pin(SqliteDataReader reader) => new(
        Enum.Parse<QuestPinTargetKind>(reader.GetString(0), ignoreCase: false),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5),
        ParseTimestamp(reader.GetString(6)));

    private static void AddScope(SqliteCommand command, QuestProfileScope scope)
    {
        command.Parameters.AddWithValue("$profileId", scope.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$gameMode", scope.GameMode.ToString());
        command.Parameters.AddWithValue("$generation", scope.Generation.Trim());
    }

    private static void AddMutationValues(
        SqliteCommand command,
        QuestProgressMutation mutation,
        long revision,
        DateTimeOffset recordedUtc)
    {
        command.Parameters.AddWithValue("$source", mutation.Source.Trim());
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$modifiedUtc", FormatTimestamp(recordedUtc));
    }

    private static void ValidateMutation(QuestProgressMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ValidateScope(mutation.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutation.ProfileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutation.Source);
        if (mutation.ProfileName.Trim().Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(mutation), "Profile name cannot exceed 256 characters.");
        }

        if (mutation.CorrelationId == Guid.Empty)
        {
            throw new ArgumentException("A correlation id is required.", nameof(mutation));
        }

        if (mutation.Actor != QuestProgressActor.User ||
            !mutation.Source.Equals("Manual", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Stage 2 progress accepts explicit manual commands only; imports and observations cannot mutate it.");
        }
    }

    private static void ValidateScope(QuestProfileScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ProfileId == Guid.Empty)
        {
            throw new ArgumentException("A profile id is required.", nameof(scope));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(scope.Generation);
        if (scope.Generation.Trim().Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(scope), "Profile generation cannot exceed 128 characters.");
        }

        if (!Enum.IsDefined(scope.GameMode))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), "Game mode is not supported.");
        }
    }

    private static string RequiredId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > 256)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Identifiers cannot exceed 256 characters.");
        }

        return normalized;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SerializerOptions);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed record PendingChange(
        QuestProgressEntityKind EntityKind,
        string EntityId,
        string FieldName,
        string PreviousJson,
        string NewJson);

    private sealed record TaskValue(RecordedTaskState State);

    private sealed record ObjectiveValue(RecordedObjectiveState State, decimal? Count);

    private sealed record HoldingValue(int Count, bool FoundInRaid);

    private sealed record PinValue(QuestPinTargetKind TargetKind, int SortOrder, string? Note);
}
