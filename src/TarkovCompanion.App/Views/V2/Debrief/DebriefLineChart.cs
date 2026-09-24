using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TarkovCompanion.App.Views.V2.Debrief;

/// <summary>A line through one value per raid, oldest on the left, zero at the bottom.</summary>
/// <remarks>
/// Drawn with Avalonia's own geometry rather than a chart package: the app has no chart dependency
/// and this needs one line, its points and a baseline. The axis labels are ordinary text beside it,
/// and the same values are the chart's table (DebriefValueChartViewModel.Rows).
/// </remarks>
public sealed class DebriefLineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<DebriefLineChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<DebriefLineChart, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> AxisProperty =
        AvaloniaProperty.Register<DebriefLineChart, IBrush?>(nameof(Axis));

    static DebriefLineChart()
    {
        AffectsRender<DebriefLineChart>(ValuesProperty, StrokeProperty, AxisProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Axis
    {
        get => GetValue(AxisProperty);
        set => SetValue(AxisProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        const double inset = 6;
        var width = bounds.Width - (2 * inset);
        var height = bounds.Height - (2 * inset);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (Axis is { } axis)
        {
            var axisPen = new Pen(axis, 1);
            context.DrawLine(axisPen, new Point(inset, inset + height), new Point(inset + width, inset + height));
            context.DrawLine(axisPen, new Point(inset, inset), new Point(inset, inset + height));
        }

        var values = Values;
        if (values is not { Count: > 0 } || Stroke is not { } stroke)
        {
            return;
        }

        var max = values.Max();
        if (max <= 0)
        {
            max = 1;
        }

        Point At(int index) => new(
            inset + (values.Count == 1 ? width / 2 : width * index / (values.Count - 1)),
            inset + height - (height * Math.Max(0, values[index]) / max));

        var pen = new Pen(stroke, 2);
        for (var index = 1; index < values.Count; index++)
        {
            context.DrawLine(pen, At(index - 1), At(index));
        }

        for (var index = 0; index < values.Count; index++)
        {
            context.DrawEllipse(stroke, null, At(index), 3.5, 3.5);
        }
    }
}
