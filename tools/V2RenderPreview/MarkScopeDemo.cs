using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.Views.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#289] "--mark-scopes-demo": one mark of each scope and several lifetimes on the open map, and
/// with "--open-mark-menu" the first row's Options menu saved as <c>&lt;out&gt;.menu.png</c>.
/// </summary>
/// <remarks>
/// Placed through the store's own <see cref="IRaidMarkStore.PlaceAsync"/>, the path the Ctrl menu
/// takes, so the rows, the map and the Team list are the real ones reading real marks.
/// </remarks>
internal static class MarkScopeDemo
{
    public static void Run(Window window, IServiceProvider services, string? mapId, string outputPath, bool openMenu, Action<int> pump)
    {
        var raid = services.GetRequiredService<RaidCockpitViewModel>();
        var store = services.GetRequiredService<IRaidMarkStore>();
        if (raid.Renderer is not { } renderer || mapId is null)
        {
            Console.Error.WriteLine("Mark scopes demo: the map is not open.");
            return;
        }

        var bounds = renderer.Scene.Bounds;
        MapScenePoint At(double fx, double fy) => new(bounds.MinimumX + (bounds.Width * fx), bounds.MinimumY + (bounds.Height * fy));
        (string? Label, RaidMarkScope Scope, RaidMarkLifetime Lifetime, MapScenePoint Point)[] marks =
        [
            ("Stash", RaidMarkScope.Private, RaidMarkLifetime.UntilRemoved, At(0.36, 0.36)),
            ("Dorms", RaidMarkScope.Squad, RaidMarkLifetime.FiveMinutes, At(0.50, 0.34)),
            (null, RaidMarkScope.Squad, RaidMarkLifetime.ThisRaid, At(0.62, 0.44)),
            (null, RaidMarkScope.Private, RaidMarkLifetime.Ping, At(0.44, 0.42)),
            (null, RaidMarkScope.Squad, RaidMarkLifetime.Ping, At(0.56, 0.40)),
        ];
        foreach (var (label, scope, lifetime, point) in marks)
        {
            var placing = store.PlaceAsync(mapId, null, point.X, point.Y, label, scope, lifetime);
            while (!placing.IsCompleted)
            {
                pump(1);
            }

            pump(4);
        }

        // Only the Marks card open, so the rows are on screen at 1080 lines.
        foreach (var card in new[] { raid.Cards.Summary, raid.Cards.Squad, raid.Cards.Objectives, raid.Cards.Extracts, raid.Cards.ExtractSelection, raid.Cards.Route, raid.Cards.Tasks, raid.Cards.LootSelection })
        {
            card.IsExpanded = false;
        }

        raid.Cards.Marks.IsExpanded = true;
        pump(60);
        Console.WriteLine("Mark pins: " + string.Join(" | ", renderer.PointMarkers
            .Where(item => item.SceneObject?.Kind is MapSceneObjectKind.Ping or MapSceneObjectKind.Waypoint)
            .Select(item => $"{item.HoverText} @ {item.AnchorLeft:0},{item.AnchorTop:0} shown={item.IsShownOnPlan}")));
        Console.WriteLine("Marks: " + string.Join(" | ", raid.Marks.Select(row => $"{row.KindLabel} {row.Label} [{row.OptionsLabel}]")));
        if (!openMenu)
        {
            return;
        }

        var button = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(candidate => AutomationProperties.GetAutomationId(candidate) == "v2-raid-mark-options" && candidate.IsEffectivelyVisible);
        if (button?.DataContext is not RaidMarkRowViewModel row)
        {
            Console.Error.WriteLine("Mark scopes demo: no Options button is on screen.");
            return;
        }

        var menu = RaidMarkMenu.ForMark(row);
        menu.ShowAt(button);
        pump(30);
        var host = TopLevel.GetTopLevel((Control)menu.Items[0]!)
            ?? throw new InvalidOperationException("The mark menu opened without a top level.");
        using var frame = host.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The mark menu produced no frame.");
        var path = Path.ChangeExtension(outputPath, ".menu.png");
        using (var stream = File.Create(path))
        {
            frame.Save(stream, new PngBitmapEncoderOptions());
        }

        Console.WriteLine($"Saved {path} ({frame.PixelSize.Width}x{frame.PixelSize.Height}) for {row.Label}.");
        menu.Hide();
        pump(5);
    }
}
