using System.Globalization;
using TarkovCompanion.App.Services.V2;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Infrastructure.Workspaces;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [#933] "Turning on things like the rare loot spawns dont seem to persist, it turns off every raid
/// still." Every row of the Raid map's Layers menu, set against its default, then put through a
/// raid start, a map change, a restart whose layers arrive late, and a plain restart.
/// </summary>
/// <remarks>
/// Each scene is built the way the Raid cockpit builds one: the view kept on the same map and fresh
/// on another, the player's choices laid over it by <see cref="MapLayerVisibilitySetting.Compose"/>,
/// the real assembler, and the real renderer, whose change requests go through
/// <see cref="MapLayerVisibilitySetting.ApplyChange"/>, which is what the cockpit calls.
/// </remarks>
public sealed class LayerChoicesSurviveRaidsTests : IDisposable
{
    public enum RaidEvent
    {
        RaidStart,
        MapChange,
        DataArrivesLate,
        Restart,
    }

    private static readonly MapSceneRendererPresentation Presentation =
        MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc);

    /// <summary>The rows the cockpit declares beside the map's own, as it declares them.</summary>
    private static readonly (MapSceneLayer Layer, LayerPresence Presence)[] CockpitLayers =
    [
        (HighValueLootLayerService.Layer, LayerPresence.Always),
        (new(MapSceneRendererViewModel.TrafficHeatLayerId, "Modelled traffic", 5, true), LayerPresence.Late),
        (new(new("my-marks"), "My marks", 40, true), LayerPresence.Always),
        (new(new("group-marks"), "Squad marks", 40, true), LayerPresence.Late),
        (new(new("drawings"), "Drawings", 45, true), LayerPresence.Late),
        (new(new("you"), "You", 70, true), LayerPresence.InRaid),
        (new(RaidCockpitViewModel.MyTrailLayerId, "My trail", 20, false), LayerPresence.Always),
        (new(new("squad"), "Squad", 60, true), LayerPresence.InRaid),
        (new(RouteLayerSwitch.Objective, "Objective route", 62, true), LayerPresence.Always),
        (new(RouteLayerSwitch.Suggested, "Suggested routes", 65, true), LayerPresence.Always),
        (new(RouteLayerSwitch.Direct, "Direct line", 64, true), LayerPresence.Always),
        (new(RaidCockpitViewModel.NearbySpawnsLayerId, "Nearby spawns", 3, true), LayerPresence.Always),
        (new(RaidCockpitViewModel.SpawnLinesLayerId, "Spawn lines", 3, true), LayerPresence.Always),
    ];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"layer-choices-{Guid.NewGuid():N}");

    private enum LayerPresence
    {
        Always,

        /// <summary>Declared once its data is in: the heatmap once traffic loads, squad marks once shared.</summary>
        Late,

        /// <summary>Declared only in raid: your position, your squad.</summary>
        InRaid,
    }

    public static TheoryData<string, RaidEvent> EveryRowAndEvent()
    {
        var data = new TheoryData<string, RaidEvent>();
        foreach (var id in EveryRowId())
        {
            foreach (var raidEvent in Enum.GetValues<RaidEvent>())
            {
                data.Add(id, raidEvent);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRowAndEvent))]
    public void A_row_set_against_its_default_stays_that_way(string layerId, RaidEvent raidEvent)
    {
        var session = new Session(LayoutPath);
        session.Build("factory4_day", inRaid: true, lateLayers: true);
        var id = new MapSceneLayerId(layerId);
        var wanted = !session.IsVisible(id);
        session.Row(id).ToggleCommand.Execute(null);
        Assert.Equal(wanted, session.IsVisible(id));

        session = Run(session, raidEvent);

        Assert.Equal(wanted, session.IsVisible(id));
        Assert.Equal(wanted, session.Row(id).IsVisible);
        Assert.Equal(wanted, new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(LayoutPath)).Get(id));
    }

    [Theory]
    [MemberData(nameof(EveryRowAndEvent))]
    public void Loot_focus_pressed_in_between_leaves_every_choice_as_the_player_made_it(string layerId, RaidEvent raidEvent)
    {
        var session = new Session(LayoutPath);
        session.Build("factory4_day", inRaid: true, lateLayers: true);
        var id = new MapSceneLayerId(layerId);
        session.Row(id).ToggleCommand.Execute(null);
        var chosen = session.VisibleStates();
        var stored = Stored();
        Assert.Equal(chosen[id], new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(LayoutPath)).Get(id));

        session.Renderer.HighValueLootPresetCommand.Execute(null);
        Assert.True(session.Renderer.IsLootFocused);
        Assert.Equal(stored, Stored());

        session = Run(session, raidEvent);
        if (session.Renderer.IsLootFocused)
        {
            // The same map in a new raid keeps Loot focus on; pressing it again ends it.
            session.Renderer.HighValueLootPresetCommand.Execute(null);
        }

        // Nothing saved but what the player chose. (The late-data restart below switches Labels off
        // and on again, which is saved as Labels on: what it was.)
        var saved = new MapLayerVisibilitySetting(new JsonFileWorkspaceLayoutStore(LayoutPath));
        Assert.Equal(chosen[id], saved.Get(id));
        foreach (var (layer, before) in chosen)
        {
            Assert.True(saved.Get(layer) is null || saved.Get(layer) == before, $"{layer.Value} saved as {saved.Get(layer)} after {raidEvent}");
        }

        foreach (var (layer, visible) in session.VisibleStates())
        {
            // Every layer is the player's choice or its own default, whatever Loot focus did.
            Assert.True(
                !chosen.TryGetValue(layer, out var before) || before == visible,
                $"{layer.Value} was {before} before Loot focus and {visible} after {raidEvent}");
        }
    }

    [Fact]
    public void High_value_loot_turned_on_stays_on_across_four_raids_two_maps_and_a_restart()
    {
        var session = new Session(LayoutPath);
        session.Build("customs", inRaid: false, lateLayers: false);
        session.Row(HighValueLootLayerService.LayerId).ToggleCommand.Execute(null);

        foreach (var map in new[] { "customs", "woods", "woods", "customs" })
        {
            session.Build(map, inRaid: true, lateLayers: true);
            Assert.True(session.IsVisible(HighValueLootLayerService.LayerId), map);
            session.Build(map, inRaid: false, lateLayers: true);
            Assert.True(session.IsVisible(HighValueLootLayerService.LayerId), map);
        }

        session = new Session(LayoutPath);
        session.Build("woods", inRaid: false, lateLayers: false);
        Assert.True(session.IsVisible(HighValueLootLayerService.LayerId));
        Assert.Equal("high-value-loot-spawns:1", Stored());
    }

    [Fact]
    public void A_tablet_moving_the_map_does_not_undo_what_was_switched_at_the_desk()
    {
        var session = new Session(LayoutPath);
        session.Build("customs", inRaid: false, lateLayers: false);
        // What the shared state held when the tablet took Control: the desk's layers then.
        var atControl = ActiveLayers(session.Renderer.Scene);

        // At the desk: loot on. Then the raid starts, and You and Squad arrive.
        session.Row(HighValueLootLayerService.LayerId).ToggleCommand.Execute(null);
        session.Build("customs", inRaid: true, lateLayers: true);

        // A move of the map from the tablet carries the list from the moment it took Control.
        Apply(session, atControl, atControl);

        Assert.True(session.IsVisible(HighValueLootLayerService.LayerId));
        Assert.True(session.IsVisible(new("you")));
        Assert.True(session.IsVisible(new("squad")));
        Assert.Equal("high-value-loot-spawns:1", Stored());

        // A switch pressed on the tablet is applied, and only that one.
        var shown = ActiveLayers(session.Renderer.Scene);
        var withoutExtracts = shown.Where(layer => layer != "extracts").ToArray();
        Apply(session, atControl, withoutExtracts);

        Assert.False(session.IsVisible(new("extracts")));
        Assert.True(session.IsVisible(HighValueLootLayerService.LayerId));
        Assert.True(session.IsVisible(new("you")));
    }

    [Fact]
    public void A_tablet_request_with_no_known_previous_list_switches_nothing()
    {
        var session = new Session(LayoutPath);
        session.Build("customs", inRaid: true, lateLayers: true);

        Assert.Empty(TabletLayerRequest.Changes(session.Renderer.Scene, null, []));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string LayoutPath => Path.Combine(_directory, "workspace-layout.json");

    private string? Stored() => new JsonFileWorkspaceLayoutStore(LayoutPath).Get(WorkspaceLayoutKeys.RaidLayerVisibility);

    private static void Apply(Session session, IReadOnlyCollection<string> previous, IReadOnlyCollection<string> requested)
    {
        foreach (var (layerId, visible) in TabletLayerRequest.Changes(session.Renderer.Scene, previous, requested))
        {
            session.Renderer.SetLayerVisibility(layerId, visible);
        }
    }

    private static string[] ActiveLayers(MapSceneSnapshot scene) =>
        scene.View.Layers.Where(state => state.IsVisible).Select(state => state.LayerId.Value).ToArray();

    private Session Run(Session session, RaidEvent raidEvent)
    {
        switch (raidEvent)
        {
            case RaidEvent.RaidStart:
                // Out of raid, on the same map, then in raid: You and Squad arrive, the view is kept.
                session.Build("factory4_day", inRaid: false, lateLayers: true);
                session.Build("factory4_day", inRaid: true, lateLayers: true);
                return session;
            case RaidEvent.MapChange:
                session.Build("customs", inRaid: false, lateLayers: false);
                session.Build("customs", inRaid: true, lateLayers: true);
                return session;
            case RaidEvent.DataArrivesLate:
            {
                // A restart whose first scene has none of the late layers yet, the raid layers
                // absent too; a switch pressed meanwhile on another row must not save them off.
                var restarted = new Session(LayoutPath);
                restarted.Build("factory4_day", inRaid: false, lateLayers: false);
                var other = new MapSceneLayerId("labels");
                restarted.Row(other).ToggleCommand.Execute(null);
                restarted.Row(other).ToggleCommand.Execute(null);
                restarted.Build("factory4_day", inRaid: false, lateLayers: true);
                restarted.Build("factory4_day", inRaid: true, lateLayers: true);
                return restarted;
            }
            case RaidEvent.Restart:
            {
                var restarted = new Session(LayoutPath);
                restarted.Build("factory4_day", inRaid: true, lateLayers: true);
                return restarted;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(raidEvent));
        }
    }

    private static IEnumerable<string> EveryRowId()
    {
        var model = Model("customs");
        return model.Overlays
            .Where(overlay => MapSceneAssembler.HasAdapter(overlay.Kind))
            .Select(overlay => MapSceneAssembler.IdFor(overlay.Kind).Value)
            .Concat(CockpitLayers.Select(entry => entry.Layer.Id.Value))
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>The map as MapPresentationService opens it, with its own default layers.</summary>
    private static MapRenderModel Model(string locationId)
    {
        var location = new MapLocation(locationId, null, locationId, null, null, []);
        var floors = new[] { new MapFloorDefinition("lower", "Lower", null, null, true, []) };
        var variant = new MapVariant(
            location.Id, $"{locationId}-plan", MapProjectionKind.TwoDimensional, "2D", null, null,
            new("https://example.test/plan.svg"), null, 256, null, null,
            new(new(0, 0), new(100, 100)), new(new(0, 0), new(100, 100)), new(1, 0, 1, 0, 0),
            null, null, null, "Example author", new("https://example.test/author"), [], floors, []);
        return new MapPresentationService().Create(location, variant, "/cache/plan.svg");
    }

    /// <summary>One run of the app: its own layout store, choices and renderer, as after a start.</summary>
    private sealed class Session(string layoutPath)
    {
        private readonly MapLayerVisibilitySetting _setting = new(new JsonFileWorkspaceLayoutStore(layoutPath));
        private long _revision;
        private MapSceneRendererViewModel? _renderer;

        public MapSceneRendererViewModel Renderer => _renderer ?? throw new InvalidOperationException("No scene yet.");

        public void Build(string locationId, bool inRaid, bool lateLayers)
        {
            var model = Model(locationId);
            var loot = Loot(locationId);
            var additional = CockpitLayers
                .Where(entry => entry.Presence == LayerPresence.Always ||
                                entry.Presence == LayerPresence.Late && lateLayers ||
                                entry.Presence == LayerPresence.InRaid && inRaid)
                .Select(entry => entry.Layer)
                .ToArray();
            // The cockpit keeps the view on the same map and opens another map on a fresh one.
            var current = _renderer is { } renderer && string.Equals(renderer.Scene.LocationId, locationId, StringComparison.Ordinal)
                ? renderer.Scene.View.Layers
                : [];
            var layers = _setting.Compose(current, _renderer?.IsLootFocused == true);
            var result = new MapSceneAssembler().Build(new(
                Interlocked.Increment(ref _revision) + 1000,
                model,
                new(0, 0, 100, 100),
                "maps-json:b",
                new(MapSceneMode.Flat2D, "lower", new(50, 50, 1, 0, 0), layers),
                [],
                additional,
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
            var scene = Assert.IsType<MapSceneSnapshot>(result.Scene);
            if (_renderer is null)
            {
                var created = new MapSceneRendererViewModel(
                    scene,
                    Presentation,
                    Guid.NewGuid,
                    highValueLoot: loot,
                    highValueLootFilterState: HighValueLootLayerFilterState.Default,
                    showsDetailsPanel: false);
                created.ViewChangeRequested += change => _setting.ApplyChange(created, change);
                _renderer = created;
            }
            else
            {
                _renderer.Present(scene, loot, HighValueLootLayerFilterState.Default);
            }
        }

        public bool IsVisible(MapSceneLayerId layerId) =>
            Renderer.Scene.View.Layers.Single(state => state.LayerId == layerId).IsVisible;

        public Dictionary<MapSceneLayerId, bool> VisibleStates() =>
            Renderer.Scene.View.Layers.ToDictionary(state => state.LayerId, state => state.IsVisible);

        public MapSceneRendererLayerViewModel Row(MapSceneLayerId id) =>
            Renderer.LayerGroups.SelectMany(group => group.Layers).Single(layer => layer.Layer.Id == id);

        /// <summary>Loot data present for the map, so Loot focus has something to focus on.</summary>
        private static HighValueLootLayerResult Loot(string locationId) => new(
            HighValueLootLayerService.Layer,
            locationId,
            "maps-json:b",
            HighValueLootFilter.Default,
            new(ResultCompleteness.Complete, FreshnessState.Current, "test"),
            "Potential spawns",
            null,
            null,
            [],
            [],
            []);
    }
}
