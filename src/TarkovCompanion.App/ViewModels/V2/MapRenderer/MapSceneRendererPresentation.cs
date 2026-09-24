using System.Globalization;
using TarkovCompanion.App.Localization;

namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

/// <summary>The selected language, number culture, and time zone used by one map renderer.</summary>
/// <remarks>
/// A renderer used to read ambient process culture and the development machine's local time
/// zone. Paired devices could consequently describe the same evidence differently. The host now
/// supplies all three presentation choices together; tests and the packaged gallery do the same.
/// </remarks>
public sealed class MapSceneRendererPresentation
{
    public MapSceneRendererPresentation(
        CultureInfo culture,
        TimeZoneInfo timeZone,
        IReadOnlyDictionary<string, string> strings)
    {
        Culture = culture ?? throw new ArgumentNullException(nameof(culture));
        TimeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        ArgumentNullException.ThrowIfNull(strings);
        Strings = new Dictionary<string, string>(strings, StringComparer.Ordinal);
    }

    public CultureInfo Culture { get; }

    public TimeZoneInfo TimeZone { get; }

    public IReadOnlyDictionary<string, string> Strings { get; }

    public string Get(string key) => Strings.TryGetValue(key, out var value)
        ? value
        : throw new KeyNotFoundException($"The map renderer has no localized text for '{key}'.");

    public string Format(string key, params object?[] arguments) =>
        string.Format(Culture, Get(key), arguments);

    public string Number(int value) => value.ToString("N0", Culture);

    public string Percent(double value) => value.ToString("P0", Culture);

    public string Instant(DateTimeOffset value)
    {
        var local = TimeZoneInfo.ConvertTime(value, TimeZone);
        var offset = local.ToString("'UTC'zzz", CultureInfo.InvariantCulture);
        return Format("Map.DateTimeWithZone", local.ToString("f", Culture), offset);
    }

    public static MapSceneRendererPresentation English(
        CultureInfo culture,
        TimeZoneInfo timeZone) => new(culture, timeZone, EnglishStrings);

    /// <summary>The map's words in English, from Localization/Strings (#314); tests and the gallery draw with these.</summary>
    /// <remarks>
    /// The "Map.*" keys moved to the string table with their meanings unchanged. Notes that sat beside
    /// them: "High-value loot only" names its empty state rather than blanking the map (#563); the
    /// short strip words are for a map column short of room, the full ones stay the tooltip and the
    /// accessible name (#838); the notice is a small chip whose tooltip carries the sentences
    /// (package 20), and each chip names the condition it is about rather than counting sentences
    /// (package 46).
    /// </remarks>
    public static IReadOnlyDictionary<string, string> EnglishStrings { get; } =
        UiText.English
            .Where(pair => pair.Key.StartsWith("Map.", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Other, StringComparer.Ordinal);

    /// <summary>The same keys in the interface language chosen at startup, English where it has no word.</summary>
    public static MapSceneRendererPresentation Current(CultureInfo culture, TimeZoneInfo timeZone) =>
        new(culture, timeZone, EnglishStrings.Keys.ToDictionary(key => key, UiText.Get, StringComparer.Ordinal));
}
