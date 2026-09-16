using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.Application.Services.Maps.Scene;

public sealed record MapSceneBuildRequest(
    long Revision,
    MapRenderModel RenderModel,
    MapCatalogProvenance CatalogProvenance,
    MapSceneBounds Bounds,
    string TransformVersion,
    MapSceneViewState RequestedView,
    IReadOnlyList<MapSceneLayer> AdditionalLayers,
    IReadOnlyList<MapSceneObject> AdditionalObjects,
    IReadOnlyList<MapSceneAsset> Assets);

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
    private static readonly MapSceneLayerId QuestsLayer = new("quest-objectives");

    public MapSceneBuildResult Build(MapSceneBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RenderModel);
        ArgumentNullException.ThrowIfNull(request.CatalogProvenance);
        ArgumentNullException.ThrowIfNull(request.RequestedView);
        ArgumentNullException.ThrowIfNull(request.RequestedView.Layers);
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
        var provenance = new DataProvenance(
            request.CatalogProvenance.SourceUri.AbsoluteUri,
            request.CatalogProvenance.RetrievedUtc,
            Reference: request.CatalogProvenance.ContentSha256,
            Confidence: Confidence.Certain);
        var legacyObjects = request.RenderModel.OverlayElements
            .Where(CanAdaptWithoutLosingMeaning)
            .Select(element => Adapt(element, request.RenderModel.Floors, provenance))
            .GroupBy(item => item.Id)
            .Select(group => group.First());
        var objects = legacyObjects.Concat(request.AdditionalObjects).ToArray();

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
            layers.Add(new(
                IdFor(source.Kind),
                source.Name,
                index,
                source.IsVisible,
                source.Kind != MapOverlayKind.Filters));
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

    private static bool CanAdaptWithoutLosingMeaning(MapOverlayElement element) => element.Layer is
        MapOverlayKind.Labels or
        MapOverlayKind.Extracts or
        MapOverlayKind.Spawns or
        MapOverlayKind.Keys or
        MapOverlayKind.QuestObjectives;

    private static MapSceneObject Adapt(
        MapOverlayElement element,
        IReadOnlyList<MapFloorDefinition> floors,
        DataProvenance provenance)
    {
        var kind = element.Layer switch
        {
            MapOverlayKind.Labels => MapSceneObjectKind.Label,
            MapOverlayKind.Extracts when element.Label.EndsWith('→') => MapSceneObjectKind.Transit,
            MapOverlayKind.Extracts => MapSceneObjectKind.Extract,
            MapOverlayKind.Spawns => MapSceneObjectKind.SpawnArea,
            MapOverlayKind.Keys => MapSceneObjectKind.Lock,
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
            provenance);
    }

    private static IReadOnlyList<string> FloorsFor(
        MapOverlayElement element,
        IReadOnlyList<MapFloorDefinition> floors)
    {
        if (element.MinimumHeight is null && element.MaximumHeight is null)
        {
            return [];
        }

        return floors
            .Where(floor => floor.Extents.Count == 0 || floor.Extents.Any(extent =>
                (extent.MinimumHeight is null || element.MaximumHeight is null || element.MaximumHeight >= extent.MinimumHeight) &&
                (extent.MaximumHeight is null || element.MinimumHeight is null || element.MinimumHeight < extent.MaximumHeight)))
            .Select(floor => floor.Id)
            .ToArray();
    }

    private static MapSceneObjectId StableId(MapOverlayElement element)
    {
        var canonical = string.Join('|',
            element.Layer.ToString(),
            element.Label,
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
        MapOverlayKind.QuestObjectives => QuestsLayer,
        MapOverlayKind.CompanionMarkers => new("companion-markers"),
        MapOverlayKind.Routes => new("routes"),
        MapOverlayKind.RiskAndTraffic => new("risk-traffic"),
        MapOverlayKind.Filters => new("filters"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
