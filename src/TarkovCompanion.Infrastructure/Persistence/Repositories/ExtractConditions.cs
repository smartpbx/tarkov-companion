using System.Globalization;
using System.Text.Json;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// What an exit asks of you before it will let you out.
/// </summary>
/// <remarks>
/// Reported as "clicking an extract should pull up a picture of it and maybe any important
/// instructions for it too". The picture has no source; the instructions do, and they have
/// been in the synced payload the whole time.
///
/// Three things are in there and each one changes whether an exit is worth running to. Who
/// may take it, which we already said. Whether a switch has to be thrown first, which is the
/// difference between an exit and a trip. And what it costs, because a vehicle extract wants
/// money and a secret exit wants a key you either have or do not.
///
/// Nothing here guesses. An exit with no conditions gets no sentence rather than a reassuring
/// one, because "no conditions" and "we did not look" read identically and only one is true.
/// </remarks>
public static class ExtractConditions
{
    /// <summary>
    /// Everything the payload says about getting through this exit, in one line.
    /// </summary>
    /// <param name="extract">One entry from the map's extract array.</param>
    /// <param name="itemName">
    /// Resolves the id of a handed-over item to its name. Given none, or given an item the
    /// catalog has never synced, the cost is still stated as a count.
    /// </param>
    public static string? Describe(JsonElement extract, Func<string, string?>? itemName = null)
    {
        if (extract.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parts = new List<string>(3);
        if (DescribeFaction(ReadText(extract, "faction")) is { } faction)
        {
            parts.Add(faction);
        }

        if (DescribeSwitches(extract) is { } switches)
        {
            parts.Add(switches);
        }

        if (DescribeCost(extract, itemName) is { } cost)
        {
            parts.Add(cost);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>Who may take it, because taking the wrong one is not possible.</summary>
    public static string? DescribeFaction(string? faction) => faction?.ToLowerInvariant() switch
    {
        "pmc" => "PMC only",
        "scav" => "Scav only",
        "shared" => "Either side",
        _ => null,
    };

    /// <summary>
    /// The item ids an exit asks to be handed over, so their names can be looked up first.
    /// </summary>
    public static IEnumerable<string> TransferItemIds(JsonElement extract)
    {
        if (extract.ValueKind == JsonValueKind.Object &&
            extract.TryGetProperty("transferItem", out var transfer) &&
            transfer.ValueKind == JsonValueKind.Object &&
            ReadText(transfer, "item") is { Length: > 0 } itemId)
        {
            yield return itemId;
        }
    }

    /// <summary>
    /// Whether a switch has to be thrown before this exit works.
    /// </summary>
    /// <remarks>
    /// The payload carries both a single <c>switch</c> and a <c>switches</c> list, and where
    /// both are present they say the same thing. The list is preferred because it is the one
    /// that can hold more than one.
    /// </remarks>
    private static string? DescribeSwitches(JsonElement extract)
    {
        var count = extract.TryGetProperty("switches", out var switches) && switches.ValueKind == JsonValueKind.Array
            ? switches.GetArrayLength()
            : ReadText(extract, "switch") is { Length: > 0 } ? 1 : 0;
        return count switch
        {
            0 => null,
            1 => "Needs a switch",
            _ => string.Create(CultureInfo.CurrentCulture, $"Needs {count} switches"),
        };
    }

    /// <summary>
    /// What it costs, in money or in an item.
    /// </summary>
    /// <remarks>
    /// A vehicle extract asks for roubles and a secret exit asks for a key, and the two read
    /// completely differently: "20,000 ₽" is a decision about whether it is worth it, and a
    /// key is a decision about whether you have one. Money is recognised by the item's own
    /// short name rather than by its id, so the same rule covers every currency the game adds.
    /// </remarks>
    private static string? DescribeCost(JsonElement extract, Func<string, string?>? itemName)
    {
        if (!extract.TryGetProperty("transferItem", out var transfer) ||
            transfer.ValueKind != JsonValueKind.Object ||
            ReadText(transfer, "item") is not { Length: > 0 } itemId)
        {
            return null;
        }

        var count = transfer.TryGetProperty("count", out var countValue) &&
            countValue.ValueKind == JsonValueKind.Number &&
            countValue.TryGetInt64(out var parsed) &&
            parsed > 0
                ? parsed
                : 1;
        var name = itemName?.Invoke(itemId);
        if (Currency(name) is { } symbol)
        {
            return string.Create(CultureInfo.CurrentCulture, $"Costs {count:N0} {symbol}");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return count == 1
                ? "Needs an item"
                : string.Create(CultureInfo.CurrentCulture, $"Needs {count} of an item");
        }

        return count == 1
            ? $"Needs {name}"
            : string.Create(CultureInfo.CurrentCulture, $"Needs {count:N0} × {name}");
    }

    /// <summary>The symbol for a currency, or null for anything that is not one.</summary>
    private static string? Currency(string? name) => name?.ToLowerInvariant() switch
    {
        "roubles" or "rouble" => "₽",
        "dollars" or "dollar" => "$",
        "euros" or "euro" => "€",
        _ => null,
    };

    private static string? ReadText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
