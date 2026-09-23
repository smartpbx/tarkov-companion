using System.Globalization;
using System.Text.Json;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>Reads extract requirements and the switch graph from one map catalog payload.</summary>
/// <remarks>
/// The map feed publishes an extract's switch ids in reverse or arbitrary order on some maps.
/// Ordering follows only explicit graph edges (<c>activatedBy</c> and <c>activates</c>), never
/// names. Broken or missing edges retain the payload order rather than inventing a route.
/// </remarks>
internal static class MapExtractRequirementReader
{
    public static IReadOnlyDictionary<string, MapSwitch> ReadSwitches(JsonElement map)
    {
        var result = new Dictionary<string, MapSwitch>(StringComparer.Ordinal);
        if (!map.TryGetProperty("switches", out var switches) || switches.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in switches.EnumerateArray())
        {
            if (ReadText(entry, "id") is not { Length: > 0 } id ||
                ReadText(entry, "name") is not { Length: > 0 } name ||
                ReadPosition(entry, "position") is not { } position)
            {
                continue;
            }

            var activations = new List<MapSwitchActivation>();
            if (entry.TryGetProperty("activates", out var activates) && activates.ValueKind == JsonValueKind.Array)
            {
                foreach (var activation in activates.EnumerateArray())
                {
                    if (ReadText(activation, "switch") is { Length: > 0 } target)
                    {
                        activations.Add(new(ReadText(activation, "operation") ?? "Activates", target));
                    }
                }
            }

            result[id] = new(
                id,
                name,
                ReadText(entry, "switchType"),
                position,
                ReadIdentifier(entry, "activatedBy"),
                activations);
        }

        return result;
    }

    public static MapExtractRequirements Read(
        JsonElement extract,
        IReadOnlyDictionary<string, MapSwitch> switches,
        Func<string, string?>? itemName = null)
    {
        ArgumentNullException.ThrowIfNull(switches);
        var ids = ReadSwitchIds(extract);
        var chain = Order(ids.Select(id => switches.GetValueOrDefault(id)).OfType<MapSwitch>().ToArray()).ToList();
        // The singular field is the control directly attached to the exit. Some real payloads
        // omit its graph edge, but still include it first in the plural list. It is the last
        // action after every explicitly ordered prerequisite, not an invented dependency.
        if (ReadIdentifier(extract, "switch") is { } finalId && chain.Count > 1 &&
            chain.FirstOrDefault(item => item.Id == finalId) is { } final)
        {
            chain.Remove(final);
            chain.Add(final);
        }
        var transfer = ReadTransfer(extract, itemName);
        var name = ReadText(extract, "name") ?? string.Empty;
        return new(
            chain,
            transfer,
            name.Contains("co-op", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("coop", StringComparison.OrdinalIgnoreCase),
            ReadBoolean(extract, "oneTime") || ReadBoolean(extract, "singleUse"));
    }

    internal static IReadOnlyList<MapSwitch> Order(IReadOnlyList<MapSwitch> switches)
    {
        if (switches.Count < 2)
        {
            return switches;
        }

        var byId = switches.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var sourceOrder = switches.Select((item, index) => (item.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var incoming = switches.ToDictionary(item => item.Id, _ => 0, StringComparer.Ordinal);
        var outgoing = switches.ToDictionary(item => item.Id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

        foreach (var item in switches)
        {
            if (item.ActivatedById is { } parent && byId.ContainsKey(parent) && outgoing[parent].Add(item.Id))
            {
                incoming[item.Id]++;
            }

            foreach (var activation in item.Activates)
            {
                if (byId.ContainsKey(activation.TargetSwitchId) && outgoing[item.Id].Add(activation.TargetSwitchId))
                {
                    incoming[activation.TargetSwitchId]++;
                }
            }
        }

        var ready = new SortedSet<(int SourceOrder, string Id)>(Comparer<(int SourceOrder, string Id)>.Create((left, right) =>
        {
            var compared = left.SourceOrder.CompareTo(right.SourceOrder);
            return compared != 0 ? compared : StringComparer.Ordinal.Compare(left.Id, right.Id);
        }));
        foreach (var item in switches.Where(item => incoming[item.Id] == 0))
        {
            ready.Add((sourceOrder[item.Id], item.Id));
        }

        var ordered = new List<MapSwitch>(switches.Count);
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            ordered.Add(byId[next.Id]);
            foreach (var target in outgoing[next.Id])
            {
                if (--incoming[target] == 0)
                {
                    ready.Add((sourceOrder[target], target));
                }
            }
        }

        // A malformed cycle is still catalog data worth showing. Keep its original order after
        // every resolvable dependency instead of dropping it or claiming an invented ordering.
        ordered.AddRange(switches.Where(item => ordered.All(existing => existing.Id != item.Id)));
        return ordered;
    }

    public static string Describe(MapExtractRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var parts = new List<string>(4);
        if (requirements.SwitchChain.Count > 0)
        {
            parts.Add("Needs power: " + string.Join(", then ", requirements.SwitchChain.Select(item => item.Name)));
        }

        if (requirements.Transfer is { } transfer)
        {
            if (transfer.CurrencySymbol is { } symbol)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"Costs {transfer.Count:N0} {symbol}"));
            }
            else if (transfer.ItemName is { Length: > 0 } name)
            {
                parts.Add(transfer.Count == 1
                    ? $"Needs key: {name}"
                    : string.Create(CultureInfo.CurrentCulture, $"Needs {transfer.Count:N0} × {name}"));
            }
            else
            {
                parts.Add(transfer.Count == 1 ? "Needs an item" : $"Needs {transfer.Count:N0} of an item");
            }
        }

        if (requirements.RequiresCoOp)
        {
            parts.Add("Needs co-op partner");
        }

        if (requirements.IsOneTime)
        {
            parts.Add("One use");
        }

        return string.Join(" · ", parts);
    }

    private static IReadOnlyList<string> ReadSwitchIds(JsonElement extract)
    {
        var ids = new List<string>();
        if (extract.TryGetProperty("switches", out var switches) && switches.ValueKind == JsonValueKind.Array)
        {
            ids.AddRange(switches.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .OfType<string>()
                .Where(value => value.Length > 0));
        }

        if (ReadIdentifier(extract, "switch") is { } single && !ids.Contains(single, StringComparer.Ordinal))
        {
            ids.Add(single);
        }

        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static MapExtractTransfer? ReadTransfer(JsonElement extract, Func<string, string?>? itemName)
    {
        if (!extract.TryGetProperty("transferItem", out var transfer) || transfer.ValueKind != JsonValueKind.Object ||
            ReadText(transfer, "item") is not { Length: > 0 } itemId)
        {
            return null;
        }

        var count = transfer.TryGetProperty("count", out var value) && value.TryGetInt64(out var parsed) && parsed > 0
            ? parsed
            : 1;
        var name = itemName?.Invoke(itemId);
        var currency = name?.ToLowerInvariant() switch
        {
            "roubles" or "rouble" => "₽",
            "dollars" or "dollar" => "$",
            "euros" or "euro" => "€",
            _ => null,
        };
        return new(itemId, name, count, currency);
    }

    private static string? ReadIdentifier(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() is { Length: > 0 } id && id != "0" ? id : null,
            _ => null,
        };
    }

    private static bool ReadBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? ReadText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static WorldPosition? ReadPosition(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var position) || position.ValueKind != JsonValueKind.Object ||
            !TryDouble(position, "x", out var x) || !TryDouble(position, "y", out var y) || !TryDouble(position, "z", out var z))
        {
            return null;
        }

        return new(x, y, z);
    }

    private static bool TryDouble(JsonElement element, string property, out double value)
    {
        value = 0;
        return element.TryGetProperty(property, out var propertyValue) &&
            propertyValue.ValueKind == JsonValueKind.Number && propertyValue.TryGetDouble(out value) && double.IsFinite(value);
    }
}
