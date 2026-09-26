using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.VisualTree;

namespace TarkovCompanion.App.Views.V2.Shell;

/// <summary>
/// [#881 reopened] Whether the rail's Settings gear and its update dot are drawn whole: inside
/// every ancestor that clips them, inside the gear button with room to spare, and the button
/// inside the rail. Empty when they are; one line per fault otherwise.
/// </summary>
/// <remarks>
/// "It also didn't fix the sidebar settings being cut off on the icon." Two faults, neither of
/// which a UI Automation rectangle can see, because the button's rectangle was whole both times.
/// The dot sat 3 px outside its 24 px icon box and the ContentControl hosting the box clips
/// (Avalonia 12 templated controls do), so its ring was cut flat. And in the 60 px icons-only rail
/// the button asked for 68 px, was given 60, and clipped its own content: the gear lost its
/// right-hand teeth and the dot became a sliver. So this measures the drawn glyph (the geometry
/// through the icon's own scale, with its stroke) and the dot, in the running view. The unit
/// tests call it headlessly; the Windows gallery asks the packaged app for it on real pixels
/// and real display scaling (the "updatewaiting" gallery scene).
/// </remarks>
public static class RailGearFit
{
    public const string GearAutomationId = "v2-shell-destination-setup";

    /// <summary>The room the glyph and the dot keep from the button's edge, in DIPs.</summary>
    public const double Room = 2;

    public static IReadOnlyList<string> Faults(Visual root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var faults = new List<string>();
        var railBorder = root.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("v2-shell-rail") && border.IsEffectivelyVisible);
        if (railBorder is null)
        {
            return ["no navigation rail is showing"];
        }

        var gear = railBorder.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == GearAutomationId && control.IsEffectivelyVisible);
        if (gear is null)
        {
            return ["the rail has no Settings gear"];
        }

        var glyph = gear.GetVisualDescendants().OfType<Path>()
            .FirstOrDefault(path => path.Classes.Contains("v2-icon") && path.IsEffectivelyVisible);
        var dot = gear.GetVisualDescendants().OfType<Ellipse>()
            .FirstOrDefault(ellipse => ellipse.IsEffectivelyVisible);
        var drawn = new List<(string Name, Visual Shape, Rect Rect)>();
        if (glyph?.Data is { } geometry && BoundsIn(glyph, geometry.Bounds.Inflate(glyph.StrokeThickness / 2), root) is { } glyphRect)
        {
            drawn.Add(("gear glyph", glyph, glyphRect));
        }
        else
        {
            faults.Add("the gear draws no glyph");
        }

        if (dot is not null && BoundsIn(dot, new Rect(dot.Bounds.Size), root) is { } dotRect)
        {
            drawn.Add(("update dot", dot, dotRect));
        }

        if (BoundsIn(gear, new Rect(gear.Bounds.Size), root) is not { } buttonRect ||
            BoundsIn(railBorder, new Rect(railBorder.Bounds.Size), root) is not { } railRect)
        {
            return ["the gear is not in the window"];
        }

        foreach (var (name, shape, rect) in drawn)
        {
            if (!buttonRect.Deflate(Room).Contains(rect))
            {
                faults.Add($"{name} {Show(rect)} is not {Room} px inside the gear button {Show(buttonRect)}");
            }

            // Every ancestor that clips, up to the root: the one that cut the dot was a
            // ContentControl, not the button.
            foreach (var clipper in shape.GetVisualAncestors().Where(visual => visual.ClipToBounds))
            {
                if (BoundsIn(clipper, new Rect(clipper.Bounds.Size), root) is { } clip && !clip.Contains(rect))
                {
                    faults.Add($"{name} {Show(rect)} is cut by a {clipper.GetType().Name} {Show(clip)}");
                }

                if (ReferenceEquals(clipper, root))
                {
                    break;
                }
            }
        }

        if (!railRect.Contains(buttonRect))
        {
            faults.Add($"the gear button {Show(buttonRect)} is not inside the rail {Show(railRect)}");
        }

        var wanted = gear.DesiredSize.Width - gear.Margin.Left - gear.Margin.Right;
        if (gear.Bounds.Width + 0.5 < wanted)
        {
            faults.Add($"the gear button was given {gear.Bounds.Width:0.#} px of the {wanted:0.#} it needs");
        }

        return faults;
    }

    private static Rect? BoundsIn(Visual visual, Rect local, Visual root) =>
        visual.TransformToVisual(root) is { } transform ? local.TransformToAABB(transform) : null;

    private static string Show(Rect rect) =>
        FormattableString.Invariant($"[{rect.Left:0.#},{rect.Top:0.#},{rect.Right:0.#},{rect.Bottom:0.#}]");
}
