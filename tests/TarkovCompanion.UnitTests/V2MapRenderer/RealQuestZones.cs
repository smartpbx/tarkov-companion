using System.Text.Json;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// Real quest objective zones on real map definitions: Customs, Lighthouse and Interchange as
/// tarkov.dev publishes them, and twenty objectives as json.tarkov.dev listed them on 2026-09-17.
/// </summary>
/// <remarks>
/// See fixtures/quest-zones/README.md for what was kept and how. The zones are the feed's own
/// world coordinates, so a test that lands them on the artwork is testing the projection and not a
/// transform this repository made up. The map entries carry <c>gameMapId</c> because the game's id
/// for a map lives in the synced maps table and not in tarkov.dev's maps.json, which is the gap
/// <see cref="TarkovCompanion.App.ViewModels.Maps.MapViewModel"/> closes when it asks the quest
/// read side for a map's objectives.
/// </remarks>
internal sealed class RealQuestZones
{
    private static readonly Lazy<RealQuestZones> Instance = new(() => new(Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "quest-zones",
        "measured-2026-09-18.json")));

    private static readonly DateTimeOffset NowUtc = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly TarkovDevMapCatalog _catalog;
    private readonly Dictionary<string, string> _gameMapIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RealObjective> _objectives = [];

    private RealQuestZones(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var maps = document.RootElement.GetProperty("maps");
        foreach (var map in maps.EnumerateArray())
        {
            _gameMapIds[map.GetProperty("normalizedName").GetString()!] = map.GetProperty("gameMapId").GetString()!;
        }

        _catalog = TarkovDevMapCatalogParser.Parse(
            maps.GetRawText(),
            new("https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json"),
            NowUtc);
        foreach (var item in document.RootElement.GetProperty("itemNames").EnumerateObject())
        {
            ItemNames[item.Name] = item.Value.GetString()!;
        }

        foreach (var objective in document.RootElement.GetProperty("objectives").EnumerateArray())
        {
            _objectives.Add(ReadObjective(objective));
        }
    }

    public static RealQuestZones Load() => Instance.Value;

    public Dictionary<string, string> ItemNames { get; } = new(StringComparer.Ordinal);

    public MapCatalogProvenance MapProvenance => _catalog.Provenance;

    public IReadOnlyList<RealObjective> Objectives(string map) => _objectives
        .Where(objective => string.Equals(objective.Map, map, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    public string GameMapId(string map) => _gameMapIds[map];

    /// <summary>The location as tarkov.dev's file publishes it: the slug, and no game id.</summary>
    public MapLocation PublishedLocation(string map) => _catalog.Locations
        .Single(location => string.Equals(location.Id, map, StringComparison.OrdinalIgnoreCase));

    /// <summary>The location with the game's id for it, as the map view gives it to the quest read side.</summary>
    public MapLocation Location(string map) => PublishedLocation(map) with { SourceId = GameMapId(map) };

    public MapVariant Variant(string map) => PublishedLocation(map).Variants.Single(variant =>
        variant.Projection == MapProjectionKind.Interactive);

    /// <summary>The render model for the map with one of the artworks it publishes on screen.</summary>
    public MapRenderModel Model(string map, MapBackgroundKind artwork = MapBackgroundKind.Svg) =>
        new MapPresentationService().Create(
            Location(map),
            Variant(map),
            artwork == MapBackgroundKind.Svg ? $"/cache/{map}.svg" : $"/cache/{map}",
            MapAssetAvailability.Available,
            null,
            [],
            artwork);

    /// <summary>What the quest read side hands the projection for a map: every fixture objective, active.</summary>
    public QuestMapObjectivesReadModel Query(string map) => new(
        Scope,
        1,
        Provenance,
        [GameMapId(map)],
        Objectives(map).Select(objective => objective.ReadModel).ToArray(),
        []);

    public IReadOnlyList<QuestMapObjectiveProjection> Project(string map, MapRenderModel model, MapFloorDefinition? floor = null) =>
        new QuestMapProjectionService()
            .Project(Query(map), Location(map), model.Variant, floor, MapProvenance)
            .Objectives;

    public static QuestProfileScope Scope { get; } = new(Guid.Parse("00000000-0000-0000-0000-000000000035"), GameMode.Regular, "fixture");

    public static QuestCatalogProvenance Provenance { get; } = new(
        "json.tarkov.dev",
        "https://json.tarkov.dev/regular/tasks",
        GameMode.Regular,
        "regular",
        "en",
        new string('a', 64),
        new string('b', 64),
        null,
        null,
        NowUtc,
        NowUtc);

    private static RealObjective ReadObjective(JsonElement element)
    {
        var zones = element.GetProperty("zones").EnumerateArray().Select(ReadZone).ToArray();
        var items = element.GetProperty("items").EnumerateArray()
            .Select(item => new QuestObjectiveItemTarget(
                item.GetProperty("itemId").GetString()!,
                item.GetProperty("field").GetString()!,
                item.GetProperty("group").GetInt32(),
                item.GetProperty("ordinal").GetInt32(),
                Decimal(item, "count"),
                item.TryGetProperty("foundInRaid", out var raid) && raid.ValueKind != JsonValueKind.Null
                    ? raid.GetBoolean()
                    : null))
            .ToArray();
        var mapId = element.GetProperty("primaryMapId").GetString()!;
        var model = new QuestMapObjectiveReadModel(
            element.GetProperty("taskId").GetString()!,
            element.GetProperty("taskName").GetString()!,
            element.GetProperty("traderId").GetString(),
            element.GetProperty("objectiveId").GetString()!,
            element.GetProperty("ordinal").GetInt32(),
            element.GetProperty("description").GetString()!,
            Enum.Parse<QuestObjectiveKind>(element.GetProperty("kind").GetString()!),
            false,
            element.TryGetProperty("optional", out var optional) && optional.ValueKind != JsonValueKind.Null
                ? optional.GetBoolean()
                : null,
            RecordedTaskState.Active,
            RecordedObjectiveState.InProgress,
            false,
            false,
            null,
            "fixture",
            null,
            element.TryGetProperty("foundInRaid", out var found) && found.ValueKind != JsonValueKind.Null
                ? found.GetBoolean()
                : null,
            [mapId],
            zones.Select(zone => zone.Zone).ToArray(),
            items)
        {
            TargetCount = Decimal(element, "targetCount"),
            WikiUri = element.TryGetProperty("wikiUri", out var wiki) ? wiki.GetString() : null,
        };
        return new(element.GetProperty("map").GetString()!, model, zones.Select(zone => zone.World).ToArray());
    }

    private static (QuestObjectiveZone Zone, RealZone World) ReadZone(JsonElement element)
    {
        WorldPosition? position = element.TryGetProperty("position", out var at) && at.ValueKind == JsonValueKind.Object
            ? Point(at)
            : null;
        var outline = element.TryGetProperty("outline", out var ring) && ring.ValueKind == JsonValueKind.Array
            ? ring.EnumerateArray().Select(Point).ToArray()
            : [];
        var zone = new QuestObjectiveZone(
            element.GetProperty("ordinal").GetInt32(),
            element.GetProperty("zoneId").GetString(),
            element.GetProperty("mapId").GetString(),
            position,
            outline,
            Double(element, "bottom"),
            Double(element, "top"),
            Double(element, "terrain"),
            null,
            element.GetProperty("name").GetString(),
            "{}");
        return (zone, new(position, outline));
    }

    private static WorldPosition Point(JsonElement element) => new(
        element.GetProperty("x").GetDouble(),
        element.GetProperty("y").GetDouble(),
        element.GetProperty("z").GetDouble());

    private static double? Double(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    /// <summary>A count as the fixture writes it: the catalog stores decimals as text.</summary>
    private static decimal? Decimal(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}

/// <summary>An objective as the feed listed it, and the world coordinates its zones name.</summary>
internal sealed record RealObjective(string Map, QuestMapObjectiveReadModel ReadModel, IReadOnlyList<RealZone> Zones)
{
    public string Id => ReadModel.ObjectiveId;

    /// <summary>The zones with their duplicates dropped, as a player counts them.</summary>
    public IReadOnlyList<RealZone> DistinctZones => Zones.DistinctBy(zone => zone.Key).ToArray();

    public bool IsCandidate => ReadModel.Zones.Any(zone => zone.IsPossibleLocation);

    public bool HasNoZones => ReadModel.Zones.Count == 0;
}

/// <summary>Where a zone is in the world: a position, an outline, or both.</summary>
internal sealed record RealZone(WorldPosition? Position, IReadOnlyList<WorldPosition> Outline)
{
    public bool IsArea => Outline.Count >= 3;

    public string Key => IsArea
        ? string.Join(';', Outline.Select(point => $"{point.X:R},{point.Y:R},{point.Z:R}"))
        : $"{Position?.X:R},{Position?.Y:R},{Position?.Z:R}";
}
