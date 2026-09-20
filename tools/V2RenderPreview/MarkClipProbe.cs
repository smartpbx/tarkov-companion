using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [Issue 551] Says what, between a map mark and the map card, clips it.
/// </summary>
/// <remarks>
/// The owner's build showed pins, pings and the facing cone cut along a straight edge, and the
/// renders made when the pins were introduced did not. This prints, for one mark of each kind,
/// every ancestor up to the plan viewport with its bounds and whether it clips, so the cause is
/// read off a list rather than guessed.
/// </remarks>
internal static class MarkClipProbe
{
    public static void Run(Window window)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var button in window.GetVisualDescendants().OfType<ToggleButton>()
                     .Where(candidate => candidate.Classes.Contains("v2-map-marker")))
        {
            var kind = button.DataContext is TarkovCompanion.App.ViewModels.V2.MapRenderer.MapSceneRendererObjectViewModel mark
                ? mark.Icon.ToString()
                : "?";
            if (!seen.Add(kind))
            {
                continue;
            }

            Console.WriteLine($"Mark clip probe: {kind}");
            foreach (var visual in button.GetVisualDescendants().Prepend(button))
            {
                if (visual is not { IsVisible: true } || (!visual.ClipToBounds && visual.Clip is null))
                {
                    continue;
                }

                Console.WriteLine($"  inside: {visual.GetType().Name} bounds {visual.Bounds} clipToBounds {visual.ClipToBounds} clip {visual.Clip}");
            }

            foreach (var ancestor in button.GetVisualAncestors())
            {
                Console.WriteLine($"  above: {ancestor.GetType().Name} {(ancestor as Control)?.Name} bounds {ancestor.Bounds} clipToBounds {ancestor.ClipToBounds} clip {ancestor.Clip}");
                if (ancestor is Control { Name: "PlanViewport" })
                {
                    break;
                }
            }
        }
    }
}
