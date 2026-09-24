using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Infrastructure.Maps;

public static class TarkovDevMapCatalogParser
{
    public static TarkovDevMapCatalog Parse(
        string json,
        Uri sourceUri,
        DateTimeOffset retrievedUtc,
        MapCatalogAvailability availability = MapCatalogAvailability.Current)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(sourceUri);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The tarkov.dev map catalog is not valid JSON.", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The tarkov.dev map catalog root must be an array.");
            }

            // The catalog tracks a live upstream file. Parsing it as one unit meant a single
            // unexpected value removed every map, so each location is isolated and the ones
            // that fail are reported instead of taking the rest down with them.
            var locations = new List<MapLocation>();
            var skipped = new List<string>();
            foreach (var locationElement in document.RootElement.EnumerateArray())
            {
                try
                {
                    locations.Add(ParseLocation(locationElement));
                }
                catch (Exception exception) when (exception is InvalidDataException
                                                  or InvalidOperationException
                                                  or FormatException
                                                  or OverflowException)
                {
                    skipped.Add(DescribeSkippedLocation(locationElement, exception));
                }
            }

            if (locations.Count == 0)
            {
                throw new InvalidDataException(
                    skipped.Count == 0
                        ? "The tarkov.dev map catalog does not contain any locations."
                        : $"No tarkov.dev map location could be parsed: {string.Join("; ", skipped)}");
            }

            var duplicateLocation = locations
                .GroupBy(location => location.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateLocation is not null)
            {
                throw new InvalidDataException($"The tarkov.dev map catalog contains duplicate location '{duplicateLocation.Key}'.");
            }

            var contentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            return new(
                locations,
                new(sourceUri, retrievedUtc.ToUniversalTime(), contentHash, availability))
            {
                SkippedLocations = skipped,
            };
        }
    }

    private static string DescribeSkippedLocation(JsonElement element, Exception exception)
    {
        var name = element.ValueKind == JsonValueKind.Object
            ? OptionalString(element, "normalizedName") ?? OptionalString(element, "name")
            : null;
        return $"'{name ?? "unnamed location"}' ({exception.Message})";
    }

    private static MapLocation ParseLocation(JsonElement element)
    {
        RequireObject(element, "location");
        var locationId = RequiredString(element, "normalizedName", "location");
        var name = OptionalString(element, "name") ?? MapDisplayName.FromId(locationId);
        if (!element.TryGetProperty("maps", out var mapsElement) || mapsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Map location '{locationId}' requires a maps array.");
        }

        var variants = mapsElement.EnumerateArray().Select(item => ParseVariant(locationId, item)).ToArray();
        var duplicateVariant = variants
            .GroupBy(variant => variant.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateVariant is not null)
        {
            throw new InvalidDataException($"Map location '{locationId}' contains duplicate variant '{duplicateVariant.Key}'.");
        }

        return new(
            locationId,
            OptionalScalarText(element, "id"),
            name,
            OptionalString(element, "description"),
            OptionalString(element, "primaryPath"),
            variants);
    }

    private static MapVariant ParseVariant(string locationId, JsonElement element)
    {
        RequireObject(element, $"variant for '{locationId}'");
        var key = RequiredString(element, "key", $"variant for '{locationId}'");
        var projectionName = RequiredString(element, "projection", $"map variant '{key}'");
        var projection = projectionName.ToUpperInvariant() switch
        {
            "INTERACTIVE" => MapProjectionKind.Interactive,
            "2D" => MapProjectionKind.TwoDimensional,
            "3D" => MapProjectionKind.ThreeDimensional,
            _ => MapProjectionKind.Unknown,
        };
        var heightRange = OptionalNumberPair(element, "heightRange", $"map variant '{key}'");
        var baseFloor = new MapFloorDefinition(
            "base",
            "Base",
            OptionalString(element, "svgLayer"),
            OptionalHttpsUri(element, "tilePath", $"map variant '{key}'"),
            true,
            [new(heightRange?.First, heightRange?.Second, [])]);
        var floors = new List<MapFloorDefinition> { baseFloor };
        if (element.TryGetProperty("layers", out var layersElement) && layersElement.ValueKind != JsonValueKind.Null)
        {
            if (layersElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Map variant '{key}' has an invalid layers value.");
            }

            floors.AddRange(layersElement.EnumerateArray().Select((layer, index) => ParseFloor(key, layer, index)));
        }

        var labels = Array.Empty<MapCatalogLabel>();
        if (element.TryGetProperty("labels", out var labelsElement) && labelsElement.ValueKind != JsonValueKind.Null)
        {
            if (labelsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Map variant '{key}' has an invalid labels value.");
            }

            labels = labelsElement.EnumerateArray().Select(label => ParseLabel(key, label)).ToArray();
        }

        var transformValues = OptionalNumberArray(element, "transform", 4, $"map variant '{key}'");
        var rotation = OptionalNumber(element, "coordinateRotation") ?? 0;
        var transform = transformValues is null
            ? null
            : new MapCatalogTransform(
                transformValues[0],
                transformValues[1],
                transformValues[2],
                transformValues[3],
                rotation);

        return new(
            locationId,
            key,
            projection,
            projectionName,
            OptionalString(element, "orientation"),
            OptionalString(element, "specific"),
            OptionalHttpsUri(element, "svgPath", $"map variant '{key}'"),
            OptionalHttpsUri(element, "tilePath", $"map variant '{key}'"),
            OptionalInteger(element, "tileSize") ?? 256,
            OptionalInteger(element, "minZoom"),
            OptionalInteger(element, "maxZoom"),
            OptionalBounds(element, "bounds", $"map variant '{key}'"),
            OptionalBounds(element, "svgBounds", $"map variant '{key}'"),
            transform,
            OptionalString(element, "svgLayer"),
            heightRange?.First,
            heightRange?.Second,
            OptionalString(element, "author"),
            OptionalUri(element, "authorLink", $"map variant '{key}'"),
            OptionalStrings(element, "altMaps", $"map variant '{key}'"),
            floors,
            labels);
    }

    private static MapFloorDefinition ParseFloor(string variantKey, JsonElement element, int index)
    {
        RequireObject(element, $"layer {index} for map variant '{variantKey}'");
        var name = RequiredString(element, "name", $"layer {index} for map variant '{variantKey}'");
        var extents = Array.Empty<MapLayerExtent>();
        if (element.TryGetProperty("extents", out var extentsElement) && extentsElement.ValueKind != JsonValueKind.Null)
        {
            if (extentsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Layer '{name}' for map variant '{variantKey}' has invalid extents.");
            }

            extents = extentsElement.EnumerateArray().Select(extent => ParseExtent(variantKey, name, extent)).ToArray();
        }

        return new(
            $"layer-{index}-{Slug(name)}",
            name,
            OptionalString(element, "svgLayer"),
            OptionalHttpsUri(element, "tilePath", $"layer '{name}' for map variant '{variantKey}'"),
            OptionalBoolean(element, "show") ?? false,
            extents);
    }

    private static MapLayerExtent ParseExtent(string variantKey, string layerName, JsonElement element)
    {
        RequireObject(element, $"extent in layer '{layerName}' for map variant '{variantKey}'");
        var height = OptionalNumberPair(element, "height", $"layer '{layerName}' for map variant '{variantKey}'");
        var bounds = Array.Empty<MapCatalogBounds>();
        if (element.TryGetProperty("bounds", out var boundsElement) && boundsElement.ValueKind != JsonValueKind.Null)
        {
            if (boundsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Layer '{layerName}' for map variant '{variantKey}' has invalid bounds.");
            }

            bounds = boundsElement.EnumerateArray()
                .Select(bound => ParseBounds(bound, $"layer '{layerName}' for map variant '{variantKey}'"))
                .ToArray();
        }

        return new(height?.First, height?.Second, bounds);
    }

    private static MapCatalogLabel ParseLabel(string variantKey, JsonElement element)
    {
        RequireObject(element, $"label for map variant '{variantKey}'");
        var position = RequiredNumberArray(element, "position", 2, $"label for map variant '{variantKey}'");
        return new(
            RequiredString(element, "text", $"label for map variant '{variantKey}'"),
            new(position[0], position[1]),
            OptionalNumber(element, "rotation") ?? 0,
            OptionalNumber(element, "size") ?? 100,
            OptionalNumber(element, "bottom"),
            OptionalNumber(element, "top"));
    }

    private static MapCatalogBounds? OptionalBounds(JsonElement parent, string property, string context) =>
        parent.TryGetProperty(property, out var element) && element.ValueKind != JsonValueKind.Null
            ? ParseBounds(element, context)
            : null;

    private static MapCatalogBounds ParseBounds(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{context} has invalid bounds.");
        }

        var points = element.EnumerateArray().ToArray();
        if (points.Length < 2)
        {
            throw new InvalidDataException($"{context} bounds require at least two points.");
        }

        var first = ParsePoint(points[0], context);
        var second = ParsePoint(points[1], context);
        var description = points.Length > 2 && points[2].ValueKind == JsonValueKind.String
            ? points[2].GetString()
            : null;
        return new(first, second) { Description = description };
    }

    private static MapCatalogPoint ParsePoint(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{context} bounds contain an invalid point.");
        }

        var values = element.EnumerateArray().ToArray();
        if (values.Length != 2 ||
            !TryReadDouble(values[0], out var x) ||
            !TryReadDouble(values[1], out var y))
        {
            throw new InvalidDataException($"{context} bounds points require exactly two numbers.");
        }

        return new(x, y);
    }

    private static (double First, double Second)? OptionalNumberPair(
        JsonElement parent,
        string property,
        string context)
    {
        var values = OptionalNumberArray(parent, property, 2, context);
        return values is null ? null : (values[0], values[1]);
    }

    private static double[]? OptionalNumberArray(JsonElement parent, string property, int length, string context)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ParseNumberArray(element, property, length, context);
    }

    private static double[] RequiredNumberArray(JsonElement parent, string property, int length, string context)
    {
        if (!parent.TryGetProperty(property, out var element))
        {
            throw new InvalidDataException($"{context} requires {property}.");
        }

        return ParseNumberArray(element, property, length, context);
    }

    private static double[] ParseNumberArray(JsonElement element, string property, int length, string context)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{context} has an invalid {property} value.");
        }

        var values = element.EnumerateArray().ToArray();
        if (values.Length != length)
        {
            throw new InvalidDataException($"{context} {property} requires exactly {length} numbers.");
        }

        var numbers = new double[length];
        for (var index = 0; index < length; index++)
        {
            if (!TryReadDouble(values[index], out numbers[index]))
            {
                throw new InvalidDataException($"{context} {property} requires exactly {length} numbers.");
            }
        }

        return numbers;
    }

    private static IReadOnlyList<string> OptionalStrings(JsonElement parent, string property, string context)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{context} has an invalid {property} value.");
        }

        var values = element.EnumerateArray().ToArray();
        return values.Any(value => value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            ? throw new InvalidDataException($"{context} {property} must contain only non-empty strings.")
            : values.Select(value => value.GetString()!).ToArray();
    }

    private static string RequiredString(JsonElement parent, string property, string context) =>
        OptionalString(parent, property) is { } value
            ? value
            : throw new InvalidDataException($"{context} requires a non-empty {property}.");

    private static string? OptionalString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string? OptionalScalarText(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString()
            : null;

    private static int? OptionalInteger(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && TryReadInt32(value, out var number) ? number : null;

    private static double? OptionalNumber(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && TryReadDouble(value, out var number) ? number : null;

    /// <summary>
    /// Reads a number that upstream may have quoted.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonElement.TryGetDouble"/> throws rather than returning false when the
    /// element is not a number, so using it as a guard turned a single quoted value into a
    /// total catalog failure. Three Customs labels currently ship a quoted rotation
    /// ("6", "5", "-9"); accepting those keeps every location usable.
    /// </remarks>
    private static bool TryReadDouble(JsonElement element, out double value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDouble(out value);
            case JsonValueKind.String:
                return double.TryParse(
                    element.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);
            default:
                value = 0;
                return false;
        }
    }

    private static bool TryReadInt32(JsonElement element, out int value)
    {
        if (TryReadDouble(element, out var number) &&
            number >= int.MinValue &&
            number <= int.MaxValue &&
            number == Math.Truncate(number))
        {
            value = (int)number;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool? OptionalBoolean(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static Uri? OptionalHttpsUri(JsonElement parent, string property, string context)
    {
        var uri = OptionalUri(parent, property, context);
        return uri is null || uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : throw new InvalidDataException($"{context} {property} must use HTTPS.");
    }

    private static Uri? OptionalUri(JsonElement parent, string property, string context)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidDataException($"{context} has an invalid {property} URI.");
        }

        var value = element.GetString();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidDataException($"{context} has an invalid {property} URI.");
    }

    private static void RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {context} must be an object.");
        }
    }

    private static string Slug(string value) =>
        string.Join('-', value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
