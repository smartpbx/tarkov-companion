using Avalonia.Media;
using Avalonia.Styling;
using TarkovCompanion.App.Themes.V2;

namespace TarkovCompanion.UnitTests.Personalization;

/// <summary>
/// The V1 instrument palette answers per theme variant, and its type ramp is a resource.
/// </summary>
/// <remarks>
/// This is the half of #266 that made light unusable rather than merely unreachable. The V2
/// workspaces repainted, and the rail, the buttons, the text boxes and the map chrome — all of
/// which paint from InstrumentStyles, not from V2ThemeVariants — stayed dark. Flatten these back
/// into one set and light goes back to grey-on-white; turn a size back into a literal and the
/// text-scale setting stops reaching it.
/// </remarks>
public sealed class InstrumentPaletteVariantTests
{
    public static TheoryData<string> SurfaceAndInkRoles =>
    [
        "CanvasBrush", "ChromeBrush", "SurfaceBrush", "InsetBrush", "ControlBrush", "ControlHoverBrush",
        "LineBrush", "LineStrongBrush", "InkBrightBrush", "InkBrush", "InkMutedBrush", "InkFaintBrush",
        "CyanBrush", "OchreBrush", "CoralBrush", "SageBrush",
    ];

    public static TheoryData<string, double> TypeRamp => new()
    {
        { "Instrument.Type.Display.Size", 26 },
        { "Instrument.Type.Title.Size", 22 },
        { "Instrument.Type.Metric.Size", 20 },
        { "Instrument.Type.Lede.Size", 16 },
        { "Instrument.Type.SectionTitle.Size", 15 },
        { "Instrument.Type.Body.Size", 14 },
        { "Instrument.Type.Label.Size", 12 },
        { "Instrument.Type.Caption.Size", 11 },
    };

    [Theory]
    [MemberData(nameof(SurfaceAndInkRoles))]
    public void EveryPaintedRoleDiffersBetweenLightAndDark(string key)
    {
        var resources = Palette();

        Assert.NotEqual(Color(resources, key, ThemeVariant.Dark), Color(resources, key, ThemeVariant.Light));
    }

    [Fact]
    public void TheDarkPaletteIsWhatItHasAlwaysBeen()
    {
        var resources = Palette();

        Assert.Equal(Avalonia.Media.Color.Parse("#11151B"), Color(resources, "CanvasBrush", ThemeVariant.Dark));
        Assert.Equal(Avalonia.Media.Color.Parse("#C6D0D8"), Color(resources, "InkBrush", ThemeVariant.Dark));
        Assert.Equal(Avalonia.Media.Color.Parse("#56B8C6"), Color(resources, "CyanBrush", ThemeVariant.Dark));
    }

    [Fact]
    public void TheCustomVariantsFallThroughToTheOneTheyInherit()
    {
        var resources = Palette();

        // V2Appearance's high-contrast and colour-vision variants override only the V2.* status
        // roles; everything the instrument palette paints has to resolve through inheritance.
        Assert.Equal(
            Color(resources, "CanvasBrush", ThemeVariant.Dark),
            Color(resources, "CanvasBrush", V2Appearance.HighContrast));
        Assert.Equal(
            Color(resources, "CanvasBrush", ThemeVariant.Light),
            Color(resources, "CanvasBrush", V2Appearance.LightMonochrome));
    }

    [Theory]
    [MemberData(nameof(TypeRamp))]
    public void TheTypeRampIsAResourceTheTextScaleCanMultiply(string key, double size)
    {
        var resources = Palette();

        Assert.True(resources.TryGetResource(key, ThemeVariant.Dark, out var value), $"{key} does not resolve.");
        Assert.Equal(size, Assert.IsType<double>(value));
    }

    private static Avalonia.Controls.IResourceDictionary Palette() =>
        Assert.IsType<Avalonia.Controls.ResourceDictionary>(LoadCompiledXaml("Themes/InstrumentPalette.axaml"));

    private static Color Color(Avalonia.Controls.IResourceDictionary resources, string key, ThemeVariant variant)
    {
        Assert.True(resources.TryGetResource(key, variant, out var value), $"{key} does not resolve for {variant.Key}.");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static object LoadCompiledXaml(string path)
    {
        var loader = typeof(V2Appearance).Assembly
            .GetType("CompiledAvaloniaXaml.!XamlLoader")
            ?.GetMethod("TryLoad", [typeof(IServiceProvider), typeof(string)])
            ?? throw new InvalidOperationException("The application assembly has no compiled XAML loader.");

        return loader.Invoke(null, [null, $"avares://TarkovCompanion/{path}"])
            ?? throw new InvalidOperationException($"No compiled XAML was found for {path}.");
    }
}
