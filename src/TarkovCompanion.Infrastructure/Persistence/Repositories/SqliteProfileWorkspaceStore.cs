using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>SQLite compare-and-swap persistence for the frozen V2 profile workspace contract.</summary>
public sealed class SqliteProfileWorkspaceStore(
    SqliteConnectionFactory connectionFactory,
    TimeProvider? timeProvider = null) : IProfileWorkspaceStore
{
    public const int MaximumWorkspaceUtf8Bytes = 2 * 1024 * 1024;
    public const int MaximumWorkspaceChildRows = 65_536;

    internal const string ProfileWorkspaceSql = """
        SELECT revision, active_profile_id, updated_utc,
               CASE WHEN active_profile_id IS NULL THEN NULL
                    WHEN typeof(active_profile_id) = 'text' THEN length(CAST(active_profile_id AS BLOB))
                    ELSE -1 END,
               CASE WHEN typeof(updated_utc) = 'text' THEN length(CAST(updated_utc AS BLOB)) ELSE -1 END
        FROM profile_workspaces WHERE workspace_key = 1;
        """;
    internal const string ProfileContextsSql = """
        SELECT profile_id, generation, name, game_mode, wipe_season, language, region,
               time_zone, data_snapshot_id, data_snapshot_published_utc, level, lifecycle,
               updated_utc, extension_json,
               CASE WHEN typeof(profile_id) = 'text' THEN length(CAST(profile_id AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(generation) = 'text' THEN length(CAST(generation AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(name) = 'text' THEN length(CAST(name AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(game_mode) = 'text' THEN length(CAST(game_mode AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(wipe_season) = 'text' THEN length(CAST(wipe_season AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(language) = 'text' THEN length(CAST(language AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(region) = 'text' THEN length(CAST(region AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(time_zone) = 'text' THEN length(CAST(time_zone AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(data_snapshot_id) = 'text' THEN length(CAST(data_snapshot_id AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(data_snapshot_published_utc) = 'text' THEN length(CAST(data_snapshot_published_utc AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(lifecycle) = 'text' THEN length(CAST(lifecycle AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(updated_utc) = 'text' THEN length(CAST(updated_utc AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(extension_json) = 'text' THEN length(CAST(extension_json AS BLOB)) ELSE -1 END
        FROM profile_contexts
        ORDER BY profile_id
        LIMIT 65;
        """;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ProfileWorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPersistedValueFailure(exception))
        {
            throw new InvalidDataException("Persisted profile workspace is invalid.", exception);
        }
    }

    private async Task<ProfileWorkspaceSnapshot> ReadCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // A workspace is one aggregate spread across the root, profile, progress, and pin tables.
        // Keep one SQLite snapshot for every read so a concurrent replacement cannot pair the old
        // root revision with the new child rows (or vice versa).
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var budget = new WorkspaceBudget();
        var (workspaceExists, revision, activeProfileId) = await ReadWorkspaceAsync(
            connection, transaction, budget, cancellationToken).ConfigureAwait(false);
        var rows = new List<RawProfile>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = ProfileContextsSql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count == ProfileWorkspaceSnapshot.MaximumProfiles)
                {
                    throw new InvalidDataException("Persisted profile workspace exceeds the 64-profile bound.");
                }

                var profileId = ReadRequiredGuid(reader, 0, 14, budget, "profile id");
                var generation = ReadRequiredText(reader, 1, 15, budget, "profile generation");
                var name = ReadRequiredText(reader, 2, 16, budget, "profile name");
                var gameMode = ReadRequiredText(reader, 3, 17, budget, "profile game mode");
                var wipeSeason = ReadRequiredText(reader, 4, 18, budget, "profile wipe season");
                var language = ReadRequiredText(reader, 5, 19, budget, "profile language");
                var region = ReadRequiredText(reader, 6, 20, budget, "profile region");
                var timeZone = ReadRequiredText(reader, 7, 21, budget, "profile time zone");
                var snapshotId = ReadRequiredText(reader, 8, 22, budget, "profile data snapshot id");
                var snapshotPublishedUtc = ReadRequiredTimestamp(
                    reader, 9, 23, budget, "profile data publication time");
                var level = ReadRequiredInt32(reader, 10, "profile level", 1, 100);
                var lifecycle = ReadRequiredText(reader, 11, 24, budget, "profile lifecycle");
                var updatedUtc = ReadRequiredTimestamp(reader, 12, 25, budget, "profile update time");
                var extensionJson = ReadJsonObject(reader, 13, 26, budget, "profile extension JSON");
                if (!TryParseCanonicalEnum<ProfileGameMode>(gameMode, out _) ||
                    !TryParseCanonicalEnum<ProfileLifecycle>(lifecycle, out _))
                {
                    throw new InvalidDataException("Persisted profile context contains an unknown enum state.");
                }

                rows.Add(new(
                    profileId, generation, name, gameMode, wipeSeason, language, region,
                    timeZone, snapshotId, snapshotPublishedUtc, level, lifecycle, updatedUtc,
                    extensionJson));
            }
        }

        if (!workspaceExists && rows.Count != 0)
        {
            throw new InvalidDataException("Persisted profile contexts are orphaned from the workspace root.");
        }

        var profiles = new List<ProfileRecord>(rows.Count);
        foreach (var row in rows)
        {
            profiles.Add(new(
                new(
                    new(row.ProfileId, row.Generation),
                    Enum.Parse<ProfileGameMode>(row.GameMode, false),
                    new(row.WipeSeason),
                    new(row.Language, row.Region, row.TimeZone),
                    new(row.SnapshotId, row.SnapshotPublishedUtc)),
                row.Name,
                await ReadProgressAsync(
                    connection, transaction, row.ProfileId, row.Level, budget, cancellationToken).ConfigureAwait(false),
                Enum.Parse<ProfileLifecycle>(row.Lifecycle, false),
                row.UpdatedUtc,
                row.ExtensionJson));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(revision, activeProfileId, profiles);
    }

    public async Task<bool> TryReplaceAsync(
        long expectedRevision,
        ProfileWorkspaceSnapshot replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (expectedRevision < 0 || expectedRevision == long.MaxValue ||
            replacement.Revision != expectedRevision + 1)
        {
            throw new ArgumentException(
                "A replacement must publish exactly the revision after the expected revision.",
                nameof(replacement));
        }

        try
        {
            ValidateReplacement(replacement);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException("The replacement exceeds the durable workspace bounds.", nameof(replacement), exception);
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO profile_workspaces(workspace_key, revision, active_profile_id, updated_utc)
            VALUES (1, 0, NULL, $updatedUtc);
            """,
            cancellationToken,
            ("$updatedUtc", FormatUtc(_timeProvider.GetUtcNow()))).ConfigureAwait(false);

        // Fence the aggregate head before replacing any child rows. The conditional write is the
        // first contested mutation in the transaction, so concurrent writers serialize here and
        // only the one that owns expectedRevision can reach the destructive replacement below.
        var headChanged = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE profile_workspaces
            SET revision = $revision, active_profile_id = $activeProfileId, updated_utc = $updatedUtc
            WHERE workspace_key = 1 AND revision = $expectedRevision;
            """,
            cancellationToken,
            ("$revision", replacement.Revision),
            ("$activeProfileId", replacement.ActiveProfileId?.ToString("D")),
            ("$updatedUtc", FormatUtc(_timeProvider.GetUtcNow())),
            ("$expectedRevision", expectedRevision)).ConfigureAwait(false);
        if (headChanged != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await ExecuteAsync(connection, transaction, "DELETE FROM profile_contexts;", cancellationToken).ConfigureAwait(false);
        foreach (var profile in replacement.Profiles)
        {
            await InsertProfileAsync(connection, transaction, profile, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<(bool Exists, long Revision, Guid? ActiveProfileId)> ReadWorkspaceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkspaceBudget budget,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ProfileWorkspaceSql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (false, 0, null);
        }

        var revision = ReadRequiredInt64(reader, 0, "workspace revision", 0, long.MaxValue);
        Guid? activeProfileId = reader.IsDBNull(1)
            ? null
            : ReadRequiredGuid(reader, 1, 3, budget, "active profile id");
        _ = ReadRequiredTimestamp(reader, 2, 4, budget, "workspace update time");
        return (true, revision, activeProfileId);
    }

    private static async Task<ProfileProgress> ReadProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        int level,
        WorkspaceBudget budget,
        CancellationToken cancellationToken)
    {
        var id = profileId.ToString("D");
        var traderLevels = await ReadDictionaryAsync(connection, transaction, "profile_trader_progress_v2", "trader_id", "level", id, 4, budget, cancellationToken).ConfigureAwait(false);
        var completedTasks = await ReadSetAsync(connection, transaction, "profile_completed_tasks_v2", "task_id", id, budget, cancellationToken).ConfigureAwait(false);
        var objectives = await ReadDictionaryAsync(connection, transaction, "profile_objective_progress_v2", "objective_id", "progress_count", id, int.MaxValue, budget, cancellationToken).ConfigureAwait(false);
        var hideout = await ReadDictionaryAsync(connection, transaction, "profile_hideout_progress_v2", "station_id", "level", id, int.MaxValue, budget, cancellationToken).ConfigureAwait(false);
        var wishlist = await ReadSetAsync(connection, transaction, "profile_wishlist_v2", "item_id", id, budget, cancellationToken).ConfigureAwait(false);
        var owned = await ReadDictionaryAsync(connection, transaction, "profile_owned_counts_v2", "item_id", "item_count", id, int.MaxValue, budget, cancellationToken).ConfigureAwait(false);
        var events = await ReadTextDictionaryAsync(connection, transaction, "profile_event_states_v2", "item_id", "state", id, budget, cancellationToken).ConfigureAwait(false);
        var overrides = await ReadTextDictionaryAsync(connection, transaction, "profile_item_overrides_v2", "item_id", "value", id, budget, cancellationToken).ConfigureAwait(false);
        var pins = await ReadPinsAsync(connection, transaction, id, budget, cancellationToken).ConfigureAwait(false);
        return new(level, traderLevels, completedTasks, objectives, hideout, wishlist, owned, events, overrides, pins);
    }

    private static async Task InsertProfileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProfileRecord profile,
        CancellationToken cancellationToken)
    {
        var context = profile.Context;
        var id = context.Identity.ProfileId.ToString("D");
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO profile_contexts(
                profile_id, generation, name, game_mode, wipe_season, language, region, time_zone,
                data_snapshot_id, data_snapshot_published_utc, level, lifecycle, updated_utc, extension_json)
            VALUES ($id, $generation, $name, $mode, $wipe, $language, $region, $timeZone,
                    $snapshot, $published, $level, $lifecycle, $updated, $extension);
            """,
            cancellationToken,
            ("$id", id), ("$generation", context.Identity.Generation), ("$name", profile.Name),
            ("$mode", context.Mode.ToString()), ("$wipe", context.WipeSeason.Value),
            ("$language", context.Locale.Language), ("$region", context.Locale.Region),
            ("$timeZone", context.Locale.TimeZone), ("$snapshot", context.DataSnapshot.SnapshotId),
            ("$published", FormatUtc(context.DataSnapshot.PublishedUtc)), ("$level", profile.Progress.Level),
            ("$lifecycle", profile.Lifecycle.ToString()), ("$updated", FormatUtc(profile.UpdatedUtc)),
            ("$extension", profile.ExtensionJson)).ConfigureAwait(false);

        await InsertDictionaryAsync(connection, transaction, "profile_trader_progress_v2", "trader_id", "level", id, profile.Progress.TraderLevels, cancellationToken).ConfigureAwait(false);
        await InsertSetAsync(connection, transaction, "profile_completed_tasks_v2", "task_id", id, profile.Progress.CompletedTaskIds, cancellationToken).ConfigureAwait(false);
        await InsertDictionaryAsync(connection, transaction, "profile_objective_progress_v2", "objective_id", "progress_count", id, profile.Progress.ObjectiveProgress, cancellationToken).ConfigureAwait(false);
        await InsertDictionaryAsync(connection, transaction, "profile_hideout_progress_v2", "station_id", "level", id, profile.Progress.HideoutStationLevels, cancellationToken).ConfigureAwait(false);
        await InsertSetAsync(connection, transaction, "profile_wishlist_v2", "item_id", id, profile.Progress.WishlistItemIds, cancellationToken).ConfigureAwait(false);
        await InsertDictionaryAsync(connection, transaction, "profile_owned_counts_v2", "item_id", "item_count", id, profile.Progress.OwnedItemCounts, cancellationToken).ConfigureAwait(false);
        await InsertTextDictionaryAsync(connection, transaction, "profile_event_states_v2", "item_id", "state", id, profile.Progress.EventItemStates, cancellationToken).ConfigureAwait(false);
        await InsertTextDictionaryAsync(connection, transaction, "profile_item_overrides_v2", "item_id", "value", id, profile.Progress.ItemOverrides, cancellationToken).ConfigureAwait(false);
        foreach (var pin in profile.Progress.Pins)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO profile_pins_v2(profile_id, target_kind, target_id, sort_order, note) VALUES ($profileId, $kind, $id, $sort, $note);",
                cancellationToken, ("$profileId", id), ("$kind", pin.TargetKind), ("$id", pin.TargetId), ("$sort", pin.SortOrder), ("$note", pin.Note)).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, int>> ReadDictionaryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string key,
        string value,
        string profileId,
        int maximumValue,
        WorkspaceBudget budget,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {key}, {value},
                   CASE WHEN typeof({key}) = 'text' THEN length(CAST({key} AS BLOB)) ELSE -1 END
            FROM {table}
            WHERE profile_id = $profileId
            ORDER BY {key}
            LIMIT {MaximumWorkspaceChildRows + 1};
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            budget.AddChildRow();
            result.Add(
                ReadRequiredText(reader, 0, 2, budget, $"{table} key"),
                ReadRequiredInt32(reader, 1, $"{table} value", 0, maximumValue));
        }

        return result;
    }

    private static async Task<Dictionary<string, string>> ReadTextDictionaryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string key,
        string value,
        string profileId,
        WorkspaceBudget budget,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {key}, {value},
                   CASE WHEN typeof({key}) = 'text' THEN length(CAST({key} AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof({value}) = 'text' THEN length(CAST({value} AS BLOB)) ELSE -1 END
            FROM {table}
            WHERE profile_id = $profileId
            ORDER BY {key}
            LIMIT {MaximumWorkspaceChildRows + 1};
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            budget.AddChildRow();
            result.Add(
                ReadRequiredText(reader, 0, 2, budget, $"{table} key"),
                ReadRequiredText(reader, 1, 3, budget, $"{table} value"));
        }

        return result;
    }

    private static async Task<List<string>> ReadSetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string key,
        string profileId,
        WorkspaceBudget budget,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {key},
                   CASE WHEN typeof({key}) = 'text' THEN length(CAST({key} AS BLOB)) ELSE -1 END
            FROM {table}
            WHERE profile_id = $profileId
            ORDER BY {key}
            LIMIT {MaximumWorkspaceChildRows + 1};
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            budget.AddChildRow();
            result.Add(ReadRequiredText(reader, 0, 1, budget, $"{table} key"));
        }

        return result;
    }

    private static async Task<List<ProfilePin>> ReadPinsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        WorkspaceBudget budget,
        CancellationToken cancellationToken)
    {
        var result = new List<ProfilePin>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT target_kind, target_id, sort_order, note,
                   CASE WHEN typeof(target_kind) = 'text' THEN length(CAST(target_kind AS BLOB)) ELSE -1 END,
                   CASE WHEN typeof(target_id) = 'text' THEN length(CAST(target_id AS BLOB)) ELSE -1 END,
                   CASE WHEN note IS NULL THEN NULL
                        WHEN typeof(note) = 'text' THEN length(CAST(note AS BLOB)) ELSE -1 END
            FROM profile_pins_v2
            WHERE profile_id = $profileId
            ORDER BY target_kind, target_id
            LIMIT {MaximumWorkspaceChildRows + 1};
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            budget.AddChildRow();
            result.Add(new(
                ReadRequiredText(reader, 0, 4, budget, "profile pin target kind"),
                ReadRequiredText(reader, 1, 5, budget, "profile pin target id"),
                ReadRequiredInt32(reader, 2, "profile pin sort order", 0, int.MaxValue),
                ReadOptionalText(reader, 3, 6, budget, "profile pin note")));
        }

        return result;
    }

    private static Task InsertDictionaryAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string key, string value, string profileId, IReadOnlyDictionary<string, int> values, CancellationToken cancellationToken) =>
        InsertRowsAsync(connection, transaction, table, key, value, profileId, values.Select(pair => (pair.Key, (object)pair.Value)), cancellationToken);

    private static Task InsertTextDictionaryAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string key, string value, string profileId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) =>
        InsertRowsAsync(connection, transaction, table, key, value, profileId, values.Select(pair => (pair.Key, (object)pair.Value)), cancellationToken);

    private static async Task InsertRowsAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string key, string value, string profileId, IEnumerable<(string Key, object Value)> values, CancellationToken cancellationToken)
    {
        foreach (var pair in values)
        {
            await ExecuteAsync(connection, transaction, $"INSERT INTO {table}(profile_id, {key}, {value}) VALUES ($profileId, $key, $value);", cancellationToken,
                ("$profileId", profileId), ("$key", pair.Key), ("$value", pair.Value)).ConfigureAwait(false);
        }
    }

    private static async Task InsertSetAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string key, string profileId, IEnumerable<string> values, CancellationToken cancellationToken)
    {
        foreach (var value in values)
        {
            await ExecuteAsync(connection, transaction, $"INSERT INTO {table}(profile_id, {key}) VALUES ($profileId, $value);", cancellationToken,
                ("$profileId", profileId), ("$value", value)).ConfigureAwait(false);
        }
    }

    private void ValidateReplacement(ProfileWorkspaceSnapshot replacement)
    {
        var budget = new WorkspaceBudget();
        AddWriteText(budget, FormatUtc(_timeProvider.GetUtcNow()), "workspace update time");
        if (replacement.ActiveProfileId is { } activeProfileId)
        {
            budget.AddBytes(Encoding.UTF8.GetByteCount(activeProfileId.ToString("D")), "active profile id");
        }

        foreach (var profile in replacement.Profiles)
        {
            if (profile.UpdatedUtc == default || profile.Context.DataSnapshot.PublishedUtc == default)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(replacement),
                    "Profile publication and update timestamps cannot be the default value.");
            }

            AddWriteText(budget, profile.Context.Identity.ProfileId.ToString("D"), "profile id");
            AddWriteText(budget, profile.Context.Identity.Generation, "profile generation");
            AddWriteText(budget, profile.Name, "profile name");
            AddWriteText(budget, profile.Context.Mode.ToString(), "profile game mode");
            AddWriteText(budget, profile.Context.WipeSeason.Value, "profile wipe season");
            AddWriteText(budget, profile.Context.Locale.Language, "profile language");
            AddWriteText(budget, profile.Context.Locale.Region, "profile region");
            AddWriteText(budget, profile.Context.Locale.TimeZone, "profile time zone");
            AddWriteText(budget, profile.Context.DataSnapshot.SnapshotId, "profile data snapshot id");
            AddWriteText(budget, FormatUtc(profile.Context.DataSnapshot.PublishedUtc), "profile data publication time");
            AddWriteText(budget, profile.Lifecycle.ToString(), "profile lifecycle");
            AddWriteText(budget, FormatUtc(profile.UpdatedUtc), "profile update time");
            ValidateExtensionJson(profile.ExtensionJson, budget, nameof(replacement));

            AddWriteDictionary(budget, profile.Progress.TraderLevels);
            AddWriteSet(budget, profile.Progress.CompletedTaskIds);
            AddWriteDictionary(budget, profile.Progress.ObjectiveProgress);
            AddWriteDictionary(budget, profile.Progress.HideoutStationLevels);
            AddWriteSet(budget, profile.Progress.WishlistItemIds);
            AddWriteDictionary(budget, profile.Progress.OwnedItemCounts);
            AddWriteTextDictionary(budget, profile.Progress.EventItemStates);
            AddWriteTextDictionary(budget, profile.Progress.ItemOverrides);
            foreach (var pin in profile.Progress.Pins)
            {
                budget.AddChildRow();
                AddWriteText(budget, pin.TargetKind, "profile pin target kind");
                AddWriteText(budget, pin.TargetId, "profile pin target id");
                if (pin.Note is { } note)
                {
                    AddWriteText(budget, note, "profile pin note");
                }
            }
        }
    }

    private static void AddWriteDictionary(WorkspaceBudget budget, IReadOnlyDictionary<string, int> values)
    {
        foreach (var key in values.Keys)
        {
            budget.AddChildRow();
            AddWriteText(budget, key, "profile progress key");
        }
    }

    private static void AddWriteTextDictionary(WorkspaceBudget budget, IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values)
        {
            budget.AddChildRow();
            AddWriteText(budget, pair.Key, "profile progress key");
            AddWriteText(budget, pair.Value, "profile progress value");
        }
    }

    private static void AddWriteSet(WorkspaceBudget budget, IReadOnlyCollection<string> values)
    {
        foreach (var value in values)
        {
            budget.AddChildRow();
            AddWriteText(budget, value, "profile progress id");
        }
    }

    private static void AddWriteText(WorkspaceBudget budget, string value, string description)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException($"{description} cannot be empty.", description);
        }

        budget.AddBytes(Encoding.UTF8.GetByteCount(value), description);
    }

    private static void ValidateExtensionJson(
        string value,
        WorkspaceBudget budget,
        string parameterName)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        budget.AddBytes(utf8.Length, "profile extension JSON");
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new ArgumentException("Profile extension JSON must be an object.", parameterName);
        }

        while (reader.Read())
        {
        }
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string ReadRequiredText(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        WorkspaceBudget budget,
        string description)
    {
        var value = ReadText(reader, valueOrdinal, lengthOrdinal, budget, description, optional: false)!;
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
        WorkspaceBudget budget,
        string description) =>
        ReadText(reader, valueOrdinal, lengthOrdinal, budget, description, optional: true);

    private static string? ReadText(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        WorkspaceBudget budget,
        string description,
        bool optional)
    {
        if (reader.IsDBNull(valueOrdinal))
        {
            if (!optional || !reader.IsDBNull(lengthOrdinal))
            {
                throw new InvalidDataException($"The persisted {description} is missing or inconsistent.");
            }

            return null;
        }

        if (reader.IsDBNull(lengthOrdinal))
        {
            throw new InvalidDataException($"The persisted {description} is not text.");
        }

        var byteLength = reader.GetInt64(lengthOrdinal);
        budget.AddBytes(byteLength, description);
        var value = reader.GetString(valueOrdinal);
        if (Encoding.UTF8.GetByteCount(value) != byteLength)
        {
            throw new InvalidDataException($"The persisted {description} has inconsistent UTF-8 length.");
        }

        return value;
    }

    private static Guid ReadRequiredGuid(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        WorkspaceBudget budget,
        string description)
    {
        var value = ReadRequiredText(reader, valueOrdinal, lengthOrdinal, budget, description);
        if (Encoding.UTF8.GetByteCount(value) != 36 ||
            !Guid.TryParseExact(value, "D", out var result) || result == Guid.Empty)
        {
            throw new InvalidDataException($"The persisted {description} is not a non-empty canonical UUID.");
        }

        return result;
    }

    private static DateTimeOffset ReadRequiredTimestamp(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        WorkspaceBudget budget,
        string description)
    {
        var value = ReadRequiredText(reader, valueOrdinal, lengthOrdinal, budget, description);
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

    private static string ReadJsonObject(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        WorkspaceBudget budget,
        string description)
    {
        var value = ReadRequiredText(reader, valueOrdinal, lengthOrdinal, budget, description);
        var utf8 = Encoding.UTF8.GetBytes(value);
        var jsonReader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        if (!jsonReader.Read() || jsonReader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException($"The persisted {description} is not a JSON object.");
        }

        while (jsonReader.Read())
        {
        }

        return value;
    }

    private static int ReadRequiredInt32(
        SqliteDataReader reader,
        int ordinal,
        string description,
        int minimum,
        int maximum)
    {
        var value = ReadRequiredInt64(reader, ordinal, description, minimum, maximum);
        return checked((int)value);
    }

    private static long ReadRequiredInt64(
        SqliteDataReader reader,
        int ordinal,
        string description,
        long minimum,
        long maximum)
    {
        if (reader.IsDBNull(ordinal) || reader.GetValue(ordinal) is not long value)
        {
            throw new InvalidDataException($"The persisted {description} is not an integer.");
        }
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException($"The persisted {description} is outside its valid range.");
        }

        return value;
    }

    private static bool IsPersistedValueFailure(Exception exception) =>
        exception is not InvalidDataException and
        (ArgumentException or FormatException or OverflowException or InvalidCastException or JsonException);

    private static bool TryParseCanonicalEnum<T>(string value, out T result)
        where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out result) &&
        Enum.IsDefined(result) &&
        string.Equals(result.ToString(), value, StringComparison.Ordinal);

    private sealed class WorkspaceBudget
    {
        private long _utf8Bytes;
        private int _childRows;

        public void AddBytes(long byteLength, string description)
        {
            if (byteLength is < 1 or > MaximumWorkspaceUtf8Bytes ||
                byteLength > MaximumWorkspaceUtf8Bytes - _utf8Bytes)
            {
                throw new InvalidDataException(
                    $"Persisted {description} exceeds the profile workspace UTF-8 budget.");
            }

            _utf8Bytes += byteLength;
        }

        public void AddChildRow()
        {
            if (++_childRows > MaximumWorkspaceChildRows)
            {
                throw new InvalidDataException("Persisted profile progress exceeds the child-row bound.");
            }
        }
    }

    private sealed record RawProfile(
        Guid ProfileId,
        string Generation,
        string Name,
        string GameMode,
        string WipeSeason,
        string Language,
        string Region,
        string TimeZone,
        string SnapshotId,
        DateTimeOffset SnapshotPublishedUtc,
        int Level,
        string Lifecycle,
        DateTimeOffset UpdatedUtc,
        string ExtensionJson);
}
