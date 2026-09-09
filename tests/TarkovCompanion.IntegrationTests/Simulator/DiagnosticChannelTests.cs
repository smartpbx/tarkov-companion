using System.Text.Json;
using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.IntegrationTests.Simulator;

public sealed class DiagnosticChannelTests
{
    private static readonly JsonSerializerOptions WebSerializerOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ChannelIsUnavailableOutsideExplicitDeveloperMode()
    {
        Assert.Null(DiagnosticCommandChannel.Start(false, "unused", new string('x', 32)));
    }

    [Fact]
    public void ProcessorExposesOnlyScanAndScenarioEvents()
    {
        var token = new string('x', 32);
        var processor = new DiagnosticCommandProcessor(token);
        var timestamp = DateTimeOffset.Parse("2026-01-15T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var scan = processor.Process(new("scan-1", DiagnosticCommandKind.Scan, token), timestamp);
        var scenario = processor.Process(new("scene-1", DiagnosticCommandKind.Scenario, token, "PositionUpdate"), timestamp);
        var unsupported = processor.Process(new("bad-1", (DiagnosticCommandKind)999, token), timestamp);

        Assert.Equal("scan-requested", scan.Event);
        Assert.Equal("scenario-selected", scenario.Event);
        Assert.False(unsupported.Accepted);
        Assert.Equal("unsupported-command", unsupported.Error);
    }

    [Fact]
    public void ProcessorSanitizesUntrustedIdentifiersBeforeAuthorization()
    {
        var token = new string('x', 32);
        var response = new DiagnosticCommandProcessor(token).Process(
            new("../../outside", DiagnosticCommandKind.Scan, "wrong-token"),
            DateTimeOffset.UtcNow);

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
            await using var channel = DiagnosticCommandChannel.Start(true, root, token);
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
                Directory.Delete(root, true);
            }
        }
    }
}
