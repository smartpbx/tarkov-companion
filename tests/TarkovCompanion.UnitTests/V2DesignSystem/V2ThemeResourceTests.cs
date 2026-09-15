using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using TarkovCompanion.App.Themes.V2;
using static TarkovCompanion.UnitTests.V2DesignSystem.V2DesignSystemFiles;

namespace TarkovCompanion.UnitTests.V2DesignSystem;

/// <summary>
/// Loads the compiled V2 dictionaries and measures what Avalonia actually resolves for each theme
/// variant, inheritance included.
/// </summary>
/// <remarks>
/// The dictionaries are built through the generated <c>CompiledAvaloniaXaml.!XamlLoader.TryLoad</c>
/// that AvaloniaXamlLoader itself calls, which needs no Application or windowing platform. Only
/// resource lookups run here; nothing is bound, laid out, or rendered.
///
/// Contrast uses WCAG 2.x relative luminance. Colour-vision separation simulates protanopia,
/// deuteranopia, and tritanopia with the Machado, Oliveira and Fernandes (2009) matrices at full
/// severity in linear sRGB, then takes the CIE76 distance in CIELAB (D65). A simulation is a model
/// of typical dichromacy, not a person; #279 keeps the human colour-vision review.
/// </remarks>
public sealed class V2ThemeResourceTests
{
    private const double TextContrastFloor = 4.5;
    private const double BoundaryContrastFloor = 3.0;
    private const double DecisionSeparationFloor = 40;
    private const double NeutralSeparationFloor = 15;

    private static readonly string[] SurfaceRoles = ["Canvas", "Surface", "SurfaceRaised"];
    private static readonly string[] TextRoles = ["TextPrimary", "TextSecondary", "Action", "Success", "Warning", "Danger", "Info", "Unknown"];
    private static readonly string[] StatusRoles = ["Success", "Warning", "Danger", "Info", "Unknown"];
    private static readonly string[] DecisionRoles = ["Success", "Warning", "Danger"];
    private static readonly string[] NeutralRoles = ["Info", "Unknown"];

    private static readonly Dictionary<string, double[,]> Deficiencies = new(StringComparer.Ordinal)
    {
        ["protanopia"] = new[,] { { 0.152286, 1.052583, -0.204868 }, { 0.114503, 0.786281, 0.099216 }, { -0.003882, -0.048116, 1.051998 } },
        ["deuteranopia"] = new[,] { { 0.367322, 0.860646, -0.227968 }, { 0.280085, 0.672501, 0.047413 }, { -0.011820, 0.042940, 0.968881 } },
        ["tritanopia"] = new[,] { { 1.255528, -0.076749, -0.178779 }, { -0.078411, 0.930809, 0.147602 }, { 0.004733, 0.691367, 0.303900 } },
    };

    public static TheoryData<string> AllVariants => new()
    {
        "Dark", "Light", "HighContrast",
        "DarkRedGreenSafe", "LightRedGreenSafe", "DarkBlueYellowSafe", "LightBlueYellowSafe", "DarkMonochrome", "LightMonochrome",
    };

    [Fact]
    public void HostEntryPointLoadsAndMergesThemesTokensAndStrings()
    {
        var resources = Assert.IsType<ResourceDictionary>(LoadCompiledXaml("Themes/V2/V2Resources.axaml"));

        Assert.True(resources.TryGetResource("V2.Brush.Canvas", ThemeVariant.Dark, out var canvas));
        Assert.IsAssignableFrom<ISolidColorBrush>(canvas);
        Assert.True(resources.TryGetResource("V2.Type.Body.Size", ThemeVariant.Dark, out var body));
        Assert.Equal(16d, Assert.IsType<double>(body));
        Assert.True(resources.TryGetResource("V2.String.Gallery.Title", ThemeVariant.Dark, out var title));
        Assert.Equal("Primitive gallery", Assert.IsType<string>(title));
    }

    [Fact]
    public void TokensLoadWithTheTypesTheirSettersNeed()
    {
        var tokens = Assert.IsType<ResourceDictionary>(LoadCompiledXaml("Themes/V2/V2Tokens.axaml"));

        // A DynamicResource of the wrong type is not converted: the first version bound a double to
        // BorderThickness, which would have left the focus indicator unset.
        Assert.IsType<Thickness>(Resolve(tokens, "V2.Focus.Indicator.Thickness", ThemeVariant.Dark));
        Assert.IsType<Thickness>(Resolve(tokens, "V2.Space.Md.Inset", ThemeVariant.Dark));
        Assert.IsType<double>(Resolve(tokens, "V2.Space.Md.Gap", ThemeVariant.Dark));
        Assert.IsType<Thickness>(Resolve(tokens, "V2.Density.Compact.Inset", ThemeVariant.Dark));
        Assert.IsType<CornerRadius>(Resolve(tokens, "V2.Shape.Card", ThemeVariant.Dark));
        Assert.Equal(10d, Assert.IsType<double>(Resolve(tokens, "V2.Shape.Card.Radius", ThemeVariant.Dark)));
        Assert.Equal(6d, Assert.IsType<double>(Resolve(tokens, "V2.Shape.Control.Radius", ThemeVariant.Dark)));
        Assert.Equal("⚠", Assert.IsType<string>(Resolve(tokens, "V2.Glyph.WarningSign", ThemeVariant.Dark)));
        Assert.Equal(TimeSpan.FromMilliseconds(160), Assert.IsType<TimeSpan>(Resolve(tokens, "V2.Motion.Full.Duration", ThemeVariant.Dark)));
        Assert.Equal(TimeSpan.Zero, Assert.IsType<TimeSpan>(Resolve(tokens, "V2.Motion.Reduced.Duration", ThemeVariant.Dark)));
    }

    [Theory]
    [MemberData(nameof(AllVariants))]
    public void EveryVariantResolvesEveryColourRoleAndElevation(string variantName)
    {
        var variant = VariantNamed(variantName);
        var themes = LoadThemes();

        Assert.Equal(ManifestColourRoles().Length, Palette(themes, variant).Count);
        foreach (var level in new[] { "Flat", "Raised", "Dialog" })
        {
            Assert.IsType<BoxShadows>(Resolve(themes, $"V2.Elevation.{level}.Shadow", variant));
        }
    }

    [Theory]
    [InlineData("DarkRedGreenSafe", "Dark")]
    [InlineData("LightRedGreenSafe", "Light")]
    [InlineData("DarkBlueYellowSafe", "Dark")]
    [InlineData("LightBlueYellowSafe", "Light")]
    [InlineData("DarkMonochrome", "Dark")]
    [InlineData("LightMonochrome", "Light")]
    public void ColourVisionVariantsInheritEverythingButStatus(string variantName, string parentName)
    {
        var themes = LoadThemes();
        var variant = VariantNamed(variantName);
        var parent = VariantNamed(parentName);
        var own = Palette(themes, variant);
        var inherited = Palette(themes, parent);

        Assert.Equal(parent, variant.InheritVariant);
        foreach (var (role, colour) in inherited)
        {
            if (Array.IndexOf(StatusRoles, role) < 0)
            {
                Assert.Equal(colour, own[role]);
            }
        }

        Assert.NotEqual(inherited["Success"], own["Success"]);
    }

    [Theory]
    [MemberData(nameof(AllVariants))]
    public void TextStatusAndBoundaryColoursMeetTheirContrastFloors(string variantName)
    {
        var palette = Palette(LoadThemes(), VariantNamed(variantName));
        var boundaries = palette.Keys.Where(role => role is "Border" or "Focus" || role.StartsWith("Chart.", StringComparison.Ordinal) || role.StartsWith("Map.", StringComparison.Ordinal)).ToArray();

        foreach (var surface in SurfaceRoles)
        {
            foreach (var role in TextRoles)
            {
                var ratio = Contrast(palette[role], palette[surface]);
                Assert.True(ratio >= TextContrastFloor, $"{variantName} {role} on {surface} is {ratio:F2}:1.");
            }

            foreach (var role in boundaries)
            {
                var ratio = Contrast(palette[role], palette[surface]);
                Assert.True(ratio >= BoundaryContrastFloor, $"{variantName} {role} on {surface} is {ratio:F2}:1.");
            }
        }
    }

    [Theory]
    [InlineData("Dark", "")]
    [InlineData("Light", "")]
    [InlineData("HighContrast", "protanopia,deuteranopia,tritanopia")]
    [InlineData("DarkRedGreenSafe", "protanopia,deuteranopia")]
    [InlineData("LightRedGreenSafe", "protanopia,deuteranopia")]
    [InlineData("DarkBlueYellowSafe", "tritanopia")]
    [InlineData("LightBlueYellowSafe", "tritanopia")]
    public void DecisionColoursStaySeparateForTheVisionEachVariantClaims(string variantName, string claimed)
    {
        var palette = Palette(LoadThemes(), VariantNamed(variantName));
        var deficiencies = claimed.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var vision in deficiencies.Prepend("typical"))
        {
            for (var first = 0; first < DecisionRoles.Length; first++)
            {
                for (var second = first + 1; second < DecisionRoles.Length; second++)
                {
                    var distance = Distance(palette[DecisionRoles[first]], palette[DecisionRoles[second]], vision);
                    Assert.True(distance >= DecisionSeparationFloor, $"{variantName} {DecisionRoles[first]}/{DecisionRoles[second]} under {vision} vision differ by only {distance:F1} ΔE.");
                }
            }

            if (deficiencies.Length == 0)
            {
                continue;
            }

            foreach (var neutral in NeutralRoles)
            {
                foreach (var decision in DecisionRoles)
                {
                    var distance = Distance(palette[neutral], palette[decision], vision);
                    Assert.True(distance >= NeutralSeparationFloor, $"{variantName} {neutral} could pass for {decision} under {vision} vision ({distance:F1} ΔE).");
                }
            }
        }
    }

    [Theory]
    [InlineData("DarkMonochrome")]
    [InlineData("LightMonochrome")]
    public void MonochromeVariantsCarryNoStatusHue(string variantName)
    {
        var palette = Palette(LoadThemes(), VariantNamed(variantName));

        Assert.All(StatusRoles, role => Assert.Equal(palette["TextPrimary"], palette[role]));
    }

    [Fact]
    public void ResolverLetsTheSystemContrastThemeWinAndPairsColourVisionWithLightness()
    {
        Assert.Equal(ThemeVariant.Dark, V2Appearance.Resolve(V2AppearancePreference.System, V2ColorVisionPreference.Standard, systemPrefersDark: true, systemRequestsHighContrast: false));
        Assert.Equal(ThemeVariant.Light, V2Appearance.Resolve(V2AppearancePreference.System, V2ColorVisionPreference.Standard, systemPrefersDark: false, systemRequestsHighContrast: false));
        Assert.Equal(ThemeVariant.Dark, V2Appearance.Resolve(V2AppearancePreference.Dark, V2ColorVisionPreference.Standard, systemPrefersDark: false, systemRequestsHighContrast: false));
        Assert.Equal(V2Appearance.LightRedGreenSafe, V2Appearance.Resolve(V2AppearancePreference.Light, V2ColorVisionPreference.RedGreenSafe, systemPrefersDark: true, systemRequestsHighContrast: false));
        Assert.Equal(V2Appearance.DarkBlueYellowSafe, V2Appearance.Resolve(V2AppearancePreference.System, V2ColorVisionPreference.BlueYellowSafe, systemPrefersDark: true, systemRequestsHighContrast: false));
        Assert.Equal(V2Appearance.DarkMonochrome, V2Appearance.Resolve(V2AppearancePreference.Dark, V2ColorVisionPreference.Monochrome, systemPrefersDark: false, systemRequestsHighContrast: false));
        Assert.Equal(V2Appearance.HighContrast, V2Appearance.Resolve(V2AppearancePreference.HighContrast, V2ColorVisionPreference.Standard, systemPrefersDark: false, systemRequestsHighContrast: false));
        Assert.Equal(V2Appearance.HighContrast, V2Appearance.Resolve(V2AppearancePreference.Light, V2ColorVisionPreference.RedGreenSafe, systemPrefersDark: false, systemRequestsHighContrast: true));
    }

    private static ThemeVariant VariantNamed(string name) => name switch
    {
        "Dark" => ThemeVariant.Dark,
        "Light" => ThemeVariant.Light,
        _ => Assert.IsType<ThemeVariant>(typeof(V2Appearance).GetProperty(name)?.GetValue(null)),
    };

    private static ResourceDictionary LoadThemes() =>
        Assert.IsType<ResourceDictionary>(LoadCompiledXaml("Themes/V2/V2ThemeVariants.axaml"));

    private static object LoadCompiledXaml(string path)
    {
        var loader = typeof(V2Appearance).Assembly
            .GetType("CompiledAvaloniaXaml.!XamlLoader")
            ?.GetMethod("TryLoad", new[] { typeof(IServiceProvider), typeof(string) })
            ?? throw new InvalidOperationException("The application assembly has no compiled XAML loader.");

        return loader.Invoke(null, new object?[] { null, $"avares://TarkovCompanion/{path}" })
            ?? throw new InvalidOperationException($"No compiled XAML was found for {path}.");
    }

    private static object Resolve(ResourceDictionary dictionary, string key, ThemeVariant variant)
    {
        var found = dictionary.TryGetResource(key, variant, out var value);
        Assert.True(found, $"{key} does not resolve for {variant.Key}.");
        return value!;
    }

    /// <summary>Manifest colour, chart, and map tokens as role names such as Success or Chart.Series1.</summary>
    private static string[] ManifestColourRoles()
    {
        using var manifest = ReadJson(ManifestPath);
        var tokens = manifest.RootElement.GetProperty("tokens");
        return Strings(tokens, "color").Select(Pascal)
            .Concat(Strings(tokens, "chart").Select(token => "Chart." + Pascal(token)))
            .Concat(Strings(tokens, "map").Select(token => "Map." + Pascal(token)))
            .ToArray();
    }

    private static Dictionary<string, Color> Palette(ResourceDictionary themes, ThemeVariant variant) =>
        ManifestColourRoles().ToDictionary(
            role => role,
            role => Assert.IsAssignableFrom<ISolidColorBrush>(Resolve(themes, $"V2.Brush.{role}", variant)).Color,
            StringComparer.Ordinal);

    private static double Contrast(Color first, Color second)
    {
        var (a, b) = (Luminance(Linear(first)), Luminance(Linear(second)));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance((double R, double G, double B) linear) =>
        (0.2126 * linear.R) + (0.7152 * linear.G) + (0.0722 * linear.B);

    private static (double R, double G, double B) Linear(Color colour) =>
        (Channel(colour.R), Channel(colour.G), Channel(colour.B));

    private static double Channel(byte value)
    {
        var encoded = value / 255d;
        return encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
    }

    private static double Distance(Color first, Color second, string vision)
    {
        var (l1, a1, b1) = Lab(Simulate(first, vision));
        var (l2, a2, b2) = Lab(Simulate(second, vision));
        return Math.Sqrt(Math.Pow(l1 - l2, 2) + Math.Pow(a1 - a2, 2) + Math.Pow(b1 - b2, 2));
    }

    private static (double R, double G, double B) Simulate(Color colour, string vision)
    {
        var linear = Linear(colour);
        if (!Deficiencies.TryGetValue(vision, out var matrix))
        {
            return linear;
        }

        var simulated = new double[3];
        for (var row = 0; row < 3; row++)
        {
            simulated[row] = Math.Clamp((matrix[row, 0] * linear.R) + (matrix[row, 1] * linear.G) + (matrix[row, 2] * linear.B), 0d, 1d);
        }

        return (simulated[0], simulated[1], simulated[2]);
    }

    private static (double L, double A, double B) Lab((double R, double G, double B) linear)
    {
        var x = ((0.4124 * linear.R) + (0.3576 * linear.G) + (0.1805 * linear.B)) / 0.95047;
        var y = (0.2126 * linear.R) + (0.7152 * linear.G) + (0.0722 * linear.B);
        var z = ((0.0193 * linear.R) + (0.1192 * linear.G) + (0.9505 * linear.B)) / 1.08883;
        var (fx, fy, fz) = (Pivot(x), Pivot(y), Pivot(z));
        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static double Pivot(double value) => value > 0.008856 ? Math.Cbrt(value) : (7.787 * value) + (16d / 116);
}
