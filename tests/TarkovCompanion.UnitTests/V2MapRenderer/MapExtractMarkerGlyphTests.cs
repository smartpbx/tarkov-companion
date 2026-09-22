using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.Views.V2.MapRenderer;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [#606] An extract's or a transit's glyph sits inside its round disc, selected or not.
/// </summary>
/// <remarks>
/// Reported twice from Windows: a "dark square box" beside the extract icon. It was V1's door
/// bracket drawn 18px wide on a 22px disc, its left stroke outside the disc: the glyph's own
/// 10px size rule was declared above the generic 18px marker-icon rule, and in Avalonia the later
/// of two matching styles wins. Only the real view under the real styles shows it.
/// </remarks>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class MapExtractMarkerGlyphTests
{
    [Fact]
    public async Task Extract_and_transit_glyphs_stay_well_inside_their_disc_selected_or_not()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(MapMarkClipTests.MarkClipApp));
        await session.Dispatch(
            () =>
            {
                var view = new MapSceneRendererView { DataContext = Renderer() };
                var window = new Window { Width = 1400, Height = 900, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                try
                {
                    var marks = view.GetVisualDescendants().OfType<ToggleButton>()
                        .Where(button => button.Classes.Contains("v2-map-marker"))
                        .ToArray();
                    Assert.Equal(2, marks.Length);
                    foreach (var selected in new[] { false, true })
                    {
                        foreach (var mark in marks)
                        {
                            mark.IsChecked = selected;
                            Dispatcher.UIThread.RunJobs();
                            var model = (MapSceneRendererObjectViewModel)mark.DataContext!;
                            var chip = mark.GetVisualDescendants().OfType<Border>()
                                .Single(border => border.Classes.Contains("v2-map-marker-chip"));
                            var glyph = chip.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                                .Single(path => path.IsVisible);
                            var topLeft = glyph.TranslatePoint(new Point(0, 0), chip)!.Value;
                            var disc = chip.Bounds.Width;
                            var label = $"{model.Icon}, selected={selected}";

                            // About half the disc, as V1's glyph was on its disc.
                            Assert.True(glyph.Bounds.Width <= disc * 0.5, $"{label}: glyph {glyph.Bounds.Width} on a {disc} disc.");
                            // Inside the disc's inscribed square, so no stroke reaches past the round edge.
                            var inset = disc * (1 - (1 / Math.Sqrt(2))) / 2;
                            Assert.True(topLeft.X >= inset && topLeft.X + glyph.Bounds.Width <= disc - inset, $"{label}: glyph x {topLeft.X} on a {disc} disc.");
                            Assert.True(topLeft.Y >= inset && topLeft.Y + glyph.Bounds.Height <= disc - inset, $"{label}: glyph y {topLeft.Y} on a {disc} disc.");
                            // A transit is hollow, not a translucent blob: the whole chip is not faded.
                            Assert.Equal(1, chip.Opacity);
                        }
                    }
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    private static MapSceneRendererViewModel Renderer()
    {
        var layer = new MapSceneLayer(new("extracts"), "Extracts", 10, true);
        MapSceneObject At(string id, MapSceneObjectKind kind, double x, MapFeatureFaction faction) => new(
            new($"object:{id}"),
            layer.Id,
            kind,
            MapSceneTruthKind.StaticReference,
            id,
            null,
            MapSceneGeometry.At(new(x, 150)),
            [],
            new DataProvenance("fixture", new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero), Confidence: new Confidence(1)),
            faction: faction);
        var scene = new MapSceneSnapshot(
            1,
            "shoreline",
            "shoreline",
            "shoreline",
            new MapSceneBounds(0, 0, 400, 300),
            [],
            new(MapSceneCapability.Available, MapSceneCapability.Unavailable("no stack"), MapSceneCapability.Unavailable("no interior")),
            new(MapSceneMode.Flat2D, null, new(200, 150, 1, 0, 0), [new(layer.Id, true)]),
            [layer],
            [
                At("extract", MapSceneObjectKind.Extract, 150, MapFeatureFaction.Pmc),
                At("transit", MapSceneObjectKind.Transit, 250, MapFeatureFaction.Unknown),
            ],
            []);
        return new MapSceneRendererViewModel(
            scene,
            MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc),
            showsDetailsPanel: false);
    }
}
