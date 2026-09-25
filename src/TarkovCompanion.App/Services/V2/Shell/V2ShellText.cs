using System.Globalization;

namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>
/// Every word the provisional shell draws, by key.
/// </summary>
/// <remarks>
/// Kept out of the views so the views carry no literal copy, the same rule the #266 gallery holds
/// itself to, and so a label #265 moves is one line here rather than a search through markup.
/// English only until #269 supplies culture resources; a message is formatted whole with an
/// explicit culture rather than assembled from fragments.
///
/// Short on purpose. A label is a line: scripts/sweep-prose.sh fails a label past 120 characters,
/// and standing policy prose belongs in Setup and docs, not in chrome that is on every page.
/// </remarks>
public static class V2ShellText
{
    public static IReadOnlyDictionary<string, string> English { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["V2.Shell.Provisional"] = "provisional - #265 not yet run",
        ["V2.Shell.Variant.A"] = "Variant A · workspace rail",
        ["V2.Shell.Variant.B"] = "Variant B · workflow hub",

        ["V2.Shell.StateGlyph.Ready"] = "✓",
        ["V2.Shell.StateGlyph.Loading"] = "…",
        ["V2.Shell.StateGlyph.Empty"] = "○",
        ["V2.Shell.StateGlyph.Offline"] = "⊘",
        ["V2.Shell.StateGlyph.Stale"] = "◷",
        ["V2.Shell.StateGlyph.Partial"] = "◔",
        ["V2.Shell.StateGlyph.Denied"] = "⌕",
        ["V2.Shell.StateGlyph.Failed"] = "×",
    };

    /// <remarks>
    /// [#314] The top bar, rail, sub-tab and banner words moved to Localization/Strings as "Shell.*".
    /// Registries still name them by their old key, so a key no longer here is looked up there.
    /// Setup's and Home's words followed as "Setup.*" the same way, then the capture panel, command
    /// palette, suggestions and announcements as "Shell.*". What is left here is not a word: the
    /// state glyphs, and the developer-only variant and provisional tags.
    /// </remarks>
    public static string Get(string key) =>
        English.TryGetValue(key ?? throw new ArgumentNullException(nameof(key)), out var text)
            ? text
            : TarkovCompanion.App.Localization.ShellText.Moved(key)
                ?? TarkovCompanion.App.Localization.SetupText.Moved(key)
                ?? throw new KeyNotFoundException($"The shell has no text for '{key}'.");

    public static string Format(string key, CultureInfo culture, params object?[] arguments) =>
        string.Format(culture, Get(key), arguments);

    /// <summary>How long ago something was observed, computed from its own time, never from now alone.</summary>
    public static string Age(DateTimeOffset observedUtc, DateTimeOffset nowUtc, CultureInfo culture)
    {
        var age = nowUtc - observedUtc;
        return age < TimeSpan.Zero ? Get("V2.Shell.Age.Future")
            : TarkovCompanion.App.Localization.UnitText.Ago(age, culture);
    }
}
