using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

    static MapSceneGeometryLayer() =>
        AffectsRender<MapSceneGeometryLayer>(ObjectsProperty, StrokeProperty, FillProperty);

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

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Stroke is null || Objects is not { Count: > 0 })
        {
            return;
        }

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
                Brush = Stroke,
                Thickness = item.Truth == MapSceneTruthKind.HistoricalEstimate ? 3 : 2,
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

            context.DrawGeometry(null, pen, geometry);
        }
    }

    private static Point ToPoint(MapSceneProjectedPoint point) => new(point.X, point.Y);
}
