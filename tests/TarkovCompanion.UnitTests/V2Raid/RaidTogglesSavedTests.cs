using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.Views.V2.Primitives;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Workspaces;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [#902 P3/P4] The Raid page's toggles are saved, read again after Backup &amp; reset without a
/// restart, and a lit selection chip stays lit when it is pressed again.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class RaidTogglesSavedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"raid-toggles-{Guid.NewGuid():N}");

    [Fact]
    public async Task Raid_toggles_survive_a_restart_and_reset_everything_shows_the_defaults_without_one()
    {
        await using (var services = Build())
        {
            var cockpit = services.GetRequiredService<RaidCockpitViewModel>();
            // The defaults, on a first start.
            Assert.False(cockpit.ShowCompletedObjectives);
            Assert.True(cockpit.ShowSquadObjectives);
            Assert.False(cockpit.RouteSquadStops);
            Assert.True(cockpit.AutoSelectsFloor);
            Assert.True(cockpit.FollowsPlayer);

            cockpit.ToggleShowCompletedObjectivesCommand.Execute(null);
            cockpit.ToggleSquadObjectivesCommand.Execute(null);
            cockpit.ToggleRouteSquadStopsCommand.Execute(null);
            cockpit.NewMarksSquadCommand.Execute(null);
            cockpit.ToggleAutoFloorCommand.Execute(null);
            cockpit.ToggleFollowCommand.Execute(null);
            cockpit.IncreaseFollowZoomCommand.Execute(null);
            cockpit.ResizeContextPanel(520);
            cockpit.Cards.Objectives.IsExpanded = false;
        }

        await using (var services = Build())
        {
            var cockpit = services.GetRequiredService<RaidCockpitViewModel>();
            Assert.True(cockpit.ShowCompletedObjectives);
            Assert.False(cockpit.ShowSquadObjectives);
            Assert.True(cockpit.RouteSquadStops);
            Assert.Equal(RaidMarkScope.Squad, cockpit.NewMarkScope);
            Assert.False(cockpit.AutoSelectsFloor);
            Assert.False(cockpit.FollowsPlayer);
            Assert.Equal(520, cockpit.ContextPanelWidth);
            Assert.False(cockpit.Cards.Objectives.IsExpanded);
            var zoomed = cockpit.FollowZoomLabel;

            var changed = new List<string?>();
            cockpit.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            // What Setup's Reset everything does to this store: empty it in one write.
            services.GetRequiredService<IWorkspaceLayoutStore>().Replace(new Dictionary<string, string>());

            Assert.False(cockpit.ShowCompletedObjectives);
            Assert.True(cockpit.ShowSquadObjectives);
            Assert.False(cockpit.RouteSquadStops);
            Assert.NotEqual(RaidMarkScope.Squad, cockpit.NewMarkScope);
            Assert.True(cockpit.AutoSelectsFloor);
            Assert.True(cockpit.FollowsPlayer);
            Assert.Equal(RaidCockpitViewModel.DefaultContextPanelWidth, cockpit.ContextPanelWidth);
            Assert.True(cockpit.Cards.Objectives.IsExpanded);
            Assert.NotEqual(zoomed, cockpit.FollowZoomLabel);
            // And the page was told, so the bound controls redraw.
            Assert.Contains(nameof(RaidCockpitViewModel.ShowCompletedObjectives), changed);
            Assert.Contains(nameof(RaidCockpitViewModel.ContextPanelWidth), changed);
            Assert.Contains(nameof(RaidCockpitViewModel.AutoSelectsFloor), changed);
            // Nothing was written back: a missing key is the default.
            Assert.Empty(services.GetRequiredService<IWorkspaceLayoutStore>().Entries);
        }
    }

    [Fact]
    public async Task Drawing_tools_in_View_turn_the_pencil_off_and_on_at_once()
    {
        await using var services = Build();
        var cockpit = services.GetRequiredService<RaidCockpitViewModel>();
        var was = cockpit.IsDrawAvailable;
        Assert.Equal(was, cockpit.IsDrawingToolsOn);

        cockpit.ToggleDrawingToolsCommand.Execute(null);

        Assert.Equal(!was, cockpit.IsDrawAvailable);
        Assert.Equal(!was, cockpit.IsDrawingToolsOn);
        Assert.False(cockpit.DrawingToolsWaitsForRestart);
        cockpit.ToggleDrawingToolsCommand.Execute(null);
        Assert.Equal(was, cockpit.IsDrawAvailable);
    }

    [Fact]
    public void Schema_2_resets_the_four_layers_set_through_controls_that_could_not_undo_them_once()
    {
        var layout = new JsonFileWorkspaceLayoutStore(Path.Combine(_root, "layout.json"));
        layout.Set(WorkspaceLayoutKeys.RaidLayerVisibility, "objective-route:0,traffic-routes:0,visited:1,spawns:1,extracts:0,labels:0");

        var setting = new MapLayerVisibilitySetting(layout);

        Assert.Null(setting.Get(new("objective-route")));
        Assert.Null(setting.Get(new("traffic-routes")));
        Assert.Null(setting.Get(new("visited")));
        Assert.Null(setting.Get(new("spawns")));
        Assert.False(setting.Get(new("extracts")));
        Assert.False(setting.Get(new("labels")));
        Assert.Equal("2", layout.Get(WorkspaceLayoutKeys.RaidLayerSchema));

        // Once: a choice made afterwards survives the next start.
        setting.Set(new("objective-route"), false);
        setting.Set(new("spawns"), true);
        var restarted = new MapLayerVisibilitySetting(layout);
        Assert.False(restarted.Get(new("objective-route")));
        Assert.True(restarted.Get(new("spawns")));
    }

    [Fact]
    public void A_first_choice_on_an_empty_store_is_not_taken_for_an_old_one()
    {
        var layout = new JsonFileWorkspaceLayoutStore(Path.Combine(_root, "layout.json"));
        var setting = new MapLayerVisibilitySetting(layout);
        Assert.Null(layout.Get(WorkspaceLayoutKeys.RaidLayerSchema));

        setting.Set(RaidCockpitViewModel.MyTrailLayerId, true);

        Assert.True(new MapLayerVisibilitySetting(layout).Get(RaidCockpitViewModel.MyTrailLayerId));
    }

    [Fact]
    public async Task My_trail_off_change_map_on_shows_only_the_new_maps_trails_and_on_follows_a_map_change()
    {
        var customsRaid = Guid.NewGuid();
        var woodsRaid = Guid.NewGuid();
        var history = TrailHistory.For(new Dictionary<string, Guid>
        {
            ["customs"] = customsRaid,
            ["woods"] = woodsRaid,
            ["shoreline"] = Guid.NewGuid(),
        });
        await using var services = Build();
        var map = ActivatorUtilities.CreateInstance<MapViewModel>(services, history);
        var customs = new MapLocation("customs", null, "Customs", null, null, []);
        var woods = new MapLocation("woods", null, "Woods", null, null, []);
        var shoreline = new MapLocation("shoreline", null, "Shoreline", null, null, []);

        await map.SelectLocationAsync(customs);
        await map.SetShowsVisitedAsync(true);
        Assert.Equal([customsRaid], map.VisitedRaids.Select(trail => trail.RaidId));

        await map.SetShowsVisitedAsync(false);
        Assert.Empty(map.VisitedRaids);
        await map.SelectLocationAsync(woods);
        await map.SetShowsVisitedAsync(true);
        Assert.Equal([woodsRaid], map.VisitedRaids.Select(trail => trail.RaidId));

        // While on, a map change reads the new map's and drops the old.
        await map.SelectLocationAsync(shoreline);
        Assert.DoesNotContain(map.VisitedRaids, trail => trail.RaidId == woodsRaid);
        Assert.Single(map.VisitedRaids);
    }

    [Fact]
    public async Task A_lit_selection_chip_pressed_again_stays_lit_and_still_runs_its_command()
    {
        using var session = HeadlessSessions.StartNew(typeof(ChipApp));
        await session.Dispatch(
            () =>
            {
                var ran = 0;
                var command = new DelegateCommand(() => ran++);
                var chip = new SelectionToggleButton { Content = "Navigate", IsChecked = true, Command = command, Width = 120, Height = 40 };
                var plain = new ToggleButton { Content = "Navigate", IsChecked = true, Command = command, Width = 120, Height = 40 };
                var panel = new StackPanel();
                panel.Children.Add(chip);
                panel.Children.Add(plain);
                var window = new Window { Width = 300, Height = 200, Content = panel };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Click(window, chip);
                Click(window, plain);

                Assert.True(chip.IsChecked);
                // The fault it replaces: a plain toggle darkens itself though nothing changed.
                Assert.False(plain.IsChecked);
                Assert.Equal(2, ran);
                window.Close();
            },
            CancellationToken.None);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    private ServiceProvider Build() => AppComposition.Build(
        new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
        new(DataRoot: _root, Offline: true));

    private static void Click(Window window, Control control)
    {
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    public sealed class ChipApp : Avalonia.Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<ChipApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    /// <summary>A raid history that knows one past raid per map and nothing else.</summary>
    public class TrailHistory : DispatchProxy
    {
        private IReadOnlyDictionary<string, Guid> _raids = new Dictionary<string, Guid>();

        public static IRaidHistoryService For(IReadOnlyDictionary<string, Guid> raids)
        {
            var proxy = Create<IRaidHistoryService, TrailHistory>();
            ((TrailHistory)(object)proxy)._raids = raids;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IRaidHistoryService.ListTrailsForMapAsync))
            {
                IReadOnlyList<RaidTrail> trails = _raids.TryGetValue((string)args![0]!, out var id)
                    ? [new RaidTrail(id, null, [])]
                    : [];
                return Task.FromResult(trails);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
