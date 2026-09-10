using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovCompanion.Infrastructure.TarkovDevJson;

public sealed class TarkovDevEnvelope<T>
{
    public required T Data { get; init; }

    public IReadOnlyList<string> Translations { get; init; } = [];
}

public sealed class TarkovDevItemsData
{
    public required IReadOnlyDictionary<string, TarkovDevItem> Items { get; init; }

    public IReadOnlyDictionary<string, TarkovDevItemCategory> ItemCategories { get; init; } =
        new Dictionary<string, TarkovDevItemCategory>();

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string ShortName { get; init; }

    public string Description { get; init; } = string.Empty;

    public string? NormalizedName { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public DateTimeOffset? Updated { get; init; }

    public long? BasePrice { get; init; }

    public long? Avg24hPrice { get; init; }

    public long? LastLowPrice { get; init; }

    public long? Low24hPrice { get; init; }

    public long? High24hPrice { get; init; }

    public string? IconLink { get; init; }

    public string? GridImageLink { get; init; }

    public string? WikiLink { get; init; }

    public IReadOnlyList<string> Types { get; init; } = [];

    public IReadOnlyList<string> Categories { get; init; } = [];

    public JsonElement? Properties { get; init; }

    public IReadOnlyList<TarkovDevTraderPrice> SellToTrader { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevTraderPrice
{
    public required string Trader { get; init; }

    public long Price { get; init; }

    public long PriceRub { get; init; }

    public string Currency { get; init; } = "RUB";

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevItemCategory
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevMapsData
{
    public required IReadOnlyDictionary<string, TarkovDevMap> Maps { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevMap
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? NormalizedName { get; init; }

    public int? RaidDuration { get; init; }

    public IReadOnlyList<TarkovDevMapSpawn> Spawns { get; init; } = [];

    public IReadOnlyList<TarkovDevMapExtract> Extracts { get; init; } = [];

    public IReadOnlyList<TarkovDevMapTransit> Transits { get; init; } = [];

    public IReadOnlyList<TarkovDevMapLock> Locks { get; init; } = [];

    public IReadOnlyList<JsonElement> Hazards { get; init; } = [];

    public IReadOnlyList<TarkovDevMapLoot> LootContainers { get; init; } = [];

    public IReadOnlyList<TarkovDevMapLoot> LootLoose { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevMapPosition
{
    public required double X { get; init; }

    public required double Y { get; init; }

    public required double Z { get; init; }
}

public sealed class TarkovDevMapSpawn
{
    public required TarkovDevMapPosition Position { get; init; }

    public string? ZoneName { get; init; }

    public IReadOnlyList<string> Sides { get; init; } = [];

    public IReadOnlyList<string> Categories { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevMapExtract
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public TarkovDevMapPosition? Position { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevMapTransit
{
    public required string Id { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevMapLock
{
    public required string Id { get; init; }

    public string? Key { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevMapLoot
{
    public required TarkovDevMapPosition Position { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevTasksData
{
    public required IReadOnlyDictionary<string, TarkovDevTask> Tasks { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevTask
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? NormalizedName { get; init; }

    public string? Trader { get; init; }

    public int? MinPlayerLevel { get; init; }

    public string? Map { get; init; }

    public string? FactionName { get; init; }

    public bool? Restartable { get; init; }

    public bool? KappaRequired { get; init; }

    public bool? LightkeeperRequired { get; init; }

    public string? RequiredPrestige { get; init; }

    public int? AvailableDelaySecondsMin { get; init; }

    public int? AvailableDelaySecondsMax { get; init; }

    public IReadOnlyList<string> GameMode { get; init; } = [];

    public IReadOnlyList<TarkovDevTaskRequirement> TaskRequirements { get; init; } = [];

    public IReadOnlyList<TarkovDevTaskObjective> Objectives { get; init; } = [];

    public IReadOnlyList<TarkovDevTaskObjective> FailConditions { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevTaskRequirement
{
    public required string Task { get; init; }

    public IReadOnlyList<string> Status { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevTaskObjective
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public string Description { get; init; } = string.Empty;

    public decimal? Count { get; init; }

    public bool? Optional { get; init; }

    public bool? FoundInRaid { get; init; }

    public IReadOnlyList<string> Items { get; init; } = [];

    public string? Item { get; init; }

    public string? QuestItem { get; init; }

    public string? MarkerItem { get; init; }

    public IReadOnlyList<string> UseAny { get; init; } = [];

    public IReadOnlyList<IReadOnlyList<string>> RequiredKeys { get; init; } = [];

    public IReadOnlyList<string> Maps { get; init; } = [];

    public IReadOnlyList<TarkovDevObjectiveZone> Zones { get; init; } = [];

    public IReadOnlyList<TarkovDevPossibleLocation> PossibleLocations { get; init; } = [];

    public string? Task { get; init; }

    public IReadOnlyList<string> Status { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevObjectiveZone
{
    public string? Id { get; init; }

    public string? Map { get; init; }

    public TarkovDevObjectivePosition? Position { get; init; }

    public IReadOnlyList<TarkovDevObjectivePosition> Outline { get; init; } = [];

    public double? Bottom { get; init; }

    public double? Top { get; init; }

    public double? TerrainElevation { get; init; }

    public TarkovDevObjectivePosition? Size { get; init; }

    public string? Name { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevPossibleLocation
{
    public string? Map { get; init; }

    public IReadOnlyList<TarkovDevObjectivePosition> Positions { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevObjectivePosition
{
    public double? X { get; init; }

    public double? Y { get; init; }

    public double? Z { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevHideoutStation
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public IReadOnlyList<TarkovDevHideoutLevel> Levels { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevHideoutLevel
{
    public required string Id { get; init; }

    public required int Level { get; init; }

    public IReadOnlyList<TarkovDevItemRequirement> ItemRequirements { get; init; } = [];

    public IReadOnlyList<TarkovDevStationRequirement> StationLevelRequirements { get; init; } = [];

    public IReadOnlyList<TarkovDevTraderRequirement> TraderRequirements { get; init; } = [];

    public IReadOnlyList<TarkovDevSkillRequirement> SkillRequirements { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevStationRequirement
{
    public required string Station { get; init; }

    public required int Level { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevTraderRequirement
{
    public required string Trader { get; init; }

    public required int Level { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevSkillRequirement
{
    public required string Skill { get; init; }

    public required int Level { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevItemRequirement
{
    public required string Item { get; init; }

    public required decimal Count { get; init; }

    public JsonElement? Attributes { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevTrader
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public IReadOnlyList<TarkovDevTraderLevel> Levels { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevTraderLevel
{
    public required string Id { get; init; }

    public required int Level { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevCraft
{
    public required string Id { get; init; }

    public string? Station { get; init; }

    public int? Level { get; init; }

    public IReadOnlyList<TarkovDevItemRequirement> RequiredItems { get; init; } = [];

    public required TarkovDevItemRequirement ProductItem { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevBarter
{
    public required string Id { get; init; }

    public string? Trader { get; init; }

    public IReadOnlyList<TarkovDevItemRequirement> RequiredItems { get; init; } = [];

    public required TarkovDevItemRequirement OfferedItem { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevPricePoint
{
    public required long Timestamp { get; init; }

    public long? Price { get; init; }

    public long? PriceMin { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed record TarkovDevResponse<T>(
    T Data,
    string Json,
    DateTimeOffset CachedUtc,
    bool IsFromCache,
    bool IsStale,
    string? ETag,
    DateTimeOffset? LastModified,
    string? RawSourceJson = null);
