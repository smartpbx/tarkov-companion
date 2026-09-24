using System.Text.Json;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.IntegrationTests.Simulator;

public sealed class DiagnosticChannelTests
{
    private static readonly JsonSerializerOptions WebSerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ChannelIsUnavailableOutsideExplicitDeveloperMode()
    {
        Assert.Null(DiagnosticCommandChannel.Start(
            false,
            "unused",
            new StubScanUseCase(CompletedResult()),
            new string('x', 32)));
    }

    [Fact]
    public async Task ProcessorExposesOnlyDelegatedScanAndScenarioEvents()
    {
        var token = new string('x', 32);
        var scanUseCase = new StubScanUseCase(CompletedResult());
        var processor = new DiagnosticCommandProcessor(token, scanUseCase);
        var timestamp = new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);

        var scan = await processor.ProcessAsync(
            new("scan-1", DiagnosticCommandKind.Scan, token),
            timestamp,
            CancellationToken.None);
        var scenario = await processor.ProcessAsync(
            new("scene-1", DiagnosticCommandKind.Scenario, token, "PositionUpdate"),
            timestamp,
            CancellationToken.None);
        var unsupported = await processor.ProcessAsync(
            new("bad-1", (DiagnosticCommandKind)999, token),
            timestamp,
            CancellationToken.None);

        Assert.Equal("scan-completed", scan.Event);
        Assert.Equal("demo-item", scan.Scan?.CanonicalItemId);
        Assert.Equal(1, scanUseCase.CallCount);
        Assert.Equal("scenario-selected", scenario.Event);
        Assert.False(unsupported.Accepted);
        Assert.Equal("unsupported-command", unsupported.Error);
    }

    [Fact]
    public async Task UnavailableScanIsReportedHonestly()
    {
        var token = new string('x', 32);
        var processor = new DiagnosticCommandProcessor(
            token,
            new StubScanUseCase(ScanExecutionResult.Unavailable(
                "No production OCR provider is configured.",
                DateTimeOffset.UtcNow)));

        var response = await processor.ProcessAsync(
            new("scan-2", DiagnosticCommandKind.Scan, token),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.True(response.Accepted);
        Assert.Equal("scan-unavailable", response.Event);
        Assert.False(response.Scan?.Succeeded);
        Assert.Contains("No production OCR", response.Scan?.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessorSanitizesUntrustedIdentifiersBeforeAuthorization()
    {
        var token = new string('x', 32);
        var response = await new DiagnosticCommandProcessor(token, new StubScanUseCase(CompletedResult()))
            .ProcessAsync(
                new("../../outside", DiagnosticCommandKind.Scan, "wrong-token"),
                DateTimeOffset.UtcNow,
                CancellationToken.None);

        Assert.False(response.Accepted);
        Assert.Equal("invalid", response.Id);
        Assert.Equal("invalid-id", response.Error);
    }

    [Fact]
    public async Task AuthorizedCommandFileReceivesResponse()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-diagnostic-channel-{Guid.NewGuid():N}");
        var token = new string('t', 32);
        try
        {
            await using var channel = DiagnosticCommandChannel.Start(
                true,
                root,
                new StubScanUseCase(CompletedResult()),
                token);
            Assert.NotNull(channel);

            var commandPath = Path.Combine(root, "commands", "scene-2.command.json");
            var temporaryCommandPath = commandPath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryCommandPath,
                JsonSerializer.Serialize(new
                {
                    id = "scene-2",
                    command = "scenario",
                    token,
                    scenario = "RaidEnd",
                }));
            File.Move(temporaryCommandPath, commandPath);

            var responsePath = Path.Combine(root, "responses", "scene-2.response.json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(responsePath))
            {
                await Task.Delay(25, timeout.Token);
            }

            var response = JsonSerializer.Deserialize<DiagnosticResponse>(
                await File.ReadAllTextAsync(responsePath),
                WebSerializerOptions);
            Assert.NotNull(response);
            Assert.True(response.Accepted);
            Assert.Equal("RaidEnd", response.Scenario);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                ScratchDirectory.Remove(root);
            }
        }
    }

    [Fact]
    public async Task ARejectedCommandFileIsAnsweredUnderItsOwnId()
    {
        // The caller waits for "<id>.response.json". A rejection used to be named after the file
        // with its ".command" left in, so a file the channel could not use looked like a timeout.
        using var scratch = new ChannelRoot();
        await using var channel = DiagnosticCommandChannel.Start(
            true, scratch.Path, new StubScanUseCase(CompletedResult()), new string('t', 32));
        Assert.NotNull(channel);

        await File.WriteAllTextAsync(scratch.Command("bad-3"), "this is not json");

        var response = await scratch.WaitForResponseAsync("bad-3");
        Assert.False(response.Accepted);
        Assert.Equal("invalid-command-file", response.Error);
        Assert.False(File.Exists(scratch.Command("bad-3")), "A rejected command file is cleared away.");
    }

    [Fact]
    public async Task ACommandFileHeldOpenForAMomentIsAnsweredOnceItIsReleasedAndRunsOnce()
    {
        // Whatever scans a freshly written file (antivirus, the indexer) can hold it briefly. That
        // used to become a rejection under the wrong name and the command was gone; it is now read
        // again on the next poll, and a scan is not run twice to make up for it.
        using var scratch = new ChannelRoot();
        var token = new string('t', 32);
        var scan = new StubScanUseCase(CompletedResult());
        await using var channel = DiagnosticCommandChannel.Start(true, scratch.Path, scan, token);
        Assert.NotNull(channel);

        var commandPath = scratch.Command("scan-3");
        var held = OpenHeldCommandFile(scratch, commandPath, JsonSerializer.Serialize(new { id = "scan-3", command = "scan", token }));
        try
        {

            await Task.Delay(TimeSpan.FromMilliseconds(600));
            Assert.False(File.Exists(scratch.Response("scan-3")), "Nothing is answered while the file is held.");
            Assert.Equal(0, scan.CallCount);
        }
        finally
        {
            await held.DisposeAsync();
        }

        var response = await scratch.WaitForResponseAsync("scan-3");
        Assert.True(response.Accepted);
        Assert.Equal("scan-completed", response.Event);
        Assert.Equal(1, scan.CallCount);
    }

    /// <summary>
    /// A command file that is already complete and already held when the channel can first see it,
    /// which is what a scanner holding a freshly renamed file looks like.
    /// </summary>
    /// <remarks>
    /// Creating the file in place with <see cref="FileShare.None"/> is not that on Linux: .NET creates
    /// the file and only then takes its advisory lock, so a poll landing between the two read an
    /// empty, unlocked file, rejected it as invalid JSON and answered while the test still "held"
    /// it (main run 35958124060, "Nothing is answered while the file is held"). So on Linux the file
    /// is written and locked under another name and renamed into place; the lock stays with the
    /// file. Windows cannot rename a file this process holds, and its create-and-deny is atomic.
    /// </remarks>
    private static FileStream OpenHeldCommandFile(ChannelRoot scratch, string commandPath, string json)
    {
        var path = OperatingSystem.IsWindows()
            ? commandPath
            : System.IO.Path.Combine(scratch.Path, System.IO.Path.GetFileName(commandPath) + ".staging");
        var held = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        held.Write(System.Text.Encoding.UTF8.GetBytes(json));
        held.Flush();
        if (!OperatingSystem.IsWindows())
        {
            File.Move(path, commandPath);
        }

        return held;
    }

    private sealed class ChannelRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tarkov-diagnostic-channel-{Guid.NewGuid():N}");

        public string Command(string id) => System.IO.Path.Combine(Path, "commands", id + ".command.json");

        public string Response(string id) => System.IO.Path.Combine(Path, "responses", id + ".response.json");

        public async Task<DiagnosticResponse> WaitForResponseAsync(string id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Response(id)))
            {
                await Task.Delay(25, timeout.Token);
            }

            var response = JsonSerializer.Deserialize<DiagnosticResponse>(
                await File.ReadAllTextAsync(Response(id), timeout.Token),
                WebSerializerOptions);
            return Assert.IsType<DiagnosticResponse>(response);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                ScratchDirectory.Remove(Path);
            }
        }
    }

    private static ScanExecutionResult CompletedResult() => new(
        true,
        true,
        "demo-item",
        "Demo item",
        120_000,
        60_000,
        "Keep",
        new Confidence(0.96),
        new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero),
        "test-fixture",
        "Fixture result.");

    private sealed class StubScanUseCase(ScanExecutionResult result) : IRuntimeScanUseCase
    {
        public int CallCount { get; private set; }

        public Task<ScanExecutionResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
