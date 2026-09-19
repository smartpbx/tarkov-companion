using System.Net;
using System.Text;
using System.Text.Json;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

/// <summary>
/// The report lifecycle as the running relay serves it, over HTTP, with its limits set the way an
/// operator sets them: by environment.
/// </summary>
public sealed class RunningRelayReportLifecycleTests : IDisposable
{
    private const string OperatorKey = "operator-key-for-the-report-tests-0123456789";

    private readonly string _state = Path.Combine(Path.GetTempPath(), "relay-reports-" + Guid.NewGuid().ToString("N"));

    public RunningRelayReportLifecycleTests() => Directory.CreateDirectory(_state);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
            // Temp.
        }
    }

    private Task<RunningRelay> StartAsync(params (string Name, string Value)[] more) =>
        RunningRelay.StartAsync(
            [("TARKOV_GROUP_STATE", _state), ("TARKOV_RELAY_ADMIN_KEY", OperatorKey), .. more]);

    private static async Task<HttpResponseMessage> ReportAsync(RunningRelay relay, string key, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "report")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/markdown"),
        };
        request.Headers.Add("X-Group-Key", key);
        return await relay.Client.SendAsync(request);
    }

    private static string Key(int index) => $"group-key-{index:D4}-for-the-report-tests";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task AReportGoesReceivedThenProcessedThenDeletedOverHttp()
    {
        await using var relay = await StartAsync();
        var sent = await ReportAsync(relay, Key(1), "the report body");
        var reference = (await JsonAsync(sent)).GetProperty("reference").GetString()!;

        var queue = await JsonAsync(await relay.GetWithKeyAsync("reports", OperatorKey));
        Assert.Equal(reference, Assert.Single(queue.EnumerateArray()).GetProperty("reference").GetString());

        var first = await relay.SendWithKeyAsync(HttpMethod.Post, $"admin/reports/{reference}/processed", OperatorKey);
        var again = await relay.SendWithKeyAsync(HttpMethod.Post, $"admin/reports/{reference}/processed", OperatorKey);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True((await JsonAsync(first)).GetProperty("changed").GetBoolean());
        Assert.False((await JsonAsync(again)).GetProperty("changed").GetBoolean());

        Assert.Empty((await JsonAsync(await relay.GetWithKeyAsync("reports", OperatorKey))).EnumerateArray());
        var ledger = await JsonAsync(await relay.GetWithKeyAsync("admin/reports", OperatorKey));
        Assert.Equal("processed", ledger.GetProperty("reports")[0].GetProperty("state").GetString());
        Assert.Equal(1, ledger.GetProperty("processed").GetInt32());

        var downgrade = await relay.SendWithKeyAsync(HttpMethod.Post, $"admin/reports/{reference}/failed", OperatorKey);
        Assert.Equal(HttpStatusCode.Conflict, downgrade.StatusCode);

        var deleted = await relay.SendWithKeyAsync(HttpMethod.Delete, $"admin/reports/{reference}", OperatorKey);
        var deletedAgain = await relay.SendWithKeyAsync(HttpMethod.Delete, $"admin/reports/{reference}", OperatorKey);
        Assert.True((await JsonAsync(deleted)).GetProperty("removed").GetBoolean());
        Assert.False((await JsonAsync(deletedAgain)).GetProperty("removed").GetBoolean());
        var gone = await relay.SendWithKeyAsync(HttpMethod.Post, $"admin/reports/{reference}/processed", OperatorKey);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task TheFilingQueueKeepsTheClosedShapeTheWorkflowValidates()
    {
        // relay-watch's file-relay-reports.sh refuses a listing whose objects have any keys but these.
        // A state field added here would make it file nothing at all.
        await using var relay = await StartAsync();
        await ReportAsync(relay, Key(1), "the report body");

        var queue = await JsonAsync(await relay.GetWithKeyAsync("reports", OperatorKey));

        Assert.Equal(
            ["bytes", "receivedUtc", "reference"],
            Assert.Single(queue.EnumerateArray()).EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task EveryReportAdminRouteRefusesAStrangerAndChangesNothing()
    {
        await using var relay = await StartAsync();
        var reference = (await JsonAsync(await ReportAsync(relay, Key(1), "body"))).GetProperty("reference").GetString()!;

        var responses = new[]
        {
            await relay.Client.GetAsync("admin/reports"),
            await relay.SendWithKeyAsync(HttpMethod.Post, $"admin/reports/{reference}/processed", "wrong-key"),
            await relay.SendWithKeyAsync(HttpMethod.Post, $"admin/reports/{reference}/failed", "wrong-key"),
            await relay.SendWithKeyAsync(HttpMethod.Delete, $"admin/reports/{reference}", "wrong-key"),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        var ledger = await JsonAsync(await relay.GetWithKeyAsync("admin/reports", OperatorKey));
        Assert.Equal("received", ledger.GetProperty("reports")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task ARelayHoldingItsLimitAnswers503WithAReasonThePlayerSees()
    {
        await using var relay = await StartAsync(("TARKOV_RELAY_REPORT_MAX_HELD", "2"));
        Assert.Equal(HttpStatusCode.OK, (await ReportAsync(relay, Key(1), "1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ReportAsync(relay, Key(2), "2")).StatusCode);

        var refused = await ReportAsync(relay, Key(3), "3");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("holding as many reports", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(refused.Headers.Contains("Retry-After"));
        var ledger = await JsonAsync(await relay.GetWithKeyAsync("admin/reports", OperatorKey));
        Assert.Equal(2, ledger.GetProperty("held").GetInt32());
    }

    [Fact]
    public async Task ARelayShortOfDiskStopsTakingReportsButKeepsAnswering()
    {
        await using var relay = await StartAsync(("TARKOV_RELAY_REPORT_MIN_FREE_MB", "100000000"));

        var refused = await ReportAsync(relay, Key(1), "body");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("short of space", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await relay.Client.GetAsync("health")).StatusCode);
    }
}
