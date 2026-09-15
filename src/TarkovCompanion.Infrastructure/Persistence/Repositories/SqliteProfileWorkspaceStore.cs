using System.Globalization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>SQLite compare-and-swap persistence for the frozen V2 profile workspace contract.</summary>
public sealed class SqliteProfileWorkspaceStore(
    SqliteConnectionFactory connectionFactory,
    TimeProvider? timeProvider = null) : IProfileWorkspaceStore
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ProfileWorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var (revision, activeProfileId) = await ReadWorkspaceAsync(connection, cancellationToken).ConfigureAwait(false);
        var rows = new List<RawProfile>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT profile_id, generation, name, game_mode, wipe_season, language, region,
                       time_zone, data_snapshot_id, data_snapshot_published_utc, level, lifecycle, updated_utc
                FROM profile_contexts
                ORDER BY profile_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new(
                    Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetInt32(10),
                    reader.GetString(11), reader.GetString(12)));
            }
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
                    new(row.SnapshotId, ParseUtc(row.SnapshotPublishedUtc))),
                row.Name,
                await ReadProgressAsync(connection, row.ProfileId, row.Level, cancellationToken).ConfigureAwait(false),
                Enum.Parse<ProfileLifecycle>(row.Lifecycle, false),
                ParseUtc(row.UpdatedUtc)));
        }

        return new(revision, activeProfileId, profiles);
    }

    public async Task<bool> TryReplaceAsync(
        long expectedRevision,
        ProfileWorkspaceSnapshot replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Revision < expectedRevision)
        {
            throw new ArgumentException("A replacement revision cannot move backwards.", nameof(replacement));
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

        var currentRevision = Convert.ToInt64(await ScalarAsync(
            connection, transaction, "SELECT revision FROM profile_workspaces WHERE workspace_key = 1;", cancellationToken)
            .ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (currentRevision != expectedRevision)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await ExecuteAsync(connection, transaction, "DELETE FROM profile_contexts;", cancellationToken).ConfigureAwait(false);
        foreach (var profile in replacement.Profiles)
        {
            await InsertProfileAsync(connection, transaction, profile, cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(
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
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<(long Revision, Guid? ActiveProfileId)> ReadWorkspaceAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision, active_profile_id FROM profile_workspaces WHERE workspace_key = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (0, null);
        }

        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)));
    }

    private static async Task<ProfileProgress> ReadProgressAsync(
        SqliteConnection connection,
        Guid profileId,
        int level,
        CancellationToken cancellationToken)
    {
        var id = profileId.ToString("D");
        var traderLevels = await ReadDictionaryAsync(connection, "profile_trader_progress_v2", "trader_id", "level", id, cancellationToken).ConfigureAwait(false);
        var completedTasks = await ReadSetAsync(connection, "profile_completed_tasks_v2", "task_id", id, cancellationToken).ConfigureAwait(false);
        var objectives = await ReadDictionaryAsync(connection, "profile_objective_progress_v2", "objective_id", "progress_count", id, cancellationToken).ConfigureAwait(false);
        var hideout = await ReadDictionaryAsync(connection, "profile_hideout_progress_v2", "station_id", "level", id, cancellationToken).ConfigureAwait(false);
        var wishlist = await ReadSetAsync(connection, "profile_wishlist_v2", "item_id", id, cancellationToken).ConfigureAwait(false);
        var owned = await ReadDictionaryAsync(connection, "profile_owned_counts_v2", "item_id", "item_count", id, cancellationToken).ConfigureAwait(false);
        var events = await ReadTextDictionaryAsync(connection, "profile_event_states_v2", "item_id", "state", id, cancellationToken).ConfigureAwait(false);
        var overrides = await ReadTextDictionaryAsync(connection, "profile_item_overrides_v2", "item_id", "value", id, cancellationToken).ConfigureAwait(false);
        var pins = await ReadPinsAsync(connection, id, cancellationToken).ConfigureAwait(false);
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
                    $snapshot, $published, $level, $lifecycle, $updated, '{}');
            """,
            cancellationToken,
            ("$id", id), ("$generation", context.Identity.Generation), ("$name", profile.Name),
            ("$mode", context.Mode.ToString()), ("$wipe", context.WipeSeason.Value),
            ("$language", context.Locale.Language), ("$region", context.Locale.Region),
            ("$timeZone", context.Locale.TimeZone), ("$snapshot", context.DataSnapshot.SnapshotId),
            ("$published", FormatUtc(context.DataSnapshot.PublishedUtc)), ("$level", profile.Progress.Level),
            ("$lifecycle", profile.Lifecycle.ToString()), ("$updated", FormatUtc(profile.UpdatedUtc))).ConfigureAwait(false);

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

    private static async Task<Dictionary<string, int>> ReadDictionaryAsync(SqliteConnection connection, string table, string key, string value, string profileId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {key}, {value} FROM {table} WHERE profile_id = $profileId ORDER BY {key};";
        command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0), reader.GetInt32(1));
        return result;
    }

    private static async Task<Dictionary<string, string>> ReadTextDictionaryAsync(SqliteConnection connection, string table, string key, string value, string profileId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {key}, {value} FROM {table} WHERE profile_id = $profileId ORDER BY {key};";
        command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }

    private static async Task<List<string>> ReadSetAsync(SqliteConnection connection, string table, string key, string profileId, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {key} FROM {table} WHERE profile_id = $profileId ORDER BY {key};";
        command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<List<ProfilePin>> ReadPinsAsync(SqliteConnection connection, string profileId, CancellationToken cancellationToken)
    {
        var result = new List<ProfilePin>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT target_kind, target_id, sort_order, note FROM profile_pins_v2 WHERE profile_id = $profileId ORDER BY sort_order, target_kind, target_id;";
        command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
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

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

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
        string SnapshotPublishedUtc,
        int Level,
        string Lifecycle,
        string UpdatedUtc);
}
