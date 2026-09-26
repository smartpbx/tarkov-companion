using Avalonia.Controls;
using Avalonia.VisualTree;
using TarkovCompanion.App.ViewModels.V2.Raid;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#929] What a right-click on the Raid plan would do, over a grid of the whole plan.
/// </summary>
/// <remarks>
/// "I still couldn't ping the map when I died from a scav raid." The renderer turns a right-click
/// that hits any drawn object into a removal and one on bare map into a placement, so a map
/// covered in hit-testable shapes swallowed pings. This counts, per grid cell, which of the two a
/// press would be (asking the renderer what the view asks it) and what took it, then places one
/// ping through the cockpit.
/// </remarks>
internal static class MarkGateProbe
{
    public static void Run(Window window, RaidCockpitViewModel raid)
    {
        if (raid.Renderer is not { } renderer ||
            window.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == "PlanViewport") is not { } viewport)
        {
            Console.WriteLine("Mark gate probe: no renderer or viewport.");
            return;
        }

        const int Columns = 24;
        const int Rows = 14;
        var width = viewport.Bounds.Width;
        var height = viewport.Bounds.Height;
        var placements = 0;
        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        (double X, double Y)? free = null;
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var x = (column + 0.5) * width / Columns;
                var y = (row + 0.5) * height / Rows;
                if (renderer.TryHitRightClickTargetAt(x, y, out var id))
                {
                    var hit = renderer.Scene.Objects.FirstOrDefault(item => item.Id == id);
                    var key = $"{hit?.LayerId}/{hit?.Kind}/{hit?.Geometry.Kind}";
                    byKind[key] = byKind.GetValueOrDefault(key) + 1;
                }
                else if (renderer.TryScenePointAt(x, y, out _))
                {
                    placements++;
                    free ??= (x, y);
                }
            }
        }

        Console.WriteLine($"Mark gate probe: viewport {width:0}x{height:0}, {placements} of {Columns * Rows} presses would place a mark, scope {raid.NewMarkScope}.");
        foreach (var (key, count) in byKind.OrderByDescending(item => item.Value))
        {
            Console.WriteLine($"  swallowed by {key}: {count}");
        }

        if (free is { } at && renderer.TryScenePointAt(at.X, at.Y, out var point))
        {
            var before = raid.Marks.Count;
            raid.PlaceMarkAt(point, RaidCockpitViewModel.MarkKindFor(false));
            Console.WriteLine($"  placed a ping: marks {before} -> {raid.Marks.Count} (list may lag a turn)");
        }
    }
}
