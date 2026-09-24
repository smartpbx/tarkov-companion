using Avalonia;
using Avalonia.Controls;

namespace TarkovCompanion.App.Views.V2.Primitives;

/// <summary>
/// Two children on one line — the first filling, the second at the right — while the first fits
/// at its natural width; otherwise the second drops to its own line under the first.
/// </summary>
/// <remarks>
/// [#314] A <c>*,Auto</c> grid keeps the pair on one line whatever the text, so a longer language
/// either wrapped a heading one word a line ("Next raid" beside Learn and Export) or cut a text
/// box's placeholder off ("Name (blank = numbered)" beside Rename, already cut in English). Here
/// the first child is measured unconstrained first; only when that and the second do not fit side
/// by side does the pair stack, and then the first gets the whole width. On one line it arranges
/// exactly as the grid did, so English that fitted looks the same.
/// On its own line the second child is arranged across the full width, so its own
/// <see cref="Layoutable.HorizontalAlignment"/> places it.
/// </remarks>
public sealed class V2FitRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<V2FitRow, double>(nameof(Spacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<V2FitRow, double>(nameof(RowSpacing), 4);

    static V2FitRow()
    {
        AffectsMeasure<V2FitRow>(SpacingProperty, RowSpacingProperty);
    }

    /// <summary>The gap between the two children on one line.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The gap between the two lines once stacked.</summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>Whether the last measure put the second child on its own line.</summary>
    public bool IsStacked { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (first, second) = Pair();
        if (first is null)
        {
            return default;
        }

        if (second is null)
        {
            first.Measure(availableSize);
            IsStacked = false;
            return first.DesiredSize;
        }

        second.Measure(availableSize);
        first.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var natural = first.DesiredSize.Width;
        IsStacked = natural + Spacing + second.DesiredSize.Width > availableSize.Width;
        if (IsStacked)
        {
            first.Measure(availableSize);
            return new Size(
                Math.Max(first.DesiredSize.Width, second.DesiredSize.Width),
                first.DesiredSize.Height + RowSpacing + second.DesiredSize.Height);
        }

        var left = double.IsPositiveInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - Spacing - second.DesiredSize.Width);
        first.Measure(new Size(left, availableSize.Height));
        return new Size(
            first.DesiredSize.Width + Spacing + second.DesiredSize.Width,
            Math.Max(first.DesiredSize.Height, second.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (first, second) = Pair();
        if (first is null)
        {
            return finalSize;
        }

        if (second is null)
        {
            first.Arrange(new Rect(finalSize));
            return finalSize;
        }

        if (IsStacked)
        {
            var top = first.DesiredSize.Height;
            first.Arrange(new Rect(0, 0, finalSize.Width, top));
            second.Arrange(new Rect(0, top + RowSpacing, finalSize.Width, Math.Max(0, finalSize.Height - top - RowSpacing)));
            return finalSize;
        }

        var secondWidth = Math.Min(second.DesiredSize.Width, finalSize.Width);
        var firstWidth = Math.Max(0, finalSize.Width - secondWidth - Spacing);
        first.Arrange(new Rect(0, 0, firstWidth, finalSize.Height));
        second.Arrange(new Rect(finalSize.Width - secondWidth, 0, secondWidth, finalSize.Height));
        return finalSize;
    }

    private (Control? First, Control? Second) Pair()
    {
        Control? first = null;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            if (first is null)
            {
                first = child;
            }
            else
            {
                return (first, child);
            }
        }

        return (first, null);
    }
}
