using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>The rectangle of map units the artwork covers, in the desktop's own projection.</summary>
public sealed record TabletMapPlan(double MinimumX, double MinimumY, double MaximumX, double MaximumY);

/// <summary>
/// The reviewed artwork the tablet draws, identified by the same content hash the desktop's scene
/// asset carries. The bytes themselves are fetched separately; this only says which ones.
/// </summary>
public sealed record TabletMapArtwork(string MediaType, string ContentSha256, int PixelWidth, int PixelHeight);

/// <summary>
/// ADR 0015's reviewed-asset record, carried to the tablet so the attribution is visible where the
/// artwork is drawn rather than only on the desktop that fetched it.
/// </summary>
public sealed record TabletMapAttribution(
    string Text,
    string SourceUri,
    string LicenseUri,
    string ContentSha256,
    string ReviewStatus);

public sealed record TabletMapLayer(string Id, string Name, int ZIndex, bool IsVisible);

/// <summary>
/// One thing on the map, already in plan units. <see cref="Points"/> is flat x,y pairs: one pair
/// for a point, several for a line or an area.
/// </summary>
public sealed record TabletMapObject(
    string Id,
    string LayerId,
    string Kind,
    string Truth,
    string Label,
    string? Detail,
    IReadOnlyList<double> Points,
    IReadOnlyList<string> FloorIds,
    double? HeadingDegrees,
    bool IsEstimate);

/// <summary>
/// One answer to a lookup the tablet asked the desktop to run.
/// </summary>
/// <remarks>
/// [V2 rough package 34] #417 sent the query to the desktop and left the answers there, which is
/// half a feature: the person is holding the tablet. The desktop resolves them because it has the
/// catalogue and the prices; the tablet only draws them.
/// </remarks>
public sealed record TabletSearchResult(
    string Id,
    string Name,
    string ShortName,
    long? FleaRoubles,
    long? TraderRoubles);

/// <summary>The lookup the desktop is currently showing, and what it found.</summary>
public sealed record TabletSearch(string Query, IReadOnlyList<TabletSearchResult> Results);

/// <summary>What the desktop is looking at: the view a following tablet mirrors.</summary>
public sealed record TabletMapView(
    string? FloorId,
    double CenterX,
    double CenterY,
    double Zoom,
    string? SelectionKind,
    string? SelectionId);

/// <summary>
/// Everything a paired tablet needs to draw the desktop's map: the plan rectangle, which artwork
/// covers it, its attribution, and every object already projected into the same rectangle.
/// </summary>
/// <remarks>
/// [V2 rough package 24, #407] The tablet used to plot world coordinates on an empty canvas and
/// say so ("A schematic, not the map"), which is not something a person can navigate by. It now
/// draws the desktop's own scene: same <c>MapSceneSnapshot</c>, same bounds from
/// <c>MapPlanProjection</c>, same object positions. Because both sides are handed one rectangle
/// and coordinates already inside it, a mark cannot land in a different place on the tablet than
/// it does on the desktop — there is no second projection to disagree with.
///
/// A map with no reviewed plan says so in <see cref="Message"/> and carries no artwork; the tablet
/// shows the message rather than an empty canvas with dots on it.
/// </remarks>
public sealed record TabletMapSurface(
    long Revision,
    string MapId,
    string MapName,
    string VariantKey,
    string TransformVersion,
    TabletMapPlan? Plan,
    TabletMapArtwork? Artwork,
    IReadOnlyList<TabletMapAttribution> Attribution,
    IReadOnlyList<string> FloorIds,
    IReadOnlyList<TabletMapLayer> Layers,
    IReadOnlyList<TabletMapObject> Objects,
    TabletMapView View,
    TabletSearch? Search,
    string? Message,
    DateTimeOffset PublishedUtc);

/// <summary>
/// Builds the tablet's surface from the desktop's assembled scene, so the two are the same scene
/// rather than two models of one.
/// </summary>
public static class TabletMapSurfaceBuilder
{
    /// <summary>Enough for every object a real map carries, and a bound a hostile scene cannot exceed.</summary>
    public const int MaximumObjects = 2000;

    /// <summary>A line or an area past this many points is drawn from a bounded sample of it.</summary>
    public const int MaximumPointsPerObject = 64;

    public static TabletMapSurface Build(
        MapSceneSnapshot scene,
        string mapName,
        TabletMapArtwork? artwork,
        WorkspaceProjection? workspace,
        TabletSearch? search,
        string? message,
        DateTimeOffset publishedUtc)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapName);

        var plan = new TabletMapPlan(
            scene.Bounds.MinimumX,
            scene.Bounds.MinimumY,
            scene.Bounds.MaximumX,
            scene.Bounds.MaximumY);
        var visible = scene.View.Layers.Count == 0
            ? null
            : scene.View.Layers.ToDictionary(state => state.LayerId.Value, state => state.IsVisible, StringComparer.Ordinal);
        var layers = scene.Layers
            .Select(layer => new TabletMapLayer(
                layer.Id.Value,
                layer.Name,
                layer.ZIndex,
                visible is not null && visible.TryGetValue(layer.Id.Value, out var isVisible)
                    ? isVisible
                    : layer.IsVisibleByDefault))
            .ToArray();
        // Loot goes last so the cap trims it and nothing else. A loot-heavy map can hold thousands
        // of potential spawns, the scene lists them before the player's marks, position and quest
        // objectives, and a plain Take() would have dropped those instead. The sort is stable, so
        // everything else keeps the order the desktop drew it in.
        var objects = scene.Objects
            .OrderBy(item => IsBulk(item.Kind) ? 1 : 0)
            .Take(MaximumObjects)
            .Select(ToTabletObject)
            .ToArray();

        // The camera the desktop is actually showing. Its centre is in plan units already, the
        // same ones every object above is in, so a following tablet needs no conversion.
        var view = new TabletMapView(
            scene.View.SelectedFloorId,
            scene.View.Camera.CenterX,
            scene.View.Camera.CenterY,
            scene.View.Camera.Zoom,
            workspace?.Selection?.Kind.ToString(),
            workspace?.Selection?.ReferenceId);

        var background = scene.Assets.FirstOrDefault(asset => asset.Kind == MapSceneAssetKind.Background2D);
        var attribution = scene.Assets
            .Where(asset => asset.Kind == MapSceneAssetKind.Background2D)
            .Select(asset => new TabletMapAttribution(
                asset.Attribution,
                asset.SourceUri.AbsoluteUri,
                asset.LicenseUri.AbsoluteUri,
                asset.ContentSha256,
                asset.ReviewStatus.ToString()))
            .ToArray();

        // Artwork is only ever claimed for a reviewed asset the desktop is itself drawing. A scene
        // whose background is missing or unreviewed carries the plan and the message, and the
        // tablet says the map has no reviewed plan instead of drawing coordinates on nothing.
        //
        // The two content hashes here are different facts and both are kept: the attribution's is
        // the reviewed upstream asset's (ADR 0015 provenance), and the artwork's is of the exact
        // bytes sent to the tablet, which is what the relay verifies on upload and what lets a
        // tablet keep a picture it already has.
        var reviewed = background is { ReviewStatus: MapSceneAssetReviewStatus.Reviewed } && artwork is not null;

        return new TabletMapSurface(
            scene.Revision,
            scene.LocationId,
            mapName,
            scene.VariantKey,
            scene.TransformVersion,
            plan,
            reviewed ? artwork : null,
            attribution,
            scene.FloorIds,
            layers,
            objects,
            view,
            search,
            reviewed ? message : message ?? "This map has no reviewed 2D plan yet.",
            publishedUtc);
    }

    /// <summary>The kinds a map can hold thousands of, which are the ones to trim first.</summary>
    private static bool IsBulk(MapSceneObjectKind kind) =>
        kind is MapSceneObjectKind.LootSpawn or MapSceneObjectKind.LootContainer;

    private static TabletMapObject ToTabletObject(MapSceneObject item)
    {
        var points = item.Geometry.Points;
        var step = points.Count <= MaximumPointsPerObject ? 1 : (int)Math.Ceiling(points.Count / (double)MaximumPointsPerObject);
        var flattened = new List<double>(Math.Min(points.Count, MaximumPointsPerObject) * 2);
        for (var index = 0; index < points.Count; index += step)
        {
            flattened.Add(points[index].X);
            flattened.Add(points[index].Y);
        }

        return new TabletMapObject(
            item.Id.Value,
            item.LayerId.Value,
            item.Kind.ToString(),
            item.Truth.ToString(),
            item.Label,
            item.Detail,
            flattened,
            item.FloorIds,
            item.HeadingDegrees,
            item.Truth == MapSceneTruthKind.HistoricalEstimate);
    }
}

/// <summary>The one JSON shape the desktop writes and the tablet page reads.</summary>
public static class TabletMapSurfaceJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static byte[] Serialize(TabletMapSurface surface) =>
        JsonSerializer.SerializeToUtf8Bytes(surface, Options);

    public static TabletMapSurface? Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize<TabletMapSurface>(utf8Json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
