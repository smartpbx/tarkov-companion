using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace TarkovCompanion.App.Localization;

/// <summary>One piece of copy: the ordinary form, and the singular where a count changes the words.</summary>
public sealed record UiString(string Other, string? One = null);

/// <summary>
/// The copy for one interface culture, falling back to English key by key.
/// </summary>
/// <remarks>
/// Built for #314. A translation that lags behind the English is the normal state of a translated
/// application, not an error, so a key the chosen culture lacks shows the English and is written to
/// the log once — once, because a missing label on a list row would otherwise log per row per
/// redraw. A key English itself lacks is a programming mistake; it shows the key, so the gap is
/// visible on screen rather than rendered as an empty label, and is logged once the same way.
///
/// Arguments are formatted with <see cref="CultureInfo.CurrentCulture"/>, the same culture every
/// label used before it was moved here, so a number or date reads the same whichever language the
/// words are in. Times still reach this class already formatted by LocalTime.
/// </remarks>
public sealed class UiStrings
{
    private readonly IReadOnlyDictionary<string, UiString> _english;
    private readonly IReadOnlyDictionary<string, UiString> _localized;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.Ordinal);

    public UiStrings(
        CultureInfo culture,
        IReadOnlyDictionary<string, UiString> english,
        IReadOnlyDictionary<string, UiString>? localized = null,
        Action<string>? log = null,
        string? name = null)
    {
        Culture = culture ?? throw new ArgumentNullException(nameof(culture));
        Name = name ?? culture.Name;
        _english = english ?? throw new ArgumentNullException(nameof(english));
        _localized = localized ?? english;
        _log = log ?? (_ => { });
    }

    /// <summary>The interface culture the words are chosen for (not the one numbers are formatted in).</summary>
    public CultureInfo Culture { get; }

    /// <summary>What the table was chosen as: the culture's name, or the pseudo-locale's.</summary>
    public string Name { get; }

    public string Get(string key) => Lookup(key).Other;

    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    /// <summary>A counted phrase; <paramref name="count"/> is argument {0}, the rest follow it.</summary>
    public string Plural(string key, long count, params object?[] arguments)
    {
        var text = Lookup(key);
        var pattern = text.One is { } one && UiPluralRule.IsOne(Culture, count) ? one : text.Other;
        return string.Format(CultureInfo.CurrentCulture, pattern, [count, .. arguments]);
    }

    /// <summary>
    /// A counted phrase formatted in <paramref name="format"/> rather than the UI culture: the
    /// self-test's probes are handed their culture so a test can pin it. One/other still follows
    /// the interface language, which is whose grammar the words are in.
    /// </summary>
    public string Plural(IFormatProvider format, string key, long count, params object?[] arguments)
    {
        var text = Lookup(key);
        var pattern = text.One is { } one && UiPluralRule.IsOne(Culture, count) ? one : text.Other;
        return string.Format(format, pattern, [count, .. arguments]);
    }

    private UiString Lookup(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_localized.TryGetValue(key, out var localized))
        {
            return localized;
        }

        if (_english.TryGetValue(key, out var english))
        {
            LogOnce(key, $"UI text '{key}' has no {Name} translation; showing English.");
            return english;
        }

        LogOnce(key, $"UI text '{key}' has no English value; showing the key.");
        return new UiString(key);
    }

    private void LogOnce(string key, string message)
    {
        if (_logged.TryAdd(key, 0))
        {
            _log(message);
        }
    }

    /// <summary>
    /// Reads a string table: a flat JSON object of key to text, or key to { "one": …, "other": … }
    /// for a phrase whose words change with a count.
    /// </summary>
    public static IReadOnlyDictionary<string, UiString> Parse(Stream json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var table = new Dictionary<string, UiString>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            table[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => new UiString(property.Value.GetString()!),
                JsonValueKind.Object => new UiString(
                    property.Value.TryGetProperty("other", out var other) && other.ValueKind == JsonValueKind.String
                        ? other.GetString()!
                        : throw new InvalidDataException($"UI text '{property.Name}' has no \"other\" form."),
                    property.Value.TryGetProperty("one", out var one) && one.ValueKind == JsonValueKind.String
                        ? one.GetString()
                        : null),
                _ => throw new InvalidDataException($"UI text '{property.Name}' is neither text nor plural forms."),
            };
        }

        return table;
    }
}

/// <summary>Whether a count takes the singular: one/other only, which is all the copy needs so far.</summary>
public static class UiPluralRule
{
    public static bool IsOne(CultureInfo culture, long count)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TwoLetterISOLanguageName switch
        {
            // No grammatical singular: every count takes the same words.
            "ja" or "zh" or "ko" or "vi" or "th" or "id" or "ms" => false,
            // Zero is singular too.
            "fr" or "pt" => count is 0 or 1,
            _ => count == 1,
        };
    }
}
