using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.UnitTests.V2MapRenderer;
using TarkovCompanion.UnitTests.V2Shell;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [#992] "Follow does not stay on between raids on the map where I turned it on", filed three
/// times. #702, #717, #803, #933 and #988 were each proven on a view model and still failed in the
/// running app. Here the whole app as composed, raids begun and ended by the game's own log lines,
/// screenshots through the coordinator the watcher feeds, a real click on the Follow button, a real
/// drag on the plan, and a restart over the same data folder.
/// </summary>
/// <remarks>
/// What turned Follow off for good on main: a drag on the plan (any look around the map during a
/// raid) saved Follow as off, for every map, and the saved value was all the next raid read. A drag
/// now only pauses following until the next raid, the next map, or a tap on "Follow paused".
/// </remarks>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class FollowStaysOnPerMapTests(ITestOutputHelper output)
{
    private const string Pmc = "f00000000000000000000001";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Follow_turned_on_for_woods_stays_on_there_raid_after_raid_and_across_a_restart(bool lookAroundInRaid)
    {
        using var session = HeadlessSessions.StartNew(typeof(V2SettingsIndexLandingTests.SetupViewApp));
        await session.Dispatch(
            async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), $"tarkov-follow-{Guid.NewGuid():N}");
                try
                {
                    await using (var app = await App.StartAsync(root, output, seed: true))
                    {
                        // Woods raid 1: the first screenshot, then Follow on with a click.
                        await app.StartRaidAsync("Woods", "woods", "RAID01");
                        await app.ScreenshotAsync("woods", 10, 5);
                        app.Pump();
                        if (app.FollowButtonIsOn())
                        {
                            // Main had Follow on everywhere until something turned it off.
                            app.ClickFollow();
                        }

                        app.ClickFollow();
                        Assert.True(app.FollowButtonIsOn(), $"A click on Follow did not turn it on. {app.Describe()}");
                        Assert.True(app.PumpUntil(() => app.CameraIsOnPlayer()), $"Follow on and the map is not on the player. {app.Describe()}");

                        if (lookAroundInRaid)
                        {
                            // The player drags the plan to look around: following pauses, Follow stays on.
                            app.DragPlan();
                            Assert.True(app.PumpUntil(() => app.FollowButtonText == "Follow paused"), $"A drag did not show Follow paused: '{app.FollowButtonText}'. {app.Describe()}");
                            Assert.True(app.FollowButtonIsOn(), "A drag turned the Follow button off.");
                            await app.ScreenshotAsync("woods", 40, -30);
                            app.Pump();
                            Assert.False(app.CameraIsOnPlayer(), "A screenshot pulled the map back while following was paused.");
                        }

                        // The raid ends, as the game writes it.
                        await app.EndRaidAsync("Woods", "RAID01");

                        // Woods raid 2: Follow is on, and the first screenshot moves the map.
                        await app.StartRaidAsync("Woods", "woods", "RAID02");
                        Assert.True(app.FollowButtonIsOn(), $"Woods raid 2: Follow is off. {app.Describe()}");
                        Assert.Equal("Follow", app.FollowButtonText);
                        await app.ScreenshotAsync("woods", -25, 20);
                        Assert.True(app.PumpUntil(() => app.CameraIsOnPlayer()), $"Woods raid 2: the first screenshot did not move the map. {app.Describe()}");
                        await app.EndRaidAsync("Woods", "RAID02");

                        // Customs: never turned on there.
                        await app.StartRaidAsync("bigmap", "customs", "RAID03");
                        await app.ScreenshotAsync("customs", 10, 5);
                        app.Pump();
                        Assert.False(app.FollowButtonIsOn(), $"Customs: Follow is on and was never turned on there. {app.Describe()}");
                        await app.EndRaidAsync("bigmap", "RAID03");

                        // Back to Woods: on.
                        await app.StartRaidAsync("Woods", "woods", "RAID04");
                        Assert.True(app.FollowButtonIsOn(), $"Woods after Customs: Follow is off. {app.Describe()}");
                        await app.ScreenshotAsync("woods", 20, 25);
                        Assert.True(app.PumpUntil(() => app.CameraIsOnPlayer()), $"Woods after Customs: the screenshot did not move the map. {app.Describe()}");
                        await app.EndRaidAsync("Woods", "RAID04");
                    }

                    // A restart over the same data folder.
                    await using (var app = await App.StartAsync(root, output, seed: false))
                    {
                        await app.StartRaidAsync("Woods", "woods", "RAID05");
                        Assert.True(app.FollowButtonIsOn(), $"Woods after a restart: Follow is off. {app.Describe()}");
                        await app.ScreenshotAsync("woods", -10, -15);
                        Assert.True(app.PumpUntil(() => app.CameraIsOnPlayer()), $"Woods after a restart: the screenshot did not move the map. {app.Describe()}");
                        await app.EndRaidAsync("Woods", "RAID05");

                        await app.StartRaidAsync("bigmap", "customs", "RAID06");
                        Assert.False(app.FollowButtonIsOn(), $"Customs after a restart: Follow is on. {app.Describe()}");
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

    /// <summary>The running window over one data folder, and what the game's log and screenshots tell it.</summary>
    private sealed class App : IAsyncDisposable
    {
        private static readonly string[] Maps = ["woods", "customs"];

        private readonly ServiceProvider _services;
        private readonly Window _window;
        private readonly MainWindowViewModel _legacy;
        private readonly ITestOutputHelper _output;
        private readonly Dictionary<string, WorldPosition> _middles = new(StringComparer.Ordinal);

        private App(ServiceProvider services, Window window, MainWindowViewModel legacy, RaidCockpitViewModel cockpit, ITestOutputHelper output)
        {
            _services = services;
            _window = window;
            _legacy = legacy;
            Cockpit = cockpit;
            _output = output;
        }

        public RaidCockpitViewModel Cockpit { get; }

        public static async Task<App> StartAsync(string root, ITestOutputHelper output, bool seed)
        {
            var services = AppComposition.Build(
                new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                new(DataRoot: root, Offline: true));
            var legacy = services.GetRequiredService<MainWindowViewModel>();
            var shell = services.GetRequiredService<V2ShellViewModel>();
            legacy.PreviewShell = shell;
            await legacy.InitializeAsync();
            shell.Router.NavigateToAddress("raid");
            var window = new MainWindow { DataContext = legacy, Width = 1920, Height = 1080 };
            window.Show();
            var cockpit = (RaidCockpitViewModel)shell.RaidCockpit!;
            var app = new App(services, window, legacy, cockpit, output);
            Assert.True(app.PumpUntil(() => cockpit.MapPicker.Count > 0), "The map catalog never loaded.");
            foreach (var map in Maps)
            {
                // Each map once, to learn where its middle is in the game's own coordinates.
                cockpit.MapPicker.First(item => item.MapId == map).SelectCommand.Execute(null);
                Assert.True(app.PumpUntil(() => cockpit.Renderer?.Scene.LocationId == map && legacy.Map.RenderModel?.Location.Id == map), $"{map} never opened.");
                app.LearnMiddle(map);
            }

            await Task.Run(() => app.SeedCatalogAsync(seed));

            // Another map open between raids, as the player would have: the raid brings its own back.
            cockpit.MapPicker.First(item => !Maps.Contains(item.MapId)).SelectCommand.Execute(null);
            Assert.True(app.PumpUntil(() => cockpit.Renderer?.Scene.LocationId is { } open && !Maps.Contains(open)), "No other map opened.");
            return app;
        }

        public async ValueTask DisposeAsync()
        {
            _window.Close();
            Pump();
            await _services.DisposeAsync();
        }

        public void Pump() => PumpUntil(() => false, 15);

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

        private void LearnMiddle(string mapId)
        {
            var model = _legacy.Map.RenderModel!;
            var bounds = Cockpit.Renderer!.Scene.Bounds;
            var centre = new RaidMark(
                Guid.NewGuid(),
                RaidMarkKind.Ping,
                new(mapId, null, (bounds.MinimumX + bounds.MaximumX) / 2, (bounds.MinimumY + bounds.MaximumY) / 2, null, null),
                DateTimeOffset.UtcNow);
            var middle = RaidCockpitViewModel.LocateMark(model, centre, null) ?? throw new Xunit.Sdk.XunitException($"{mapId}: the plan's middle has no game position.");
            var upper = model.Floors.Where(floor => floor.Id != "base").SelectMany(floor => floor.Extents).ToArray();
            var height = Enumerable.Range(-60, 121).Select(step => step * 2.5)
                .OrderBy(Math.Abs)
                .First(y => !upper.Any(extent => extent.Contains(new(middle.X, y, middle.Z))));
            _middles[mapId] = middle with { Y = height };
        }

        /// <summary>Each map's row in the maps table as a sync writes it, so the log's ids resolve.</summary>
        private async Task SeedCatalogAsync(bool insert)
        {
            if (insert)
            {
                foreach (var (mapId, nameId, name) in new[] { ("woods", "Woods", "Woods"), ("customs", "bigmap", "Customs") })
                {
                    await using var connection = await _services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(CancellationToken.None);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO maps (id, name, normalized_name, source_json) VALUES ($id, $name, $slug, $json);";
                    command.Parameters.AddWithValue("$id", $"test-{mapId}");
                    command.Parameters.AddWithValue("$name", name);
                    command.Parameters.AddWithValue("$slug", mapId);
                    command.Parameters.AddWithValue("$json", $$"""{"id":"test-{{mapId}}","name":"{{name}}","normalizedName":"{{mapId}}","nameId":"{{nameId}}","spawns":[]}""");
                    await command.ExecuteNonQueryAsync();
                }
            }

            _services.GetRequiredService<IMapFeatureCatalog>().Invalidate();
            var aliases = _services.GetRequiredService<SqliteMapAliasCatalog>();
            aliases.Invalidate();
            _services.GetRequiredService<EftLogParser>().UpdateAliases(await aliases.GetAsync(CancellationToken.None));
        }

        /// <summary>The lines the game writes as a PMC raid begins, read as the log tail reads them.</summary>
        public async Task StartRaidAsync(string logLocation, string mapId, string shortId)
        {
            var stamp = Stamp(out var now);
            await ApplyAsync(
                now,
                $"{stamp}|1.1.5.1.47510|Info|application|CompleteSelectedProfile ProfileId:{Pmc} AccountId: 1000001",
                $"{stamp}|1.1.5.1.47510|Debug|application|TRACE-NetworkGameCreate profileStatus: 'Profileid: {Pmc}, Status: Busy, RaidMode: Online, Ip: 203.0.113.10, Port: 17000, Location: {logLocation}, Sid: FAKE-SID, GameMode: deathmatch, shortId: {shortId}'",
                Notification(stamp, "userConfirmed", "Busy", logLocation, shortId));
            Assert.True(
                PumpUntil(() => Cockpit.Renderer?.Scene.LocationId == mapId && _legacy.Map.RenderModel?.Location.Id == mapId),
                $"The raid on '{logLocation}' did not bring {mapId} up: the map shows '{Cockpit.Renderer?.Scene.LocationId}'.");
            Pump();
        }

        /// <summary>The game's end of the raid, the result screen following it.</summary>
        public async Task EndRaidAsync(string logLocation, string shortId)
        {
            var stamp = Stamp(out var now);
            await ApplyAsync(now, Notification(stamp, "userMatchOver", "Free", logLocation, shortId));
            var store = _services.GetRequiredService<IRuntimeStateStore>();
            Assert.True(PumpUntil(() => store.Current.Raid.State != RaidLifecycleState.InRaid), $"The raid did not end: {store.Current.Raid.State}.");
        }

        private static string Stamp(out DateTimeOffset now)
        {
            now = DateTimeOffset.Now;
            return now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        private static string Notification(string stamp, string type, string status, string location, string shortId) =>
            $"{stamp}|1.1.5.1.47510|Info|backend|WebSocketSharp - message received: NOTIFICATION [EV-{shortId}-{type}] {type} " +
            "[{\"type\":\"" + type + "\",\"eventId\":\"EV-" + shortId + "-" + type + "\",\"profileid\":\"" + Pmc +
            "\",\"status\":\"" + status + "\",\"location\":\"" + location +
            "\",\"raidMode\":\"Online\",\"mode\":\"deathmatch\",\"shortId\":\"" + shortId + "\"}]";

        private async Task ApplyAsync(DateTimeOffset now, params string[] lines)
        {
            var parser = _services.GetRequiredService<EftLogParser>();
            var coordinator = _services.GetRequiredService<RaidActivityCoordinator>();
            foreach (var line in lines)
            {
                if (parser.ParseLine(line, now.ToUniversalTime()) is { } evidence)
                {
                    var raid = await Task.Run(() => coordinator.ApplyEvidenceAsync(evidence, CancellationToken.None));
                    _output.WriteLine($"log: {raid.State} map '{raid.MapId}'");
                }
            }
        }

        /// <summary>A screenshot at an offset from the middle of the map, named as the game names it.</summary>
        public async Task ScreenshotAsync(string mapId, double dx, double dz)
        {
            var middle = _middles[mapId];
            var now = DateTimeOffset.Now;
            var file = string.Create(
                CultureInfo.InvariantCulture,
                $"{now:yyyy-MM-dd[HH-mm]}_{middle.X + dx:0.00}, {middle.Y:0.00}, {middle.Z + dz:0.00}_0.00000, 0.70711, 0.00000, 0.70711_12.00 (0).png");
            Assert.True(_services.GetRequiredService<IScreenshotFilenameParser>().TryParse(file, now.Offset, out var position), $"'{file}' did not parse.");
            await Task.Run(() => _services.GetRequiredService<RaidActivityCoordinator>().ApplyPositionAsync(position!, CancellationToken.None));
            PumpUntil(() => _legacy.Map.PlayerPosition?.Filename == position!.Filename);
        }

        private ToggleButton FollowButton() =>
            _window.GetVisualDescendants().OfType<ToggleButton>().Single(control => AutomationProperties.GetAutomationId(control) == "v2-raid-follow");

        public bool FollowButtonIsOn() => FollowButton().IsChecked == true;

        public string FollowButtonText => FollowButton().Content?.ToString() ?? string.Empty;

        /// <summary>A real left click in the middle of the Follow button.</summary>
        public void ClickFollow()
        {
            var button = FollowButton();
            _window.UpdateLayout();
            var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), _window)!.Value;
            _window.MouseMove(at);
            _window.MouseDown(at, MouseButton.Left);
            _window.MouseUp(at, MouseButton.Left);
            Pump();
        }

        /// <summary>A real drag across bare plan, the way a player looks around the map.</summary>
        public void DragPlan()
        {
            var view = _window.GetVisualDescendants().OfType<MapSceneRendererView>().Single();
            var plan = view.FindControl<Border>("PlanViewport")!;
            var renderer = Cockpit.Renderer!;
            Point? spot = null;
            for (var x = plan.Bounds.Width * 0.3; x < plan.Bounds.Width * 0.7 && spot is null; x += 7)
            {
                for (var y = plan.Bounds.Height * 0.3; y < plan.Bounds.Height * 0.7; y += 7)
                {
                    if (!renderer.TryHitRightClickTargetAt(x, y, out _))
                    {
                        spot = new Point(x, y);
                        break;
                    }
                }
            }

            Assert.NotNull(spot);
            var start = plan.TranslatePoint(spot.Value, _window)!.Value;
            _window.MouseMove(start);
            _window.MouseDown(start, MouseButton.Left);
            for (var step = 1; step <= 6; step++)
            {
                _window.MouseMove(start + new Point(step * 25, step * 15));
            }

            _window.MouseUp(start + new Point(150, 90), MouseButton.Left);
            Pump();
        }

        /// <summary>Whether the camera is centred on the player's last screenshot, at a following zoom.</summary>
        public bool CameraIsOnPlayer()
        {
            if (Cockpit.Renderer is not { } renderer || _legacy.Map.PlayerPosition is not { } position ||
                _legacy.Map.RenderModel is not { } model || !model.TryMapPosition(position.Position, out var point))
            {
                return false;
            }

            var camera = renderer.Scene.View.Camera;
            var tolerance = Math.Max(renderer.Scene.Bounds.Width, renderer.Scene.Bounds.Height) * 0.01;
            return Math.Abs(camera.CenterX - point.X) < tolerance && Math.Abs(camera.CenterY - point.Y) < tolerance;
        }

        public string Describe()
        {
            var raid = _services.GetRequiredService<IRuntimeStateStore>().Current.Raid;
            var camera = Cockpit.Renderer?.Scene.View.Camera;
            var player = _legacy.Map.PlayerPosition is { } position && _legacy.Map.RenderModel is { } model && model.TryMapPosition(position.Position, out var point)
                ? $"{point.X:0.0},{point.Y:0.0}"
                : "none";
            return $"Raid {raid.State} on '{raid.MapId}'; map '{Cockpit.Renderer?.Scene.LocationId}'; follow {Cockpit.FollowsPlayer} button '{FollowButtonText}' checked {FollowButton().IsChecked}; " +
                $"camera {camera?.CenterX:0.0},{camera?.CenterY:0.0} x{camera?.Zoom:0.00}; player {player}";
        }
    }
}
