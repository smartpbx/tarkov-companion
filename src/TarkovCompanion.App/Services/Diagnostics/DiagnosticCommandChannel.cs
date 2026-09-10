using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.Services.Diagnostics;

public enum DiagnosticCommandKind
{
    Scan,
    Scenario,
}

public sealed record DiagnosticCommand(
    string? Id,
    DiagnosticCommandKind Command,
    string? Token,
    string? Scenario = null);

public sealed record DiagnosticResponse(
    string Id,
    bool Accepted,
    string Event,
    string? Scenario,
    DateTimeOffset ProcessedUtc,
    string? Error = null,
    ScanExecutionResult? Scan = null);

public sealed class DiagnosticCommandProcessor(string requiredToken, IRuntimeScanUseCase scanUseCase)
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
                var scan = await scanUseCase.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                return new(
                    commandId,
                    true,
                    scan.Succeeded ? "scan-completed" : scan.IsAvailable ? "scan-no-result" : "scan-unavailable",
                    null,
                    processedUtc,
                    null,
                    scan);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new(commandId, true, "scan-failed", null, processedUtc, exception.Message);
            }
        }

        return command.Command switch
        {
            DiagnosticCommandKind.Scenario when IsSafeIdentifier(command.Scenario) =>
                new(commandId, true, "scenario-selected", command.Scenario, processedUtc),
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

    private DiagnosticCommandChannel(string channelPath, string token, IRuntimeScanUseCase scanUseCase)
    {
        commandDirectory = Path.Combine(channelPath, "commands");
        responseDirectory = Path.Combine(channelPath, "responses");
        Directory.CreateDirectory(commandDirectory);
        Directory.CreateDirectory(responseDirectory);
        processor = new(token, scanUseCase);
        worker = Task.Run(() => RunAsync(stopping.Token));
    }

    public static DiagnosticCommandChannel? Start(
        bool developerMode,
        string? channelPath,
        IRuntimeScanUseCase scanUseCase,
        string? token = null)
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

        return new(Path.GetFullPath(channelPath), token, scanUseCase);
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
