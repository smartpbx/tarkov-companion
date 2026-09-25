using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.Settings;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>[#914] Lines from nearby PMC spawn areas to the player, with a radius per map.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class SpawnLinesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"spawn-lines-{Guid.NewGuid():N}");

    [Fact]
    public void Five_points_in_one_yard_draw_one_line_and_one_label()
    {
        MapFeature[] features =
        [
            Spawn("Yard", 100, 0), Spawn("Yard", 104, 3), Spawn("Yard", 98, -4), Spawn("Yard", 110, 6), Spawn("Yard", 95, 8),
            Spawn("Gate", 0, 130),
        ];
        var areas = SpawnProximity.Near(features, At(0, 0), At(10, 0), MapFeatureFaction.Pmc);

        var scene = RaidCockpitViewModel.BuildSpawnLineScene(areas, Plan, At(10, 0), 1, Now);

        var lines = scene.Objects.Where(item => item.Kind == MapSceneObjectKind.Route).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal(2, scene.Objects.Count(item => item.Kind == MapSceneObjectKind.Label));
        Assert.All(scene.Objects, item =>
        {
            Assert.Equal(RaidCockpitViewModel.SpawnLinesLayerId, item.LayerId);
            Assert.Equal(MapSceneTruthKind.PotentialSpawn, item.Truth);
        });
        // Each line runs from its area to the player, and says "possible", with the distance to the player.
        Assert.All(lines, line => Assert.Equal(new MapScenePoint(10, 0), line.Geometry.Points[^1]));
        Assert.Contains(lines, line => line.Label.StartsWith("possible PMC spawn · ", StringComparison.Ordinal) &&
            line.Label.EndsWith(" m", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.True(scene.Styles[line.Id].Dashed));
    }

    [Fact]
    public void The_radius_keeps_only_areas_that_close_to_the_start()
    {
        MapFeature[] features = [Spawn("A", 45, 0), Spawn("B", 0, 90), Spawn("C", -140, 0), Spawn("D", 0, -250)];
        var areas = SpawnProximity.Near(features, At(0, 0), At(0, 0), MapFeatureFaction.Pmc);

        Assert.Equal(["A"], SpawnLines.Within(areas, 50).Select(area => area.Name));
        Assert.Equal(["A", "B"], SpawnLines.Within(areas, 100).Select(area => area.Name));
        Assert.Equal(["A", "B", "C"], SpawnLines.Within(areas, 150).Select(area => area.Name));
        Assert.Equal(4, SpawnLines.Within(areas, 300).Count);
        Assert.Equal(2, RaidCockpitViewModel.BuildSpawnLineScene(SpawnLines.Within(areas, 100), Plan, At(0, 0), 1, Now)
            .Objects.Count(item => item.Kind == MapSceneObjectKind.Route));
    }

    [Theory]
    [InlineData(null, 150)]
    [InlineData("", 150)]
    [InlineData("75", 150)]
    [InlineData("abc", 150)]
    [InlineData("50", 50)]
    [InlineData("300", 300)]
    public void A_stored_radius_is_one_of_the_choices_or_the_default(string? stored, int expected) =>
        Assert.Equal(expected, SpawnLines.ParseRadius(stored));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(180, 1)]
    [InlineData(240, 0.5)]
    [InlineData(299, 1.0 / 120)]
    [InlineData(300, 0)]
    [InlineData(900, 0)]
    public void Lines_hold_for_three_minutes_then_fade_to_nothing_at_five(int seconds, double expected) =>
        Assert.Equal(expected, SpawnLines.Strength(TimeSpan.FromSeconds(seconds)), 6);

    [Fact]
    public void Only_a_pmc_raid_inside_its_first_five_minutes_draws_lines()
    {
        var clock = new FixedClock(Now);
        var policy = new EarlyRaidSpawnPolicy(clock);
        NearbySpawn[] areas = [new("Yard", MapFeatureFaction.Pmc, At(80, 0), 80, 70, "E")];

        double StrengthFor(MapFeatureFaction side, TimeSpan since)
        {
            var selection = policy.Select(areas, side, Now - since);
            return RaidCockpitViewModel.SpawnLineStrength(selection.Phase, Now - since, Now);
        }

        Assert.Equal(1, StrengthFor(MapFeatureFaction.Pmc, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, StrengthFor(MapFeatureFaction.Pmc, TimeSpan.FromMinutes(5)));
        Assert.Equal(0, StrengthFor(MapFeatureFaction.Pmc, TimeSpan.FromMinutes(12)));
        Assert.Equal(0, StrengthFor(MapFeatureFaction.Scav, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, StrengthFor(MapFeatureFaction.Unknown, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, RaidCockpitViewModel.SpawnLineStrength(EarlyRaidSpawnPhase.Active, null, Now));

        // Nothing drawn at strength 0, nor without a player position to draw to.
        Assert.Empty(RaidCockpitViewModel.BuildSpawnLineScene(areas, Plan, At(0, 0), 0, Now).Objects);
        Assert.Empty(RaidCockpitViewModel.BuildSpawnLineScene(areas, Plan, null, 1, Now).Objects);
    }

    [Fact]
    public void A_fading_line_fades_its_label_too()
    {
        NearbySpawn[] areas = [new("Yard", MapFeatureFaction.Pmc, At(80, 0), 80, 70, "E")];

        var scene = RaidCockpitViewModel.BuildSpawnLineScene(areas, Plan, At(0, 0), 0.5, Now);

        var line = scene.Objects.Single(item => item.Kind == MapSceneObjectKind.Route);
        var label = scene.Objects.Single(item => item.Kind == MapSceneObjectKind.Label);
        Assert.Equal(0.45, scene.Styles[line.Id].Opacity!.Value, 6);
        Assert.Equal("#80E05C5C", scene.Styles[label.Id].Color);
    }

    [Fact]
    public async Task The_radius_is_remembered_per_map_across_a_restart_and_reset_to_the_default()
    {
        await using (var services = Build())
        {
            var cockpit = services.GetRequiredService<RaidCockpitViewModel>();
            Assert.Equal(SpawnLines.DefaultRadiusMetres, cockpit.SpawnRadiusFor("customs"));
            cockpit.SetSpawnRadius("customs", 100);
            cockpit.SetSpawnRadius("woods", 300);
            cockpit.SetSpawnRadius("factory4_day", 75);
        }

        await using (var services = Build())
        {
            var cockpit = services.GetRequiredService<RaidCockpitViewModel>();
            Assert.Equal(100, cockpit.SpawnRadiusFor("customs"));
            Assert.Equal(300, cockpit.SpawnRadiusFor("woods"));
            Assert.Equal(SpawnLines.DefaultRadiusMetres, cockpit.SpawnRadiusFor("factory4_day"));
            // Setup names the key, so Backup & reset lists it.
            Assert.NotNull(SettingsRegistry.FindLayoutKey(WorkspaceLayoutKeys.RaidSpawnRadius("customs")));

            services.GetRequiredService<IWorkspaceLayoutStore>().Replace(new Dictionary<string, string>());
            Assert.Equal(SpawnLines.DefaultRadiusMetres, cockpit.SpawnRadiusFor("customs"));
        }
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

    private static MapScenePoint? Plan(WorldPosition position) => new(position.X, position.Z);

    private static WorldPosition At(double x, double z) => new(x, 0, z);

    private static MapFeature Spawn(string name, double x, double z) =>
        new(MapFeatureKind.Spawn, name, At(x, z), "pmc", "player");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
