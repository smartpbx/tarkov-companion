using System.Globalization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads back the imports already applied, and what each one could not do.
/// </summary>
/// <remarks>
/// The write half has existed since the exchange was built. Every import records exactly which
/// changes it refused and why, and neither table had a reader, so an import that half-worked
/// said "kept 3 local · 2 unresolved" once in a status line and could never say which.
///
/// Scoped to one profile and game mode, because an import belongs to the progress it changed
/// and showing somebody another profile's would be showing them somebody else's raid.
/// </remarks>
public sealed class SqliteQuestProgressImportHistory(SqliteConnectionFactory connectionFactory)
    : IQuestProgressImportHistory
{
    public async Task<IReadOnlyList<QuestImportRecord>> GetRecentAsync(
        QuestProfileScope scope,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var imports = await ReadImportsAsync(connection, scope, limit, cancellationToken).ConfigureAwait(false);
        if (imports.Count == 0)
        {
            return [];
        }

        var conflicts = await ReadConflictsAsync(connection, imports.Keys, cancellationToken).ConfigureAwait(false);
        var unresolved = await ReadUnresolvedAsync(connection, imports.Keys, cancellationToken).ConfigureAwait(false);
        return imports
            .Select(entry => entry.Value with
            {
                Conflicts = conflicts.GetValueOrDefault(entry.Key, []),
                Unresolved = unresolved.GetValueOrDefault(entry.Key, []),
            })
            .ToArray();
    }

    private static async Task<Dictionary<string, QuestImportRecord>> ReadImportsAsync(
        SqliteConnection connection,
        QuestProfileScope scope,
        int limit,
        CancellationToken cancellationToken)
    {
        var imports = new Dictionary<string, QuestImportRecord>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT import_id, imported_utc, profile_name, source_app_version,
                   applied_change_count, kept_local_count
            FROM quest_progress_imports
            WHERE profile_id = $profileId AND game_mode = $gameMode AND generation = $generation
            ORDER BY imported_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$profileId", scope.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$gameMode", scope.GameMode.ToString());
        command.Parameters.AddWithValue("$generation", scope.Generation);
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            imports[id] = new(
                Guid.TryParse(id, out var importId) ? importId : Guid.Empty,
                DateTimeOffset.TryParse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var importedUtc)
                    ? importedUtc
                    : DateTimeOffset.UnixEpoch,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                [],
                []);
        }

        return imports;
    }

    private static async Task<Dictionary<string, IReadOnlyList<QuestImportConflictRecord>>> ReadConflictsAsync(
        SqliteConnection connection,
        IEnumerable<string> importIds,
        CancellationToken cancellationToken)
    {
        var byImport = new Dictionary<string, IReadOnlyList<QuestImportConflictRecord>>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT import_id, entity_kind, entity_id, local_value_json, incoming_value_json, reason, resolution
            FROM quest_progress_import_conflicts
            WHERE import_id IN ({Parameters(command, importIds)})
            ORDER BY import_id, proposal_key;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var row = new QuestImportConflictRecord(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                Enum.TryParse<QuestImportResolution>(reader.GetString(6), out var resolution)
                    ? resolution
                    : QuestImportResolution.KeepLocal);
            byImport[id] = byImport.TryGetValue(id, out var existing) ? [.. existing, row] : [row];
        }

        return byImport;
    }

    private static async Task<Dictionary<string, IReadOnlyList<QuestImportUnresolvedRecord>>> ReadUnresolvedAsync(
        SqliteConnection connection,
        IEnumerable<string> importIds,
        CancellationToken cancellationToken)
    {
        var byImport = new Dictionary<string, IReadOnlyList<QuestImportUnresolvedRecord>>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT import_id, entity_kind, entity_id, incoming_value_json, reason
            FROM quest_progress_import_unresolved
            WHERE import_id IN ({Parameters(command, importIds)})
            ORDER BY import_id, proposal_key;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var row = new QuestImportUnresolvedRecord(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4));
            byImport[id] = byImport.TryGetValue(id, out var existing) ? [.. existing, row] : [row];
        }

        return byImport;
    }

    /// <summary>
    /// Binds each import id to its own parameter and returns the list for an IN clause.
    /// </summary>
    /// <remarks>
    /// Built rather than interpolated. The ids come from a database column and would almost
    /// certainly be safe interpolated, and "almost certainly safe" is how a query that reads a
    /// whole table by accident gets written.
    /// </remarks>
    private static string Parameters(SqliteCommand command, IEnumerable<string> importIds)
    {
        var names = new List<string>();
        foreach (var importId in importIds)
        {
            var name = "$id" + names.Count.ToString(CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(name, importId);
            names.Add(name);
        }

        return string.Join(", ", names);
    }
}
