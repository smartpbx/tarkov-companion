using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Infrastructure.TarkovDevJson;

public sealed class TarkovDevQuestCatalogNormalizer
{
    private const string SourceKey = "json.tarkov.dev/tasks";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public QuestCatalogSnapshot Normalize(
        TarkovDevResponse<TarkovDevTasksData> response,
        GameMode gameMode,
        string language,
        DateTimeOffset validatedUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        var sourceMode = SourceMode(gameMode);
        var normalizedLanguage = language.Trim().ToLowerInvariant();
        var rawSourceJson = response.RawSourceJson ?? response.Json;
        var rawTasks = ReadRawTasks(rawSourceJson);
        var taskIds = new HashSet<string>(StringComparer.Ordinal);
        var tasks = new List<QuestTaskDefinition>(response.Data.Tasks.Count);

        foreach (var pair in response.Data.Tasks.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var task = pair.Value;
            ValidateTask(pair.Key, task, taskIds);
            rawTasks.TryGetValue(pair.Key, out var rawTask);
            rawTask ??= RawTask.FromDto(task);

            var requirements = NormalizeRequirements(task.TaskRequirements ?? [], rawTask.Requirements);
            var objectives = NormalizeObjectives(task.Id, task.Objectives ?? [], false, rawTask.Objectives);
            var failures = NormalizeObjectives(task.Id, task.FailConditions ?? [], true, rawTask.Failures);
            tasks.Add(new(
                task.Id,
                task.Name,
                task.NormalizedName,
                task.Trader,
                task.MinPlayerLevel,
                task.FactionName,
                task.Map,
                task.Restartable,
                task.KappaRequired,
                task.LightkeeperRequired,
                task.RequiredPrestige,
                task.AvailableDelaySecondsMin,
                task.AvailableDelaySecondsMax,
                (task.GameMode ?? []).ToArray(),
                requirements,
                objectives,
                failures,
                rawTask.Json)
            {
                WikiUri = task.WikiLink,
            });
        }

        var provenance = new QuestCatalogProvenance(
            SourceKey,
            $"https://json.tarkov.dev/{sourceMode}/tasks",
            gameMode,
            sourceMode,
            normalizedLanguage,
            Hash(rawSourceJson),
            Hash(response.Json),
            response.ETag,
            response.LastModified?.ToUniversalTime(),
            response.CachedUtc.ToUniversalTime(),
            validatedUtc.ToUniversalTime());
        return new(provenance, tasks, rawSourceJson, response.Json);
    }

    private static IReadOnlyList<QuestTaskRequirement> NormalizeRequirements(
        IReadOnlyList<TarkovDevTaskRequirement> requirements,
        IReadOnlyList<string> rawRequirements)
    {
        var normalized = new List<QuestTaskRequirement>(requirements.Count);
        for (var index = 0; index < requirements.Count; index++)
        {
            var requirement = requirements[index];
            if (string.IsNullOrWhiteSpace(requirement.Task))
            {
                throw new InvalidDataException($"Task requirement at index {index} is missing its required task id.");
            }

            normalized.Add(new(
                index,
                requirement.Task,
                (requirement.Status ?? []).ToArray(),
                RawAt(rawRequirements, index, requirement)));
        }

        return normalized;
    }

    private static IReadOnlyList<QuestObjectiveDefinition> NormalizeObjectives(
        string taskId,
        IReadOnlyList<TarkovDevTaskObjective> objectives,
        bool isFailureCondition,
        IReadOnlyList<string> rawObjectives)
    {
        var normalized = new List<QuestObjectiveDefinition>(objectives.Count);
        var objectiveIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < objectives.Count; index++)
        {
            var objective = objectives[index];
            if (string.IsNullOrWhiteSpace(objective.Id) || string.IsNullOrWhiteSpace(objective.Type))
            {
                throw new InvalidDataException($"Task '{taskId}' has an objective at index {index} without a required id or type.");
            }

            if (!objectiveIds.Add(objective.Id))
            {
                throw new InvalidDataException($"Task '{taskId}' contains duplicate objective id '{objective.Id}'.");
            }

            var rawJson = RawAt(rawObjectives, index, objective);
            normalized.Add(new(
                objective.Id,
                taskId,
                objective.Type,
                ObjectiveKind(objective.Type),
                isFailureCondition,
                index,
                objective.Description,
                objective.Count,
                objective.Optional,
                objective.FoundInRaid,
                objective.Task,
                (objective.Status ?? []).ToArray(),
                NormalizeItemTargets(objective),
                NormalizeMapAssociations(objective),
                NormalizeZones(objective.Zones ?? [], objective.PossibleLocations ?? [], rawJson),
                SubtypeJson(rawJson),
                rawJson));
        }

        return normalized;
    }

    private static IReadOnlyList<QuestObjectiveItemTarget> NormalizeItemTargets(TarkovDevTaskObjective objective)
    {
        var targets = new List<QuestObjectiveItemTarget>();
        AddSet(targets, objective.Items ?? [], "items", 0, objective.Count, objective.FoundInRaid);
        AddSingle(targets, objective.Item, "item", objective.Count, objective.FoundInRaid);
        AddSingle(targets, objective.QuestItem, "questItem", objective.Count, objective.FoundInRaid);
        AddSingle(targets, objective.MarkerItem, "markerItem", objective.Count, objective.FoundInRaid);
        AddSet(targets, objective.UseAny ?? [], "useAny", 0, objective.Count, objective.FoundInRaid);

        var requiredKeys = objective.RequiredKeys ?? [];
        for (var group = 0; group < requiredKeys.Count; group++)
        {
            AddSet(targets, requiredKeys[group] ?? [], "requiredKeys", group, null, null);
        }

        return targets;
    }

    private static void AddSingle(
        List<QuestObjectiveItemTarget> targets,
        string? itemId,
        string sourceField,
        decimal? targetCount,
        bool? foundInRaid)
    {
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            targets.Add(new(itemId, sourceField, 0, 0, targetCount, foundInRaid));
        }
    }

    private static void AddSet(
        List<QuestObjectiveItemTarget> targets,
        IReadOnlyList<string> itemIds,
        string sourceField,
        int alternativeGroup,
        decimal? targetCount,
        bool? foundInRaid)
    {
        for (var index = 0; index < itemIds.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(itemIds[index]))
            {
                targets.Add(new(itemIds[index], sourceField, alternativeGroup, index, targetCount, foundInRaid));
            }
        }
    }

    private static IReadOnlyList<QuestMapAssociation> NormalizeMapAssociations(TarkovDevTaskObjective objective)
    {
        var associations = new List<QuestMapAssociation>();
        AddMaps(associations, objective.Maps ?? [], QuestMapAssociationKind.Declared);
        AddMaps(
            associations,
            (objective.Zones ?? []).Select(zone => zone.Map),
            QuestMapAssociationKind.Zone);
        AddMaps(
            associations,
            (objective.PossibleLocations ?? []).Select(location => location.Map),
            QuestMapAssociationKind.PossibleLocation);
        return associations;
    }

    private static void AddMaps(
        List<QuestMapAssociation> associations,
        IEnumerable<string?> mapIds,
        QuestMapAssociationKind kind)
    {
        var sourceOrdinal = 0;
        foreach (var mapId in mapIds)
        {
            if (!string.IsNullOrWhiteSpace(mapId))
            {
                associations.Add(new(kind, sourceOrdinal, mapId));
            }

            sourceOrdinal++;
        }
    }

    private static IReadOnlyList<QuestObjectiveZone> NormalizeZones(
        IReadOnlyList<TarkovDevObjectiveZone> zones,
        IReadOnlyList<TarkovDevPossibleLocation> possibleLocations,
        string rawObjectiveJson)
    {
        var rawZones = ReadRawArray(rawObjectiveJson, "zones");
        var normalized = new List<QuestObjectiveZone>(zones.Count);
        for (var index = 0; index < zones.Count; index++)
        {
            var zone = zones[index];
            ValidateFinite(zone.Bottom, "bottom elevation");
            ValidateFinite(zone.Top, "top elevation");
            ValidateFinite(zone.TerrainElevation, "terrain elevation");
            normalized.Add(new(
                index,
                zone.Id,
                zone.Map,
                Position(zone.Position),
                (zone.Outline ?? []).Select(Position).OfType<WorldPosition>().ToArray(),
                zone.Bottom,
                zone.Top,
                zone.TerrainElevation,
                Size(zone.Size),
                zone.Name,
                RawAt(rawZones, index, zone)));
        }

        var sourceOrdinal = zones.Count;
        for (var locationIndex = 0; locationIndex < possibleLocations.Count; locationIndex++)
        {
            var possibleLocation = possibleLocations[locationIndex];
            for (var positionIndex = 0; positionIndex < possibleLocation.Positions.Count; positionIndex++)
            {
                var position = Position(possibleLocation.Positions[positionIndex]);
                if (position is null)
                {
                    continue;
                }

                normalized.Add(new(
                    sourceOrdinal++,
                    null,
                    possibleLocation.Map,
                    position,
                    [],
                    null,
                    null,
                    null,
                    null,
                    QuestObjectiveZone.PossibleLocationName,
                    rawObjectiveJson));
            }
        }

        return normalized;
    }

    private static WorldPosition? Position(TarkovDevObjectivePosition? position)
    {
        if (position is null) return null;
        ValidateFinite(position.X, "x coordinate");
        ValidateFinite(position.Y, "y coordinate");
        ValidateFinite(position.Z, "z coordinate");
        return position is { X: not null, Y: not null, Z: not null }
            ? new(position.X.Value, position.Y.Value, position.Z.Value)
            : null;
    }

    private static QuestZoneSize? Size(TarkovDevObjectivePosition? size)
    {
        if (size is null) return null;
        ValidateFinite(size.X, "zone width");
        ValidateFinite(size.Y, "zone height");
        ValidateFinite(size.Z, "zone depth");
        return new(size.X, size.Y, size.Z);
    }

    private static void ValidateFinite(double? value, string field)
    {
        if (value is { } number && !double.IsFinite(number))
            throw new InvalidDataException($"Quest data contains a non-finite {field}.");
    }

    private static string SubtypeJson(string rawObjectiveJson)
    {
        var node = JsonNode.Parse(rawObjectiveJson) as JsonObject;
        if (node is null)
        {
            return "{}";
        }

        foreach (var commonField in new[]
                 {
                     "id", "type", "description", "count", "optional", "foundInRaid", "items", "maps", "zones",
                 })
        {
            node.Remove(commonField);
        }

        return node.ToJsonString();
    }

    private static void ValidateTask(string dictionaryKey, TarkovDevTask task, HashSet<string> taskIds)
    {
        if (!string.Equals(dictionaryKey, task.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Task dictionary key '{dictionaryKey}' does not match task id '{task.Id}'.");
        }

        if (string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.Name))
        {
            throw new InvalidDataException($"Task '{dictionaryKey}' is missing a required normalized field.");
        }

        if (!taskIds.Add(task.Id))
        {
            throw new InvalidDataException($"Task id '{task.Id}' occurs more than once.");
        }
    }

    private static QuestObjectiveKind ObjectiveKind(string type) => type switch
    {
        "buildWeapon" => QuestObjectiveKind.BuildWeapon,
        "dialogue" => QuestObjectiveKind.Dialogue,
        "experience" => QuestObjectiveKind.Experience,
        "extract" => QuestObjectiveKind.Extract,
        "findItem" => QuestObjectiveKind.FindItem,
        "findQuestItem" => QuestObjectiveKind.FindQuestItem,
        "giveItem" => QuestObjectiveKind.GiveItem,
        "giveQuestItem" => QuestObjectiveKind.GiveQuestItem,
        "globalVariable" => QuestObjectiveKind.GlobalVariable,
        "mark" => QuestObjectiveKind.Mark,
        "plantItem" => QuestObjectiveKind.PlantItem,
        "plantQuestItem" => QuestObjectiveKind.PlantQuestItem,
        "sellItem" => QuestObjectiveKind.SellItem,
        "shoot" => QuestObjectiveKind.Shoot,
        "skill" => QuestObjectiveKind.Skill,
        "taskStatus" => QuestObjectiveKind.TaskStatus,
        "traderLevel" => QuestObjectiveKind.TraderLevel,
        "traderStanding" => QuestObjectiveKind.TraderStanding,
        "useItem" => QuestObjectiveKind.UseItem,
        "visit" => QuestObjectiveKind.Visit,
        _ => QuestObjectiveKind.Unsupported,
    };

    private static IReadOnlyDictionary<string, RawTask> ReadRawTasks(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("tasks", out var tasks) ||
            tasks.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The raw task catalog is missing the required data.tasks object.");
        }

        var result = new Dictionary<string, RawTask>(StringComparer.Ordinal);
        foreach (var task in tasks.EnumerateObject())
        {
            result[task.Name] = new(
                task.Value.GetRawText(),
                ReadRawArray(task.Value, "taskRequirements"),
                ReadRawArray(task.Value, "objectives"),
                ReadRawArray(task.Value, "failConditions"));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadRawArray(string objectJson, string propertyName)
    {
        using var document = JsonDocument.Parse(objectJson);
        return ReadRawArray(document.RootElement, propertyName);
    }

    private static IReadOnlyList<string> ReadRawArray(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var array) &&
        array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(item => item.GetRawText()).ToArray()
            : [];

    private static string RawAt<T>(IReadOnlyList<string> values, int index, T fallback) =>
        index < values.Count ? values[index] : JsonSerializer.Serialize(fallback, SerializerOptions);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string SourceMode(GameMode gameMode) => gameMode switch
    {
        GameMode.Regular => "regular",
        GameMode.Pve => "pve",
        GameMode.PvpSeason => "pvp-season",
        _ => throw new ArgumentOutOfRangeException(nameof(gameMode)),
    };

    private sealed record RawTask(
        string Json,
        IReadOnlyList<string> Requirements,
        IReadOnlyList<string> Objectives,
        IReadOnlyList<string> Failures)
    {
        public static RawTask FromDto(TarkovDevTask task)
        {
            var json = JsonSerializer.Serialize(task, SerializerOptions);
            return new(
                json,
                ReadRawArray(json, "taskRequirements"),
                ReadRawArray(json, "objectives"),
                ReadRawArray(json, "failConditions"));
        }
    }
}
