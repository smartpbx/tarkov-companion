using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.Views.V2.MapRenderer;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [#974] "the 100k per item box pushes the whole grouping of items way out from the side." The
/// loot value chip is wider than the icon buttons above it and set the stack's width, so the
/// buttons sat on its left edge instead of at the map's side.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class MapCommandStackTests
{
    [Fact]
    public async Task The_map_buttons_stay_at_the_edge_when_the_loot_value_chip_is_wider()
    {
        using var session = HeadlessSessions.StartNew(typeof(MapMarkClipTests.MarkClipApp));
        await session.Dispatch(
            () =>
            {
                var view = new MapSceneRendererView { DataContext = Renderer() };
                var window = new Window { Width = 1400, Height = 900, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                try
                {
                    var zoomOut = ById(view, "v2-map-zoom-out");
                    var chip = ById(view, "v2-map-loot-compact-filter");
                    Assert.True(chip.Bounds.Width > zoomOut.Bounds.Width + 20, $"chip {chip.Bounds.Width} vs button {zoomOut.Bounds.Width}");
                    var buttonRight = zoomOut.TranslatePoint(new Point(zoomOut.Bounds.Width, 0), view)!.Value.X;
                    var chipRight = chip.TranslatePoint(new Point(chip.Bounds.Width, 0), view)!.Value.X;
                    Assert.Equal(chipRight, buttonRight, 1);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    private static Control ById(Control root, string id) =>
        root.GetVisualDescendants().OfType<Control>()
            .First(control => control.IsEffectivelyVisible && AutomationProperties.GetAutomationId(control) == id);

    private static MapSceneRendererViewModel Renderer()
    {
        var extracts = new MapSceneLayer(new("extracts"), "Extracts", 10, true);
        var scene = new MapSceneSnapshot(
            1,
            "customs",
            "customs-plan",
            "transform-1",
            new MapSceneBounds(0, 0, 400, 300),
            [],
            new(MapSceneCapability.Available, MapSceneCapability.Unavailable("no stack"), MapSceneCapability.Unavailable("no interior")),
            new(MapSceneMode.Flat2D, null, new(200, 150, 1, 0, 0), [new(extracts.Id, true), new(HighValueLootLayerService.LayerId, true)]),
            [extracts, HighValueLootLayerService.Layer],
            [],
            []);
        return new MapSceneRendererViewModel(
            scene,
            MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc),
            showsDetailsPanel: false,
            highValueLoot: new(
                HighValueLootLayerService.Layer,
                "customs",
                "transform-1",
                HighValueLootFilter.Default,
                new(ResultCompleteness.Unavailable, FreshnessState.Unknown, "fixture.unavailable"),
                "Potential spawns · Data unavailable",
                null,
                null,
                [],
                [],
                []));
    }
}
