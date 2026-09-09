using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.EftSimulator;

namespace TarkovCompanion.WindowsSmokeTests.Simulator;

public sealed class DiagnosticSafetyTests
{
    [Fact]
    public void SimulatorStatesDeclareNoGameOrInputControl()
    {
        var state = new SimulatorFixtureState(
            1,
            SimulatorScenarioCatalog.All[0].Id,
            true,
            "simulator-fixture",
            false,
            false,
            null,
            null);

        Assert.False(state.IsGame);
        Assert.False(state.SendsInput);
    }

    [Fact]
    public void InvalidScenarioPayloadCannotEscapeIdentifierGrammar()
    {
        var token = new string('z', 32);
        var response = new DiagnosticCommandProcessor(token).Process(
            new("scene-3", DiagnosticCommandKind.Scenario, token, "../../command.exe"),
            DateTimeOffset.UtcNow);

        Assert.False(response.Accepted);
        Assert.Equal("invalid-scenario", response.Error);
    }
}
