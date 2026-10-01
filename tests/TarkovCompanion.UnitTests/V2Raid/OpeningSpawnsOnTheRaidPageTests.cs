using Avalonia.Automation;
using Avalonia.Controls;
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
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.UnitTests.V2MapRenderer;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [#985] The whole app as composed, the real main window on the Raid page: a PMC raid on Customs
/// shows its possible PMC spawns as soon as it starts, with no screenshot and nothing pressed.
/// </summary>
/// <remarks>
/// The catalog is a synthetic Customs row (five PMC areas, one of them three points, a shared
/// one, and a scav area), written into the composed app's own database as a sync would.
/// </remarks>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class OpeningSpawnsOnTheRaidPageTests
{
    private const string CustomsRow = """
        {"id":"test-customs","name":"Customs","normalizedName":"customs","nameId":"bigmap","spawns":[
          {"position":{"x":100,"y":0,"z":-100},"zoneName":"ZoneGasStation","sides":["pmc"],"categories":["player"]},
          {"position":{"x":104,"y":0,"z":-96},"zoneName":"ZoneGasStation","sides":["pmc"],"categories":["player"]},
          {"position":{"x":97,"y":0,"z":-104},"zoneName":"ZoneGasStation","sides":["pmc"],"categories":["player"]},
          {"position":{"x":-200,"y":0,"z":-150},"zoneName":"ZoneCrossroads","sides":["pmc"],"categories":["player"]},
          {"position":{"x":300,"y":0,"z":50},"zoneName":"ZoneDorms","sides":["pmc"],"categories":["player"]},
          {"position":{"x":500,"y":0,"z":-50},"zoneName":"ZoneOldStation","sides":["pmc"],"categories":["player"]},
          {"position":{"x":20,"y":0,"z":20},"zoneName":"ZoneBrige","sides":["all"],"categories":["player"]},
          {"position":{"x":120,"y":0,"z":-30},"zoneName":"ZoneScavBase","sides":["scav"],"categories":["player"]}
        ]}
        """;

    [Fact]
    public async Task A_pmc_raid_shows_possible_pmc_spawns_from_its_start_then_lines_after_a_screenshot_and_a_scav_raid_none()
    {
        await RunAsync((cockpit, store, window) =>
        {
            var now = DateTimeOffset.UtcNow;

            // The raid has started; no screenshot. Every PMC area, labelled possible, and the map says why.
            Enter(store, side: "PMC", startedAgo: TimeSpan.FromSeconds(20), trail: []);
            const string BeforeScreenshot = "Possible PMC spawns · first 5 min · take a screenshot to see which are near you";
            // Waited for as a whole: a rebuild already under way when the raid landed can draw once more from the moment before.
            Pump(() => Objects(cockpit, RaidCockpitViewModel.NearbySpawnsLayerId).Length == 5 && Status(window) == BeforeScreenshot);
            var markers = Objects(cockpit, RaidCockpitViewModel.NearbySpawnsLayerId);
            Assert.Equal(5, markers.Length);
            Assert.All(markers, marker =>
            {
                Assert.Equal("possible PMC spawn", marker.Label);
                Assert.Equal(MapSceneTruthKind.PotentialSpawn, marker.Truth);
            });
            Assert.Empty(Objects(cockpit, RaidCockpitViewModel.SpawnLinesLayerId));
            Assert.True(
                Status(window) == BeforeScreenshot,
                $"The map says '{Status(window)}' with the raid's side '{store.Current.Raid.Side}' and the cockpit's '{cockpit.OpeningSpawnStatus}'.");

            // The first screenshot: the two areas within 150 m, a line and a distance to each.
            ScreenshotPosition shot = new(now, new(90, 0, -40), default, 90, null, null, "first.png");
            Enter(store, side: "PMC", startedAgo: TimeSpan.FromSeconds(40), trail: [shot]);
            Pump(() => Objects(cockpit, RaidCockpitViewModel.SpawnLinesLayerId).Length > 0 && Status(window).Contains("within", StringComparison.Ordinal));
            Assert.Equal(2, Objects(cockpit, RaidCockpitViewModel.NearbySpawnsLayerId).Length);
            var lines = Objects(cockpit, RaidCockpitViewModel.SpawnLinesLayerId).Where(item => item.Kind == MapSceneObjectKind.Route).ToArray();
            Assert.Equal(2, lines.Length);
            Assert.All(lines, line => Assert.StartsWith("possible PMC spawn · ", line.Label, StringComparison.Ordinal));
            Assert.Equal("Possible PMC spawns within 150 m of your first screenshot · first 5 min", Status(window));

            // Five minutes on: gone, and so is the line.
            Enter(store, side: "PMC", startedAgo: TimeSpan.FromMinutes(6), trail: [shot]);
            Pump(() => Objects(cockpit, RaidCockpitViewModel.NearbySpawnsLayerId).Length == 0 && Status(window).Length == 0);
            Assert.Empty(Objects(cockpit, RaidCockpitViewModel.NearbySpawnsLayerId));
            Assert.Empty(Objects(cockpit, RaidCockpitViewModel.SpawnLinesLayerId));
            Assert.Equal(string.Empty, Status(window));

            // Nothing named the side: shown as PMC, and the line says so.
            Enter(store, side: null, startedAgo: TimeSpan.FromSeconds(20), trail: []);
            Pump(() => Objects(cockpit, RaidCockpitViewModel.NearbySpawnsLayerId).Length == 5 && Status(window).Contains("side unknown", StringComparison.Ordinal));
            Assert.Equal(5, Objects(cockpit, RaidCockpitViewModel.NearbySpawnsLayerId).Length);
            Assert.EndsWith("· side unknown, shown as PMC", Status(window), StringComparison.Ordinal);

            // A scav raid stays clean.
            Enter(store, side: "scav", startedAgo: TimeSpan.FromSeconds(20), trail: []);
            Pump(() => Objects(cockpit, RaidCockpitViewModel.NearbySpawnsLayerId).Length == 0 && Status(window).Length == 0);
            Assert.Empty(Objects(cockpit, RaidCockpitViewModel.NearbySpawnsLayerId));
            Assert.Equal(string.Empty, Status(window));
        });
    }

    private static MapSceneObject[] Objects(RaidCockpitViewModel cockpit, MapSceneLayerId layer) =>
        cockpit.Renderer?.Scene.Objects.Where(item => item.LayerId == layer).ToArray() ?? [];

    /// <summary>What the map's opening-spawns line says on screen; empty when it is not showing.</summary>
    private static string Status(Window window)
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => AutomationProperties.GetAutomationId(border) == "v2-raid-opening-spawns") is { IsEffectivelyVisible: true } line
            ? AutomationProperties.GetName(line) ?? string.Empty
            : string.Empty;
    }

    /// <summary>A new raid on Customs each time, as the log would start one.</summary>
    private static void Enter(IRuntimeStateStore store, string? side, TimeSpan startedAgo, ScreenshotPosition[] trail)
    {
        var now = DateTimeOffset.UtcNow;
        store.Update(snapshot => snapshot with
        {
            Raid = snapshot.Raid with
            {
                RaidId = Guid.NewGuid(),
                State = RaidLifecycleState.InRaid,
                MapId = "customs",
                Side = side,
                StartedUtc = now - startedAgo,
                UpdatedUtc = now,
                LastKnownPosition = trail.Length > 0 ? trail[^1] : null,
                PositionTrail = trail,
            },
        });
    }

    private static void Pump(Func<bool> done, int turns = 300)
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
    }

    private static async Task RunAsync(Action<RaidCockpitViewModel, IRuntimeStateStore, Window> test)
    {
        using var session = HeadlessSessions.StartNew(typeof(V2SettingsIndexLandingTests.SetupViewApp));
        await session.Dispatch(
            async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), $"tarkov-opening-spawns-{Guid.NewGuid():N}");
                try
                {
                    await using var services = AppComposition.Build(
                        new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                        new(DataRoot: root, Offline: true));
                    var legacy = services.GetRequiredService<MainWindowViewModel>();
                    var shell = services.GetRequiredService<V2ShellViewModel>();
                    legacy.PreviewShell = shell;
                    await legacy.InitializeAsync();
                    await Task.Run(() => SeedCustomsAsync(services));
                    shell.Router.NavigateToAddress("raid");
                    var window = new MainWindow { DataContext = legacy, Width = 1920, Height = 1080 };
                    window.Show();
                    try
                    {
                        var cockpit = (RaidCockpitViewModel)shell.RaidCockpit!;
                        Pump(() => cockpit.MapPicker.Count > 0);
                        // Another map first: the one open at startup was read before the row was written.
                        cockpit.MapPicker.First(item => item.MapId != "customs").SelectCommand.Execute(null);
                        Pump(() => cockpit.Renderer?.Scene.LocationId is { } open && open != "customs");
                        cockpit.MapPicker.First(item => item.MapId == "customs").SelectCommand.Execute(null);
                        Pump(() => cockpit.Renderer?.Scene.LocationId == "customs" && legacy.Map.MapFeatures.Count > 0);
                        test(cockpit, services.GetRequiredService<IRuntimeStateStore>(), window);
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

    private static async Task SeedCustomsAsync(IServiceProvider services)
    {
        await using (var connection = await services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO maps (id, name, normalized_name, source_json) VALUES ('test-customs', 'Customs', 'customs', $json);";
            command.Parameters.AddWithValue("$json", CustomsRow);
            await command.ExecuteNonQueryAsync();
        }

        services.GetRequiredService<IMapFeatureCatalog>().Invalidate();
    }
}
