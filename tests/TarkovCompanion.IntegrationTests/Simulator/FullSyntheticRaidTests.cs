using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.IntegrationTests.Simulator;

public sealed class FullSyntheticRaidTests
{
    [Fact]
    public async Task ReplaysMapPositionItemExtractsContainerAndRaidEnd()
    {
        var fixturePath = Path.Combine(
            FindRepositoryRoot(),
            "fixtures",
            "simulator",
            "full-raid.json");

        var report = await DemoRaidReplay.RunAsync(fixturePath, CancellationToken.None);

        Assert.True(report.Complete);
        Assert.False(report.UsesLiveDetection);
        Assert.Equal("synthetic-harbor", report.MapId);
        Assert.Equal("synthetic-circuit-board", Assert.Single(report.ScannedItemIds));
        Assert.Equal(["harbor-gate", "rail-yard"], report.ActiveExtractIds);
        Assert.Equal(["synthetic-bolts", "synthetic-wire", "synthetic-medkit"], report.ContainerItemIds);
        Assert.Equal("extracted", report.Outcome);
        Assert.Equal(90, report.LastPosition.HeadingDegrees, 3);
        Assert.Equal(
            ["map", "position", "item", "extracts", "container", "raid-end"],
            report.EventTypes);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
