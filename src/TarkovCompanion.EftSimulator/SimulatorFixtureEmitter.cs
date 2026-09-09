using System.Text.Json;

namespace TarkovCompanion.EftSimulator;

public sealed record SimulatorFixtureState(
    int SchemaVersion,
    string Scenario,
    bool DeveloperMode,
    string Source,
    bool IsGame,
    bool SendsInput,
    string? LogPath,
    string? ScreenshotPath);

public static class SimulatorFixtureEmitter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<SimulatorFixtureState> EmitAsync(
        SimulatorCommandLine options,
        SimulatorScenario scenario,
        CancellationToken cancellationToken)
    {
        if (!options.DeveloperMode)
        {
            throw new InvalidOperationException("Simulator fixture output requires explicit --developer-mode.");
        }

        string? logPath = null;
        if (!string.IsNullOrWhiteSpace(options.LogRoot))
        {
            var logRoot = Path.GetFullPath(options.LogRoot);
            Directory.CreateDirectory(logRoot);
            logPath = Path.Combine(logRoot, $"simulator-{scenario.Id}.log");
            var lines = new[]
            {
                "2026-01-15T20:00:00.0000000+00:00 simulator source=fixture developer-mode=true",
                $"2026-01-15T20:00:01.0000000+00:00 {scenario.LogEvent}",
            };
            await File.WriteAllLinesAsync(logPath, lines, cancellationToken).ConfigureAwait(false);
        }

        string? screenshotPath = null;
        if (!string.IsNullOrWhiteSpace(options.ScreenshotRoot))
        {
            var screenshotRoot = Path.GetFullPath(options.ScreenshotRoot);
            Directory.CreateDirectory(screenshotRoot);
            screenshotPath = Path.Combine(screenshotRoot, scenario.ScreenshotFilename);
            await using var marker = new FileStream(
                screenshotPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                1,
                FileOptions.Asynchronous);
            await marker.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var state = new SimulatorFixtureState(
            1,
            scenario.Id,
            true,
            "simulator-fixture",
            false,
            false,
            logPath,
            screenshotPath);

        if (!string.IsNullOrWhiteSpace(options.StateOutput))
        {
            var statePath = Path.GetFullPath(options.StateOutput);
            var stateDirectory = Path.GetDirectoryName(statePath)
                ?? throw new InvalidOperationException("The simulator state output has no parent directory.");
            Directory.CreateDirectory(stateDirectory);
            await using var output = File.Create(statePath);
            await JsonSerializer.SerializeAsync(output, state, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        return state;
    }

    public static async Task WriteScenarioListAsync(Stream destination, CancellationToken cancellationToken) =>
        await JsonSerializer.SerializeAsync(
                destination,
                SimulatorScenarioCatalog.All.Select(scenario => scenario.Id),
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);
}
