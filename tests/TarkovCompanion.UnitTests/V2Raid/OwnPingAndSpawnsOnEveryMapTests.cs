using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.Views;
using TarkovCompanion.App.Views.V2.MapRenderer;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.UnitTests.Group;
using TarkovCompanion.UnitTests.V2MapRenderer;
using TarkovCompanion.UnitTests.V2Shell;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [#983, #985] On build 91 the owner, on Woods, right-clicked the Raid map, read "Ping sent to
/// squad", and saw no ping; no possible PMC spawn showed either. Every earlier proof ran on
/// Customs. Here every map of the catalog, the whole app as composed, the raid started by the
/// game's own log lines with the location id each map really writes, a screenshot, and a real
/// right-click on the real window.
/// </summary>
/// <remarks>
/// The location ids are the ones the game writes on the notifications: the owner's logs of
/// 2026-09-27 to 30 wrote <c>Woods</c>, <c>Lighthouse</c>, <c>TarkovStreets</c>,
/// <c>factory4_day</c> and <c>Interchange</c>; the rest are tarkov.dev's <c>nameId</c> for the map,
/// which is what the game writes (EFT_LOG_FACTS.md, the 1.1.5.1 format pack). Each map's row in
/// the maps table pairs that id with tarkov.dev's <c>normalizedName</c>, as a sync writes it, and
/// carries made-up PMC spawn areas around the middle of the map so every map has some to show.
/// </remarks>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class OwnPingAndSpawnsOnEveryMapTests(ITestOutputHelper output)
{
    private const string Pmc = "f00000000000000000000001";
    private const string SquadKey = "a-key-long-enough-for-a-room";

    /// <summary>
    /// tarkov.dev's maps table, 2026-09-14: the catalog map the raid is drawn on, nameId (what the
    /// log writes), name. Night Factory and Ground Zero 21+ have rows of their own
    /// (<see cref="RaidSlugs"/>) and no catalog location: they are drawn on Factory's and Ground Zero's.
    /// </summary>
    public static TheoryData<string, string, string> Maps => new()
    {
        { "woods", "Woods", "Woods" },
        { "streets-of-tarkov", "TarkovStreets", "Streets of Tarkov" },
        { "lighthouse", "Lighthouse", "Lighthouse" },
        { "factory", "factory4_day", "Factory" },
        { "interchange", "Interchange", "Interchange" },
        { "customs", "bigmap", "Customs" },
        { "reserve", "RezervBase", "Reserve" },
        { "shoreline", "Shoreline", "Shoreline" },
        { "the-lab", "laboratory", "The Lab" },
        { "ground-zero", "Sandbox", "Ground Zero" },
        { "ground-zero", "Sandbox_high", "Ground Zero 21+" },
        { "factory", "factory4_night", "Night Factory" },
        { "terminal", "Terminal", "Terminal" },
        { "the-labyrinth", "Labyrinth", "The Labyrinth" },
    };

    /// <summary>The maps table's own slug for the two maps drawn on another's plan.</summary>
    private static readonly IReadOnlyDictionary<string, string> RaidSlugs = new Dictionary<string, string>
    {
        ["factory4_night"] = "night-factory",
        ["Sandbox_high"] = "ground-zero-21",
    };

    /// <summary>
    /// A PMC raid begun by the log shows its possible PMC spawns, a screenshot adds the lines, and
    /// a right-click draws a ping where it landed, on every map.
    /// </summary>
    [Theory]
    [MemberData(nameof(Maps))]
    public async Task A_logged_pmc_raid_shows_possible_spawns_then_lines_and_a_right_click_ping_is_drawn(string mapId, string logLocation, string name)
    {
        await RunAsync(mapId, logLocation, name, storedLayers: null, async app =>
        {
            await app.StartRaidAsync();
            Assert.True(
                app.PumpUntil(() => app.Shown(RaidCockpitViewModel.NearbySpawnsLayerId).Length > 0),
                $"{name}: no possible PMC spawn is drawn at raid start. {app.Describe()}");

            await app.ScreenshotAsync();
            Assert.True(
                app.PumpUntil(() => app.Shown(RaidCockpitViewModel.SpawnLinesLayerId).Length > 0),
                $"{name}: no spawn line is drawn after the screenshot. {app.Describe()}");

            var mark = app.RightClickMap();
            Assert.True(app.IsDrawn(mark), $"{name}: the ping was placed ({mark.State.MapId}, floor {mark.State.FloorId ?? "none"}) and not drawn. {app.Describe()}");
            Assert.True(app.PingDotIsVisible(), $"{name}: no ping disc is visible in the window.");
        });
    }

    /// <summary>
    /// As the owner plays: in a squad, a squadmate on the same map sharing through the relay. The
    /// ping is sent, the map says so, and it is still drawn after the group has answered a few times.
    /// </summary>
    [Theory]
    [InlineData("woods", "Woods", "Woods")]
    [InlineData("streets-of-tarkov", "TarkovStreets", "Streets of Tarkov")]
    [InlineData("lighthouse", "Lighthouse", "Lighthouse")]
    public async Task In_a_squad_a_right_click_ping_is_sent_and_stays_drawn(string mapId, string logLocation, string name)
    {
        await using var relay = await InProcessRelay.StartAsync();
        await RunAsync(mapId, logLocation, name, storedLayers: null, async app =>
        {
            await app.JoinSquadAsync(relay.Address);
            await app.StartRaidAsync();
            Assert.True(
                app.PumpUntil(() => app.Shown(RaidCockpitViewModel.NearbySpawnsLayerId).Length > 0),
                $"{name}: no possible PMC spawn is drawn at raid start in a squad. {app.Describe()}");
            var mark = app.RightClickMap();
            Assert.True(app.PumpUntil(() => app.Status == "Ping sent to squad"), $"{name}: the map said '{app.Status}'.");
            Assert.True(app.IsDrawn(mark), $"{name}: 'Ping sent to squad' and the ping is not drawn. {app.Trace(mark)}. {app.Describe()}");
            app.PumpUntil(() => false, 100);
            Assert.True(app.IsDrawn(mark), $"{name}: the ping was drawn and then went, well inside its 45 s. {app.Describe()}");
            Assert.True(app.PingDotIsVisible(), $"{name}: no ping disc is visible in the window.");
        }, relay.Address);
    }

    /// <summary>
    /// [#933] Layers switched off before #933 stayed off for good: Loot focus saved its steps as
    /// the player's choices, and those included My marks, Nearby spawns and Spawn lines. A ping
    /// the player has just placed is drawn whatever was stored, and the map says the layer was off.
    /// </summary>
    [Theory]
    [InlineData("woods", "Woods", "Woods")]
    [InlineData("streets-of-tarkov", "TarkovStreets", "Streets of Tarkov")]
    public async Task A_ping_placed_with_my_marks_stored_off_is_drawn_and_the_layer_comes_back_on(string mapId, string logLocation, string name)
    {
        await RunAsync(mapId, logLocation, name, storedLayers: "my-marks:0", async app =>
        {
            await app.StartRaidAsync();
            app.PumpUntil(() => false, 20);
            var mark = app.RightClickMap();

            Assert.True(app.IsDrawn(mark), $"{name}: the ping was placed and the stored My marks switch hid it. {app.Trace(mark)}. {app.Describe()}");
            Assert.True(app.PingDotIsVisible(), $"{name}: no ping disc is visible in the window.");
            Assert.True(App.LayerOn(app.Cockpit.Renderer!, new("my-marks")), "My marks is still off.");
            Assert.True(
                app.PumpUntil(() => app.Status.Contains("My marks", StringComparison.Ordinal)),
                $"The map said '{app.Status}', not that My marks was off.");
        });
    }

    /// <summary>
    /// [#933] The opening window's two spawn layers and My marks, stored off by the old Loot focus
    /// (schema 2), are back on after the update, so a raid shows its possible PMC spawns again.
    /// </summary>
    [Fact]
    public async Task Spawn_layers_stored_off_by_the_old_loot_focus_show_again_in_a_new_raid()
    {
        await RunAsync("woods", "Woods", "Woods", storedLayers: "nearby-spawns:0,spawn-lines:0,my-marks:0", storedSchema: "2", test: async app =>
        {
            await app.StartRaidAsync();
            Assert.True(
                app.PumpUntil(() => app.Shown(RaidCockpitViewModel.NearbySpawnsLayerId).Length > 0),
                $"Woods: the stored Nearby spawns switch hid the possible PMC spawns. {app.Describe()}");
            await app.ScreenshotAsync();
            Assert.True(
                app.PumpUntil(() => app.Shown(RaidCockpitViewModel.SpawnLinesLayerId).Length > 0),
                $"Woods: the stored Spawn lines switch hid the lines. {app.Describe()}");
        });
    }

    private async Task RunAsync(string mapId, string logLocation, string name, string? storedLayers, Func<App, Task> test, string? relayAddress = null, string? storedSchema = null)
    {
        using var session = HeadlessSessions.StartNew(typeof(V2SettingsIndexLandingTests.SetupViewApp));
        await session.Dispatch(
            async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), $"tarkov-allmaps-{Guid.NewGuid():N}");
                try
                {
                    await using var services = AppComposition.Build(
                        new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                        new(DataRoot: root, Offline: true));
                    if (storedLayers is not null)
                    {
                        // As build 91 found it in the owner's Config: written by a Loot focus before #933.
                        var layout = services.GetRequiredService<IWorkspaceLayoutStore>();
                        layout.Set(WorkspaceLayoutKeys.RaidLayerVisibility, storedLayers);
                        layout.Set(WorkspaceLayoutKeys.RaidLayerSchema, storedSchema ?? MapLayerVisibilitySetting.CurrentSchema);
                    }

                    if (relayAddress is not null)
                    {
                        await services.GetRequiredService<IGroupSettingsStore>().SaveAsync(
                            new GroupSharingSettings(true, relayAddress, "Alpha", SquadKey, false, false), CancellationToken.None);
                    }

                    var legacy = services.GetRequiredService<MainWindowViewModel>();
                    var shell = services.GetRequiredService<V2ShellViewModel>();
                    legacy.PreviewShell = shell;
                    await legacy.InitializeAsync();
                    shell.Router.NavigateToAddress("raid");
                    var window = new MainWindow { DataContext = legacy, Width = 1920, Height = 1080 };
                    window.Show();
                    try
                    {
                        var cockpit = (RaidCockpitViewModel)shell.RaidCockpit!;
                        var app = new App(window, cockpit, legacy, services, mapId, logLocation, output);
                        Assert.True(app.PumpUntil(() => cockpit.MapPicker.Count > 0), "The map catalog never loaded.");
                        output.WriteLine("catalog: " + string.Join(", ", legacy.Map.Locations.Select(location => $"{location.Id}/{location.SourceId}")));
                        Assert.True(
                            cockpit.MapPicker.Any(item => item.MapId == mapId),
                            $"{name} ({mapId}) is not in the map catalog: {string.Join(", ", cockpit.MapPicker.Select(item => item.MapId))}");

                        // This map first, to learn where its middle is in the game's own coordinates.
                        cockpit.MapPicker.First(item => item.MapId == mapId).SelectCommand.Execute(null);
                        Assert.True(app.PumpUntil(() => cockpit.Renderer?.Scene.LocationId == mapId && legacy.Map.RenderModel?.Location.Id == mapId), $"{name} never opened.");
                        app.LearnMiddle();
                        await Task.Run(() => app.SeedCatalogAsync(name));

                        // Then another map, as the player would have open between raids: the raid has to bring this one back.
                        cockpit.MapPicker.First(item => item.MapId != mapId).SelectCommand.Execute(null);
                        Assert.True(app.PumpUntil(() => cockpit.Renderer?.Scene.LocationId is { } open && open != mapId), "No other map opened.");
                        try
                        {
                            await test(app);
                        }
                        finally
                        {
                            await app.LeaveSquadAsync();
                        }
                    }
                    finally
                    {
                        window.Close();
                    }
                }
                finally
                {
                    try
                    {
                        Directory.Delete(root, recursive: true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                return 0;
            },
            CancellationToken.None);
    }

    /// <summary>The running window, what the game's log and screenshots tell it, and what a player does to it.</summary>
    private sealed class App(
        Window window,
        RaidCockpitViewModel cockpit,
        MainWindowViewModel legacy,
        IServiceProvider services,
        string mapId,
        string logLocation,
        ITestOutputHelper output)
    {
        private WorldPosition _middle;

        public RaidCockpitViewModel Cockpit { get; } = cockpit;

        public bool PumpUntil(Func<bool> done, int turns = 400)
        {
            for (var turn = 0; turn < turns && !done(); turn++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }

            for (var turn = 0; turn < 5; turn++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }

            return done();
        }

        /// <summary>The game coordinates of the plan's middle on the open map.</summary>
        public void LearnMiddle()
        {
            var model = legacy.Map.RenderModel!;
            var bounds = Cockpit.Renderer!.Scene.Bounds;
            var centre = new RaidMark(
                Guid.NewGuid(),
                RaidMarkKind.Ping,
                new(mapId, null, (bounds.MinimumX + bounds.MaximumX) / 2, (bounds.MinimumY + bounds.MaximumY) / 2, null, null),
                DateTimeOffset.UtcNow);
            var middle = RaidCockpitViewModel.LocateMark(model, centre, null) ?? throw new Xunit.Sdk.XunitException($"{mapId}: the plan's middle has no game position.");
            // A height on the ground plan: inside no upper floor's or basement's extent, for the spawns and the screenshot.
            var upper = model.Floors.Where(floor => floor.Id != "base").SelectMany(floor => floor.Extents).ToArray();
            var height = Enumerable.Range(-60, 121).Select(step => step * 2.5)
                .OrderBy(Math.Abs)
                .First(y => !new[] { (0d, 0d), (12d, 0d), (-15d, 8d), (0d, -18d), (30d, 25d), (10d, 5d) }
                    .Any(offset => upper.Any(extent => extent.Contains(new(middle.X + offset.Item1, y, middle.Z + offset.Item2)))));
            _middle = middle with { Y = height };
            output.WriteLine($"{mapId}: middle at {_middle.X:0.0}, {_middle.Y:0.0}, {_middle.Z:0.0}; floors {string.Join(", ", model.Floors.Select(floor => floor.Id))}");
        }

        /// <summary>The map's row in the maps table as a sync writes it, with PMC areas around the middle.</summary>
        public async Task SeedCatalogAsync(string name)
        {
            string Spawn(double dx, double dz, string zone) => string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"position":{"x":{{_middle.X + dx}},"y":{{_middle.Y}},"z":{{_middle.Z + dz}}},"zoneName":"{{zone}}","sides":["pmc"],"categories":["player"]}""");
            var realRows = Environment.GetEnvironmentVariable("TARKOV_REAL_MAPS_DIR") is { Length: > 0 } dir && File.Exists(Path.Combine(dir, mapId + ".json"))
                ? await File.ReadAllTextAsync(Path.Combine(dir, mapId + ".json"))
                : null;
            var row = realRows ?? $$"""
                {"id":"test-{{mapId}}","name":"{{name}}","normalizedName":"{{mapId}}","nameId":"{{(RaidSlugs.ContainsKey(logLocation) ? mapId + "-day" : logLocation)}}","spawns":[
                  {{Spawn(12, 0, "ZoneNear")}},{{Spawn(-15, 8, "ZoneWest")}},{{Spawn(0, -18, "ZoneSouth")}},{{Spawn(30, 25, "ZoneEast")}}
                ]}
                """;
            await using (var connection = await services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(CancellationToken.None))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO maps (id, name, normalized_name, source_json) VALUES ($id, $name, $slug, $json);";
                command.Parameters.AddWithValue("$id", $"test-{mapId}");
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$slug", mapId);
                command.Parameters.AddWithValue("$json", row);
                await command.ExecuteNonQueryAsync();
            }

            if (RaidSlugs.TryGetValue(logLocation, out var raidSlug))
            {
                // The map's own row, as the sync writes it, so the parser learns its id; spawns come from the plan's map.
                await using var connection = await services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(CancellationToken.None);
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO maps (id, name, normalized_name, source_json) VALUES ($id, $name, $slug, $json);";
                command.Parameters.AddWithValue("$id", $"test-{raidSlug}");
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$slug", raidSlug);
                command.Parameters.AddWithValue("$json", $$"""{"id":"test-{{raidSlug}}","name":"{{name}}","normalizedName":"{{raidSlug}}","nameId":"{{logLocation}}","spawns":[]}""");
                await command.ExecuteNonQueryAsync();
            }

            services.GetRequiredService<IMapFeatureCatalog>().Invalidate();
            // What a finished sync does: the parser learns the game's id for each map from the table.
            var aliases = services.GetRequiredService<SqliteMapAliasCatalog>();
            aliases.Invalidate();
            services.GetRequiredService<EftLogParser>().UpdateAliases(await aliases.GetAsync(CancellationToken.None));
        }

        private GroupSessionService? _bravo;
        private readonly HttpClient _bravoClient = new();

        /// <summary>Bravo, in a raid on this map, sharing through the same relay; waits until Alpha is sharing too.</summary>
        public async Task JoinSquadAsync(string relayAddress)
        {
            var now = DateTimeOffset.UtcNow;
            var bravoState = new RuntimeStateStore(new RuntimeOptions(false, true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5)));
            bravoState.Update(snapshot => snapshot with
            {
                Raid = snapshot.Raid with
                {
                    RaidId = Guid.NewGuid(),
                    State = RaidLifecycleState.InRaid,
                    MapId = mapId,
                    Side = "PMC",
                    StartedUtc = now.AddMinutes(-1),
                    UpdatedUtc = now,
                    LastKnownPosition = new(now, new(_middle.X - 30, _middle.Y, _middle.Z + 20), default, 90, null, null, "bravo.png"),
                },
            });
            _bravo = new GroupSessionService(new BravoSettings(relayAddress), bravoState, _bravoClient, Microsoft.Extensions.Logging.Abstractions.NullLogger<GroupSessionService>.Instance);
            _bravo.Start();
            var store = services.GetRequiredService<IRuntimeStateStore>();
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < until && !(store.Current.Group.IsSharing && store.Current.Group.Members.Count > 0))
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }

            Assert.True(store.Current.Group.IsSharing, "Alpha never reached the relay.");
        }

        public async ValueTask LeaveSquadAsync()
        {
            if (_bravo is not null)
            {
                await _bravo.DisposeAsync();
            }

            _bravoClient.Dispose();
        }

        private sealed class BravoSettings(string address) : IGroupSettingsStore
        {
            public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
                Task.FromResult(new GroupSharingSettings(true, address, "Bravo", SquadKey, false, false));

            public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        /// <summary>The lines the game writes as a PMC raid begins, read as the log tail reads them.</summary>
        public async Task StartRaidAsync()
        {
            var now = DateTimeOffset.Now;
            var stamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string[] lines =
            [
                $"{stamp}|1.1.5.1.47510|Info|application|CompleteSelectedProfile ProfileId:{Pmc} AccountId: 1000001",
                $"{stamp}|1.1.5.1.47510|Debug|application|TRACE-NetworkGameCreate profileStatus: 'Profileid: {Pmc}, Status: Busy, RaidMode: Online, Ip: 203.0.113.10, Port: 17000, Location: {logLocation}, Sid: FAKE-SID, GameMode: deathmatch, shortId: FAKE01'",
                $"{stamp}|1.1.5.1.47510|Info|backend|WebSocketSharp - message received: NOTIFICATION e0000000000000000000000a userConfirmed [{{\"type\":\"userConfirmed\",\"eventId\":\"e0000000000000000000000b\",\"profileid\":\"{Pmc}\",\"profileToken\":\"FAKE-TOKEN\",\"location\":\"{logLocation}\",\"status\":\"Busy\",\"sid\":\"FAKE-SID\",\"ip\":\"203.0.113.10\",\"port\":17000,\"version\":\"live\",\"raidMode\":\"Online\",\"mode\":\"deathmatch\",\"shortId\":\"FAKE01\",\"additional_info\":[]}}]",
            ];
            var parser = services.GetRequiredService<EftLogParser>();
            var coordinator = services.GetRequiredService<RaidActivityCoordinator>();
            foreach (var line in lines)
            {
                if (parser.ParseLine(line, now.ToUniversalTime()) is { } evidence)
                {
                    var raid = await Task.Run(() => coordinator.ApplyEvidenceAsync(evidence, CancellationToken.None));
                    output.WriteLine($"log: {raid.State} map '{raid.MapId}' side '{raid.Side}'");
                }
            }

            Assert.True(
                PumpUntil(() => Cockpit.Renderer?.Scene.LocationId == mapId),
                $"The raid on '{logLocation}' did not bring {mapId} up: the map shows '{Cockpit.Renderer?.Scene.LocationId}', the raid says '{services.GetRequiredService<IRuntimeStateStore>().Current.Raid.MapId}'.");
        }

        /// <summary>A screenshot near the middle of the map, named as the game names it.</summary>
        public async Task ScreenshotAsync()
        {
            var now = DateTimeOffset.Now;
            var file = string.Create(
                CultureInfo.InvariantCulture,
                $"{now:yyyy-MM-dd[HH-mm]}_{_middle.X + 10:0.00}, {_middle.Y:0.00}, {_middle.Z + 5:0.00}_0.00000, 0.70711, 0.00000, 0.70711_12.00 (0).png");
            Assert.True(services.GetRequiredService<IScreenshotFilenameParser>().TryParse(file, now.Offset, out var position), $"'{file}' did not parse.");
            await Task.Run(() => services.GetRequiredService<RaidActivityCoordinator>().ApplyPositionAsync(position!, CancellationToken.None));
        }

        /// <summary>The objects of one layer the map draws on the plan now.</summary>
        public MapSceneObjectId[] Shown(MapSceneLayerId layer)
        {
            var ids = Cockpit.Renderer?.Scene.Objects.Where(item => item.LayerId == layer).Select(item => item.Id).ToHashSet() ?? [];
            return Cockpit.Renderer is { } renderer && LayerOn(renderer, layer)
                ?
                [
                    .. renderer.SpatialObjects.Where(item => item.ObjectId is { } id && ids.Contains(id) && item.IsShownOnPlan).Select(item => item.ObjectId!.Value),
                    // A line is geometry, not a marker.
                    .. renderer.GeometryObjects.Where(item => ids.Contains(item.SceneObject.Id) && item.Points.Count > 1).Select(item => item.SceneObject.Id),
                ]
                : [];
        }

        public string Status => Find("v2-raid-ping-status") is { IsEffectivelyVisible: true } line
            ? AutomationProperties.GetName(line) ?? string.Empty
            : string.Empty;

        public string Describe()
        {
            var raid = services.GetRequiredService<IRuntimeStateStore>().Current.Raid;
            var renderer = Cockpit.Renderer;
            var layers = renderer is null ? "no renderer" : string.Join(", ", renderer.Scene.Layers.Select(layer => $"{layer.Id}={(LayerOn(renderer, layer.Id) ? "on" : "off")}"));
            var nearby = renderer?.Scene.Objects.Where(item => item.LayerId == RaidCockpitViewModel.NearbySpawnsLayerId).ToArray() ?? [];
            var nearbyText = string.Join(" ", nearby.Select(item => $"[{string.Join(",", item.FloorIds)}|{item.Geometry.Kind}|{(renderer!.Scene.Bounds.Contains(item.Geometry.Points[0]) ? "in" : "out")}|{(renderer.SpatialObjects.Any(marker => marker.ObjectId == item.Id) ? "marker" : "nomarker")}]"));
            return $"nearby objects {nearby.Length} {nearbyText}; Raid {raid.State} on '{raid.MapId}' side '{raid.Side}', trail {raid.PositionTrail.Count}; map '{renderer?.Scene.LocationId}' floor '{renderer?.Scene.View.SelectedFloorId}'; " +
                $"spawn status '{Cockpit.OpeningSpawnStatus}'; features {legacy.Map.MapFeatures.Count}; layers {layers}";
        }

        /// <summary>Right-clicks bare map near the middle of the plan, and answers the one mark it placed.</summary>
        public RaidMark RightClickMap()
        {
            var marks = services.GetRequiredService<IRaidMarkStore>();
            var view = window.GetVisualDescendants().OfType<MapSceneRendererView>().Single();
            var plan = view.FindControl<Border>("PlanViewport")!;
            var renderer = Cockpit.Renderer!;
            Point? spot = null;
            for (var x = plan.Bounds.Width * 0.15; x < plan.Bounds.Width * 0.85 && spot is null; x += 7)
            {
                for (var y = plan.Bounds.Height * 0.15; y < plan.Bounds.Height * 0.85; y += 7)
                {
                    if (!renderer.TryHitRightClickTargetAt(x, y, out _) && renderer.TryScenePointAt(x, y, out var scenePoint) && renderer.Scene.Bounds.Contains(scenePoint))
                    {
                        spot = new Point(x, y);
                        break;
                    }
                }
            }

            Assert.NotNull(spot);
            var before = marks.Marks.Select(mark => mark.Id).ToHashSet();
            var at = plan.TranslatePoint(spot.Value, window)!.Value;
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Right);
            window.MouseUp(at, MouseButton.Right);
            PumpUntil(() => marks.Marks.Any(mark => !before.Contains(mark.Id)));
            output.WriteLine($"right-click at {spot}: map says '{Status}'");
            return Assert.Single(marks.Marks, mark => !before.Contains(mark.Id));
        }

        public bool IsDrawn(RaidMark mark)
        {
            var id = $"mark:{mark.Id}";
            return PumpUntil(() => LayerOn(Cockpit.Renderer!, new("my-marks")) &&
                Cockpit.Renderer!.SpatialObjects.Any(item => item.ObjectId?.Value == id && item.IsShownOnPlan));
        }

        /// <summary>Whether the scene's view has the layer on, or its default does when the view does not say.</summary>
        public static bool LayerOn(TarkovCompanion.App.ViewModels.V2.MapRenderer.MapSceneRendererViewModel renderer, MapSceneLayerId layer) =>
            renderer.Scene.View.Layers.FirstOrDefault(state => state.LayerId == layer)?.IsVisible ??
            renderer.Scene.Layers.FirstOrDefault(item => item.Id == layer)?.IsVisibleByDefault ?? false;

        /// <summary>Where the mark is between the store and the screen, for a failure message.</summary>
        public string Trace(RaidMark mark)
        {
            var id = $"mark:{mark.Id}";
            var renderer = Cockpit.Renderer!;
            var inStore = services.GetRequiredService<IRaidMarkStore>().Marks.Any(item => item.Id == mark.Id);
            var inScene = renderer.Scene.Objects.FirstOrDefault(item => item.Id.Value == id);
            var marker = renderer.SpatialObjects.FirstOrDefault(item => item.ObjectId?.Value == id);
            return $"store {inStore}; scene {(inScene is null ? "no" : $"layer {inScene.LayerId} floors [{string.Join(",", inScene.FloorIds)}] expires {inScene.ExpiresUtc:HH:mm:ss} now {DateTimeOffset.UtcNow:HH:mm:ss}")}; " +
                $"marker {(marker is null ? "none" : $"shown {marker.IsShownOnPlan}")}; points {renderer.SpatialObjects.Count}, clusters {renderer.ClusterMarkers.Count}";
        }

        public bool PingDotIsVisible()
        {
            window.UpdateLayout();
            return window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>()
                .Any(ellipse => ellipse.Classes.Contains("v2-map-ping-dot") && ellipse.IsEffectivelyVisible);
        }

        private Control? Find(string automationId) => window.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == automationId);
    }
}
