using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.Views.V2.MapRenderer;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Infrastructure.Workspaces;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [#902] "Layers can always be turned back on." Each map here is built the way the Raid cockpit
/// builds one: the map's default layers from MapPresentationService, the remembered choices laid
/// over the requested view, the real assembler, and the real renderer, whose change requests are
/// applied and remembered the way RaidCockpitViewModel.ViewChangeRequested does it.
/// </summary>
public sealed class LayersCanAlwaysBeTurnedBackOnTests : IDisposable
{
    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"layers-back-on-{Guid.NewGuid():N}");
    private long _revision;

    [Fact]
    public void Any_layer_switched_off_on_an_empty_map_stays_off_across_a_map_change_and_a_restart_and_switches_back_on()
    {
        var path = Path.Combine(_directory, "workspace-layout.json");
        var setting = new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(path));
        // An empty map: no labels, no extracts, nothing. Every row reads "none on this map".
        var renderer = Host(Build("customs", [], setting), setting);
        var ids = renderer.Layers.Select(layer => layer.Layer.Id).ToArray();
        Assert.NotEmpty(ids);
        Assert.All(renderer.Layers, layer => Assert.True(layer.HasNothingToShow));

        foreach (var id in ids)
        {
            if (Row(renderer, id).IsVisible)
            {
                Row(renderer, id).ToggleCommand.Execute(null);
            }

            Assert.False(Row(renderer, id).IsVisible);
        }

        // Another map, still empty: every layer is off, and every switch still turns it on.
        renderer.Present(Build("woods", [], setting));
        foreach (var id in ids)
        {
            Assert.False(Row(renderer, id).IsVisible);
        }

        // A restart: the choices were saved.
        var restarted = new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(path));
        var afterRestart = Host(Build("customs", [], restarted), restarted);
        foreach (var id in ids)
        {
            Assert.False(Row(afterRestart, id).IsVisible);
            Row(afterRestart, id).ToggleCommand.Execute(null);
            Assert.True(Row(afterRestart, id).IsVisible);
        }

        var again = new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(path));
        Assert.All(ids, id => Assert.True(again.Get(id)));
    }

    [Fact]
    public void A_layer_switched_back_on_draws_again()
    {
        var setting = new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(Path.Combine(_directory, "layout.json")));
        var extracts = MapSceneAssembler.IdFor(MapOverlayKind.Extracts);
        var renderer = Host(Build("customs", [], setting), setting);
        Row(renderer, extracts).ToggleCommand.Execute(null);

        var withExtract = new MapOverlayElement(MapOverlayKind.Extracts, new(40, 40), "Crossroads");
        renderer.Present(Build("woods", [withExtract], setting));
        Assert.DoesNotContain(renderer.SpatialObjects, item => item.Label == "Crossroads");

        Row(renderer, extracts).ToggleCommand.Execute(null);

        Assert.Contains(renderer.SpatialObjects, item => item.Label == "Crossroads");
    }

    [Fact]
    public void Every_layers_row_a_map_opens_with_has_an_adapter()
    {
        // The four V1 layers were rows nothing ever put an object on. One element of every kind
        // the map lists: each row must end up with something to draw.
        var model = Model("customs", []);
        var elements = model.Overlays
            .Select((overlay, index) => new MapOverlayElement(overlay.Kind, new(10 + index, 10 + index), $"{overlay.Kind} {index}"))
            .ToArray();

        var scene = Build("customs", elements, new MapLayerVisibilitySetting(null));

        Assert.NotEmpty(scene.Layers);
        Assert.All(scene.Layers, layer => Assert.Contains(scene.Objects, item => item.LayerId == layer.Id));
        Assert.DoesNotContain(scene.Layers, layer => layer.Id.Value is "companion-markers" or "routes" or "risk-traffic" or "filters");
    }

    [Fact]
    public void A_stored_choice_for_a_removed_v1_layer_is_dropped_on_read()
    {
        var layout = new MemoryLayout();
        layout.Set(WorkspaceLayoutKeys.RaidLayerVisibility, "companion-markers:0,routes:0,risk-traffic:0,filters:0,extracts:0");

        var setting = new MapLayerVisibilitySetting(layout);

        Assert.Null(setting.Get(new("companion-markers")));
        Assert.Null(setting.Get(new("filters")));
        Assert.False(setting.Get(MapSceneAssembler.IdFor(MapOverlayKind.Extracts)));
        setting.Set(new("labels"), false);
        Assert.Equal("extracts:0,labels:0", layout.Get(WorkspaceLayoutKeys.RaidLayerVisibility));
    }

    [Fact]
    public void Loot_focus_is_undone_by_pressing_it_again_and_is_never_saved()
    {
        var renderer = MapSceneRendererGalleryViewModel.Create(largeText: false).Renderer;
        var layout = new MemoryLayout();
        var setting = new MapLayerVisibilitySetting(layout);
        renderer.ViewChangeRequested += change =>
            setting.Record(change, MapSceneViewChangeStatus.Applied, renderer.IsDispatchingLootFocus);
        var before = renderer.Scene.View.Layers.ToDictionary(state => state.LayerId, state => state.IsVisible);
        Assert.True(before[new("estimates")]);

        renderer.HighValueLootPresetCommand.Execute(null);

        Assert.True(renderer.IsLootFocused);
        Assert.False(IsVisible(renderer, new("estimates")));
        Assert.True(IsVisible(renderer, new("squad")));
        Assert.True(IsVisible(renderer, HighValueLootLayerService.LayerId));
        Assert.Null(layout.Get(WorkspaceLayoutKeys.RaidLayerVisibility));

        renderer.HighValueLootPresetCommand.Execute(null);

        Assert.False(renderer.IsLootFocused);
        Assert.Equal(before, renderer.Scene.View.Layers.ToDictionary(state => state.LayerId, state => state.IsVisible));
        Assert.Null(layout.Get(WorkspaceLayoutKeys.RaidLayerVisibility));
    }

    [Fact]
    public void A_layer_switched_by_hand_during_loot_focus_is_saved_and_kept_when_the_focus_ends()
    {
        var renderer = MapSceneRendererGalleryViewModel.Create(largeText: false).Renderer;
        var setting = new MapLayerVisibilitySetting(new MemoryLayout());
        renderer.ViewChangeRequested += change =>
            setting.Record(change, MapSceneViewChangeStatus.Applied, renderer.IsDispatchingLootFocus);
        var extracts = new MapSceneLayerId("extracts");
        renderer.HighValueLootPresetCommand.Execute(null);

        renderer.SetLayerVisibility(extracts, false);
        renderer.HighValueLootPresetCommand.Execute(null);

        Assert.False(IsVisible(renderer, extracts));
        Assert.True(IsVisible(renderer, new("estimates")));
        Assert.False(setting.Get(extracts));
        Assert.Null(setting.Get(new("estimates")));
    }

    [Theory]
    [InlineData("you", "You")]
    [InlineData("my-trail", "You")]
    [InlineData("nearby-spawns", "Map")]
    [InlineData("squad", "Squad")]
    [InlineData("traffic-routes", "Routes")]
    [InlineData("objective-route", "Routes")]
    [InlineData("quest-objectives", "Quests")]
    [InlineData("extracts", "Map")]
    [InlineData("drawings", "Marks")]
    [InlineData("high-value-loot-spawns", "Loot")]
    [InlineData("traffic-prior", "Traffic")]
    [InlineData("a-layer-nobody-told-the-menu-about", "Map")]
    public void Each_layer_sits_under_its_header(string layerId, string group) =>
        Assert.Equal(group, MapLayerGroups.KeyOf(new(layerId)));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static bool IsVisible(MapSceneRendererViewModel renderer, MapSceneLayerId layerId) =>
        renderer.Scene.View.Layers.Single(state => state.LayerId == layerId).IsVisible;

    private static MapSceneRendererLayerViewModel Row(MapSceneRendererViewModel renderer, MapSceneLayerId id)
    {
        // The menu shows the grouped rows; a row there is the same row as in Layers.
        var row = renderer.LayerGroups.SelectMany(group => group.Layers).Single(layer => layer.Layer.Id == id);
        Assert.Same(renderer.Layers.Single(layer => layer.Layer.Id == id), row);
        return row;
    }

    private static MapSceneRendererViewModel Host(MapSceneSnapshot scene, MapLayerVisibilitySetting setting)
    {
        var renderer = new MapSceneRendererViewModel(scene, Presentation, Guid.NewGuid);
        renderer.ViewChangeRequested += change =>
        {
            var result = MapSceneViewReducer.Apply(renderer.Scene, change);
            renderer.Present(result.Scene);
            setting.Record(change, result.Status, renderer.IsDispatchingLootFocus);
        };
        return renderer;
    }

    private MapSceneSnapshot Build(string locationId, IReadOnlyList<MapOverlayElement> elements, MapLayerVisibilitySetting setting)
    {
        var model = Model(locationId, elements);
        var result = new MapSceneAssembler().Build(new(
            Interlocked.Increment(ref _revision),
            model,
            new(0, 0, 100, 100),
            "maps-json:b",
            new(MapSceneMode.Flat2D, "lower", new(50, 50, 1, 0, 0), setting.Apply([])),
            [.. elements.Select(element => new MapSceneLegacyElement(element, new DataProvenance("test", DateTimeOffset.UnixEpoch)))],
            [],
            [],
            [new(
                new("asset:plan"),
                MapSceneAssetKind.Background2D,
                new("https://example.test/plan.asset"),
                new("https://example.test/licence"),
                new string('a', 64),
                "Example author",
                "map-1",
                "game-1",
                MapSceneAssetReviewStatus.Reviewed,
                DateTimeOffset.UnixEpoch)]));
        return Assert.IsType<MapSceneSnapshot>(result.Scene);
    }

    /// <summary>The map as MapPresentationService opens it, with its own default layers.</summary>
    private static MapRenderModel Model(string locationId, IReadOnlyList<MapOverlayElement> elements)
    {
        var location = new MapLocation(locationId, null, locationId, null, null, []);
        var floors = new[] { new MapFloorDefinition("lower", "Lower", null, null, true, []) };
        var variant = new MapVariant(
            location.Id, $"{locationId}-plan", MapProjectionKind.TwoDimensional, "2D", null, null,
            new("https://example.test/plan.svg"), null, 256, null, null,
            new(new(0, 0), new(100, 100)), new(new(0, 0), new(100, 100)), new(1, 0, 1, 0, 0),
            null, null, null, "Example author", new("https://example.test/author"), [], floors, []);
        return new MapPresentationService().Create(location, variant, "/cache/plan.svg") with { OverlayElements = elements };
    }

    private sealed class MemoryLayout : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
