using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.Views.V2.MapRenderer;

/// <summary>Draws sourced scene geometry as geometry, never as an invented point marker.</summary>
public sealed class MapSceneGeometryLayer : Control
{
    public static readonly StyledProperty<IReadOnlyList<MapSceneRendererGeometryViewModel>?> ObjectsProperty =
        AvaloniaProperty.Register<MapSceneGeometryLayer, IReadOnlyList<MapSceneRendererGeometryViewModel>?>(nameof(Objects));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<MapSceneGeometryLayer, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<MapSceneGeometryLayer, IBrush?>(nameof(Fill));

    /// <summary>The line for a path somebody's own screenshots put them on.</summary>
    public static readonly StyledProperty<IBrush?> LocalStrokeProperty =
        AvaloniaProperty.Register<MapSceneGeometryLayer, IBrush?>(nameof(LocalStroke));

    /// <summary>The line for a path the group shared.</summary>
    public static readonly StyledProperty<IBrush?> TeamStrokeProperty =
        AvaloniaProperty.Register<MapSceneGeometryLayer, IBrush?>(nameof(TeamStroke));

    /// <summary>
    /// The camera zoom the plan is drawn at. Lines live inside the zoomed surface, so a thickness
    /// meant to be a few pixels on screen has to be divided by it or a trail turns into a ribbon
    /// as soon as somebody zooms in — which is exactly when they are following it.
    /// </summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<MapSceneGeometryLayer, double>(nameof(Zoom), 1d);

    static MapSceneGeometryLayer() =>
        AffectsRender<MapSceneGeometryLayer>(
            ObjectsProperty,
            StrokeProperty,
            FillProperty,
            LocalStrokeProperty,
            TeamStrokeProperty,
            ZoomProperty);

    public IReadOnlyList<MapSceneRendererGeometryViewModel>? Objects
    {
        get => GetValue(ObjectsProperty);
        set => SetValue(ObjectsProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? LocalStroke
    {
        get => GetValue(LocalStrokeProperty);
        set => SetValue(LocalStrokeProperty, value);
    }

    public IBrush? TeamStroke
    {
        get => GetValue(TeamStrokeProperty);
        set => SetValue(TeamStrokeProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Stroke is null || Objects is not { Count: > 0 })
        {
            return;
        }

        var zoom = double.IsFinite(Zoom) && Zoom > 0.01 ? Zoom : 1;
        foreach (var item in Objects)
        {
            if (item.Points.Count < 2 || item.Points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            {
                continue;
            }

            var closes = item.Kind is MapSceneGeometryKind.Area or MapSceneGeometryKind.Region;
            var geometry = new StreamGeometry();
            using (var builder = geometry.Open())
            {
                builder.BeginFigure(ToPoint(item.Points[0]), closes);
                for (var index = 1; index < item.Points.Count; index++)
                {
                    builder.LineTo(ToPoint(item.Points[index]), true);
                }

                builder.EndFigure(closes);
            }

            var pen = new Pen
            {
                Brush = BrushFor(item),
                Thickness = (item.ThicknessHint ?? ThicknessFor(item)) / zoom,
                DashStyle = item.Truth is MapSceneTruthKind.HistoricalEstimate or MapSceneTruthKind.PotentialSpawn
                    ? DashStyle.Dash
                    : null,
                LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            if (closes && Fill is not null)
            {
                using (context.PushOpacity(item.Truth == MapSceneTruthKind.HistoricalEstimate ? 0.1 : 0.16))
                {
                    context.DrawGeometry(Fill, null, geometry);
                }
            }

            using (context.PushOpacity(Math.Clamp(item.OpacityHint, 0, 1)))
            {
                context.DrawGeometry(null, pen, geometry);
            }
        }
    }

    /// <summary>
    /// Where this line came from decides how it is drawn: a squadmate's own colour when the host
    /// named one, then a colour per kind of evidence, then the layer's default.
    /// </summary>
    private IBrush? BrushFor(MapSceneRendererGeometryViewModel item) =>
        ParseHint(item.ColorHint) ?? item.Truth switch
        {
            MapSceneTruthKind.LocalLastKnown => LocalStroke ?? Stroke,
            MapSceneTruthKind.TeamSharedLastKnown => TeamStroke ?? Stroke,
            _ => Stroke,
        };

    /// <summary>Your own path is the one being followed, so it is the one drawn boldest.</summary>
    private static double ThicknessFor(MapSceneRendererGeometryViewModel item) => item.Truth switch
    {
        MapSceneTruthKind.LocalLastKnown => 3,
        MapSceneTruthKind.TeamSharedLastKnown => 2.5,
        MapSceneTruthKind.HistoricalEstimate => 3,
        _ => 2,
    };

    private static IBrush? ParseHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint) || !Color.TryParse(hint, out var color))
        {
            return null;
        }

        return new ImmutableSolidColorBrush(color);
    }

    private static Point ToPoint(MapSceneProjectedPoint point) => new(point.X, point.Y);
}
