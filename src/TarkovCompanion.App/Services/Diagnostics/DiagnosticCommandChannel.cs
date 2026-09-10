using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.Services.Diagnostics;

public enum DiagnosticCommandKind
{
    Scan,
    Scenario,
    State,
}

public sealed record DiagnosticCommand(
    string? Id,
    DiagnosticCommandKind Command,
    string? Token,
    string? Scenario = null,
    string? ScreenshotPath = null,
    string? LogPath = null,
    string? PositionFilename = null);

public sealed record DiagnosticObservation(
    string Code,
    string Source,
    string Detail,
    DateTimeOffset ObservedUtc);

public sealed record DiagnosticResponse(
    string Id,
    bool Accepted,
    string Event,
    string? Scenario,
    DateTimeOffset ProcessedUtc,
    string? Error = null,
    ScanExecutionResult? Scan = null,
    ApplicationRuntimeSnapshot? Runtime = null,
    IReadOnlyList<DiagnosticObservation>? Evidence = null);

public sealed class DiagnosticCommandProcessor(
    string requiredToken,
    IRuntimeScanUseCase scanUseCase,
    IRuntimeStateStore? stateStore = null,
    DiagnosticScenarioProcessor? scenarioProcessor = null)
{
    private const int MaximumIdentifierLength = 80;

    public async Task<DiagnosticResponse> ProcessAsync(
        DiagnosticCommand command,
        DateTimeOffset processedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!IsSafeIdentifier(command.Id))
        {
            return Reject("invalid", processedUtc, "invalid-id");
        }

        var commandId = command.Id!;
        if (!FixedTimeEquals(command.Token, requiredToken))
        {
            return Reject(commandId, processedUtc, "unauthorized");
        }

        if (command.Command == DiagnosticCommandKind.Scan)
        {
            try
            {
                var scan = await scanUseCase
                    .ExecuteAsync(new(command.ScreenshotPath), cancellationToken)
                    .ConfigureAwait(false);
                return new(
                    commandId,
                    true,
                    scan.Succeeded ? "scan-completed" : scan.IsAvailable ? "scan-no-result" : "scan-unavailable",
                    null,
                    processedUtc,
                    null,
                    scan,
                    stateStore?.Current,
                    scan.Outcome?.Evidence.Select(evidence => new DiagnosticObservation(
                        evidence.Code,
                        scan.Source,
                        evidence.Detail,
                        evidence.ObservedUtc)).ToArray());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new(commandId, true, "scan-failed", null, processedUtc, exception.Message);
            }
        }

        if (command.Command == DiagnosticCommandKind.State)
        {
            return stateStore is null
                ? Reject(commandId, processedUtc, "runtime-state-unavailable")
                : new(
                    commandId,
                    true,
                    "state-read",
                    null,
                    processedUtc,
                    Runtime: stateStore.Current,
                    Evidence:
                    [
                        new(
                            "runtime-state",
                            "application-runtime-state-store",
                            "Returned current persisted/cache, raid, position, extract, and scan state.",
                            processedUtc),
                    ]);
        }

        if (command.Command == DiagnosticCommandKind.Scenario && IsSafeIdentifier(command.Scenario))
        {
            try
            {
                var evidence = scenarioProcessor is null
                    ? []
                    : await scenarioProcessor.ProcessAsync(command, processedUtc, cancellationToken)
                        .ConfigureAwait(false);
                return new(
                    commandId,
                    true,
                    evidence.Count == 0 ? "scenario-selected" : "scenario-evidence-applied",
                    command.Scenario,
                    processedUtc,
                    Runtime: stateStore?.Current,
                    Evidence: evidence);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new(
                    commandId,
                    true,
                    "scenario-failed",
                    command.Scenario,
                    processedUtc,
                    exception.Message,
                    Runtime: stateStore?.Current);
            }
        }

        return command.Command switch
        {
            DiagnosticCommandKind.Scenario => Reject(commandId, processedUtc, "invalid-scenario"),
            _ => Reject(commandId, processedUtc, "unsupported-command"),
        };
    }

    private static DiagnosticResponse Reject(string id, DateTimeOffset processedUtc, string error) =>
        new(id, false, "rejected", null, processedUtc, error);

    private static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumIdentifierLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool FixedTimeEquals(string? supplied, string expected)
    {
        if (supplied is null || supplied.Length != expected.Length)
        {
            return false;
        }

        var difference = 0;
        for (var index = 0; index < supplied.Length; index++)
        {
            difference |= supplied[index] ^ expected[index];
        }

        return difference == 0;
    }
}

public sealed class DiagnosticScenarioProcessor(
    EftLogParser logParser,
    IScreenshotFilenameParser screenshotFilenameParser,
    RaidActivityCoordinator raidActivityCoordinator)
{
    private const long MaximumLogBytes = 1024 * 1024;

    public async Task<IReadOnlyList<DiagnosticObservation>> ProcessAsync(
        DiagnosticCommand command,
        DateTimeOffset processedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var evidence = new List<DiagnosticObservation>();
        var observationIndex = 0;
        if (!string.IsNullOrWhiteSpace(command.LogPath))
        {
            var logPath = ValidateLogPath(command.LogPath);
            var lines = await File.ReadAllLinesAsync(logPath, cancellationToken).ConfigureAwait(false);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var observedUtc = processedUtc.AddMilliseconds(observationIndex++);
                var parsed = logParser.ParseLine(lines[lineIndex], observedUtc);
                if (parsed is null)
                {
                    continue;
                }

                var snapshot = await raidActivityCoordinator.ApplyEvidenceAsync(parsed, cancellationToken)
                    .ConfigureAwait(false);
                evidence.Add(new(
                    "log-evidence",
                    $"eft-log-parser:{Path.GetFileName(logPath)}:{lineIndex + 1}",
                    $"Applied {parsed.SuggestedState?.ToString() ?? "state-unchanged"}; map={parsed.MapId ?? "unchanged"}; resulting-state={snapshot.State}.",
                    observedUtc));
            }
        }

        if (!string.IsNullOrWhiteSpace(command.PositionFilename))
        {
            if (!screenshotFilenameParser.TryParse(command.PositionFilename, TimeSpan.Zero, out var parsedPosition) ||
                parsedPosition is null)
            {
                throw new InvalidDataException("The supplied synthetic screenshot filename is not parseable position evidence.");
            }

            var observedUtc = processedUtc.AddMilliseconds(observationIndex);
            var position = parsedPosition with { Timestamp = observedUtc };
            await raidActivityCoordinator.ApplyPositionAsync(position, cancellationToken).ConfigureAwait(false);
            evidence.Add(new(
                "position-evidence",
                "screenshot-filename-parser:" + parsedPosition.Filename,
                $"Parsed world=({position.Position.X:F2},{position.Position.Y:F2},{position.Position.Z:F2}); heading={position.HeadingDegrees:F2}; filename-time={parsedPosition.Timestamp:O}.",
                observedUtc));
        }

        return evidence;
    }

    private static string ValidateLogPath(string value)
    {
        var path = Path.GetFullPath(value);
        if (!string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Diagnostic scenario evidence accepts .log files only.");
        }

        var file = new FileInfo(path);
        if (!file.Exists || file.Length > MaximumLogBytes)
        {
            throw new InvalidDataException("The diagnostic log is missing or exceeds the 1 MiB input limit.");
        }

        return path;
    }
}

public sealed class DiagnosticCommandChannel : IAsyncDisposable
{
    public const string TokenEnvironmentVariable = "TARKOV_COMPANION_DIAGNOSTIC_TOKEN";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

    private readonly string commandDirectory;
    private readonly string responseDirectory;
    private readonly DiagnosticCommandProcessor processor;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;

    private DiagnosticCommandChannel(
        string channelPath,
        string token,
        IRuntimeScanUseCase scanUseCase,
        IRuntimeStateStore? stateStore,
        DiagnosticScenarioProcessor? scenarioProcessor)
    {
        commandDirectory = Path.Combine(channelPath, "commands");
        responseDirectory = Path.Combine(channelPath, "responses");
        Directory.CreateDirectory(commandDirectory);
        Directory.CreateDirectory(responseDirectory);
        processor = new(token, scanUseCase, stateStore, scenarioProcessor);
        worker = Task.Run(() => RunAsync(stopping.Token));
    }

    public static DiagnosticCommandChannel? Start(
        bool developerMode,
        string? channelPath,
        IRuntimeScanUseCase scanUseCase,
        string? token = null,
        IRuntimeStateStore? stateStore = null,
        DiagnosticScenarioProcessor? scenarioProcessor = null)
    {
        ArgumentNullException.ThrowIfNull(scanUseCase);
        if (!developerMode || string.IsNullOrWhiteSpace(channelPath))
        {
            return null;
        }

        token ??= Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
        {
            throw new InvalidOperationException(
                $"Developer diagnostics require a token of at least 32 characters in {TokenEnvironmentVariable}.");
        }

        return new(Path.GetFullPath(channelPath), token, scanUseCase, stateStore, scenarioProcessor);
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            stopping.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var commandPath in Directory.EnumerateFiles(commandDirectory, "*.command.json")
                         .Order(StringComparer.Ordinal))
            {
                await ProcessFileAsync(commandPath, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessFileAsync(string commandPath, CancellationToken cancellationToken)
    {
        DiagnosticResponse response;
        string responseName;
        try
        {
            await using var stream = File.OpenRead(commandPath);
            var command = await JsonSerializer.DeserializeAsync<DiagnosticCommand>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new JsonException("The diagnostic command was empty.");
            response = await processor
                .ProcessAsync(command, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            responseName = $"{response.Id}.response.json";
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            response = new(
                Path.GetFileNameWithoutExtension(commandPath),
                false,
                "rejected",
                null,
                DateTimeOffset.UtcNow,
                "invalid-command-file");
            responseName = $"{Path.GetFileNameWithoutExtension(commandPath)}.response.json";
        }

        var responsePath = Path.Combine(responseDirectory, responseName);
        var temporaryPath = responsePath + ".tmp";
        await using (var output = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(output, response, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, responsePath, true);
        File.Delete(commandPath);
    }
}
