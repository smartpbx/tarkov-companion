using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#286] "--mode-demo inspect|route|navigate": the Raid map in one of its modes, driven through
/// the same view-model calls a click makes, so a render shows the lit mode icon and its bar.
/// </summary>
internal static class ModesDemo
{
    public static void Run(RaidCockpitViewModel raid, string mode, Action<int> pump)
    {
        if (raid.Renderer is not { } renderer)
        {
            Console.Error.WriteLine("Mode demo: the map is not open.");
            return;
        }

        if (raid.FollowsPlayer)
        {
            raid.ToggleFollowCommand.Execute(null);
        }

        renderer.FitPlanCommand.Execute(null);
        pump(20);
        var bounds = renderer.Scene.Bounds;
        MapScenePoint At(double fx, double fy) =>
            new(bounds.MinimumX + (bounds.Width * fx), bounds.MinimumY + (bounds.Height * fy));

        switch (mode)
        {
            case "inspect":
                raid.SetInteractionMode(MapInteractionMode.Inspect);
                // A spot with something on it: the objective, else the place name, nearest the middle.
                var middle = At(0.5, 0.5);
                var target = renderer.Scene.VisibleObjects
                    .Where(item => item.Kind is MapSceneObjectKind.QuestObjective or MapSceneObjectKind.Label)
                    .OrderBy(item => item.Kind == MapSceneObjectKind.QuestObjective ? 0 : 1)
                    .ThenBy(item => Math.Pow(item.Geometry.Bounds.MinimumX - middle.X, 2) + Math.Pow(item.Geometry.Bounds.MinimumY - middle.Y, 2))
                    .Select(item => (MapScenePoint?)item.Geometry.Points[0])
                    .FirstOrDefault() ?? middle;
                pump(10);
                raid.ModeClicked(new(target.X + (bounds.Width * 0.01), target.Y + (bounds.Height * 0.01)));
                pump(40);
                Console.WriteLine($"Inspect: {raid.InspectTitle} | " + string.Join(" | ", raid.InspectSections.Select(section => $"{section.Title}: {string.Join("; ", section.Lines)}")));
                break;
            case "route":
                raid.SetInteractionMode(MapInteractionMode.Route);
                foreach (var (fx, fy) in new[] { (0.30, 0.62), (0.38, 0.52), (0.47, 0.56), (0.56, 0.47), (0.63, 0.36) })
                {
                    raid.ModeClicked(At(fx, fy));
                    pump(5);
                }

                pump(80);
                Console.WriteLine($"Route: {raid.PlannedRouteSummary} | marks {string.Join(",", raid.Marks.Select(mark => mark.Label))}");
                break;
            default:
                raid.SetInteractionMode(MapInteractionMode.Navigate);
                pump(10);
                break;
        }
    }
}
