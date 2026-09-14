using System.Text.Json;
using TarkovCompanion.App.Views.V2.Primitives;

namespace TarkovCompanion.UnitTests.V2DesignSystem;

public sealed class V2DesignSystemContractTests
{
    [Fact]
    public void SemanticManifestIsVersionedPlatformNeutralAndComplete()
    {
        using var document = ReadJson("src", "TarkovCompanion.App", "Assets", "V2", "semantic-manifest.v1.json");
        var root = document.RootElement;

        Assert.Equal("1.0.0", root.GetProperty("version").GetString());
        Assert.Contains("Avalonia classes", root.GetProperty("platformBoundary").GetProperty("excluded").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("semantic roles", root.GetProperty("platformBoundary").GetProperty("shared").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("unknown", StateIds(root));
        Assert.Contains("failed", StateIds(root));
        Assert.Contains("captureQueue", Strings(root, "primitives"));
        Assert.Contains("pairedDevice", Strings(root, "primitives"));
        Assert.False(root.GetProperty("accessibility").GetProperty("colorAlone").GetBoolean());
    }

    [Fact]
    public void GalleryUsesResourcesAndExposesRequiredUiaSemantics()
    {
        var gallery = File.ReadAllText(PathFor("src", "TarkovCompanion.App", "Views", "V2", "Primitives", "V2PrimitiveGallery.axaml"));

        Assert.Contains("AutomationProperties.LandmarkType=\"Main\"", gallery, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.HeadingLevel=\"1\"", gallery, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", gallery, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Assertive\"", gallery, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.IsColumnHeader=\"True\"", gallery, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.IsRowHeader=\"True\"", gallery, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"[", gallery, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"[", gallery, StringComparison.Ordinal);
    }

    [Fact]
    public void BaselineMatrixCoversAllRequiredTextScalesAndNarrowWidths()
    {
        using var document = ReadJson("src", "TarkovCompanion.App", "Assets", "V2", "visual-baselines.v1.json");
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var scales = cases.Select(item => item.GetProperty("scale").GetInt32()).ToHashSet();

        Assert.True(scales.SetEquals([100, 125, 150, 200]));
        Assert.Contains(cases, item => item.GetProperty("effectiveWidth").GetString() == "narrow");
        Assert.Contains(cases, item => Strings(item, "required").Contains("focusRing"));
        Assert.Contains(cases, item => Strings(item, "required").Contains("orderedDataAlternative"));
    }

    [Fact]
    public void PseudoLocalizationFormattingAndRtlDecisionRemainExplicit()
    {
        var pseudo = File.ReadAllText(PathFor("src", "TarkovCompanion.App", "Assets", "V2", "strings.qps-ploc.json"));
        var document = JsonDocument.Parse(pseudo);
        using var manifest = ReadJson("src", "TarkovCompanion.App", "Assets", "V2", "semantic-manifest.v1.json");
        var localization = manifest.RootElement.GetProperty("localization");

        Assert.Equal("qps-ploc", document.RootElement.GetProperty("locale").GetString());
        Assert.Contains("RTL", localization.GetProperty("rtl").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(ReadStringKeys("src", "TarkovCompanion.App", "Assets", "V2", "strings.en.json").SetEquals(
            document.RootElement.GetProperty("strings").EnumerateObject().Select(property => property.Name)));
        Assert.Equal("[Ëxåmplë]", V2PresentationFormatting.PseudoLocalize("Example"));
    }

    [Fact]
    public void PrimitiveContractIncludesCaptureAndPairedDeviceOperationalFacts()
    {
        Assert.Contains("semanticTable", V2PrimitiveContracts.Required);
        Assert.Contains("whyDisclosure", V2PrimitiveContracts.Required);
        Assert.Contains("review", V2PrimitiveContracts.CaptureStages);
        Assert.Contains("acknowledgementLag", V2PrimitiveContracts.DeviceStates);
        Assert.Contains("conflict", V2PrimitiveContracts.DeviceStates);
    }

    [Fact]
    public void ThemeVariantTextAndFocusPairsMeetTheDocumentedContrastFloor()
    {
        var theme = File.ReadAllText(PathFor("src", "TarkovCompanion.App", "Themes", "V2", "V2ThemeVariants.axaml"));
        var variants = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        string? current = null;

        foreach (var line in theme.Split('\n'))
        {
            var key = Attribute(line, "x:Key");
            if (key is "Dark" or "Light" or "HighContrast")
            {
                current = key;
                variants[current] = new(StringComparer.Ordinal);
            }
            else if (current is not null && key is not null && key.StartsWith("V2.Color.", StringComparison.Ordinal))
            {
                variants[current][key] = Between(line, ">", "<");
            }
        }

        Assert.Equal(3, variants.Count);
        foreach (var (variant, colors) in variants)
        {
            Assert.True(Contrast(colors["V2.Color.TextPrimary"], colors["V2.Color.Canvas"]) >= 4.5, $"{variant} primary text is too faint.");
            Assert.True(Contrast(colors["V2.Color.TextSecondary"], colors["V2.Color.Canvas"]) >= 4.5, $"{variant} secondary text is too faint.");
            Assert.True(Contrast(colors["V2.Color.Focus"], colors["V2.Color.Canvas"]) >= 3, $"{variant} focus is too faint.");
        }
    }

    private static IEnumerable<string?> StateIds(JsonElement root) =>
        root.GetProperty("states").EnumerateArray().Select(item => item.GetProperty("id").GetString());

    private static IEnumerable<string?> Strings(JsonElement root, string property) =>
        root.GetProperty(property).EnumerateArray().Select(item => item.GetString());

    private static JsonDocument ReadJson(params string[] segments) => JsonDocument.Parse(File.ReadAllText(PathFor(segments)));

    private static HashSet<string> ReadStringKeys(params string[] segments)
    {
        using var document = ReadJson(segments);
        return document.RootElement.GetProperty("strings").EnumerateObject().Select(property => property.Name).ToHashSet();
    }

    private static string PathFor(params string[] segments) => Path.Combine([RepositoryRoot(), .. segments]);

    private static string? Attribute(string line, string name)
    {
        var prefix = $"{name}=\"";
        var start = line.IndexOf(prefix, StringComparison.Ordinal);
        return start < 0 ? null : line[(start + prefix.Length)..].Split('"')[0];
    }

    private static string Between(string line, string start, string end)
    {
        var from = line.IndexOf(start, StringComparison.Ordinal) + start.Length;
        return line[from..line.IndexOf(end, from, StringComparison.Ordinal)].Trim();
    }

    private static double Contrast(string first, string second)
    {
        static double Luminance(string hex)
        {
            var rgb = Enumerable.Range(1, 3).Select(index => Convert.ToInt32(hex.Substring(index * 2 - 1, 2), 16) / 255d)
                .Select(value => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4)).ToArray();
            return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
        }

        var (lighter, darker) = (Luminance(first), Luminance(second));
        return (Math.Max(lighter, darker) + 0.05) / (Math.Min(lighter, darker) + 0.05);
    }

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
