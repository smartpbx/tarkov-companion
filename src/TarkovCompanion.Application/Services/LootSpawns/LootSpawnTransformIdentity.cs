using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.Application.Services.LootSpawns;

/// <summary>
/// The one transform identity a loot-spawn publication and the map scene it is drawn on share.
/// </summary>
/// <remarks>
/// [Issue 563] The normalizer stamped every snapshot with this hash while the Raid cockpit named
/// its scene after the bare variant key ("default"), so the layer service refused every snapshot
/// as "Transform mismatch" and "High-value loot only" showed nothing on any PC with a complete
/// publication. Both sides now compute it here, from the same catalog variant, so they cannot
/// drift apart again. Changing the canonical text changes every identity and orphans every cached
/// publication until its next refresh.
/// </remarks>
public static class LootSpawnTransformIdentity
{
    public static string For(string mapId, MapVariant variant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentNullException.ThrowIfNull(variant);
        var transform = variant.Transform;
        var bounds = variant.Bounds;
        var canonical = string.Join('|',
            mapId,
            variant.Key,
            Number(transform?.ScaleX),
            Number(transform?.OffsetX),
            Number(transform?.ScaleY),
            Number(transform?.OffsetY),
            Number(transform?.RotationDegrees),
            Number(bounds?.First.X),
            Number(bounds?.First.Y),
            Number(bounds?.Second.X),
            Number(bounds?.Second.Y),
            string.Join(';', variant.Floors.Select(floor => string.Join(',',
                floor.Id,
                floor.Extents.Count.ToString(CultureInfo.InvariantCulture),
                string.Join(':', floor.Extents.Select(ExtentIdentity))))));
        return $"tarkov-dev-map-v1:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
    }

    private static string ExtentIdentity(MapLayerExtent extent) => string.Join('/',
        Number(extent.MinimumHeight),
        Number(extent.MaximumHeight),
        string.Join(':', extent.Bounds.Select(bound => string.Join(',',
            Number(bound.First.X),
            Number(bound.First.Y),
            Number(bound.Second.X),
            Number(bound.Second.Y)))));

    private static string Number(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? "unknown";
}
