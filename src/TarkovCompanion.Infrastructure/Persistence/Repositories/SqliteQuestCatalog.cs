using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

public sealed class SqliteQuestCatalog(SqliteConnectionFactory connectionFactory) : IQuestCatalog
{
    private const string SourceKey = "json.tarkov.dev/tasks";
    internal const string TaskCatalogSql = """
        SELECT id, name, normalized_name, trader_id, min_player_level, faction_name,
               primary_map_id, restartable, kappa_required, lightkeeper_required,
               required_prestige_id, available_delay_seconds_min,
               available_delay_seconds_max, source_game_modes_json, raw_json
        FROM quest_catalog_tasks
        WHERE source_key = $sourceKey AND source_mode = $sourceMode AND language = $language
        ORDER BY id COLLATE BINARY;
        """;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<QuestCatalogSnapshot?> GetAsync(
        GameMode gameMode,
        string language,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var sourceMode = SourceMode(gameMode);
        var normalizedLanguage = language.Trim().ToLowerInvariant();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await LoadSnapshotAsync(
            connection,
            sourceMode,
            normalizedLanguage,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        var taskRows = await LoadTasksAsync(connection, sourceMode, normalizedLanguage, cancellationToken).ConfigureAwait(false);
        var requirements = await LoadRequirementsAsync(connection, sourceMode, normalizedLanguage, cancellationToken).ConfigureAwait(false);
        var objectives = await LoadObjectivesAsync(connection, sourceMode, normalizedLanguage, cancellationToken).ConfigureAwait(false);
        await LoadObjectiveStatusesAsync(connection, sourceMode, normalizedLanguage, objectives, cancellationToken).ConfigureAwait(false);
        await LoadMapLinksAsync(connection, sourceMode, normalizedLanguage, objectives, cancellationToken).ConfigureAwait(false);
        await LoadItemTargetsAsync(connection, sourceMode, normalizedLanguage, objectives, cancellationToken).ConfigureAwait(false);
        await LoadZonesAsync(connection, sourceMode, normalizedLanguage, objectives, cancellationToken).ConfigureAwait(false);

        var tasks = taskRows.Select(task => new QuestTaskDefinition(
            task.Id,
            task.Name,
            task.NormalizedName,
            task.TraderId,
            task.MinimumPlayerLevel,
            task.FactionName,
            task.PrimaryMapId,
            task.Restartable,
            task.KappaRequired,
            task.LightkeeperRequired,
            task.RequiredPrestigeId,
            task.DelayMinimum,
            task.DelayMaximum,
            task.SourceGameModes,
            requirements.Where(value => value.TaskId == task.Id).Select(value => value.ToDomain()).ToArray(),
            objectives.Values
                .Where(value => value.TaskId == task.Id && !value.IsFailureCondition)
                .OrderBy(value => value.SourceOrdinal)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .Select(value => value.ToDomain())
                .ToArray(),
            objectives.Values
                .Where(value => value.TaskId == task.Id && value.IsFailureCondition)
                .OrderBy(value => value.SourceOrdinal)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .Select(value => value.ToDomain())
                .ToArray(),
            task.RawJson)).ToArray();

        return new(snapshot.Provenance, tasks, snapshot.RawJson, snapshot.TranslatedJson);
    }

    private static async Task<SnapshotRow?> LoadSnapshotAsync(
        SqliteConnection connection,
        string sourceMode,
        string language,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_uri, local_game_mode, payload_sha256, translated_payload_sha256,
                   etag, last_modified_utc, fetched_utc, validated_utc, raw_json, translated_json
            FROM quest_catalog_snapshots
            WHERE source_key = $sourceKey AND source_mode = $sourceMode AND language = $language;
            """;
        AddScope(command, sourceMode, language);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var storedMode = Enum.Parse<GameMode>(reader.GetString(1), false);
        return new(
            new(
                SourceKey,
                reader.GetString(0),
                storedMode,
                sourceMode,
                language,
                reader.GetString(2),
                reader.GetString(3),
                NullableString(reader, 4),
                NullableTimestamp(reader, 5),
                Timestamp(reader, 6),
                Timestamp(reader, 7)),
            reader.GetString(8),
            reader.GetString(9));
    }

    private static async Task<IReadOnlyList<TaskRow>> LoadTasksAsync(
        SqliteConnection connection,
        string sourceMode,
        string language,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = TaskCatalogSql;
        AddScope(command, sourceMode, language);
        var rows = new List<TaskRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                NullableString(reader, 2),
                NullableString(reader, 3),
                NullableInt(reader, 4),
                NullableString(reader, 5),
                NullableString(reader, 6),
                NullableBool(reader, 7),
                NullableBool(reader, 8),
                NullableBool(reader, 9),
                NullableString(reader, 10),
                NullableInt(reader, 11),
                NullableInt(reader, 12),
                JsonSerializer.Deserialize<string[]>(reader.GetString(13), SerializerOptions) ?? [],
                reader.GetString(14)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<RequirementBuilder>> LoadRequirementsAsync(
        SqliteConnection connection,
        string sourceMode,
        string language,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT requirement.task_id, requirement.source_ordinal, requirement.required_task_id,
                   requirement.raw_json, status.status_ordinal, status.required_status
            FROM quest_task_requirements AS requirement
            LEFT JOIN quest_task_requirement_statuses AS status
              ON status.source_key = requirement.source_key
             AND status.source_mode = requirement.source_mode
             AND status.language = requirement.language
             AND status.task_id = requirement.task_id
             AND status.requirement_ordinal = requirement.source_ordinal
            WHERE requirement.source_key = $sourceKey
              AND requirement.source_mode = $sourceMode
              AND requirement.language = $language
            ORDER BY requirement.task_id COLLATE BINARY, requirement.source_ordinal, status.status_ordinal;
            """;
        AddScope(command, sourceMode, language);
        var rows = new List<RequirementBuilder>();
        RequirementBuilder? current = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var taskId = reader.GetString(0);
            var ordinal = reader.GetInt32(1);
            if (current is null || current.TaskId != taskId || current.SourceOrdinal != ordinal)
            {
                current = new(taskId, ordinal, reader.GetString(2), reader.GetString(3));
                rows.Add(current);
            }

            if (!reader.IsDBNull(5))
            {
                current.Statuses.Add(reader.GetString(5));
            }
        }

        return rows;
    }

    private static async Task<Dictionary<ObjectiveKey, ObjectiveBuilder>> LoadObjectivesAsync(
        SqliteConnection connection,
        string sourceMode,
        string language,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT task_id, id, is_failure_condition, source_ordinal, source_type,
                   normalized_kind, is_unsupported, description, target_count, optional,
                   found_in_raid_required, target_task_id, subtype_json, raw_json
            FROM quest_catalog_objectives
            WHERE source_key = $sourceKey AND source_mode = $sourceMode AND language = $language
            ORDER BY task_id COLLATE BINARY, is_failure_condition, source_ordinal, id COLLATE BINARY;
            """;
        AddScope(command, sourceMode, language);
        var rows = new Dictionary<ObjectiveKey, ObjectiveBuilder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var taskId = reader.GetString(0);
            var id = reader.GetString(1);
            var isFailure = reader.GetBoolean(2);
            var unsupported = reader.GetBoolean(6);
            var kind = !unsupported && Enum.TryParse<QuestObjectiveKind>(reader.GetString(5), false, out var parsed)
                ? parsed
                : QuestObjectiveKind.Unsupported;
            rows.Add(
                new(taskId, id, isFailure),
                new(
                    taskId,
                    id,
                    isFailure,
                    reader.GetInt32(3),
                    reader.GetString(4),
                    kind,
                    reader.GetString(7),
                    NullableDecimal(reader, 8),
                    NullableBool(reader, 9),
                    NullableBool(reader, 10),
                    NullableString(reader, 11),
                    reader.GetString(12),
                    reader.GetString(13)));
        }

        return rows;
    }

    private static async Task LoadObjectiveStatusesAsync(
        SqliteConnection connection,
        string sourceMode,
        string language,
        IReadOnlyDictionary<ObjectiveKey, ObjectiveBuilder> objectives,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT task_id, objective_id, is_failure_condition, target_status
            FROM quest_objective_target_statuses
            WHERE source_key = $sourceKey AND source_mode = $sourceMode AND language = $language
            ORDER BY task_id COLLATE BINARY, objective_id COLLATE BINARY,
                     is_failure_condition, status_ordinal;
            """;
        AddScope(command, sourceMode, language);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            objectives[new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2))]
                .Statuses.Add(reader.GetString(3));
        }
    }

    private static async Task LoadMapLinksAsync(
        SqliteConnection connection,
        string sourceMode,
        string language,
        IReadOnlyDictionary<ObjectiveKey, ObjectiveBuilder> objectives,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT task_id, objective_id, is_failure_condition, association_kind,
                   source_ordinal, map_id
            FROM quest_objective_map_links
            WHERE source_key = $sourceKey AND source_mode = $sourceMode AND language = $language
            ORDER BY task_id COLLATE BINARY, objective_id COLLATE BINARY,
                     is_failure_condition, association_kind COLLATE BINARY, source_ordinal;
            """;
        AddScope(command, sourceMode, language);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var kind = Enum.Parse<QuestMapAssociationKind>(reader.GetString(3), false);
            objectives[new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2))]
                .MapAssociations.Add(new(kind, reader.GetInt32(4), reader.GetString(5)));
        }
    }

    private static async Task LoadItemTargetsAsync(
        SqliteConnection connection,
        string sourceMode,
        string language,
        IReadOnlyDictionary<ObjectiveKey, ObjectiveBuilder> objectives,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT task_id, objective_id, is_failure_condition, source_field,
                   alternative_group, source_ordinal, item_id, target_count,
                   found_in_raid_required
            FROM quest_objective_item_targets
            WHERE source_key = $sourceKey AND source_mode = $sourceMode AND language = $language
            ORDER BY task_id COLLATE BINARY, objective_id COLLATE BINARY,
                     is_failure_condition, source_field COLLATE BINARY,
                     alternative_group, source_ordinal;
            """;
        AddScope(command, sourceMode, language);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            objectives[new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2))]
                .ItemTargets.Add(new(
                    reader.GetString(6),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    NullableDecimal(reader, 7),
                    NullableBool(reader, 8)));
        }
    }

    private static async Task LoadZonesAsync(
        SqliteConnection connection,
        string sourceMode,
        string language,
        IReadOnlyDictionary<ObjectiveKey, ObjectiveBuilder> objectives,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT task_id, objective_id, is_failure_condition, source_ordinal,
                   source_zone_id, map_id, position_x, position_y, position_z,
                   outline_json, bottom_elevation, top_elevation, terrain_elevation,
                   size_json, name, raw_json
            FROM quest_objective_zones
            WHERE source_key = $sourceKey AND source_mode = $sourceMode AND language = $language
            ORDER BY task_id COLLATE BINARY, objective_id COLLATE BINARY,
                     is_failure_condition, source_ordinal;
            """;
        AddScope(command, sourceMode, language);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            WorldPosition? position = reader.IsDBNull(6) || reader.IsDBNull(7) || reader.IsDBNull(8)
                ? null
                : new(reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8));
            var outline = JsonSerializer.Deserialize<WorldPosition[]>(reader.GetString(9), SerializerOptions) ?? [];
            QuestZoneSize? size = reader.IsDBNull(13)
                ? null
                : JsonSerializer.Deserialize<QuestZoneSize>(reader.GetString(13), SerializerOptions);
            objectives[new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2))]
                .Zones.Add(new(
                    reader.GetInt32(3),
                    NullableString(reader, 4),
                    NullableString(reader, 5),
                    position,
                    outline,
                    NullableDouble(reader, 10),
                    NullableDouble(reader, 11),
                    NullableDouble(reader, 12),
                    size,
                    NullableString(reader, 14),
                    reader.GetString(15)));
        }
    }

    private static void AddScope(SqliteCommand command, string sourceMode, string language)
    {
        command.Parameters.AddWithValue("$sourceKey", SourceKey);
        command.Parameters.AddWithValue("$sourceMode", sourceMode);
        command.Parameters.AddWithValue("$language", language);
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static double? NullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static bool? NullableBool(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);

    private static decimal? NullableDecimal(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : decimal.Parse(reader.GetString(ordinal), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static DateTimeOffset Timestamp(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? NullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Timestamp(reader, ordinal);

    private static string SourceMode(GameMode gameMode) => gameMode switch
    {
        GameMode.Regular => "regular",
        GameMode.Pve => "pve",
        GameMode.PvpSeason => "pvp-season",
        _ => throw new ArgumentOutOfRangeException(nameof(gameMode)),
    };

    private sealed record SnapshotRow(QuestCatalogProvenance Provenance, string RawJson, string TranslatedJson);

    private sealed record TaskRow(
        string Id,
        string Name,
        string? NormalizedName,
        string? TraderId,
        int? MinimumPlayerLevel,
        string? FactionName,
        string? PrimaryMapId,
        bool? Restartable,
        bool? KappaRequired,
        bool? LightkeeperRequired,
        string? RequiredPrestigeId,
        int? DelayMinimum,
        int? DelayMaximum,
        IReadOnlyList<string> SourceGameModes,
        string RawJson);

    private sealed class RequirementBuilder(
        string taskId,
        int sourceOrdinal,
        string requiredTaskId,
        string rawJson)
    {
        public string TaskId { get; } = taskId;

        public int SourceOrdinal { get; } = sourceOrdinal;

        public List<string> Statuses { get; } = [];

        public QuestTaskRequirement ToDomain() => new(SourceOrdinal, requiredTaskId, Statuses, rawJson);
    }

    private readonly record struct ObjectiveKey(string TaskId, string ObjectiveId, bool IsFailureCondition);

    private sealed class ObjectiveBuilder(
        string taskId,
        string id,
        bool isFailureCondition,
        int sourceOrdinal,
        string sourceType,
        QuestObjectiveKind kind,
        string description,
        decimal? targetCount,
        bool? optional,
        bool? foundInRaid,
        string? targetTaskId,
        string subtypeJson,
        string rawJson)
    {
        public string TaskId { get; } = taskId;

        public string Id { get; } = id;

        public bool IsFailureCondition { get; } = isFailureCondition;

        public int SourceOrdinal { get; } = sourceOrdinal;

        public List<string> Statuses { get; } = [];

        public List<QuestObjectiveItemTarget> ItemTargets { get; } = [];

        public List<QuestMapAssociation> MapAssociations { get; } = [];

        public List<QuestObjectiveZone> Zones { get; } = [];

        public QuestObjectiveDefinition ToDomain() => new(
            Id,
            TaskId,
            sourceType,
            kind,
            IsFailureCondition,
            SourceOrdinal,
            description,
            targetCount,
            optional,
            foundInRaid,
            targetTaskId,
            Statuses,
            ItemTargets,
            MapAssociations,
            Zones,
            subtypeJson,
            rawJson);
    }
}
