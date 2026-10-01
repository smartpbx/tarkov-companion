using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.UnitTests.Group;
using TarkovCompanion.UnitTests.V2MapRenderer;
using TarkovCompanion.UnitTests.V2Shell;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// #983: "i STILL cannot ping on the map at all", in a squad, in a raid and after dying with a
/// squadmate still inside. The whole app as it is composed, the real main window on the Raid page,
/// and a real pointer on the real controls; a relay in this process standing in for the group's.
/// </summary>
/// <remarks>
/// Every earlier fix (#584, #707, #929) was proved on a view model or on a hit test, and every one
/// was reported broken again. Measured here on main before this fix, a right-click on bare map did
/// place and send a ping in all four cases. What failed was everything around it: nothing on the
/// page said that pinging is a right-click (the buttons and the sentence went in rough package 46),
/// a left-click (what a player tries) never pings, a ping placed while the group was between two
/// answers became "Just me" for good and never reached the squad, and no outcome was ever shown.
/// </remarks>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class MapPingThroughTheAppTests(ITestOutputHelper output)
{
    private const string Key = "a-key-long-enough-for-a-room";

    public enum Situation
    {
        NotInRaid,
        InRaid,
        AfterDeath,
    }

    /// <summary>
    /// Fails on main: there is no Ping tool to press, and a left-click on the map only selects.
    /// </summary>
    [Theory]
    [InlineData(Situation.NotInRaid)]
    [InlineData(Situation.InRaid)]
    [InlineData(Situation.AfterDeath)]
    public async Task In_a_squad_the_Ping_tool_and_a_click_ping_the_map_and_the_squad_gets_it(Situation situation)
    {
        await using var relay = await InProcessRelay.StartAsync();
        await RunAsync(relay.Address, situation, async app =>
        {
            app.Press("v2-raid-mode-ping");
            Assert.True(app.IsShowing("v2-raid-ping-bar"), $"The Ping tool did not switch the map to pinging (mode {app.Cockpit.InteractionMode}).");
            var mark = app.ClickMap(MouseButton.Left);

            Assert.Equal(RaidMarkKind.Ping, mark.Kind);
            Assert.Equal(RaidMarkScope.Squad, mark.Scope);
            Assert.True(app.IsDrawn(mark), "The ping is not drawn on the map.");
            Assert.True(await app.UntilAsync(() => relay.Marks.Contains("ping customs by Alpha")), "The relay never got the ping.");
            Assert.True(await app.UntilAsync(() => app.Status == "Ping sent to squad"), $"The map said '{app.Status}'.");
            Assert.True(app.IsShowing("v2-raid-ping-status"));
        });
    }

    /// <summary>The gesture that has always pinged keeps working where Clayton plays, and says so.</summary>
    [Theory]
    [InlineData(Situation.InRaid)]
    [InlineData(Situation.AfterDeath)]
    public async Task In_a_squad_a_right_click_pings_and_the_map_says_the_squad_has_it(Situation situation)
    {
        await using var relay = await InProcessRelay.StartAsync();
        await RunAsync(relay.Address, situation, async app =>
        {
            Assert.True(app.IsShowing("v2-raid-ping-hint"), "Nothing on the map says how to ping.");
            var mark = app.ClickMap(MouseButton.Right);

            Assert.Equal(RaidMarkKind.Ping, mark.Kind);
            Assert.True(app.IsDrawn(mark), "The ping is not drawn on the map.");
            Assert.True(await app.UntilAsync(() => relay.Marks.Contains("ping customs by Alpha")), "The relay never got the ping.");
            Assert.True(await app.UntilAsync(() => app.Status == "Ping sent to squad"), $"The map said '{app.Status}'.");
            Assert.False(app.IsShowing("v2-raid-ping-hint"));
        });
    }

    /// <summary>
    /// [#983] The ping that was placed can be seen: a disc of at least 28 screen pixels at the
    /// fitted zoom, drawn above the traffic heat, the loot and the labels, and above every other
    /// mark. Fails on main, where it was a 16 px ring and an 8 px dot drawn at seven-tenths size.
    /// </summary>
    [Fact]
    public async Task A_placed_ping_is_a_large_disc_drawn_above_the_heat_and_every_other_mark()
    {
        await using var relay = await InProcessRelay.StartAsync();
        await RunAsync(relay.Address, Situation.InRaid, app =>
        {
            app.Cockpit.Renderer!.FitPlanCommand.Execute(null);
            app.Pump(() => false, 10);
            var mark = app.ClickMap(MouseButton.Right);
            Assert.True(app.IsDrawn(mark), "The ping is not drawn on the map.");
            app.Pump(() => false, 10);

            var (diameter, aboveHeat, onTop) = app.MeasurePing();
            Assert.True(diameter >= 28, $"The ping's disc is {diameter:0.0} px across on screen.");
            Assert.True(aboveHeat, "The ping is drawn under the traffic heat, the loot or the labels.");
            Assert.True(onTop, "Another mark is drawn above the ping.");
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Fails on main: with the relay not answering at the moment of the press, the ping was made
    /// "Just me" and stayed on this PC after the relay came back.
    /// </summary>
    [Fact]
    public async Task A_ping_placed_while_the_relay_is_down_says_so_and_reaches_the_squad_when_it_is_back()
    {
        var port = InProcessRelay.FreePort();
        await RunAsync($"http://127.0.0.1:{port}/", Situation.InRaid, async app =>
        {
            var mark = app.ClickMap(MouseButton.Right);

            Assert.Equal(RaidMarkScope.Squad, mark.Scope);
            Assert.True(app.IsDrawn(mark), "The ping is not drawn on the map.");
            Assert.True(
                await app.UntilAsync(() => app.Status == "Ping kept on this PC · relay offline, sends when it is back"),
                $"The map said '{app.Status}'.");

            await using var relay = await InProcessRelay.StartAsync(port);
            Assert.True(
                await app.UntilAsync(() => relay.Marks.Contains("ping customs by Alpha"), TimeSpan.FromSeconds(25)),
                "The ping never reached the relay once it was back.");
        }, waitForGroup: false);
    }

    /// <summary>Solo, a ping is drawn here and the map says why the squad does not have it. Fails on main: it said nothing.</summary>
    [Fact]
    public async Task Without_a_squad_a_ping_is_drawn_here_and_the_map_says_to_join_one()
    {
        await RunAsync(null, Situation.InRaid, app =>
        {
            var mark = app.ClickMap(MouseButton.Right);

            Assert.Equal(RaidMarkScope.Private, mark.Scope);
            Assert.True(app.IsDrawn(mark), "The ping is not drawn on the map.");
            Assert.Equal("Ping on this PC only · join a squad to share pings", app.Status);
            Assert.True(app.IsShowing("v2-raid-ping-status"));
            return Task.CompletedTask;
        });
    }

    private async Task RunAsync(string? relayAddress, Situation situation, Func<App, Task> test, bool waitForGroup = true)
    {
        using var session = HeadlessSessions.StartNew(typeof(V2SettingsIndexLandingTests.SetupViewApp));
        await session.Dispatch(
            async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), $"tarkov-ping-{Guid.NewGuid():N}");
                GroupSessionService? bravo = null;
                using var bravoClient = new HttpClient();
                try
                {
                    await using var services = AppComposition.Build(
                        new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                        new(DataRoot: root, Offline: true));
                    if (relayAddress is not null)
                    {
                        await services.GetRequiredService<IGroupSettingsStore>().SaveAsync(
                            new GroupSharingSettings(true, relayAddress, "Alpha", Key, false, false), CancellationToken.None);
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
                        var app = new App(window, (RaidCockpitViewModel)shell.RaidCockpit!, services.GetRequiredService<IRaidMarkStore>(), output);
                        app.Pump(() => app.Cockpit.MapPicker.Count > 0);
                        app.Cockpit.MapPicker.First(item => item.MapId == "customs").SelectCommand.Execute(null);
                        app.Pump(() => app.Cockpit.Renderer?.Scene.LocationId == "customs");
                        var store = services.GetRequiredService<IRuntimeStateStore>();
                        bravo = Enter(store, legacy, app.Cockpit, situation, relayAddress, bravoClient);
                        if (relayAddress is not null && waitForGroup)
                        {
                            app.Pump(() => store.Current.Group.IsSharing && store.Current.Group.Members.Count > 0, 400);
                            Assert.True(store.Current.Group.IsSharing, "Alpha never reached the relay.");
                        }

                        app.Pump(() => false, 10);
                        await test(app);
                    }
                    finally
                    {
                        window.Close();
                        if (bravo is not null)
                        {
                            await bravo.DisposeAsync();
                        }
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

    /// <summary>Puts the player in the situation, with Bravo in raid on Customs through the same relay.</summary>
    private static GroupSessionService? Enter(
        IRuntimeStateStore store,
        MainWindowViewModel legacy,
        RaidCockpitViewModel cockpit,
        Situation situation,
        string? relayAddress,
        HttpClient bravoClient)
    {
        var model = legacy.Map.RenderModel!;
        var bounds = cockpit.Renderer!.Scene.Bounds;
        WorldPosition World(double x, double y) => RaidCockpitViewModel.LocateMark(
            model,
            new RaidMark(Guid.NewGuid(), RaidMarkKind.Ping, new("customs", null, bounds.MinimumX + ((bounds.MaximumX - bounds.MinimumX) * x), bounds.MinimumY + ((bounds.MaximumY - bounds.MinimumY) * y), null, null), DateTimeOffset.UtcNow),
            0)!.Value;
        var now = DateTimeOffset.UtcNow;
        var trail = Enumerable.Range(0, 6)
            .Select(step => new ScreenshotPosition(now.AddSeconds(-20 * (6 - step)), World(0.42 + (0.02 * step), 0.5 - (0.02 * step)), default, 40 * step, null, null, $"alpha-{step}.png"))
            .ToArray();
        store.Update(snapshot => snapshot with
        {
            Raid = situation == Situation.NotInRaid
                ? snapshot.Raid with { RaidId = null, State = RaidLifecycleState.Menu, MapId = null, LastKnownPosition = null, PositionTrail = [] }
                : snapshot.Raid with
                {
                    RaidId = Guid.NewGuid(),
                    State = situation == Situation.InRaid ? RaidLifecycleState.InRaid : RaidLifecycleState.PostRaid,
                    MapId = "customs",
                    Side = "PMC",
                    StartedUtc = now.AddMinutes(-6),
                    UpdatedUtc = now,
                    LastKnownPosition = trail[^1],
                    PositionTrail = trail,
                },
        });
        if (relayAddress is null)
        {
            return null;
        }

        var bravoState = new RuntimeStateStore(new RuntimeOptions(false, true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5)));
        bravoState.Update(snapshot => snapshot with
        {
            Raid = snapshot.Raid with
            {
                RaidId = Guid.NewGuid(),
                State = RaidLifecycleState.InRaid,
                MapId = "customs",
                Side = "PMC",
                StartedUtc = now.AddMinutes(-6),
                UpdatedUtc = now,
                LastKnownPosition = new(now, World(0.6, 0.4), default, 90, null, null, "bravo.png"),
            },
        });
        var bravo = new GroupSessionService(new FixedSettings(relayAddress, "Bravo"), bravoState, bravoClient, NullLogger<GroupSessionService>.Instance);
        bravo.Start();
        return bravo;
    }

    /// <summary>The running window, and what a player does to it.</summary>
    private sealed class App(Window window, RaidCockpitViewModel cockpit, IRaidMarkStore marks, ITestOutputHelper output)
    {
        public RaidCockpitViewModel Cockpit { get; } = cockpit;

        public void Pump(Func<bool> done, int turns = 200)
        {
            for (var turn = 0; turn < turns && !done(); turn++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }

            Dispatcher.UIThread.RunJobs();
        }

        public async Task<bool> UntilAsync(Func<bool> done, TimeSpan? budget = null)
        {
            var until = DateTime.UtcNow + (budget ?? TimeSpan.FromSeconds(10));
            while (DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                if (done())
                {
                    return true;
                }

                await Task.Delay(20);
            }

            Dispatcher.UIThread.RunJobs();
            return done();
        }

        /// <summary>Presses a control by its automation id with the left button, where it is drawn.</summary>
        public void Press(string automationId)
        {
            // Settled first: a raid state landing moves strip controls between its rows (#838).
            Pump(() => false, 10);
            window.UpdateLayout();
            var control = Find(automationId) ?? throw new Xunit.Sdk.XunitException($"Nothing on the page is '{automationId}'.");
            Assert.True(control.IsEffectivelyVisible && control.IsEffectivelyEnabled, $"'{automationId}' cannot be pressed.");
            // The pointer arrives before it presses, as a hand's does: arriving can wake the map's
            // controls and move the strip, and a press aimed at where the button was misses it.
            Point Centre() => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            window.MouseMove(Centre());
            Pump(() => false, 5);
            window.UpdateLayout();
            var centre = Centre();
            window.MouseMove(centre);
            Pump(() => false, 2);
            centre = Centre();
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Pump(() => false, 3);
        }

        public bool IsShowing(string automationId) => Find(automationId) is { IsEffectivelyVisible: true };

        /// <summary>What the map's ping line says, as a screen reader hears it; empty when it is not showing.</summary>
        public string Status => Find("v2-raid-ping-status") is { IsEffectivelyVisible: true } line
            ? AutomationProperties.GetName(line) ?? string.Empty
            : string.Empty;

        /// <summary>Clicks bare map near the middle of the plan, and answers the one mark it placed.</summary>
        public RaidMark ClickMap(MouseButton button)
        {
            var view = window.GetVisualDescendants().OfType<MapSceneRendererView>().Single();
            var plan = view.FindControl<Border>("PlanViewport")!;
            var renderer = Cockpit.Renderer!;
            Point? spot = null;
            for (var x = plan.Bounds.Width * 0.35; x < plan.Bounds.Width * 0.65 && spot is null; x += 17)
            {
                for (var y = plan.Bounds.Height * 0.35; y < plan.Bounds.Height * 0.65; y += 13)
                {
                    if (!renderer.TryHitObjectAt(x, y, out _) && renderer.TryScenePointAt(x, y, out _))
                    {
                        spot = new Point(x, y);
                        break;
                    }
                }
            }

            Assert.NotNull(spot);
            var before = marks.Marks.Select(mark => mark.Id).ToHashSet();
            var at = plan.TranslatePoint(spot.Value, window)!.Value;
            window.MouseDown(at, button);
            window.MouseUp(at, button);
            Pump(() => marks.Marks.Any(mark => !before.Contains(mark.Id)));
            Pump(() => false, 5);
            output.WriteLine($"{button} click at {spot}: map says '{Status}'");
            return Assert.Single(marks.Marks, mark => !before.Contains(mark.Id));
        }

        /// <summary>
        /// The drawn ping's disc: its width in window pixels, whether its layer comes after the
        /// heat, loot and label layers on the camera surface, and whether no other mark sorts above it.
        /// </summary>
        public (double Diameter, bool AboveHeat, bool OnTop) MeasurePing()
        {
            var dot = window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>()
                .Single(ellipse => ellipse.Classes.Contains("v2-map-ping-dot") && ellipse.IsEffectivelyVisible);
            var left = dot.TranslatePoint(new Point(0, 0), window)!.Value;
            var right = dot.TranslatePoint(new Point(dot.Bounds.Width, 0), window)!.Value;
            var diameter = Math.Sqrt(Math.Pow(right.X - left.X, 2) + Math.Pow(right.Y - left.Y, 2));

            var surface = window.GetVisualDescendants().OfType<Canvas>().Single(canvas => canvas.Name == "CameraSurface");
            var layers = surface.Children.ToList();
            var pingLayer = layers.Single(layer => dot.GetVisualAncestors().Contains(layer));
            var heat = layers.OfType<Image>().Single();
            // The order is the markup's, whether or not this offline catalog has any heat to draw
            // (the Customs render with the seed catalog is where it was looked at with heat on).
            var loot = layers.OfType<ItemsControl>().Where(layer => layer.GetVisualDescendants().OfType<Control>().Any(item => item.Classes.Contains("v2-map-loot")));
            var labels = layers.Where(layer => !layer.IsHitTestVisible && layer is ItemsControl);
            var below = new Control[] { heat }.Concat(loot).Concat(labels);
            var aboveHeat = below.All(layer => layers.IndexOf(layer) < layers.IndexOf(pingLayer));

            var container = dot.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(presenter => presenter.GetVisualParent() is Canvas);
            var siblings = ((Canvas)container.GetVisualParent()!).Children;
            var onTop = siblings.All(other => ReferenceEquals(other, container) || other.ZIndex < container.ZIndex);
            output.WriteLine($"ping disc {diameter:0.0} px, layer {layers.IndexOf(pingLayer)} of {layers.Count}, heat {layers.IndexOf(heat)}, z {container.ZIndex}");
            return (diameter, aboveHeat, onTop);
        }

        /// <summary>Whether the map draws this mark, on the plan, now.</summary>
        public bool IsDrawn(RaidMark mark)
        {
            Pump(() => Cockpit.Renderer!.SpatialObjects.Any(item => item.ObjectId?.Value == $"mark:{mark.Id}"));
            return Cockpit.Renderer!.SpatialObjects.Any(item => item.ObjectId?.Value == $"mark:{mark.Id}" && item.IsShownOnPlan);
        }

        private Control? Find(string automationId) => window.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == automationId);
    }

    private sealed class FixedSettings(string address, string name) : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GroupSharingSettings(true, address, name, Key, false, false));

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
