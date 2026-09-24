using System.Globalization;
using System.Text.Json;
using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.App.Localization;

/// <summary>
/// The interface's copy for the chosen culture, read from the string tables under
/// Localization/Strings/ (#314).
/// </summary>
/// <remarks>
/// Workspaces reach it through a typed accessor (<see cref="DebriefText"/>), never by key, so a
/// view that names a label that does not exist fails to compile. The culture is chosen once, at
/// composition, before any view is built: views read their labels when they load, so a change of
/// language takes a restart, which is how most desktop applications behave anyway.
/// </remarks>
public static class UiText
{
    /// <summary>A developer override for the preference file, e.g. "qps-ploc".</summary>
    public const string CultureEnvironmentVariable = "TARKOV_COMPANION_UI_CULTURE";

    private const string ResourcePrefix = "TarkovCompanion.Strings.";
    private static readonly Lazy<IReadOnlyDictionary<string, UiString>> EnglishTable = new(() => Load("en")!);
    private static readonly AsyncLocal<UiStrings?> Scoped = new();
    private static UiStrings? _current;

    /// <summary>What every label reads from: a test's scoped table, else the process's.</summary>
    public static UiStrings Current => Scoped.Value ?? (_current ??= Create(null));

    /// <summary>The shipped English table, the fallback for every other culture.</summary>
    public static IReadOnlyDictionary<string, UiString> English => EnglishTable.Value;

    /// <summary>The culture the words were chosen for: "en", "qps-ploc", "de"…</summary>
    public static string CultureName => Current.Name;

    /// <summary>
    /// Chooses the interface culture for the rest of the process. Null or empty means the system's
    /// UI culture; a culture with no table of its own (nor its parent) reads English.
    /// </summary>
    public static void Use(string? cultureName) => _current = Create(cultureName);

    /// <summary>Uses <paramref name="strings"/> for the calling flow only, so a test cannot change another's labels.</summary>
    public static IDisposable Scope(UiStrings strings)
    {
        var previous = Scoped.Value;
        Scoped.Value = strings ?? throw new ArgumentNullException(nameof(strings));
        return new Restore(() => Scoped.Value = previous);
    }

    public static string Get(string key) => Current.Get(key);

    public static string Format(string key, params object?[] arguments) => Current.Format(key, arguments);

    public static string Plural(string key, long count, params object?[] arguments) => Current.Plural(key, count, arguments);

    /// <summary>Builds the table for a culture name; public so a test can build one without choosing it.</summary>
    public static UiStrings Create(string? cultureName, Action<string>? log = null)
    {
        log ??= message => CrashLog.Write("warning/UiText", message);
        var requested = string.IsNullOrWhiteSpace(cultureName) ? CultureInfo.CurrentUICulture.Name : cultureName.Trim();
        if (PseudoLocale.Is(requested))
        {
            return new UiStrings(CultureInfo.GetCultureInfo("en"), English, PseudoLocale.Of(English), log, PseudoLocale.Name);
        }

        CultureInfo culture;
        try
        {
            culture = string.IsNullOrEmpty(requested) ? CultureInfo.GetCultureInfo("en") : CultureInfo.GetCultureInfo(requested);
        }
        catch (CultureNotFoundException)
        {
            log($"UI culture '{requested}' is not known; using English.");
            culture = CultureInfo.GetCultureInfo("en");
        }

        for (var candidate = culture; !string.IsNullOrEmpty(candidate.Name); candidate = candidate.Parent)
        {
            if (string.Equals(candidate.TwoLetterISOLanguageName, "en", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (Load(candidate.Name) is { } table)
            {
                // A translated table holds only what it translates; the rest falls back to English.
                return new UiStrings(candidate, English, table, log);
            }
        }

        return new UiStrings(CultureInfo.GetCultureInfo("en"), English, English, log);
    }

    /// <summary>The tables shipped in this build, by culture name.</summary>
    public static IReadOnlyList<string> Shipped() =>
        [.. typeof(UiText).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..^".json".Length])];

    public static IReadOnlyDictionary<string, UiString>? Load(string cultureName)
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream($"{ResourcePrefix}{cultureName}.json");
        return stream is null ? null : UiStrings.Parse(stream);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>Where the player's interface language is kept: Config/interface-language.json.</summary>
/// <remarks>
/// { "culture": "de" } picks German, { "culture": "qps-ploc" } the pseudo-locale; no file, or no
/// culture in it, follows Windows. The environment variable wins over the file so a developer can
/// look at a build in the pseudo-locale without editing a player's settings.
/// </remarks>
public static class UiCulturePreference
{
    public const string FileName = "interface-language.json";

    /// <summary>The Config folder composition read the language from, for Setup's picker to write back to.</summary>
    public static string? ConfigDirectory { get; private set; }

    public static string? Read(string configDirectory)
    {
        ConfigDirectory = configDirectory;
        if (Environment.GetEnvironmentVariable(UiText.CultureEnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        return ReadFile(configDirectory);
    }

    /// <summary>The culture the file names, ignoring the environment override: what Setup's picker shows as chosen.</summary>
    public static string? ReadFile(string configDirectory)
    {
        try
        {
            var path = Path.Combine(configDirectory, FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("culture", out var culture)
                && culture.ValueKind == JsonValueKind.String
                    ? culture.GetString()
                    : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            CrashLog.Write("warning/UiText", $"The interface language preference could not be read: {exception.Message}");
            return null;
        }
    }

    /// <summary>Writes { "culture": … } whole, through a temporary file, so a crash never leaves half a file.</summary>
    public static void Write(string configDirectory, string cultureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(cultureName);
        Directory.CreateDirectory(configDirectory);
        var path = Path.Combine(configDirectory, FileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Dictionary<string, string> { ["culture"] = cultureName }));
        File.Move(temporary, path, overwrite: true);
    }
}
