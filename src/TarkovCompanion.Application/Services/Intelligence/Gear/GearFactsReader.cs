using System.Globalization;
using System.Text.Json;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Gear;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.Application.Services.Intelligence.Gear;

/// <summary>
/// Reads a catalog item's property blob into <see cref="GearFacts"/>, keeping what the blob does
/// not say unknown.
/// </summary>
/// <remarks>
/// The keys are the ones the synced json.tarkov.dev items carry (checked against the catalog: an
/// armor has <c>class</c>, <c>durability</c>, <c>zones</c> and <c>armorSlots[].allowedPlates</c>; a rig or bag has
/// <c>capacity</c>; a headset has its compressor settings; a medical item has <c>uses</c> and
/// <c>cures</c>). The blob is untrusted input from a cache: a key that is missing, of another type or
/// out of range is unknown and never a fault, and a plate list may name an id that is not a plate,
/// which the caller is left to filter because only it knows what the catalog holds.
/// </remarks>
public static class GearFactsReader
{
    private static readonly IReadOnlyList<string> None = [];

    /// <summary>True for the categories that have gear facts.</summary>
    public static bool IsGear(ItemCategory category) =>
        category is ItemCategory.Armor or ItemCategory.Plate or ItemCategory.Helmet or ItemCategory.Headset
            or ItemCategory.Rig or ItemCategory.Backpack or ItemCategory.Medicine;

    /// <summary>
    /// Gear facts for an item, or null when the item is not gear. Gear with no property blob still
    /// yields facts, with every figure unknown, so "we know nothing about it" stays visible.
    /// </summary>
    public static GearFacts? Read(
        string itemId,
        ItemCategory category,
        JsonElement? properties,
        DataProvenance provenance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(provenance);
        if (!IsGear(category))
        {
            return null;
        }

        var root = properties is { ValueKind: JsonValueKind.Object } value ? value : (JsonElement?)null;
        return new(
            itemId,
            category,
            ArmorClass: ReadInt(root, "class", 0, 10),
            Durability: ReadNumber(root, "durability", 0, 10_000),
            Material: ReadText(root, "material"),
            ArmorType: ReadText(root, "armorType"),
            Zones: ReadTexts(root, "zones"),
            BluntThroughput: ReadNumber(root, "bluntThroughput", 0, 1),
            BlindnessProtection: ReadNumber(root, "blindnessProtection", 0, 1),
            SpeedPenalty: ReadNumber(root, "speedPenalty", -1, 1),
            TurnPenalty: ReadNumber(root, "turnPenalty", -1, 1),
            ErgoPenalty: ReadNumber(root, "ergoPenalty", -1, 1),
            CarryCells: ReadInt(root, "capacity", 0, 1_000),
            Uses: ReadInt(root, "uses", 0, 1_000),
            UseTimeSeconds: ReadNumber(root, "useTime", 0, 600),
            Cures: ReadTexts(root, "cures"),
            Audio: category == ItemCategory.Headset ? ReadAudio(root) : null,
            PlateSlots: ReadPlateSlots(root),
            provenance);
    }

    /// <summary>
    /// One short line of the figures that are known, such as
    /// "Class 6 · Ceramic · 60 durability · ergo -1.5%", or null when none is.
    /// </summary>
    public static string? Summarize(GearFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var parts = new List<string>();
        if (facts.ArmorClass is { } armorClass && armorClass > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Class {armorClass}"));
        }

        if (!string.IsNullOrWhiteSpace(facts.Material))
        {
            parts.Add(facts.Material!);
        }

        if (facts.Durability is { } durability)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{durability:0.##} durability"));
        }

        if (facts.CarryCells is { } cells && cells > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{cells} cells"));
        }

        if (facts.Uses is { } uses && uses > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{uses} {(uses == 1 ? "use" : "uses")}"));
        }

        // A headset's settings are the source's raw audio parameters, unrated: what they sound like is
        // a subjective note and not a fact, so they are listed by the source's own names and no more.
        if (facts.Audio is { } audio)
        {
            if (audio.CompressorGain is { } gain)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"gain {gain:0.##}"));
            }

            if (audio.DistanceModifier is { } distance)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"distance {distance:0.##}"));
            }

            if (audio.AmbientVolume is { } ambient)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"ambient {ambient:0.##}"));
            }
        }

        if (facts.ErgoPenalty is { } ergo and not 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"ergo {ergo * 100:+0.#;-0.#}%"));
        }

        if (facts.SpeedPenalty is { } speed and not 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"speed {speed * 100:+0.#;-0.#}%"));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static HeadsetAudio? ReadAudio(JsonElement? root)
    {
        var audio = new HeadsetAudio(
            ReadNumber(root, "ambientVolume", -200, 200),
            ReadNumber(root, "compressorGain", -200, 200),
            ReadNumber(root, "compressorThreshold", -200, 200),
            ReadNumber(root, "compressorAttack", 0, 10_000),
            ReadNumber(root, "compressorRelease", 0, 10_000),
            ReadNumber(root, "distanceModifier", 0, 100),
            ReadNumber(root, "distortion", 0, 100),
            ReadNumber(root, "dryVolume", -200, 200));
        return audio == new HeadsetAudio(null, null, null, null, null, null, null, null) ? null : audio;
    }

    private static IReadOnlyList<PlateSlot> ReadPlateSlots(JsonElement? root)
    {
        if (root is not { } element ||
            !element.TryGetProperty("armorSlots", out var slots) ||
            slots.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<PlateSlot>();
        foreach (var slot in slots.EnumerateArray())
        {
            // A slot with no plate list is one of a helmet's or armor's own protected zones, not a
            // place a plate goes.
            if (slot.ValueKind != JsonValueKind.Object ||
                !slot.TryGetProperty("allowedPlates", out var allowed) ||
                allowed.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var plateIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var plate in allowed.EnumerateArray())
            {
                if (plate.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(plate.GetString()))
                {
                    plateIds.Add(plate.GetString()!);
                }
            }

            if (plateIds.Count > 0)
            {
                result.Add(new(ReadText(slot, "nameId") ?? "plate", ReadTexts(slot, "zones"), plateIds));
            }
        }

        return result;
    }

    private static double? ReadNumber(JsonElement? root, string name, double minimum, double maximum) =>
        root is { } element &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) &&
        double.IsFinite(number) && number >= minimum && number <= maximum
            ? number
            : null;

    private static int? ReadInt(JsonElement? root, string name, int minimum, int maximum) =>
        root is { } element &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) &&
        number >= minimum && number <= maximum
            ? number
            : null;

    private static string? ReadText(JsonElement? root, string name) =>
        root is { } element &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadTexts(JsonElement? root, string name)
    {
        if (root is not { } element ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return None;
        }

        return value.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
            .Select(entry => entry.GetString()!)
            .ToArray();
    }
}
