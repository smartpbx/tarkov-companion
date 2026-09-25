using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.Application.Services.Maps.Scene;

public sealed record MapSceneBuildRequest(
    long Revision,
    MapRenderModel RenderModel,
    MapSceneBounds Bounds,
    string TransformVersion,
    MapSceneViewState RequestedView,
    IReadOnlyList<MapSceneLegacyElement> LegacyElements,
    IReadOnlyList<MapSceneLayer> AdditionalLayers,
    IReadOnlyList<MapSceneObject> AdditionalObjects,
    IReadOnlyList<MapSceneAsset> Assets);

/// <summary>An old overlay paired with the source evidence that the old model did not carry.</summary>
public sealed record MapSceneLegacyElement(
    MapOverlayElement Element,
    DataProvenance Provenance,
    MapSceneOfferState OfferState = MapSceneOfferState.Unknown)
{
    public MapOverlayElement Element { get; } = Element ?? throw new ArgumentNullException(nameof(Element));

    public DataProvenance Provenance { get; } = Provenance ?? throw new ArgumentNullException(nameof(Provenance));

    public MapSceneOfferState OfferState { get; } = Enum.IsDefined(OfferState)
        ? OfferState
        : throw new ArgumentOutOfRangeException(nameof(OfferState));
}

public sealed record MapSceneBuildResult(MapSceneSnapshot? Scene, string? UnavailableReason)
{
    public bool IsAvailable => Scene is not null;
}

/// <summary>Adapts the existing map read model into the one scene shared by every renderer.</summary>
/// <remarks>
/// The old overlay model is intentionally accepted only at this boundary. New feature adapters
/// add typed scene objects directly, which prevents a route, a teammate, and a user mark from
/// all becoming an indistinguishable coloured point before the tablet receives them.
/// </remarks>
public sealed class MapSceneAssembler
{
    private static readonly MapSceneLayerId LabelsLayer = new("labels");
    private static readonly MapSceneLayerId ExtractsLayer = new("extracts");
    private static readonly MapSceneLayerId SpawnsLayer = new("spawns");
    private static readonly MapSceneLayerId KeysLayer = new("keys");
    private static readonly MapSceneLayerId SwitchesLayer = new("switches");
    private static readonly MapSceneLayerId QuestsLayer = new("quest-objectives");

    public MapSceneBuildResult Build(MapSceneBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RenderModel);
        ArgumentNullException.ThrowIfNull(request.RequestedView);
        ArgumentNullException.ThrowIfNull(request.RequestedView.Layers);
        ArgumentNullException.ThrowIfNull(request.LegacyElements);
        ArgumentNullException.ThrowIfNull(request.AdditionalLayers);
        ArgumentNullException.ThrowIfNull(request.AdditionalObjects);
        ArgumentNullException.ThrowIfNull(request.Assets);

        var assets = request.Assets.Where(asset => asset.IsRenderable).ToArray();
        var hasPlan = assets.Any(asset => asset.Kind is MapSceneAssetKind.Background2D or MapSceneAssetKind.Floor2D);
        if (!request.RenderModel.CanRender || !hasPlan)
        {
            return new(null, "A reviewed, hashed 2D map asset is not available. The spatial scene is unavailable.");
        }

        var layers = CreateLayers(request.RenderModel).Concat(request.AdditionalLayers).ToArray();
        var legacyObjects = request.LegacyElements
            .Where(item => CanAdaptWithoutLosingMeaning(item.Element))
            .Select(item => Adapt(item, request.RenderModel.Floors));
        var objects = legacyObjects.Concat(request.AdditionalObjects).ToArray();
        if (objects.GroupBy(item => item.Id).Any(group => group.Count() > 1))
        {
            return new(null, "Two map objects resolved to the same stable identity; the scene was withheld for review.");
        }

        var floorIds = request.RenderModel.Floors.Select(floor => floor.Id).ToArray();
        var capabilities = new MapSceneCapabilities(
            MapSceneCapability.Available,
            request.RenderModel.Floors.Count > 1
                ? MapSceneCapability.Available
                : MapSceneCapability.Unavailable("This map has no reviewed multi-floor plan."),
            assets.Any(asset => asset.Kind == MapSceneAssetKind.InteriorModel)
                ? MapSceneCapability.Available
                : MapSceneCapability.Unavailable("No reviewed interior model is available for this map."));
        var view = NormalizeView(request.RequestedView, request.RenderModel, layers, capabilities);

        return new(new MapSceneSnapshot(
            request.Revision,
            request.RenderModel.Location.Id,
            request.RenderModel.Variant.Key,
            request.TransformVersion,
            request.Bounds,
            floorIds,
            capabilities,
            view,
            layers,
            objects,
            assets), null);
    }

    private static IReadOnlyList<MapSceneLayer> CreateLayers(MapRenderModel model)
    {
        var layers = new List<MapSceneLayer>(model.Overlays.Count);
        for (var index = 0; index < model.Overlays.Count; index++)
        {
            var source = model.Overlays[index];
            // [#902] A layer nothing adapts objects onto is a switch that can never draw
            // anything, which is what the four V1 layers were in the Layers menu.
            if (!HasAdapter(source.Kind))
            {
                continue;
            }

            layers.Add(new(
                IdFor(source.Kind),
                source.Name,
                index,
                source.IsVisible));
        }

        return layers;
    }

    private static MapSceneViewState NormalizeView(
        MapSceneViewState requested,
        MapRenderModel model,
        IReadOnlyList<MapSceneLayer> layers,
        MapSceneCapabilities capabilities)
    {
        var mode = capabilities.Supports(requested.Mode) ? requested.Mode : MapSceneMode.Flat2D;
        var floorId = requested.SelectedFloorId;
        if (floorId is not null && !model.Floors.Any(floor => string.Equals(floor.Id, floorId, StringComparison.OrdinalIgnoreCase)))
        {
            floorId = model.SelectedFloor?.Id;
        }

        var states = layers
            .Select(layer => new MapSceneLayerState(
                layer.Id,
                requested.Layers.FirstOrDefault(state => state.LayerId == layer.Id)?.IsVisible ?? layer.IsVisibleByDefault))
            .ToArray();
        return new(mode, floorId, requested.Camera, states);
    }

    private static bool CanAdaptWithoutLosingMeaning(MapOverlayElement element) => HasAdapter(element.Layer);

    /// <summary>Whether catalog elements of this kind become scene objects, and so whether its layer is listed.</summary>
    public static bool HasAdapter(MapOverlayKind kind) => kind is
        MapOverlayKind.Labels or
        MapOverlayKind.Extracts or
        MapOverlayKind.Spawns or
        MapOverlayKind.Keys or
        MapOverlayKind.Switches or
        MapOverlayKind.QuestObjectives;

    private static MapSceneObject Adapt(
        MapSceneLegacyElement source,
        IReadOnlyList<MapFloorDefinition> floors)
    {
        var element = source.Element;
        var kind = element.Layer switch
        {
            MapOverlayKind.Labels => MapSceneObjectKind.Label,
            MapOverlayKind.Extracts when element.Label.EndsWith('→') => MapSceneObjectKind.Transit,
            MapOverlayKind.Extracts => MapSceneObjectKind.Extract,
            MapOverlayKind.Spawns => MapSceneObjectKind.SpawnArea,
            MapOverlayKind.Keys => MapSceneObjectKind.Lock,
            MapOverlayKind.Switches => MapSceneObjectKind.Switch,
            MapOverlayKind.QuestObjectives => MapSceneObjectKind.QuestObjective,
            _ => throw new ArgumentOutOfRangeException(nameof(element)),
        };
        var truth = element.Layer switch
        {
            MapOverlayKind.Spawns => MapSceneTruthKind.PotentialSpawn,
            MapOverlayKind.QuestObjectives => MapSceneTruthKind.PersonalPlan,
            _ => MapSceneTruthKind.StaticReference,
        };
        var point = new MapScenePoint(element.Position.X, element.Position.Y);
        var id = StableId(element);
        return new(
            id,
            IdFor(element.Layer),
            kind,
            truth,
            element.Label,
            element.Detail,
            MapSceneGeometry.At(point),
            FloorsFor(element, floors),
            source.Provenance,
            faction: element.Faction,
            offerState: source.OfferState,
            catalogId: element.CatalogId,
            extractRequirements: element.ExtractRequirements,
            mapSwitch: element.Switch);
    }

    private static IReadOnlyList<string> FloorsFor(
        MapOverlayElement element,
        IReadOnlyList<MapFloorDefinition> floors) =>
        FloorsForHeights(element.MinimumHeight, element.MaximumHeight, floors);

    /// <summary>
    /// The floors a thing that spans these heights is on. A single height is a point and must sit
    /// inside a floor's range; a span is on every floor it overlaps. No heights at all means it
    /// is on every floor, which is the empty answer.
    /// </summary>
    /// <remarks>
    /// Public so that every adapter that puts something with a height on the plan decides floors the
    /// same way the catalog's own elements do: a quest objective in Interchange's mall belongs on
    /// the floor its elevation says, and not on whichever the renderer happens to show.
    /// </remarks>
    public static IReadOnlyList<string> FloorsForHeights(
        double? minimumHeight,
        double? maximumHeight,
        IReadOnlyList<MapFloorDefinition> floors)
    {
        ArgumentNullException.ThrowIfNull(floors);
        if (minimumHeight is null && maximumHeight is null)
        {
            return [];
        }

        var pointHeight = minimumHeight is { } minimum && maximumHeight == minimum
            ? minimum
            : (double?)null;
        return floors
            .Where(floor => floor.Extents.Count == 0 || floor.Extents.Any(extent => pointHeight is { } height
                ? (extent.MinimumHeight is null || height >= extent.MinimumHeight) &&
                  (extent.MaximumHeight is null || height < extent.MaximumHeight)
                : (extent.MinimumHeight is null || maximumHeight is null || maximumHeight > extent.MinimumHeight) &&
                  (extent.MaximumHeight is null || minimumHeight is null || minimumHeight < extent.MaximumHeight)))
            .Select(floor => floor.Id)
            .ToArray();
    }

    private static MapSceneObjectId StableId(MapOverlayElement element)
    {
        var canonical = string.Join('|',
            element.Layer.ToString(),
            element.Label,
            element.Faction.ToString(),
            element.Position.X.ToString("R", CultureInfo.InvariantCulture),
            element.Position.Y.ToString("R", CultureInfo.InvariantCulture),
            element.MinimumHeight?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
            element.MaximumHeight?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new($"catalog:{hash}");
    }

    public static MapSceneLayerId IdFor(MapOverlayKind kind) => kind switch
    {
        MapOverlayKind.Labels => LabelsLayer,
        MapOverlayKind.Extracts => ExtractsLayer,
        MapOverlayKind.Spawns => SpawnsLayer,
        MapOverlayKind.Keys => KeysLayer,
        MapOverlayKind.Switches => SwitchesLayer,
        MapOverlayKind.QuestObjectives => QuestsLayer,
        MapOverlayKind.CompanionMarkers => new("companion-markers"),
        MapOverlayKind.Routes => new("routes"),
        MapOverlayKind.RiskAndTraffic => new("risk-traffic"),
        MapOverlayKind.Filters => new("filters"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
