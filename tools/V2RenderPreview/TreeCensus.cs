using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#678] <c>--tree-census N</c>: the controls on screen as a tree of subtrees with at least N
/// controls, so "the first Raid visit builds 2,400 controls" can say which panel built them.
/// </summary>
internal static class TreeCensus
{
    public static void Print(Visual root, int threshold)
    {
        Console.WriteLine($"[tree-census] {Count(root)} controls under {root.GetType().Name}");
        Walk(root, 0, threshold);
    }

    private static int Count(Visual visual) => 1 + visual.GetVisualChildren().Sum(Count);

    private static void Walk(Visual visual, int depth, int threshold)
    {
        foreach (var child in visual.GetVisualChildren())
        {
            var count = Count(child);
            if (count < threshold)
            {
                continue;
            }

            var name = child is Control { Name: { Length: > 0 } n } ? "#" + n : string.Empty;
            var context = child is StyledElement { DataContext: { } dc } && !ReferenceEquals(dc, (visual as StyledElement)?.DataContext) ? " dc=" + dc.GetType().Name : string.Empty;
            var shown = child.IsVisible ? string.Empty : " HIDDEN";
            Console.WriteLine($"[tree-census] {new string(' ', depth * 2)}{count,5} {child.GetType().Name}{name}{context}{shown}");
            Walk(child, depth + 1, threshold);
        }
    }
}
