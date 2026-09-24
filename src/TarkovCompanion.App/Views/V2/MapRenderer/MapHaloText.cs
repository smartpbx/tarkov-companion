using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace TarkovCompanion.App.Views.V2.MapRenderer;

/// <summary>
/// One line of lettering on the map with a halo of the ground colour stroked behind every glyph.
/// </summary>
/// <remarks>
/// [#266] A place name used to be a plain TextBlock in the theme's secondary ink. The game's
/// artwork does not change with the application's theme, so the light theme wrote dark grey
/// over dark satellite imagery and over the orange traffic heat, and "Warehouse 17" and
/// "Repair Shop" were close to unreadable in the committed light capture. A halo in the ground
/// colour gives the text its own background whatever is under it; the ink-on-halo pair is
/// measured in ControlChromeContrastTests.
///
/// Drawn as one geometry, stroked then filled, rather than as a render effect or as V1's five
/// stacked TextBlocks: up to MapSceneRendererViewModel.MaximumPlaceNames of these can be on the
/// plan at once, and the geometry is built once per text and size, not once per frame.
/// </remarks>
public sealed class MapHaloText : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MapHaloText, string?>(nameof(Text));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<MapHaloText>();

    public static readonly StyledProperty<IBrush?> HaloProperty =
        AvaloniaProperty.Register<MapHaloText, IBrush?>(nameof(Halo));

    /// <summary>Halo width on each side of a glyph edge, in unscaled pixels.</summary>
    public static readonly StyledProperty<double> HaloThicknessProperty =
        AvaloniaProperty.Register<MapHaloText, double>(nameof(HaloThickness), 2.5);

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<MapHaloText>();

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<MapHaloText>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner<MapHaloText>();

    private Geometry? _geometry;
    private double _geometryWidth = double.NaN;

    static MapHaloText()
    {
        AffectsMeasure<MapHaloText>(TextProperty, FontSizeProperty, FontFamilyProperty, FontWeightProperty, HaloThicknessProperty);
        AffectsRender<MapHaloText>(ForegroundProperty, HaloProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? Halo
    {
        get => GetValue(HaloProperty);
        set => SetValue(HaloProperty, value);
    }

    public double HaloThickness
    {
        get => GetValue(HaloThicknessProperty);
        set => SetValue(HaloThicknessProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == FontSizeProperty
            || change.Property == FontFamilyProperty || change.Property == FontWeightProperty
            || change.Property == WidthProperty)
        {
            _geometry = null;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsNaN(Width) ? availableSize.Width : Width;
        var text = Build(width);
        if (text is null)
        {
            return default;
        }

        var pad = HaloThickness;
        return new Size(
            double.IsInfinity(width) ? text.Width + (pad * 2) : width,
            text.Height + (pad * 2));
    }

    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        if (_geometry is null || _geometryWidth != Bounds.Width)
        {
            _geometryWidth = Bounds.Width;
            var text = Build(Bounds.Width);
            if (text is null)
            {
                return;
            }

            // Centred horizontally inside the box the label view model gives it, the same as the
            // TextAlignment="Center" TextBlock this replaced.
            var left = Math.Max(0, (Bounds.Width - text.Width) / 2);
            _geometry = text.BuildGeometry(new Point(left, HaloThickness));
        }

        if (_geometry is null)
        {
            return;
        }

        if (Halo is { } halo && HaloThickness > 0)
        {
            context.DrawGeometry(null, new Pen(halo, HaloThickness * 2, lineJoin: PenLineJoin.Round), _geometry);
        }

        context.DrawGeometry(Foreground, null, _geometry);
    }

    private FormattedText? Build(double width)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return null;
        }

        var text = new FormattedText(
            Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle.Normal, FontWeight),
            FontSize,
            Foreground);
        if (!double.IsInfinity(width) && width > 0)
        {
            text.MaxTextWidth = Math.Max(1, width - (HaloThickness * 2));
            text.MaxLineCount = 1;
            text.Trimming = TextTrimming.CharacterEllipsis;
        }

        return text;
    }
}
