using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>
/// When this client exchanges, and what it asks the relay for while it does.
/// </summary>
/// <remarks>
/// The exchange used to be a five-second tick and nothing else. Two things changed: it goes as
/// soon as what it publishes changes, and it asks the relay to hold the answer until the room
/// moves. Both have to stay bounded, and neither may break against a relay that has never heard
/// of either — which is the case Clayton actually runs, older builds beside newer ones.
/// </remarks>
public sealed class GroupSessionExchangeTests
{
    /// <summary>
    /// A relay that answers with no revision is exchanged with on the old tick.
    /// </summary>
    /// <remarks>
    /// This is the trap in the whole design. A client that holds its exchanges and then meets a
    /// relay that answers immediately every time would exchange as fast as the network allowed
    /// — a busy loop against somebody else's server, caused by an upgrade at this end.
    /// </remarks>
    [Fact]
    public async Task AnOlderRelayIsStillExchangedWithOnTheOldTick()
    {
        var asked = new ConcurrentQueue<string>();
        var handler = new RecordingHandler(asked, request => Json("""{"members":[]}"""));
        await using var service = Service(handler, out var store);

        service.Start();
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.InRange(asked.Count, 1, 3);
        Assert.All(asked, query => Assert.Equal(string.Empty, query));
    }

    /// <summary>A relay that answers with one is asked to hold the next exchange.</summary>
    [Fact]
    public async Task ARelayThatAnswersWithARevisionIsAskedToHoldTheNextExchange()
    {
        var asked = new ConcurrentQueue<string>();
        var handler = new RecordingHandler(asked, request => Json("""{"members":[],"revision":7}"""));
        await using var service = Service(handler, out var store);

        service.Start();
        await WaitUntilAsync(() => asked.Count >= 2);

        var queries = asked.ToArray();
        Assert.Equal(string.Empty, queries[0]);
        Assert.Contains("since=7", queries[1], StringComparison.Ordinal);
        Assert.Contains("wait=", queries[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A new position goes up now, rather than on the next tick.
    /// </summary>
    /// <remarks>
    /// The measurement in TeammatePositionLatencyTests covers this end to end; this covers the
    /// one decision — that a change to what is published ends the current hold — without a
    /// relay, a folder or a second member.
    /// </remarks>
    [Fact]
    public async Task APositionPublishesWithoutWaitingForTheNextTick()
    {
        var asked = new ConcurrentQueue<string>();
        var bodies = new ConcurrentQueue<string>();
        var handler = new RecordingHandler(
            asked,
            request => Json("""{"members":[],"revision":1}"""),
            bodies);
        await using var service = Service(handler, out var store);

        service.Start();
        await WaitUntilAsync(() => asked.Count >= 2);
        var before = asked.Count;

        var started = Stopwatch.GetTimestamp();
        store.Update(current => current with { Raid = current.Raid with { LastKnownPosition = Somewhere() } });
        await WaitUntilAsync(() => bodies.Any(body => body.Contains("\"x\":12.5", StringComparison.Ordinal)));

        Assert.True(
            Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2),
            "The position should have gone up on the change, not on the next tick.");
        Assert.True(asked.Count > before);
    }

    /// <summary>
    /// A local state that changes constantly does not become a request per change.
    /// </summary>
    /// <remarks>
    /// A raid moves the runtime snapshot several times a second, most of it nothing to do with
    /// the group. Both guards are here: only what is published counts as a change, and what does
    /// count is rate limited.
    /// </remarks>
    [Fact]
    public async Task AFloodOfLocalChangesIsBoundedToAFewExchanges()
    {
        var asked = new ConcurrentQueue<string>();
        var handler = new RecordingHandler(asked, request => Json("""{"members":[],"revision":1}"""));
        await using var service = Service(handler, out var store);

        service.Start();
        await WaitUntilAsync(() => asked.Count >= 1);
        var before = asked.Count;

        var flooding = Stopwatch.GetTimestamp();
        for (var change = 0; change < 200; change++)
        {
            var moved = Somewhere(change);
            store.Update(current => current with { Raid = current.Raid with { LastKnownPosition = moved } });
            await Task.Delay(5);
        }

        var elapsed = Stopwatch.GetElapsedTime(flooding);
        var exchanges = asked.Count - before;
        var allowed = (int)Math.Ceiling(elapsed.TotalSeconds * 4) + 2;
        Assert.True(exchanges <= allowed, $"{exchanges} exchanges in {elapsed.TotalSeconds:0.0}s exceeds {allowed}.");
    }

    private static ScreenshotPosition Somewhere(int step = 0) => new(
        DateTimeOffset.UtcNow.AddMilliseconds(step),
        new WorldPosition(12.5 + step, 0, 3.5),
        new QuaternionOrientation(0, 0, 0, 1),
        0,
        null,
        null,
        $"shot-{step}.png");

    private static GroupSessionService Service(HttpMessageHandler handler, out RuntimeStateStore store)
    {
        store = new(new(
            false,
            Offline: true,
            GameMode.Regular,
            "en",
            TimeSpan.FromHours(9),
            TimeSpan.FromMinutes(5)));
        return new(
            new FixedSettings(),
            store,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<GroupSessionService>.Instance);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 200 && !ready(); attempt++)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>Notes what was asked for, and answers whatever the test decided.</summary>
    private sealed class RecordingHandler(
        ConcurrentQueue<string> queries,
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        ConcurrentQueue<string>? bodies = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            queries.Enqueue(request.RequestUri?.Query.TrimStart('?') ?? string.Empty);
            if (bodies is not null && request.Content is { } content)
            {
                bodies.Enqueue(await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            // A relay that holds would not answer yet; this one answers at once, because what is
            // being measured here is what the client does with the answer.
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            return respond(request);
        }
    }

    private sealed class FixedSettings : IGroupSettingsStore
    {
        private static readonly GroupSharingSettings Settings = new(
            true,
            "https://relay.example.test/",
            "Clay",
            "a-key-long-enough",
            false,
            false);

        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Settings);

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
