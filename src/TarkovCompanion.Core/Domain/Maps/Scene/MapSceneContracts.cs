using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Core.Domain.Maps.Scene;

public enum MapSceneMode
{
    Flat2D,
    FloorStack2D,
    Interior3D,
}

public enum MapSceneGeometryKind
{
    Point,
    Line,
    Area,
    Region,
}

public enum MapSceneObjectKind
{
    Extract,
    Transit,
    SpawnArea,
    LootSpawn,
    LootContainer,
    Hazard,
    Lock,
    QuestObjective,
    Route,
    Risk,
    Traffic,
    LastKnownPosition,
    TeammateLastKnown,
    Ping,
    Waypoint,
    Label,
    Custom,
}

/// <summary>What an object is asserting, independently of how a renderer styles it.</summary>
/// <remarks>
/// Keeping this in the scene contract prevents a potential spawn or historical estimate from
/// becoming indistinguishable from an observation when desktop and tablet use different renderers.
/// There is deliberately no live-enemy state in this vocabulary.
/// </remarks>
public enum MapSceneTruthKind
{
    StaticReference,
    PotentialSpawn,
    LocalLastKnown,
    TeamSharedLastKnown,
    HistoricalEstimate,
    PersonalPlan,
    UserAuthored,
}

public enum MapSceneAssetReviewStatus
{
    Reviewed,
    Unavailable,
}

public enum MapSceneAssetKind
{
    Background2D,
    Floor2D,
    InteriorModel,
}

public readonly record struct MapSceneLayerId
{
    public MapSceneLayerId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 96)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A scene-layer ID cannot exceed 96 characters.");
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct MapSceneObjectId
{
    public MapSceneObjectId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 160)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A scene-object ID cannot exceed 160 characters.");
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct MapSceneAssetId
{
    public MapSceneAssetId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 160)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A scene-asset ID cannot exceed 160 characters.");
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct MapScenePoint
{
    public MapScenePoint(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Scene coordinates must be finite.");
        }

        X = x;
        Y = y;
    }

    public double X { get; }

    public double Y { get; }
}

public readonly record struct MapSceneBounds
{
    public MapSceneBounds(double minimumX, double minimumY, double maximumX, double maximumY)
    {
        if (!double.IsFinite(minimumX) || !double.IsFinite(minimumY) ||
            !double.IsFinite(maximumX) || !double.IsFinite(maximumY) ||
            maximumX <= minimumX || maximumY <= minimumY)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumX), "Scene bounds must be finite and non-empty.");
        }

        MinimumX = minimumX;
        MinimumY = minimumY;
        MaximumX = maximumX;
        MaximumY = maximumY;
    }

    public double MinimumX { get; }

    public double MinimumY { get; }

    public double MaximumX { get; }

    public double MaximumY { get; }

    public double Width => MaximumX - MinimumX;

    public double Height => MaximumY - MinimumY;

    public bool Contains(MapScenePoint point) =>
        point.X >= MinimumX && point.X <= MaximumX && point.Y >= MinimumY && point.Y <= MaximumY;
}

public sealed record MapSceneGeometry
{
    public MapSceneGeometry(MapSceneGeometryKind kind, IReadOnlyList<MapScenePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var copy = points.ToArray();
        var minimum = kind switch
        {
            MapSceneGeometryKind.Point => 1,
            MapSceneGeometryKind.Line => 2,
            MapSceneGeometryKind.Area or MapSceneGeometryKind.Region => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (copy.Length < minimum || kind == MapSceneGeometryKind.Point && copy.Length != 1)
        {
            throw new ArgumentException($"{kind} geometry requires {(kind == MapSceneGeometryKind.Point ? "exactly" : "at least")} {minimum} point(s).", nameof(points));
        }

        if (kind is MapSceneGeometryKind.Area or MapSceneGeometryKind.Region && Math.Abs(SignedArea(copy)) < 1e-9)
        {
            throw new ArgumentException("Area and region geometry must enclose a non-zero area.", nameof(points));
        }

        Kind = kind;
        Points = copy;
    }

    public MapSceneGeometryKind Kind { get; }

    public IReadOnlyList<MapScenePoint> Points { get; }

    public MapSceneBounds Bounds
    {
        get
        {
            var minimumX = Points.Min(point => point.X);
            var minimumY = Points.Min(point => point.Y);
            var maximumX = Points.Max(point => point.X);
            var maximumY = Points.Max(point => point.Y);

            // Points and axis-aligned lines have no area. A tiny deterministic envelope lets
            // renderers index them without pretending the source supplied a larger shape.
            var (boundedMinimumX, boundedMaximumX) = Expand(minimumX, maximumX);
            var (boundedMinimumY, boundedMaximumY) = Expand(minimumY, maximumY);
            return new(
                boundedMinimumX,
                boundedMinimumY,
                boundedMaximumX,
                boundedMaximumY);
        }
    }

    public static MapSceneGeometry At(MapScenePoint point) => new(MapSceneGeometryKind.Point, [point]);

    private static double SignedArea(IReadOnlyList<MapScenePoint> points)
    {
        var area = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var next = (index + 1) % points.Count;
            area += (points[index].X * points[next].Y) - (points[next].X * points[index].Y);
        }

        return area / 2d;
    }

    private static (double Minimum, double Maximum) Expand(double minimum, double maximum)
    {
        if (maximum > minimum)
        {
            return (minimum, maximum);
        }

        var next = Math.BitIncrement(maximum);
        return double.IsFinite(next)
            ? (minimum, next)
            : (Math.BitDecrement(minimum), maximum);
    }
}

public sealed record MapSceneEstimateMetadata
{
    public MapSceneEstimateMetadata(
        DateTimeOffset observedFromUtc,
        DateTimeOffset dataThroughUtc,
        DateTimeOffset generatedUtc,
        string coverage,
        string calibration,
        string transformVersion,
        string modelVersion)
    {
        RequireUtc(observedFromUtc, nameof(observedFromUtc));
        RequireUtc(dataThroughUtc, nameof(dataThroughUtc));
        RequireUtc(generatedUtc, nameof(generatedUtc));
        if (dataThroughUtc < observedFromUtc || generatedUtc < dataThroughUtc)
        {
            throw new ArgumentException("Estimate timestamps must progress from observation through generation.");
        }

        ObservedFromUtc = observedFromUtc;
        DataThroughUtc = dataThroughUtc;
        GeneratedUtc = generatedUtc;
        Coverage = Required(coverage, nameof(coverage), 256);
        Calibration = Required(calibration, nameof(calibration), 256);
        TransformVersion = Required(transformVersion, nameof(transformVersion), 128);
        ModelVersion = Required(modelVersion, nameof(modelVersion), 128);
    }

    public DateTimeOffset ObservedFromUtc { get; }

    public DateTimeOffset DataThroughUtc { get; }

    public DateTimeOffset GeneratedUtc { get; }

    public string Coverage { get; }

    public string Calibration { get; }

    public string TransformVersion { get; }

    public string ModelVersion { get; }

    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Estimate timestamps must be UTC.", name);
        }
    }

    private static string Required(string value, string name, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value.Length <= maximumLength
            ? value
            : throw new ArgumentOutOfRangeException(name, $"The value cannot exceed {maximumLength} characters.");
    }
}

public sealed record MapSceneLayer(
    MapSceneLayerId Id,
    string Name,
    int ZIndex,
    bool IsVisibleByDefault,
    bool IsAvailableInList = true);

public sealed record MapSceneObject
{
    public MapSceneObject(
        MapSceneObjectId id,
        MapSceneLayerId layerId,
        MapSceneObjectKind kind,
        MapSceneTruthKind truth,
        string label,
        string? detail,
        MapSceneGeometry geometry,
        IReadOnlyList<string> floorIds,
        DataProvenance provenance,
        MapSceneEstimateMetadata? estimate = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(floorIds);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (label.Length > 256 || detail?.Length > 2048)
        {
            throw new ArgumentOutOfRangeException(nameof(label), "Scene labels and details must be bounded.");
        }

        if (truth == MapSceneTruthKind.HistoricalEstimate != (estimate is not null))
        {
            throw new ArgumentException("Historical estimates require estimate metadata, and only estimates may carry it.", nameof(estimate));
        }

        if (string.IsNullOrWhiteSpace(provenance.Source) || provenance.ObservedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Scene-object provenance requires a source and UTC observation time.", nameof(provenance));
        }

        if (truth == MapSceneTruthKind.HistoricalEstimate && provenance.Confidence is null)
        {
            throw new ArgumentException("Historical estimates require source confidence.", nameof(provenance));
        }

        Id = id;
        LayerId = layerId;
        Kind = kind;
        Truth = truth;
        Label = label;
        Detail = detail;
        Geometry = geometry;
        FloorIds = floorIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Provenance = provenance;
        Estimate = estimate;
    }

    public MapSceneObjectId Id { get; }

    public MapSceneLayerId LayerId { get; }

    public MapSceneObjectKind Kind { get; }

    public MapSceneTruthKind Truth { get; }

    public string Label { get; }

    public string? Detail { get; }

    public MapSceneGeometry Geometry { get; }

    public IReadOnlyList<string> FloorIds { get; }

    public DataProvenance Provenance { get; }

    public MapSceneEstimateMetadata? Estimate { get; }
}

public sealed record MapSceneAsset
{
    public MapSceneAsset(
        MapSceneAssetId id,
        MapSceneAssetKind kind,
        Uri sourceUri,
        Uri licenseUri,
        string contentSha256,
        string attribution,
        string mapVersion,
        string gameVersion,
        MapSceneAssetReviewStatus reviewStatus,
        DateTimeOffset reviewedUtc)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        ArgumentNullException.ThrowIfNull(licenseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(reviewStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "Scene asset kind and review status must be known.");
        }
        if (!sourceUri.IsAbsoluteUri || !licenseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("Scene asset source and licence URIs must be absolute.");
        }

        if (contentSha256.Length != 64 || contentSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Scene assets require a SHA-256 content hash.", nameof(contentSha256));
        }

        if (reviewedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Asset review timestamps must be UTC.", nameof(reviewedUtc));
        }

        Id = id;
        Kind = kind;
        SourceUri = sourceUri;
        LicenseUri = licenseUri;
        ContentSha256 = contentSha256.ToLowerInvariant();
        Attribution = Required(attribution, nameof(attribution));
        MapVersion = Required(mapVersion, nameof(mapVersion));
        GameVersion = Required(gameVersion, nameof(gameVersion));
        ReviewStatus = reviewStatus;
        ReviewedUtc = reviewedUtc;
    }

    public MapSceneAssetId Id { get; }

    public MapSceneAssetKind Kind { get; }

    public Uri SourceUri { get; }

    public Uri LicenseUri { get; }

    public string ContentSha256 { get; }

    public string Attribution { get; }

    public string MapVersion { get; }

    public string GameVersion { get; }

    public MapSceneAssetReviewStatus ReviewStatus { get; }

    public DateTimeOffset ReviewedUtc { get; }

    public bool IsRenderable => ReviewStatus == MapSceneAssetReviewStatus.Reviewed;

    private static string Required(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value.Length <= 512
            ? value
            : throw new ArgumentOutOfRangeException(name, "Scene asset metadata cannot exceed 512 characters.");
    }
}

public sealed record MapSceneCapability(bool IsAvailable, string? UnavailableReason = null)
{
    public static MapSceneCapability Available { get; } = new(true);

    public static MapSceneCapability Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(false, reason);
    }
}

public sealed record MapSceneCapabilities(
    MapSceneCapability Flat2D,
    MapSceneCapability FloorStack2D,
    MapSceneCapability Interior3D)
{
    public bool Supports(MapSceneMode mode) => mode switch
    {
        MapSceneMode.Flat2D => Flat2D.IsAvailable,
        MapSceneMode.FloorStack2D => FloorStack2D.IsAvailable,
        MapSceneMode.Interior3D => Interior3D.IsAvailable,
        _ => false,
    };
}

public readonly record struct MapSceneCamera
{
    public MapSceneCamera(double centerX, double centerY, double zoom, double bearingDegrees, double pitchDegrees)
    {
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY) ||
            !double.IsFinite(zoom) || zoom is < 0.05 or > 64 ||
            !double.IsFinite(bearingDegrees) || bearingDegrees is < 0 or >= 360 ||
            !double.IsFinite(pitchDegrees) || pitchDegrees is < 0 or > 75)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), "Scene camera values are outside their supported bounds.");
        }

        CenterX = centerX;
        CenterY = centerY;
        Zoom = zoom;
        BearingDegrees = bearingDegrees;
        PitchDegrees = pitchDegrees;
    }

    public double CenterX { get; }

    public double CenterY { get; }

    public double Zoom { get; }

    public double BearingDegrees { get; }

    public double PitchDegrees { get; }
}

public sealed record MapSceneLayerState(MapSceneLayerId LayerId, bool IsVisible);

public sealed record MapSceneViewState(
    MapSceneMode Mode,
    string? SelectedFloorId,
    MapSceneCamera Camera,
    IReadOnlyList<MapSceneLayerState> Layers);

public sealed record MapSceneListEntry(
    MapSceneObjectId Id,
    MapSceneObjectKind Kind,
    string Label,
    string? Detail,
    MapSceneTruthKind Truth,
    DataProvenance Provenance);

/// <summary>The single renderer-neutral scene consumed by desktop and paired clients.</summary>
public sealed record MapSceneSnapshot
{
    public MapSceneSnapshot(
        long revision,
        string locationId,
        string variantKey,
        string transformVersion,
        MapSceneBounds bounds,
        IReadOnlyList<string> floorIds,
        MapSceneCapabilities capabilities,
        MapSceneViewState view,
        IReadOnlyList<MapSceneLayer> layers,
        IReadOnlyList<MapSceneObject> objects,
        IReadOnlyList<MapSceneAsset> assets)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(transformVersion);
        ArgumentNullException.ThrowIfNull(floorIds);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(capabilities.Flat2D);
        ArgumentNullException.ThrowIfNull(capabilities.FloorStack2D);
        ArgumentNullException.ThrowIfNull(capabilities.Interior3D);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(view.Layers);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(assets);

        var layerCopy = layers.ToArray();
        var objectCopy = objects.ToArray();
        var assetCopy = assets.ToArray();
        if (layerCopy.Any(layer => string.IsNullOrWhiteSpace(layer.Id.Value) || string.IsNullOrWhiteSpace(layer.Name)) ||
            objectCopy.Any(item => string.IsNullOrWhiteSpace(item.Id.Value) || string.IsNullOrWhiteSpace(item.LayerId.Value)) ||
            assetCopy.Any(asset => string.IsNullOrWhiteSpace(asset.Id.Value)))
        {
            throw new ArgumentException("Scene layers, objects, and assets require non-empty identities and names.");
        }

        RequireUnique(layerCopy.Select(layer => layer.Id.Value), "scene layer");
        RequireUnique(objectCopy.Select(item => item.Id.Value), "scene object");
        RequireUnique(assetCopy.Select(asset => asset.Id.Value), "scene asset");
        RequireUnique(view.Layers.Select(item => item.LayerId.Value), "scene view-layer state");

        var layerIds = layerCopy.Select(layer => layer.Id).ToHashSet();
        if (objectCopy.Any(item => !layerIds.Contains(item.LayerId)) || view.Layers.Any(item => !layerIds.Contains(item.LayerId)))
        {
            throw new ArgumentException("Every scene object and view-layer state must reference a declared layer.");
        }

        var normalizedFloors = floorIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (view.SelectedFloorId is not null && !normalizedFloors.Contains(view.SelectedFloorId, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The selected floor must exist in the scene.", nameof(view));
        }

        if (objectCopy.Any(item => item.FloorIds.Any(floorId => !normalizedFloors.Contains(floorId, StringComparer.OrdinalIgnoreCase))))
        {
            throw new ArgumentException("Scene objects may reference only declared floors.", nameof(objects));
        }

        if (!capabilities.Supports(view.Mode))
        {
            throw new ArgumentException("The selected presentation mode is unavailable for this scene.", nameof(view));
        }

        if (assetCopy.Any(asset => !asset.IsRenderable))
        {
            throw new ArgumentException("Unavailable or unreviewed assets cannot enter a renderable scene.", nameof(assets));
        }

        Revision = revision;
        LocationId = locationId;
        VariantKey = variantKey;
        TransformVersion = transformVersion;
        Bounds = bounds;
        FloorIds = normalizedFloors;
        Capabilities = capabilities;
        View = view;
        Layers = layerCopy;
        Objects = objectCopy;
        Assets = assetCopy;
    }

    public long Revision { get; }

    public string LocationId { get; }

    public string VariantKey { get; }

    public string TransformVersion { get; }

    public MapSceneBounds Bounds { get; }

    public IReadOnlyList<string> FloorIds { get; }

    public MapSceneCapabilities Capabilities { get; }

    public MapSceneViewState View { get; }

    public IReadOnlyList<MapSceneLayer> Layers { get; }

    public IReadOnlyList<MapSceneObject> Objects { get; }

    public IReadOnlyList<MapSceneAsset> Assets { get; }

    public IReadOnlyList<MapSceneObject> VisibleObjects => Objects
        .Where(item => IsLayerVisible(item.LayerId) && IsOnSelectedFloor(item))
        .OrderBy(item => Layers.Single(layer => layer.Id == item.LayerId).ZIndex)
        .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Id.Value, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<MapSceneListEntry> ListEntries => VisibleObjects
        .Where(item => Layers.Single(layer => layer.Id == item.LayerId).IsAvailableInList)
        .Select(item => new MapSceneListEntry(item.Id, item.Kind, item.Label, item.Detail, item.Truth, item.Provenance))
        .ToArray();

    private bool IsLayerVisible(MapSceneLayerId layerId) =>
        View.Layers.FirstOrDefault(item => item.LayerId == layerId)?.IsVisible ??
        Layers.Single(layer => layer.Id == layerId).IsVisibleByDefault;

    private bool IsOnSelectedFloor(MapSceneObject item) =>
        View.SelectedFloorId is null || item.FloorIds.Count == 0 ||
        item.FloorIds.Contains(View.SelectedFloorId, StringComparer.OrdinalIgnoreCase);

    private static void RequireUnique(IEnumerable<string> values, string name)
    {
        if (values.GroupBy(value => value, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new ArgumentException($"Duplicate {name} IDs are not allowed.");
        }
    }
}
