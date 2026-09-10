using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Runtime;
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
    public async Task InvalidScenarioPayloadCannotEscapeIdentifierGrammar()
    {
        var token = new string('z', 32);
        var response = await new DiagnosticCommandProcessor(token, new UnavailableScanUseCase())
            .ProcessAsync(
                new("scene-3", DiagnosticCommandKind.Scenario, token, "../../command.exe"),
                DateTimeOffset.UtcNow,
                CancellationToken.None);

        Assert.False(response.Accepted);
        Assert.Equal("invalid-scenario", response.Error);
    }

    private sealed class UnavailableScanUseCase : IRuntimeScanUseCase
    {
        public Task<ScanExecutionResult> ExecuteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ScanExecutionResult.Unavailable("Unavailable in safety test.", DateTimeOffset.UtcNow));
    }
}
