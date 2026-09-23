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

    /// <summary>
    /// The flea market's own settings, which ride in the items payload rather than an endpoint
    /// of their own. Optional: a payload without them still refreshes every item.
    /// </summary>
    public TarkovDevFleaMarket? FleaMarket { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevFleaMarket
{
    public double? SellOfferFeeRate { get; init; }

    public double? SellRequirementFeeRate { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// The short name, or empty where upstream stopped sending one.
    /// </summary>
    /// <remarks>
    /// Not required, and that is the whole of #119's cheap half. Persistence already falls back
    /// to the full name when this is blank, so a payload without it works — but while the
    /// marker was here System.Text.Json threw before any of that code ran, and one renamed
    /// field took the entire items endpoint down for every installed client until a new build
    /// shipped.
    ///
    /// Required is for the fields a record is meaningless without: the id it is keyed by, and
    /// the name and size persistence refuses a hollow row for. A display name with a documented
    /// fallback is not one of them.
    /// </remarks>
    public string ShortName { get; init; } = string.Empty;

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

    /// <summary>Published item mass in kilograms; absent remains unknown.</summary>
    public double? Weight { get; init; }

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

    public long? Price { get; init; }

    public long? PriceRub { get; init; }

    public string Currency { get; init; } = "RUB";

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevItemCategory
{
    public required string Id { get; init; }

    /// <summary>What it is called, or empty where upstream stopped saying so.</summary>
    /// <remarks>
    /// Falls back to the id on the way into the database, which is this application's standing
    /// rule for a name it does not have — "wrong is worse than ugly", as the trader lookup puts
    /// it. Losing a name is then a row that reads awkwardly; requiring one made it an endpoint
    /// that did not load at all.
    /// </remarks>
    public string Name { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevMapsData
{
    public required IReadOnlyDictionary<string, TarkovDevMap> Maps { get; init; }

    /// <summary>
    /// What each kind of container on a map is called.
    /// </summary>
    /// <remarks>
    /// A map's loot list carries a container id and a position and no name at all, so without
    /// this a container is an identifier. Downloaded in the same payload as the maps and
    /// discarded until now.
    /// </remarks>
    public IReadOnlyDictionary<string, TarkovDevLootContainer> LootContainers { get; init; } =
        new Dictionary<string, TarkovDevLootContainer>(StringComparer.Ordinal);

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

/// <summary>
/// One kind of container that appears on maps.
/// </summary>
/// <remarks>
/// <see cref="Name"/> is not a name. Upstream publishes the literal string
/// "578f87a3245977356274f2cb Name" for every one of them, so the only readable field is
/// <see cref="NormalizedName"/>, which carries "duffle-bag" and "ration-supply-crate".
/// Anything that renders Name puts an identifier on the player's map.
/// </remarks>
public sealed class TarkovDevLootContainer
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public string? NormalizedName { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevMap
{
    public required string Id { get; init; }

    /// <summary>What it is called, or empty where upstream stopped saying so.</summary>
    /// <remarks>
    /// Falls back to the id on the way into the database, which is this application's standing
    /// rule for a name it does not have — "wrong is worse than ugly", as the trader lookup puts
    /// it. Losing a name is then a row that reads awkwardly; requiring one made it an endpoint
    /// that did not load at all.
    /// </remarks>
    public string Name { get; init; } = string.Empty;

    public string? NormalizedName { get; init; }

    /// <summary>The internal location token the game writes to its own logs.</summary>
    /// <remarks>
    /// This is what lets raid tracking turn a log line into a map without a hand-maintained
    /// table of guesses. Upstream publishes it alongside the normalized name, so the pairing
    /// is a fact rather than an assumption, and it gains new maps automatically.
    /// </remarks>
    public string? NameId { get; init; }

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
    public double? X { get; init; }

    public double? Y { get; init; }

    public double? Z { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevMapSpawn
{
    public TarkovDevMapPosition? Position { get; init; }

    public string? ZoneName { get; init; }

    public IReadOnlyList<string> Sides { get; init; } = [];

    public IReadOnlyList<string> Categories { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalData { get; init; } = [];
}

public sealed class TarkovDevMapExtract
{
    public required string Id { get; init; }

    /// <summary>What it is called, or empty where upstream stopped saying so.</summary>
    /// <remarks>
    /// Falls back to the id on the way into the database, which is this application's standing
    /// rule for a name it does not have — "wrong is worse than ugly", as the trader lookup puts
    /// it. Losing a name is then a row that reads awkwardly; requiring one made it an endpoint
    /// that did not load at all.
    /// </remarks>
    public string Name { get; init; } = string.Empty;

    public TarkovDevMapPosition? Position { get; init; }

    /// <summary>Which side may use it: pmc, scav, or shared.</summary>
    /// <remarks>
    /// Worth carrying because a scav extract a PMC cannot take is worse than no marker at all:
    /// it sends the player somewhere they cannot leave from.
    /// </remarks>
    public string? Faction { get; init; }

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
    public TarkovDevMapPosition? Position { get; init; }

    /// <summary>The container type at this position, when this is a container record.</summary>
    public string? LootContainer { get; init; }

    /// <summary>
    /// The explicitly unweighted item pool at this position, when this is a loose-loot record.
    /// </summary>
    /// <remarks>
    /// These IDs used to survive only in extension data. Keeping them typed is what lets the
    /// high-value layer join a location to the separately evidenced item catalog without treating
    /// a container type as if it were an item that can spawn there.
    /// </remarks>
    public IReadOnlyList<string> Items { get; init; } = [];

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

    /// <summary>What it is called, or empty where upstream stopped saying so.</summary>
    /// <remarks>
    /// Falls back to the id on the way into the database, which is this application's standing
    /// rule for a name it does not have — "wrong is worse than ugly", as the trader lookup puts
    /// it. Losing a name is then a row that reads awkwardly; requiring one made it an endpoint
    /// that did not load at all.
    /// </remarks>
    public string Name { get; init; } = string.Empty;

    public string? NormalizedName { get; init; }

    public string? Trader { get; init; }

    public int? MinPlayerLevel { get; init; }

    public string? Map { get; init; }

    public string? FactionName { get; init; }

    public bool? Restartable { get; init; }

    public bool? KappaRequired { get; init; }

    public bool? LightkeeperRequired { get; init; }

    public string? RequiredPrestige { get; init; }

    public string? WikiLink { get; init; }

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

    public string? Trader { get; init; }

    public int? Level { get; init; }

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

/// <summary>
/// A hideout trader requirement.
/// </summary>
/// <remarks>
/// Unlike the station and skill requirements, json.tarkov.dev expresses this one as a
/// comparison rather than a bare level: <c>{"requirementType":"level","compareMethod":"&gt;=",
/// "value":2,"trader":"..."}</c>. Treating <c>level</c> as required made the whole hideout
/// endpoint fail to deserialize, which in turn aborted the entire first-run data refresh.
/// Both spellings are accepted so the shape can drift back without breaking the refresh.
/// </remarks>
public sealed class TarkovDevTraderRequirement
{
    public required string Trader { get; init; }

    public int? Level { get; init; }

    public int? Value { get; init; }

    public string? RequirementType { get; init; }

    public string? CompareMethod { get; init; }

    /// <summary>The trader loyalty level this requirement compares against.</summary>
    [JsonIgnore]
    public int RequiredLevel => Level ?? Value ?? 0;

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
    public long? Timestamp { get; init; }

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
    string? RawSourceJson = null,
    string? RefusalReason = null,
    string? SourceKey = null);
