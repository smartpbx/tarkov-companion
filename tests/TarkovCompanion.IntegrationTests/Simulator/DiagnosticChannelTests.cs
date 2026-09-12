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
                TemporaryDirectory.Remove(root);
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
