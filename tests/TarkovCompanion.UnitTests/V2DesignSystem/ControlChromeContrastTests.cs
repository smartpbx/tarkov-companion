using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using TarkovCompanion.App.Themes.V2;

namespace TarkovCompanion.UnitTests.V2DesignSystem;

/// <summary>
/// [#266] Measures the colours buttons, text boxes, expanders and map lettering are actually
/// painted with, in dark, light and high contrast.
/// </summary>
/// <remarks>
/// V2ThemeResourceTests holds the V2 roles to their floors, but a button, a text box and an
/// expander paint from the instrument palette (InstrumentPalette.axaml) and from the Fluent keys
/// in ControlChrome.axaml, and nothing measured those. Run against the colours they had before,
/// this test fails on: button, text-box and expander edges at 1.7-2.0:1 in dark and 2.6-2.9:1 in
/// light, floating map-panel edges at 2.3-2.4:1, light's primary button with pale ink on a pale
/// fill at 1.4:1, and light's focus ring at 1.0-1.2:1.
///
/// WCAG 2.x relative luminance. Text and placeholder ink need 4.5:1 on the fill behind it; an
/// edge that marks a control, a focus ring and a chevron need 3:1 against every ground a control
/// sits on (WCAG 1.4.11). Translucent brushes are composited over the ground they float on.
/// </remarks>
public sealed class ControlChromeContrastTests
{
    private const double Text = 4.5;
    private const double Boundary = 3.0;

    /// <summary>Every ground a control is placed on: the instrument grounds and the V2 ones.</summary>
    private static readonly string[] Grounds =
        ["CanvasBrush", "ChromeBrush", "SurfaceBrush", "InsetBrush", "V2.Brush.Canvas", "V2.Brush.Surface"];

    /// <summary>Ink on the fill it is drawn over.</summary>
    private static readonly (string Ink, string Fill, string What)[] TextPairs =
    [
        ("InkBrush", "ControlBrush", "button label"),
        ("InkBrightBrush", "ControlHoverBrush", "button label under the pointer"),
        ("ButtonForeground", "ButtonBackground", "button label (Fluent part)"),
        ("ButtonForegroundPointerOver", "ButtonBackgroundPointerOver", "button label under the pointer (Fluent part)"),
        ("ButtonForegroundPressed", "ButtonBackgroundPressed", "pressed button label"),
        ("PrimaryInkBrush", "CyanDeepBrush", "primary button label"),
        ("PrimaryHoverInkBrush", "PrimaryHoverBrush", "primary button label under the pointer"),
        ("InkMutedBrush", "SurfaceBrush", "quiet button label"),
        ("InkMutedBrush", "CanvasBrush", "quiet button label on the canvas"),
        ("TextControlForeground", "TextControlBackground", "text box entry"),
        ("TextControlForegroundPointerOver", "TextControlBackgroundPointerOver", "text box entry under the pointer"),
        ("TextControlForegroundFocused", "TextControlBackgroundFocused", "focused text box entry"),
        ("TextControlPlaceholderForeground", "TextControlBackground", "text box placeholder"),
        ("TextControlPlaceholderForegroundFocused", "TextControlBackgroundFocused", "focused text box placeholder"),
        ("TextControlPlaceholderForegroundPointerOver", "TextControlBackgroundPointerOver", "text box placeholder under the pointer"),
        ("ExpanderHeaderForeground", "ExpanderHeaderBackground", "expander header"),
        ("ExpanderHeaderForegroundPointerOver", "ExpanderHeaderBackgroundPointerOver", "expander header under the pointer"),
        ("ExpanderHeaderForeground", "V2.Brush.Surface", "expander header on a cleared V2 panel"),
        ("InkBrightBrush", "FloatingBrush", "floating map panel text"),
    ];

    /// <summary>An edge, ring or glyph that must stand out from every ground.</summary>
    private static readonly (string Edge, string What)[] BoundaryRoles =
    [
        ("LineStrongBrush", "button and text box edge"),
        ("ButtonBorderBrush", "button edge (Fluent part)"),
        ("ButtonBorderBrushPointerOver", "button edge under the pointer"),
        ("InkMutedBrush", "button edge under the pointer (InstrumentStyles)"),
        ("CyanBrush", "primary button edge"),
        ("TextControlBorderBrush", "text box edge"),
        ("TextControlBorderBrushPointerOver", "text box edge under the pointer"),
        ("TextControlBorderBrushFocused", "focused text box edge"),
        ("FocusRingBrush", "keyboard focus ring"),
        ("ExpanderHeaderBorderBrush", "expander header edge"),
        ("ExpanderChevronForeground", "expander chevron"),
        ("FloatingLineBrush", "floating map panel edge"),
    ];

    public static TheoryData<string> Variants => ["Dark", "Light", "HighContrast"];

    [Theory]
    [MemberData(nameof(Variants))]
    public void ControlInkMeetsTheTextFloorOnItsFill(string variantName)
    {
        var colours = new Colours(VariantNamed(variantName));
        var failures = new List<string>();

        foreach (var (ink, fill, what) in TextPairs)
        {
            foreach (var ground in Grounds)
            {
                var behind = colours.Over(fill, colours.Opaque(ground));
                var ratio = Contrast(colours.Over(ink, behind), behind);
                if (ratio < Text)
                {
                    failures.Add($"{what}: {ink} on {fill} (over {ground}) is {ratio:F2}:1");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{variantName}:\n" + string.Join('\n', failures.Distinct()));
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void ControlEdgesMeetTheBoundaryFloorOnEveryGround(string variantName)
    {
        var colours = new Colours(VariantNamed(variantName));
        var failures = new List<string>();

        foreach (var (edge, what) in BoundaryRoles)
        {
            foreach (var ground in Grounds)
            {
                var behind = edge == "FloatingLineBrush"
                    ? colours.Over("FloatingBrush", colours.Opaque(ground))
                    : colours.Opaque(ground);
                var ratio = Contrast(colours.Over(edge, behind), behind);
                if (ratio < Boundary)
                {
                    failures.Add($"{what}: {edge} on {ground} is {ratio:F2}:1");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{variantName}:\n" + string.Join('\n', failures));
    }

    /// <summary>
    /// A place name is primary ink over a halo of the ground colour, drawn on the game's artwork,
    /// which does not change with the theme. The halo has to carry the text over anything from
    /// black to white.
    /// </summary>
    [Theory]
    [MemberData(nameof(Variants))]
    public void MapLetteringReadsOverAnyArtwork(string variantName)
    {
        var colours = new Colours(VariantNamed(variantName));

        foreach (var artwork in new[] { Colors.Black, Colors.White, Color.Parse("#E0601C"), Color.Parse("#4A5A3A") })
        {
            var halo = colours.Over("MapHaloBrush", artwork);
            var ratio = Contrast(colours.Over("V2.Brush.TextPrimary", halo), halo);
            Assert.True(ratio >= Text, $"{variantName} place name over {artwork} is {ratio:F2}:1.");
        }
    }

    /// <summary>
    /// High contrast has one ground for all three V2 surfaces, so a pane is only visible by its
    /// edge. In dark and light the divider is the raised surface's colour, a grouping line that
    /// is not held to 3:1.
    /// </summary>
    [Fact]
    public void HighContrastPanelEdgesStandOutFromTheGround()
    {
        var colours = new Colours(V2Appearance.HighContrast);

        foreach (var ground in new[] { "V2.Brush.Canvas", "V2.Brush.Surface", "V2.Brush.SurfaceRaised" })
        {
            var ratio = Contrast(colours.Opaque("V2.Brush.Divider"), colours.Opaque(ground));
            Assert.True(ratio >= Boundary, $"Divider on {ground} is {ratio:F2}:1.");
        }
    }

    private static ThemeVariant VariantNamed(string name) => name switch
    {
        "Dark" => ThemeVariant.Dark,
        "Light" => ThemeVariant.Light,
        _ => V2Appearance.HighContrast,
    };

    private static double Contrast(Color first, Color second)
    {
        var (a, b) = (Luminance(first), Luminance(second));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color colour) =>
        (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));

    private static double Channel(byte value)
    {
        var encoded = value / 255d;
        return encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
    }

    /// <summary>The three dictionaries a control's colours come from, resolved for one variant.</summary>
    private sealed class Colours(ThemeVariant variant)
    {
        private static readonly ResourceDictionary[] Sources =
        [
            Load("Themes/V2/V2ThemeVariants.axaml"),
            Load("Themes/InstrumentPalette.axaml"),
            Load("Themes/ControlChrome.axaml"),
        ];

        public Color Raw(string key)
        {
            foreach (var source in Sources)
            {
                if (source.TryGetResource(key, variant, out var value))
                {
                    return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
                }
            }

            throw new Xunit.Sdk.XunitException($"{key} does not resolve for {variant.Key}.");
        }

        public Color Opaque(string key)
        {
            var colour = Raw(key);
            Assert.True(colour.A == 255, $"{key} is translucent and cannot be a ground.");
            return colour;
        }

        /// <summary>The key's colour composited over <paramref name="behind"/>.</summary>
        public Color Over(string key, Color behind)
        {
            var top = Raw(key);
            var alpha = top.A / 255d;
            byte Mix(byte front, byte back) => (byte)Math.Round((alpha * front) + ((1 - alpha) * back));
            return Color.FromRgb(Mix(top.R, behind.R), Mix(top.G, behind.G), Mix(top.B, behind.B));
        }

        private static ResourceDictionary Load(string path)
        {
            var loader = typeof(V2Appearance).Assembly
                .GetType("CompiledAvaloniaXaml.!XamlLoader")
                ?.GetMethod("TryLoad", [typeof(IServiceProvider), typeof(string)])
                ?? throw new InvalidOperationException("The application assembly has no compiled XAML loader.");

            return Assert.IsType<ResourceDictionary>(loader.Invoke(null, [null, $"avares://TarkovCompanion/{path}"]));
        }
    }
}
