using System.Text;
using System.Text.Json;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>Checked extract conditions omitted by the primary map feed.</summary>
/// <remarks>
/// This is deliberately bounded by map and extract name, like the switch correction table. It
/// adds only reviewed facts; an unlisted extract keeps the primary catalog answer unchanged.
/// </remarks>
internal static class MapExtractConditionOverrideCatalog
{
    private const string ResourceName =
        "TarkovCompanion.Infrastructure.Maps.extract-condition-overrides.json";

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MapExtractCondition>>>>
        Overrides = new(ReadOverrides);

    public static bool TryGet(
        string mapName,
        string extractName,
        out IReadOnlyList<MapExtractCondition> conditions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapName);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractName);

        if (Overrides.Value.TryGetValue(Identity(mapName), out var extracts) &&
            extracts.TryGetValue(Identity(extractName), out var found))
        {
            conditions = found;
            return true;
        }

        conditions = [];
        return false;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MapExtractCondition>>> ReadOverrides()
    {
        using var stream = typeof(MapExtractConditionOverrideCatalog).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded extract condition resource '{ResourceName}' was not found.");
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("overrides", out var maps) ||
            maps.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Extract condition overrides must contain an object named 'overrides'.");
        }

        var result = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<MapExtractCondition>>>(StringComparer.Ordinal);
        foreach (var map in maps.EnumerateObject())
        {
            if (map.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Extract conditions for '{map.Name}' must be an object.");
            }

            var extracts = new Dictionary<string, IReadOnlyList<MapExtractCondition>>(StringComparer.Ordinal);
            foreach (var extract in map.Value.EnumerateObject())
            {
                if (extract.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException($"Extract conditions for '{map.Name}/{extract.Name}' must be an array.");
                }

                var conditions = extract.Value.EnumerateArray()
                    .Select(value => ReadCondition(map.Name, extract.Name, value))
                    .ToArray();
                if (conditions.Length == 0 || conditions.Select(item => item.Kind).Distinct().Count() != conditions.Length)
                {
                    throw new InvalidDataException($"Extract conditions for '{map.Name}/{extract.Name}' must be non-empty and have unique kinds.");
                }

                if (!extracts.TryAdd(Identity(extract.Name), conditions))
                {
                    throw new InvalidDataException($"Extract conditions for '{map.Name}/{extract.Name}' are duplicated after normalization.");
                }
            }

            if (!result.TryAdd(Identity(map.Name), extracts))
            {
                throw new InvalidDataException($"Extract conditions for '{map.Name}' are duplicated after normalization.");
            }
        }

        return result;
    }

    private static MapExtractCondition ReadCondition(string map, string extract, JsonElement value)
    {
        var row = $"{map}/{extract}";
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("kind", out var kindValue) ||
            kindValue.ValueKind != JsonValueKind.String ||
            !Enum.TryParse<MapExtractConditionKind>(kindValue.GetString(), ignoreCase: false, out var kind))
        {
            throw new InvalidDataException($"Extract condition for '{row}' has an invalid kind.");
        }

        return kind switch
        {
            MapExtractConditionKind.NoBackpack or MapExtractConditionKind.NoArmor => new(kind, [], null),
            MapExtractConditionKind.Items => new(kind, ReadItems(row, value), null),
            MapExtractConditionKind.TimedWindow => new(kind, [], ReadWindow(row, value)),
            _ => throw new InvalidDataException($"Extract condition for '{row}' has an unsupported kind."),
        };
    }

    private static IReadOnlyList<string> ReadItems(string row, JsonElement value)
    {
        if (!value.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Item condition for '{row}' must contain an items array.");
        }

        var result = items.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null)
            .ToArray();
        if (result.Length == 0 || result.Any(string.IsNullOrWhiteSpace) ||
            result.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
        {
            throw new InvalidDataException($"Item condition for '{row}' must contain unique non-empty names.");
        }

        return result.OfType<string>().ToArray();
    }

    private static MapExtractTimedWindow ReadWindow(string row, JsonElement value)
    {
        var starts = ReadPositiveInt(row, value, "arrivalStartsAtTimeLeftMinutes");
        var ends = ReadPositiveInt(row, value, "arrivalEndsAtTimeLeftMinutes");
        var duration = ReadPositiveInt(row, value, "durationMinutes");
        if (starts < ends)
        {
            throw new InvalidDataException($"Timed condition for '{row}' must count down from its start to its end.");
        }

        return new(TimeSpan.FromMinutes(starts), TimeSpan.FromMinutes(ends), TimeSpan.FromMinutes(duration));
    }

    private static int ReadPositiveInt(string row, JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var number) ||
            number.ValueKind != JsonValueKind.Number ||
            !number.TryGetInt32(out var result) || result <= 0)
        {
            throw new InvalidDataException($"Timed condition for '{row}' needs a positive integer '{property}'.");
        }

        return result;
    }

    private static string Identity(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                result.Append(char.ToLowerInvariant(character));
            }
        }

        return result.ToString();
    }
}
