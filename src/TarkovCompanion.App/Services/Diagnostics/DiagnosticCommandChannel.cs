using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.Services.Diagnostics;

public enum DiagnosticCommandKind
{
    Scan,
    Scenario,

    /// <summary>[#279] Waits for the launch's gallery scene: the map drawn and its seeded state on it.</summary>
    Ready,
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
    ScanExecutionResult? Scan = null,
    string? Detail = null);

public sealed class DiagnosticCommandProcessor(
    string requiredToken,
    IRuntimeScanUseCase scanUseCase,
    GalleryReadiness? readiness = null,
    TimeSpan? readyTimeout = null)
{
    private const int MaximumIdentifierLength = 80;

    /// <summary>How long one "ready" command waits before answering "not-ready".</summary>
    private readonly TimeSpan _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(150);

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

        if (command.Command == DiagnosticCommandKind.Ready)
        {
            if (readiness is null)
            {
                return new(commandId, true, "not-ready", null, processedUtc, "This launch has no --gallery-scene.");
            }

            // [#279] A scenario on a "ready" names what to wait for after a gallery step.
            if (command.Scenario is not null && !IsSafeIdentifier(command.Scenario))
            {
                return Reject(commandId, processedUtc, "invalid-scenario");
            }

            var (ready, detail) = await readiness.WaitAsync(_readyTimeout, cancellationToken, command.Scenario).ConfigureAwait(false);
            return ready
                ? new(commandId, true, "ready", null, processedUtc, Detail: detail)
                : new(commandId, true, "not-ready", null, processedUtc, detail);
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

    private DiagnosticCommandChannel(string channelPath, string token, IRuntimeScanUseCase scanUseCase, GalleryReadiness? readiness)
    {
        commandDirectory = Path.Combine(channelPath, "commands");
        responseDirectory = Path.Combine(channelPath, "responses");
        Directory.CreateDirectory(commandDirectory);
        Directory.CreateDirectory(responseDirectory);
        processor = new(token, scanUseCase, readiness);
        worker = Task.Run(() => RunAsync(stopping.Token));
    }

    public static DiagnosticCommandChannel? Start(
        bool developerMode,
        string? channelPath,
        IRuntimeScanUseCase scanUseCase,
        string? token = null,
        GalleryReadiness? readiness = null)
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

        return new(Path.GetFullPath(channelPath), token, scanUseCase, readiness);
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

    /// <summary>
    /// A command file that stays unreadable for this many polls (about five seconds) is rejected
    /// rather than retried for ever.
    /// </summary>
    private const int UnreadableAttempts = 50;

    private const string CommandSuffix = ".command.json";

    private readonly Dictionary<string, int> unreadableAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DiagnosticResponse> unwritten = new(StringComparer.Ordinal);
    private readonly HashSet<string> answered = new(StringComparer.Ordinal);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                foreach (var commandPath in Directory.EnumerateFiles(commandDirectory, "*" + CommandSuffix)
                             .Order(StringComparer.Ordinal))
                {
                    await ProcessFileAsync(commandPath, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // One file the machine will not let go of must not end the channel: this loop is the
                // only reader, so a faulted task here reads from outside as every later command
                // timing out. Whatever was left undone is picked up again on the next poll.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessFileAsync(string commandPath, CancellationToken cancellationToken)
    {
        // The caller matches a response to its command by the id in the file's name, so a rejection
        // has to be named that way too. GetFileNameWithoutExtension would leave ".command" in it
        // and the caller would wait for a response that was never going to appear.
        var fileId = Path.GetFileName(commandPath)[..^CommandSuffix.Length];
        if (answered.Contains(commandPath))
        {
            // Answered on an earlier poll; only the cleanup failed. Do not run the command again:
            // a scan writes a row, and running it twice would be a second row.
            if (DeleteQuietly(commandPath))
            {
                answered.Remove(commandPath);
            }

            return;
        }

        // A command runs once. Its response is kept until it has been written, so a response that
        // will not write yet is retried on the next poll instead of running the command again.
        if (!unwritten.TryGetValue(commandPath, out var response))
        {
            var command = await ReadCommandAsync(commandPath, fileId, cancellationToken).ConfigureAwait(false);
            if (command.Rejection is null && command.Command is null)
            {
                return;
            }

            response = command.Rejection ?? await processor
                .ProcessAsync(command.Command!, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            unwritten[commandPath] = response;
        }

        await WriteResponseAsync(response, cancellationToken).ConfigureAwait(false);
        unwritten.Remove(commandPath);
        if (!DeleteQuietly(commandPath))
        {
            answered.Add(commandPath);
        }
    }

    /// <summary>
    /// Reads a command file. Neither member set means "not readable yet, ask again next poll".
    /// </summary>
    private async Task<(DiagnosticCommand? Command, DiagnosticResponse? Rejection)> ReadCommandAsync(
        string commandPath,
        string fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(commandPath);
            var command = await JsonSerializer.DeserializeAsync<DiagnosticCommand>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new JsonException("The diagnostic command was empty.");
            unreadableAttempts.Remove(commandPath);
            return (command, null);
        }
        catch (JsonException)
        {
            unreadableAttempts.Remove(commandPath);
            return (null, Rejected(fileId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file that has only just been renamed into place can be held for a moment by whatever
            // scans new files (antivirus, the indexer). That is not a rejection: wait a poll and read
            // it again, and reject only a file that stays unreadable.
            var attempts = unreadableAttempts.GetValueOrDefault(commandPath) + 1;
            if (attempts < UnreadableAttempts)
            {
                unreadableAttempts[commandPath] = attempts;
                return (null, null);
            }

            unreadableAttempts.Remove(commandPath);
            return (null, Rejected(fileId));
        }
    }

    private static DiagnosticResponse Rejected(string id) =>
        new(id, false, "rejected", null, DateTimeOffset.UtcNow, "invalid-command-file");

    private async Task WriteResponseAsync(DiagnosticResponse response, CancellationToken cancellationToken)
    {
        var responsePath = Path.Combine(responseDirectory, $"{response.Id}.response.json");
        var temporaryPath = responsePath + ".tmp";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using (var output = File.Create(temporaryPath))
                {
                    await JsonSerializer.SerializeAsync(output, response, SerializerOptions, cancellationToken)
                        .ConfigureAwait(false);
                }

                File.Move(temporaryPath, responsePath, true);
                return;
            }
            catch (Exception exception) when (attempt < 20 && exception is IOException or UnauthorizedAccessException)
            {
                // The same brief hold, on the file being handed back to the caller.
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left in place; the caller remembers it is answered, so the next poll only retries this.
            return false;
        }
    }
}
