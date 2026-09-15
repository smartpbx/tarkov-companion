using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TarkovCompanion.UnitTests.V2DesignSystem;

/// <summary>
/// Reads the V2 design-system sources as data. The first version of these tests searched the files
/// for substrings, which passed for a table whose header properties Avalonia ignores and for a
/// palette that no dictionary merged; parsing the XAML and JSON lets a test ask what is declared
/// where, rather than whether some text appears somewhere.
/// </summary>
internal static partial class V2DesignSystemFiles
{
    public static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    public static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static readonly string[] ManifestPath = ["src", "TarkovCompanion.App", "Assets", "V2", "semantic-manifest.v1.json"];
    public static readonly string[] RenderMatrixPath = ["src", "TarkovCompanion.App", "Assets", "V2", "render-matrix.v1.json"];
    public static readonly string[] EnglishStringsPath = ["src", "TarkovCompanion.App", "Assets", "V2", "strings.en.json"];
    public static readonly string[] PseudoStringsPath = ["src", "TarkovCompanion.App", "Assets", "V2", "strings.qps-ploc.json"];
    public static readonly string[] ThemeVariantsPath = ["src", "TarkovCompanion.App", "Themes", "V2", "V2ThemeVariants.axaml"];
    public static readonly string[] TokensPath = ["src", "TarkovCompanion.App", "Themes", "V2", "V2Tokens.axaml"];
    public static readonly string[] StringsXamlPath = ["src", "TarkovCompanion.App", "Themes", "V2", "V2Strings.axaml"];
    public static readonly string[] StylesPath = ["src", "TarkovCompanion.App", "Themes", "V2", "V2PrimitiveStyles.axaml"];
    public static readonly string[] GalleryPath = ["src", "TarkovCompanion.App", "Views", "V2", "Primitives", "V2PrimitiveGallery.axaml"];

    /// <summary>Manifest token group to the Avalonia resource-key prefix that realises it.</summary>
    public static readonly IReadOnlyDictionary<string, string> TokenPrefixes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["color"] = "V2.Brush.",
        ["chart"] = "V2.Brush.Chart.",
        ["map"] = "V2.Brush.Map.",
        ["type"] = "V2.Type.",
        ["space"] = "V2.Space.",
        ["shape"] = "V2.Shape.",
        ["elevation"] = "V2.Elevation.",
        ["focus"] = "V2.Focus.",
        ["motion"] = "V2.Motion.",
        ["density"] = "V2.Density.",
        ["target"] = "V2.Target.",
    };

    public static string ReadText(string[] segments) => File.ReadAllText(Path.Combine([RepositoryRoot(), .. segments]));

    public static JsonDocument ReadJson(string[] segments) => JsonDocument.Parse(ReadText(segments));

    public static XElement ReadXaml(string[] segments) => XDocument.Parse(ReadText(segments)).Root!;

    public static Dictionary<string, string> ReadStrings(string[] segments)
    {
        using var document = ReadJson(segments);
        return document.RootElement.GetProperty("strings").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }

    public static IEnumerable<string> Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(item => item.GetString()!).ToArray();

    public static string Pascal(string id) => char.ToUpperInvariant(id[0]) + id[1..];

    public static string? Attr(XElement element, string name) => element.Attribute(name)?.Value;

    public static string[] Classes(XElement element) =>
        (Attr(element, "Classes") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static string? Key(XElement element) => element.Attribute(Xaml + "Key")?.Value;

    /// <summary>Theme dictionaries by variant name: Dark, Light, or a V2Appearance member name.</summary>
    public static Dictionary<string, XElement> ThemeDictionaries()
    {
        const string staticPrefix = "{x:Static v2:V2Appearance.";
        return ReadXaml(ThemeVariantsPath)
            .Element(Avalonia + "ResourceDictionary.ThemeDictionaries")!
            .Elements(Avalonia + "ResourceDictionary")
            .ToDictionary(
                dictionary => Key(dictionary)!.StartsWith(staticPrefix, StringComparison.Ordinal)
                    ? Key(dictionary)![staticPrefix.Length..^1]
                    : Key(dictionary)!,
                StringComparer.Ordinal);
    }

    public static HashSet<string> KeysOf(XElement dictionary) =>
        dictionary.Elements().Select(Key).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>Every theme-realised or theme-independent adapter key that a manifest token must name.</summary>
    public static HashSet<string> AdapterResourceKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dictionary in ThemeDictionaries().Values)
        {
            keys.UnionWith(KeysOf(dictionary));
        }

        keys.UnionWith(KeysOf(ReadXaml(TokensPath)).Where(key => !key.StartsWith("V2.Glyph.", StringComparison.Ordinal)));
        return keys;
    }

    public static IEnumerable<string> DynamicResourceKeys(string text) =>
        DynamicResourcePattern().Matches(text).Select(match => match.Groups[1].Value);

    [GeneratedRegex(@"\{DynamicResource ([^}\s]+)\}")]
    private static partial Regex DynamicResourcePattern();

    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
