using System.Net;
using System.Text;
using System.Text.Json;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

/// <summary>
/// What the running relay publishes to a stranger and what it keeps for its operator.
/// </summary>
/// <remarks>
/// The public <c>/health</c> route carried the number of rooms and members the relay held, how many
/// requests it was holding open and when it started, while the model that was written to say whether
/// a relay is fit to serve (<c>RelayReadiness</c>) was called by nothing (#281). Both defects are
/// about what <c>Program.cs</c> does, so these start it. The operator key is process-wide state, so
/// it is given to a child process, never set in this one.
/// </remarks>
public sealed class RunningRelayReadinessTests
{
    private const string OperatorKey = "operator-key-for-the-readiness-tests-0123456789";

    [Fact]
    public async Task PublicHealthNamesTheBuildAndTheProtocolAndNothingAboutActivity()
    {
        await using var relay = await RunningRelay.StartAsync();

        var response = await relay.Client.GetAsync("health");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Exactly these, so a count added back to the public route fails here. The updater requires
        // version, commit and protocol from it after an install, and relay-watch reads the commit.
        Assert.Equal(
            ["commit", "protocol", "status", "version"],
            body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReadinessIsRefusedWithoutTheOperatorKeyAndWithAWrongOne()
    {
        await using var relay = await RunningRelay.StartAsync(("TARKOV_RELAY_ADMIN_KEY", OperatorKey));

        var anonymous = await relay.Client.GetAsync("admin/readiness");
        var wrong = await relay.GetWithKeyAsync("admin/readiness", "not-the-operator-key");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task ReadinessRefusesEveryoneWhenNoOperatorKeyIsConfigured()
    {
        await using var relay = await RunningRelay.StartAsync();

        var response = await relay.GetWithKeyAsync("admin/readiness", OperatorKey);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheOperatorGetsARealVerdictWithTheCountsHealthNoLongerCarries()
    {
        await using var relay = await RunningRelay.StartAsync(("TARKOV_RELAY_ADMIN_KEY", OperatorKey));

        var response = await relay.GetWithKeyAsync("admin/readiness", OperatorKey);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ready", body.GetProperty("status").GetString());
        Assert.Equal(7, body.GetProperty("checks").GetArrayLength());
        var details = body.GetProperty("relay");
        Assert.Equal(0, details.GetProperty("rooms").GetInt32());
        Assert.Equal(0, details.GetProperty("heldTabletReads").GetInt32());
        Assert.Equal("memory", details.GetProperty("storage").GetString());
        // The security headers are on this answer like every other, and it must never be cached.
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRunningRelayCountsWhatItRejectsAndReportsItAsASignalNotAsUnreadiness()
    {
        await using var relay = await RunningRelay.StartAsync(("TARKOV_RELAY_ADMIN_KEY", OperatorKey));

        // More than the tolerated number of malformed pairing offers, each answered 400.
        for (var i = 0; i < 5; i++)
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var rejected = await relay.Client.PostAsync("v2/companion/pairing/offers", content);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        var response = await relay.GetWithKeyAsync("admin/readiness", OperatorKey);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var abuse = body.GetProperty("signals").EnumerateArray()
            .Single(signal => signal.GetProperty("signal").GetString() == "rejectedInputAbuse");
        Assert.True(abuse.GetProperty("elevated").GetBoolean());
        // A relay correctly refusing hostile input is doing its job.
        Assert.Equal("ready", body.GetProperty("status").GetString());
    }
}
