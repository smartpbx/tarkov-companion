using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Infrastructure.Workspaces;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [Issue 796] "if we turn the traffic heatmap off, it comes back on always." Each scene here is
/// built the way the Raid cockpit builds one: the requested view, with the remembered choices laid
/// over it, through the real assembler, whose defaults are what used to win.
/// </summary>
public sealed class MapLayerVisibilitySettingTests : IDisposable
{
    private static readonly MapSceneLayerId Traffic = new("traffic-prior");
    private static readonly MapSceneLayerId Switches = MapSceneAssembler.IdFor(MapOverlayKind.Switches);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"layer-visibility-{Guid.NewGuid():N}");

    [Fact]
    public void Traffic_turned_off_stays_off_across_a_map_switch_a_new_raid_and_a_restart()
    {
        var path = Path.Combine(_directory, "workspace-layout.json");
        var setting = new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(path));
        var customs = Build("customs", [], setting);
        Assert.True(IsVisible(customs, Traffic));

        setting.Set(Traffic, false);

        // A map switch starts from an empty view, which is where the default came back.
        var woods = Build("woods", [], setting);
        Assert.False(IsVisible(woods, Traffic));

        // A new raid on the same map keeps the view, but the heatmap layer can arrive after the
        // first build (traffic loads late) with no state of its own.
        var newRaid = Build("woods", [.. woods.View.Layers.Where(state => state.LayerId != Traffic)], setting);
        Assert.False(IsVisible(newRaid, Traffic));

        var restarted = new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(path));
        var afterRestart = Build("customs", [], restarted);
        Assert.False(IsVisible(afterRestart, Traffic));
    }

    [Fact]
    public void An_untouched_layer_keeps_its_map_default()
    {
        var setting = new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(Path.Combine(_directory, "layout.json")));
        setting.Set(Traffic, false);

        Assert.True(IsVisible(Build("the-lab", [], setting), Switches));
        Assert.False(IsVisible(Build("customs", [], setting), Switches));
    }

    [Fact]
    public void A_choice_beats_the_map_default_in_both_directions()
    {
        var setting = new MapLayerVisibilitySetting(new MemoryLayout());
        setting.Set(Switches, false);
        Assert.False(IsVisible(Build("the-lab", [], setting), Switches));

        setting.Set(Switches, true);
        Assert.True(IsVisible(Build("customs", [], setting), Switches));
    }

    [Fact]
    public void A_full_value_keeps_the_most_recent_choices()
    {
        var layout = new MemoryLayout();
        var setting = new MapLayerVisibilitySetting(layout);
        for (var index = 0; index < 40; index++)
        {
            setting.Set(new($"layer-{index:00}"), index % 2 == 0);
        }

        var stored = layout.Get(WorkspaceLayoutKeys.RaidLayerVisibility)!;
        Assert.True(stored.Length <= 256);
        var restored = new MapLayerVisibilitySetting(layout);
        Assert.False(restored.Get(new("layer-39")));
        Assert.True(restored.Get(new("layer-38")));
        Assert.Null(restored.Get(new("layer-00")));
    }

    [Theory]
    [InlineData("traffic-prior:yes")]
    [InlineData(":0")]
    [InlineData("bad id:0")]
    [InlineData("")]
    public void A_hand_edited_value_that_does_not_parse_is_ignored(string stored)
    {
        var layout = new MemoryLayout();
        layout.Set(WorkspaceLayoutKeys.RaidLayerVisibility, stored);

        var setting = new MapLayerVisibilitySetting(layout);

        Assert.Null(setting.Get(Traffic));
        Assert.Empty(setting.Apply([]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static bool IsVisible(MapSceneSnapshot scene, MapSceneLayerId layerId) =>
        scene.View.Layers.Single(state => state.LayerId == layerId).IsVisible;

    private static MapSceneSnapshot Build(
        string locationId,
        IReadOnlyList<MapSceneLayerState> requested,
        MapLayerVisibilitySetting setting)
    {
        var model = Model(locationId);
        var result = new MapSceneAssembler().Build(new(
            1,
            model,
            new(0, 0, 100, 100),
            "maps-json:b",
            new(MapSceneMode.Flat2D, "lower", new(50, 50, 1, 0, 0), setting.Apply(requested)),
            [],
            [new MapSceneLayer(Traffic, "Modelled traffic", 5, true)],
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

    /// <summary>The overlay defaults MapPresentationService gives a map: Switches on only for Labs, Reserve and Interchange.</summary>
    private static MapRenderModel Model(string locationId)
    {
        var location = new MapLocation(locationId, null, locationId, null, null, []);
        var floors = new[] { new MapFloorDefinition("lower", "Lower", null, null, true, []) };
        var variant = new MapVariant(
            location.Id, $"{locationId}-plan", MapProjectionKind.TwoDimensional, "2D", null, null,
            new("https://example.test/plan.svg"), null, 256, null, null,
            new(new(0, 0), new(100, 100)), new(new(0, 0), new(100, 100)), new(1, 0, 1, 0, 0),
            null, null, null, "Example author", new("https://example.test/author"), [], floors, []);
        var overlays = Enum.GetValues<MapOverlayKind>()
            .Select(kind => new MapOverlayLayer(
                kind,
                kind.ToString(),
                kind switch
                {
                    MapOverlayKind.Spawns or MapOverlayKind.Keys => false,
                    MapOverlayKind.Switches => locationId is "the-lab" or "reserve" or "interchange",
                    _ => true,
                },
                false))
            .ToArray();
        return new(
            location,
            variant,
            new(MapBackgroundKind.Svg, variant.SvgPath!, "/cache/plan.svg", MapAssetAvailability.Available, null),
            MapTransformAvailability.Valid,
            "Validated transform.",
            overlays,
            [],
            floors,
            floors[0],
            "Example attribution",
            new("https://example.test/licence"));
    }

    private sealed class MemoryLayout : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
