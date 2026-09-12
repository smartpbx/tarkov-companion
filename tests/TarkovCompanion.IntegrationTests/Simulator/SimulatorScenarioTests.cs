using TarkovCompanion.Application.Services;
using TarkovCompanion.EftSimulator;

namespace TarkovCompanion.IntegrationTests.Simulator;

public sealed class SimulatorScenarioTests
{
    private static readonly string[] ExpectedScenarioIds =
    [
        "RaidStart_Customs",
        "Inspect_GraphicsCard",
        "Inspect_AmmoPack",
        "Inspect_Key",
        "Inspect_Consumable",
        "ExtractList_Customs",
        "Container_Mixed",
        "Flea_VisibleListings",
        "PositionUpdate",
        "RaidEnd",
    ];

    [Fact]
    public void CatalogContainsCanonicalScenariosInDeterministicOrder()
    {
        Assert.Equal(ExpectedScenarioIds, SimulatorScenarioCatalog.All.Select(scenario => scenario.Id));
        Assert.Equal(ExpectedScenarioIds.Length, SimulatorScenarioCatalog.All.Select(scenario => scenario.Id).Distinct().Count());
    }

    [Fact]
    public void ScenarioSelectionIsCaseSensitive()
    {
        Assert.True(SimulatorScenarioCatalog.TryGet("PositionUpdate", out _));
        Assert.False(SimulatorScenarioCatalog.TryGet("positionupdate", out _));
        Assert.Throws<ArgumentException>(() => SimulatorScenarioCatalog.Get("Unknown"));
    }

    [Fact]
    public async Task FixtureEmitterWritesFakeLogAndParseableScreenshotFilename()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var logRoot = Path.Combine(root, "logs");
            var screenshotRoot = Path.Combine(root, "screenshots");
            var stateOutput = Path.Combine(root, "state.json");
            var options = new SimulatorCommandLine(
                true,
                false,
                true,
                "PositionUpdate",
                logRoot,
                screenshotRoot,
                stateOutput);

            var state = await SimulatorFixtureEmitter.EmitAsync(
                options,
                SimulatorScenarioCatalog.Get(options.Scenario),
                CancellationToken.None);

            Assert.NotNull(state.LogPath);
            Assert.NotNull(state.ScreenshotPath);
            Assert.True(File.Exists(state.LogPath));
            Assert.True(File.Exists(state.ScreenshotPath));
            Assert.True(File.Exists(stateOutput));
            Assert.False(state.IsGame);
            Assert.False(state.SendsInput);
            Assert.True(new ScreenshotFilenameParser().TryParse(state.ScreenshotPath, TimeSpan.Zero, out _));
        }
        finally
        {
            TemporaryDirectory.Remove(root);
        }
    }

    [Fact]
    public async Task FixtureEmitterRejectsProductionMode()
    {
        var options = new SimulatorCommandLine(false, false, true, "RaidEnd", null, null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SimulatorFixtureEmitter.EmitAsync(
            options,
            SimulatorScenarioCatalog.Get(options.Scenario),
            CancellationToken.None));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-simulator-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
