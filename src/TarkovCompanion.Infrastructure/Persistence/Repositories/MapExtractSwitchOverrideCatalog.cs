using System.Reflection;
using System.Text;
using System.Text.Json;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>Checked corrections for extract-to-switch links that the primary feed publishes incorrectly.</summary>
/// <remarks>
/// The primary catalog remains authoritative except for the named map/extract pairs in the
/// embedded review file. An empty list is meaningful: it clears a false catalog link. The ids
/// are already in player-action order, so graph inference must not rearrange them.
/// </remarks>
internal static class MapExtractSwitchOverrideCatalog
{
    private const string ResourceName =
        "TarkovCompanion.Infrastructure.Maps.extract-switch-overrides.json";

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>>
        Overrides = new(ReadOverrides);

    public static bool TryGet(
        string mapName,
        string extractName,
        out IReadOnlyList<string> switchIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapName);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractName);

        if (Overrides.Value.TryGetValue(Identity(mapName), out var extracts) &&
            extracts.TryGetValue(Identity(extractName), out var found))
        {
            switchIds = found;
            return true;
        }

        switchIds = [];
        return false;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadOverrides()
    {
        using var stream = typeof(MapExtractSwitchOverrideCatalog).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded switch override resource '{ResourceName}' was not found.");
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("overrides", out var maps) ||
            maps.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Extract switch overrides must contain an object named 'overrides'.");
        }

        var result = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        foreach (var map in maps.EnumerateObject())
        {
            if (map.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Switch overrides for '{map.Name}' must be an object.");
            }

            var extracts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var extract in map.Value.EnumerateObject())
            {
                if (extract.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException($"Switch override for '{map.Name}/{extract.Name}' must be an array.");
                }

                var ids = extract.Value.EnumerateArray()
                    .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                    .ToArray();
                if (ids.Any(id => string.IsNullOrWhiteSpace(id)))
                {
                    throw new InvalidDataException($"Switch override for '{map.Name}/{extract.Name}' contains an invalid id.");
                }

                if (!extracts.TryAdd(Identity(extract.Name), ids.OfType<string>().ToArray()))
                {
                    throw new InvalidDataException($"Switch override for '{map.Name}/{extract.Name}' is duplicated after normalization.");
                }
            }

            if (!result.TryAdd(Identity(map.Name), extracts))
            {
                throw new InvalidDataException($"Switch overrides for '{map.Name}' are duplicated after normalization.");
            }
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
