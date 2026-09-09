using System.Text.Json;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>Reads the app's normalized cached map document while tolerating unknown JSON fields.</summary>
public static class MapCacheJsonParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static MapDefinition Parse(string json, DataProvenance provenance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(provenance);
        MapDocument document;
        try
        {
            document = JsonSerializer.Deserialize<MapDocument>(json, Options)
                ?? throw new InvalidDataException("Map cache document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Map cache document is not valid JSON.", exception);
        }

        if (string.IsNullOrWhiteSpace(document.Id) || string.IsNullOrWhiteSpace(document.Name))
        {
            throw new InvalidDataException("Map cache document requires non-empty id and name fields.");
        }

        var floors = (document.Floors ?? [])
            .Select(floor => new MapFloorLayer(
                Required(floor.Id, "floor id"),
                Required(floor.Name, "floor name"),
                floor.MinHeight,
                floor.MaxHeight,
                floor.AssetReference))
            .ToArray();
        var extracts = (document.Extracts ?? [])
            .Select(extract => new MapExtract(
                Required(extract.Id, "extract id"),
                document.Id,
                Required(extract.Name, "extract name"),
                extract.X is not null && extract.Y is not null ? new MapPoint(extract.X.Value, extract.Y.Value) : null,
                extract.Conditions,
                provenance))
            .ToArray();
        MapTransformConfig? transform = null;
        if (document.Transform is { } sourceTransform)
        {
            transform = new(
                document.Id,
                sourceTransform.WorldMinX,
                sourceTransform.WorldMaxX,
                sourceTransform.WorldMinZ,
                sourceTransform.WorldMaxZ,
                sourceTransform.VisualWidth,
                sourceTransform.VisualHeight,
                sourceTransform.RotationDegrees,
                sourceTransform.FlipX,
                sourceTransform.FlipY,
                provenance);
        }

        return new(
            document.Id,
            document.Name,
            Minutes(document.PmcRaidDurationMinutes),
            Minutes(document.ScavRaidDurationMinutes),
            floors,
            extracts,
            transform,
            provenance);
    }

    private static string Required(string? value, string field) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Map cache document requires a non-empty {field}.");

    private static TimeSpan? Minutes(double? value) => value is > 0 and <= 240 ? TimeSpan.FromMinutes(value.Value) : null;

    private sealed class MapDocument
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public double? PmcRaidDurationMinutes { get; init; }

        public double? ScavRaidDurationMinutes { get; init; }

        public MapFloorDocument[]? Floors { get; init; }

        public MapExtractDocument[]? Extracts { get; init; }

        public MapTransformDocument? Transform { get; init; }
    }

    private sealed class MapFloorDocument
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public double MinHeight { get; init; }

        public double MaxHeight { get; init; }

        public string? AssetReference { get; init; }
    }

    private sealed class MapExtractDocument
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public double? X { get; init; }

        public double? Y { get; init; }

        public string? Conditions { get; init; }
    }

    private sealed class MapTransformDocument
    {
        public double WorldMinX { get; init; }

        public double WorldMaxX { get; init; }

        public double WorldMinZ { get; init; }

        public double WorldMaxZ { get; init; }

        public double VisualWidth { get; init; }

        public double VisualHeight { get; init; }

        public double RotationDegrees { get; init; }

        public bool FlipX { get; init; }

        public bool FlipY { get; init; }
    }
}
