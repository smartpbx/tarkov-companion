using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Application.Services.Maps;

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

            var locations = document.RootElement
                .EnumerateArray()
                .Select(ParseLocation)
                .ToArray();
            if (locations.Length == 0)
            {
                throw new InvalidDataException("The tarkov.dev map catalog does not contain any locations.");
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
                new(sourceUri, retrievedUtc.ToUniversalTime(), contentHash, availability));
        }
    }

    private static MapLocation ParseLocation(JsonElement element)
    {
        RequireObject(element, "location");
        var locationId = RequiredString(element, "normalizedName", "location");
        var name = OptionalString(element, "name") ?? Humanize(locationId);
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
        if (element.TryGetProperty("layers", out var layersElement))
        {
            if (layersElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Map variant '{key}' has an invalid layers value.");
            }

            floors.AddRange(layersElement.EnumerateArray().Select((layer, index) => ParseFloor(key, layer, index)));
        }

        var labels = Array.Empty<MapCatalogLabel>();
        if (element.TryGetProperty("labels", out var labelsElement))
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
        if (element.TryGetProperty("extents", out var extentsElement))
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
        if (element.TryGetProperty("bounds", out var boundsElement))
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
        if (points.Length != 2)
        {
            throw new InvalidDataException($"{context} bounds require exactly two points.");
        }

        var first = ParsePoint(points[0], context);
        var second = ParsePoint(points[1], context);
        return new(first, second);
    }

    private static MapCatalogPoint ParsePoint(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{context} bounds contain an invalid point.");
        }

        var values = element.EnumerateArray().ToArray();
        if (values.Length != 2 || !values.All(value => value.TryGetDouble(out _)))
        {
            throw new InvalidDataException($"{context} bounds points require exactly two numbers.");
        }

        return new(values[0].GetDouble(), values[1].GetDouble());
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
        if (values.Length != length || !values.All(value => value.TryGetDouble(out _)))
        {
            throw new InvalidDataException($"{context} {property} requires exactly {length} numbers.");
        }

        return values.Select(value => value.GetDouble()).ToArray();
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

        var values = element.EnumerateArray().Select(value => value.GetString()).ToArray();
        return values.Any(string.IsNullOrWhiteSpace)
            ? throw new InvalidDataException($"{context} {property} must contain only non-empty strings.")
            : values.Select(value => value!).ToArray();
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
        parent.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static double? OptionalNumber(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.TryGetDouble(out var number) ? number : null;

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
        var value = OptionalString(parent, property);
        if (value is null)
        {
            return null;
        }

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

    private static string Humanize(string value) =>
        string.Join(' ', value.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(word =>
            char.ToUpperInvariant(word[0]) + word[1..]));

    private static string Slug(string value) =>
        string.Join('-', value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
