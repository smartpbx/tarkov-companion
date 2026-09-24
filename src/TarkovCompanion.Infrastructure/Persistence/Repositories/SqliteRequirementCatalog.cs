using System.Globalization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads the quest and hideout item requirements every sync writes but nothing ever read back.
/// </summary>
/// <remarks>
/// <para>
/// The projections feed <see cref="ProfileNeedAggregationService"/>, which <em>sums</em> the
/// requirements it is handed per item. That makes this class arithmetic, not presentation: one
/// spurious row becomes a number the player is told to go and farm. Every read here therefore
/// prefers dropping a row it cannot trust over guessing a value for it.
/// </para>
/// <para>
/// That service also rejects negative quantities and non-positive hideout target levels by
/// throwing from its constructor, so a single malformed row would take down the Hideout page and
/// three of the scanner's five ranking reasons rather than just itself. Those rows are filtered
/// out here instead.
/// </para>
/// <para>
/// Each projection is loaded on first use rather than at construction because on a clean install
/// the database is still empty when the container is built and the first sync lands seconds
/// later; <see cref="Invalidate"/> is what makes that sync visible.
/// </para>
/// </remarks>
public sealed class SqliteRequirementCatalog(SqliteConnectionFactory connectionFactory) : IRequirementCatalog
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<HideoutItemRequirement>? _hideoutRequirements;
    private IReadOnlyList<QuestItemRequirement>? _questRequirements;
    private IReadOnlyList<HideoutStationSummary>? _stations;
    private int _generation;

    internal const string HideoutItemRequirementsSql = """
        SELECT station_id, level, item_id, count
        FROM hideout_requirements
        WHERE requirement_type = 'item' AND item_id IS NOT NULL;
        """;

    internal const string QuestItemRequirementsSql = """
        SELECT item.task_id, item.objective_id, item.item_id,
               item.count, item.found_in_raid_required
        FROM task_objective_items AS item
        JOIN task_objectives AS objective
            ON objective.task_id = item.task_id AND objective.id = item.objective_id
        ORDER BY item.task_id COLLATE BINARY, item.objective_id COLLATE BINARY,
                 item.item_id COLLATE BINARY;
        """;

    /// <summary>Every outstanding hideout item requirement, one per station level and item.</summary>
    /// <remarks>
    /// Only the <c>item</c> requirement kind is returned; the <c>station</c>, <c>trader</c> and
    /// <c>skill</c> rows share the table but put a level or loyalty rank in the same count column
    /// and carry no item id at all, so reading them would invent item needs out of trader ranks.
    /// </remarks>
    public Task<IReadOnlyList<HideoutItemRequirement>> GetHideoutRequirementsAsync(CancellationToken cancellationToken) =>
        GetOrLoadAsync<HideoutItemRequirement>(
            () => _hideoutRequirements,
            value => _hideoutRequirements = value,
            (connection, token) => LoadHideoutRequirementsAsync(connection, token),
            cancellationToken);

    /// <summary>Every quest item requirement, one per objective and item.</summary>
    /// <remarks>
    /// <para>
    /// Read from <c>task_objective_items</c> joined to <c>task_objectives</c> for the owning task
    /// id, in preference to <c>quest_objective_item_targets</c>. Both are written by the same
    /// sync, but the <c>quest_catalog_*</c> tables are keyed by
    /// <c>(source_key, source_mode, language)</c> and legitimately hold one row per scope: a
    /// database that has synced both regular and PvE, or two languages, holds the same logical
    /// requirement two or more times. This interface takes neither a game mode nor a language, so
    /// there is no honest way to pick a scope here, and reading them all would multiply every
    /// quest need by the number of scopes present. <c>task_objective_items</c> is the single
    /// mode-agnostic projection and is keyed <c>(task_id, objective_id, item_id)</c> — upstream
    /// reuses one objective id across several tasks for the same shared objective, so the task id
    /// is part of the key too — and it cannot hold the same requirement twice.
    /// </para>
    /// <para>
    /// <c>found_in_raid_required</c> is real data on that table: the sync writes the objective's
    /// own found-in-raid flag into it, so nothing is fabricated here.
    /// </para>
    /// <para>
    /// Known and deliberate scope limit: the sync only mirrors targets whose source field is
    /// <c>items</c> into this table, which is the hand-over / find-in-raid family of objectives.
    /// Build-weapon (<c>item</c>), quest-item (<c>questItem</c>), marker (<c>markerItem</c>),
    /// use-item (<c>useAny</c>) and required-key targets are absent, so those are under-reported
    /// rather than mis-reported.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<QuestItemRequirement>> GetQuestRequirementsAsync(CancellationToken cancellationToken) =>
        GetOrLoadAsync<QuestItemRequirement>(
            () => _questRequirements,
            value => _questRequirements = value,
            (connection, token) => LoadQuestRequirementsAsync(connection, token),
            cancellationToken);

    /// <summary>Every hideout station with the levels it can be built to, ascending.</summary>
    public Task<IReadOnlyList<HideoutStationSummary>> GetStationsAsync(CancellationToken cancellationToken) =>
        GetOrLoadAsync<HideoutStationSummary>(
            () => _stations,
            value => _stations = value,
            (connection, token) => LoadStationsAsync(connection, token),
            cancellationToken);

    /// <summary>Drops every cached projection so the next read reflects a completed sync.</summary>
    public void Invalidate()
    {
        // The stamp is bumped before the fields are cleared so a load that is already in flight
        // sees the change and declines to cache the rows it read from before the sync.
        Interlocked.Increment(ref _generation);
        _hideoutRequirements = null;
        _questRequirements = null;
        _stations = null;
    }

    private static async Task<IReadOnlyList<HideoutItemRequirement>> LoadHideoutRequirementsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "hideout_requirements", cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = HideoutItemRequirementsSql;

        // A repeated (station, level, item) is collapsed rather than summed. The refresh deletes
        // the whole table and reinserts it inside one transaction, so duplicates cannot survive
        // from an earlier sync; the only way to get two rows is the upstream feed listing the same
        // item twice for one level, which is one requirement described twice. Summing those would
        // quietly double what the player is told to gather. The larger of the two counts is kept
        // so a collapse can never report less than a row that was actually written.
        var collapsed = new Dictionary<HideoutRequirementKey, int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var stationId = reader.GetString(0);
            var itemId = reader.GetString(2);
            if (ReadWholeNumber(reader, 1) is not { } targetLevel ||
                ReadWholeNumber(reader, 3) is not { } required ||
                targetLevel <= 0 ||
                string.IsNullOrWhiteSpace(stationId) ||
                string.IsNullOrWhiteSpace(itemId))
            {
                continue;
            }

            var key = new HideoutRequirementKey(stationId, targetLevel, itemId);
            collapsed[key] = collapsed.TryGetValue(key, out var existing)
                ? Math.Max(existing, required)
                : required;
        }

        return collapsed
            .OrderBy(pair => pair.Key.StationId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.TargetLevel)
            .ThenBy(pair => pair.Key.ItemId, StringComparer.Ordinal)
            .Select(pair => new HideoutItemRequirement(
                pair.Key.StationId,
                pair.Key.TargetLevel,
                pair.Key.ItemId,
                pair.Value))
            .ToArray();
    }

    private static async Task<IReadOnlyList<QuestItemRequirement>> LoadQuestRequirementsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "task_objective_items", cancellationToken).ConfigureAwait(false) ||
            !await TableExistsAsync(connection, "task_objectives", cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        // No collapsing pass here: the (task_id, objective_id, item_id) primary key already
        // makes a duplicate impossible, and the join adds exactly one task row per objective. Several
        // rows for one objective are the objective's alternative items ("hand over A or B"), and
        // they must stay separate — the aggregation service counts each against its own item id,
        // so merging them would erase a need rather than double one.
        await using var command = connection.CreateCommand();
        command.CommandText = QuestItemRequirementsSql;
        var requirements = new List<QuestItemRequirement>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var taskId = reader.GetString(0);
            var objectiveId = reader.GetString(1);
            var itemId = reader.GetString(2);
            if (ReadWholeNumber(reader, 3) is not { } required ||
                string.IsNullOrWhiteSpace(taskId) ||
                string.IsNullOrWhiteSpace(objectiveId) ||
                string.IsNullOrWhiteSpace(itemId))
            {
                continue;
            }

            requirements.Add(new(taskId, objectiveId, itemId, required, ReadFlag(reader, 4)));
        }

        return requirements;
    }

    private static async Task<IReadOnlyList<HideoutStationSummary>> LoadStationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "hideout_stations", cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var levels = await LoadStationLevelsAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name
            FROM hideout_stations
            ORDER BY name COLLATE NOCASE, id COLLATE BINARY;
            """;
        var stations = new List<HideoutStationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            // A station with no level rows is still a station; it is listed with no levels rather
            // than dropped, so a partial sync shows up as an incomplete station instead of a
            // missing one.
            int[] stationLevels = levels.TryGetValue(id, out var found) ? found.ToArray() : [];
            stations.Add(new(id, reader.GetString(1), stationLevels));
        }

        return stations;
    }

    private static async Task<Dictionary<string, SortedSet<int>>> LoadStationLevelsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var levels = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        if (!await TableExistsAsync(connection, "hideout_levels", cancellationToken).ConfigureAwait(false))
        {
            return levels;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT station_id, level FROM hideout_levels;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var stationId = reader.GetString(0);
            if (ReadWholeNumber(reader, 1) is not { } level || string.IsNullOrWhiteSpace(stationId))
            {
                continue;
            }

            // The sorted set carries both the ascending order and the distinctness the summary
            // promises, without depending on SQL ordering or on the primary key still being there.
            if (!levels.TryGetValue(stationId, out var stationLevels))
            {
                stationLevels = new();
                levels[stationId] = stationLevels;
            }

            stationLevels.Add(level);
        }

        return levels;
    }

    /// <summary>
    /// Reports whether a table is present before it is queried.
    /// </summary>
    /// <remarks>
    /// A database created before these tables existed would otherwise turn a page load into a
    /// "no such table" <see cref="SqliteException"/>, and this catalog is read during startup.
    /// Columns are not probed separately: every column read here ships in the same migration as
    /// its table, so a table that exists has them.
    /// </remarks>
    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    /// <summary>
    /// Reads a count or level that may have been stored as an integer, a real or text.
    /// </summary>
    /// <remarks>
    /// The refresh binds these as <see cref="decimal"/>, which the SQLite provider sends as text;
    /// column affinity usually folds that back to an integer, but a value that does not fold
    /// stays a real or text, and older databases may hold either. Anything that is not a
    /// non-negative whole number in range — including a fractional count, which cannot be
    /// represented faithfully as a quantity — returns null so the caller can drop the row. It is
    /// never defaulted to zero or one, because an invented quantity is indistinguishable from a
    /// real one once it has been summed.
    /// </remarks>
    private static int? ReadWholeNumber(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        if (value is long integer)
        {
            return integer >= 0 && integer <= int.MaxValue ? (int)integer : null;
        }

        if (value is double real)
        {
            return double.IsInteger(real) && real >= 0 && real <= int.MaxValue ? (int)real : null;
        }

        if (value is string text &&
            decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) &&
            parsed == decimal.Truncate(parsed) &&
            parsed >= 0 &&
            parsed <= int.MaxValue)
        {
            return (int)parsed;
        }

        return null;
    }

    /// <summary>
    /// Reads a boolean column that may have been stored as an integer, a real or text.
    /// </summary>
    /// <remarks>
    /// Unlike a quantity, an unreadable flag has a safe answer: false understates the requirement
    /// rather than telling the player an item must be found in raid when it need not be.
    /// </remarks>
    private static bool ReadFlag(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool flag => flag,
            long integer => integer != 0,
            double real => real != 0,
            string text => bool.TryParse(text, out var parsed)
                ? parsed
                : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) &&
                  numeric != 0,
            _ => false,
        };
    }

    private async Task<IReadOnlyList<T>> GetOrLoadAsync<T>(
        Func<IReadOnlyList<T>?> read,
        Action<IReadOnlyList<T>> store,
        Func<SqliteConnection, CancellationToken, Task<IReadOnlyList<T>>> load,
        CancellationToken cancellationToken)
    {
        if (read() is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (read() is { } loadedElsewhere)
            {
                return loadedElsewhere;
            }

            // Invalidate() is synchronous and cannot take this gate, so a sync that completes
            // while this read is in flight would otherwise be immediately overwritten by the
            // pre-sync rows. On a clean install those rows are empty and would then be cached for
            // the life of the process — the exact "0 needed" failure this catalog exists to fix.
            var generation = Volatile.Read(ref _generation);
            await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var projection = await load(connection, cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _generation) == generation)
            {
                store(projection);
            }

            return projection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private readonly record struct HideoutRequirementKey(string StationId, int TargetLevel, string ItemId);
}
