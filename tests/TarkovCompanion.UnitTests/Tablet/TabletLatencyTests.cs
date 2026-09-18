using System.Diagnostics;
using System.Text;
using TarkovCompanion.GroupServer;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.TabletSurface;

/// <summary>
/// How long a change takes to reach the second screen, measured rather than assumed.
/// </summary>
/// <remarks>
/// [V2 rough package 34] #422 took the desktop's own path from 5.2 s median to 0.70 s and said
/// plainly that the tablet still polled. It did: the desktop published on a fixed one-second
/// throttle and the page read on its own 1.5 s timer, so the second screen was two waits behind
/// the desk for a payload of a few hundred bytes.
///
/// What is timed here is the tablet leg — the desktop publishing a moved marker, to the paired
/// tablet's read returning it — over a real Kestrel on a loopback port. The desktop leg in front
/// of it (screenshot written → scene rebuilt) is #422's and is measured by #422's own harness; the
/// end-to-end number is the two added, and the PR quotes both rather than pretending one
/// measurement covers both halves.
///
/// The thresholds are generous on purpose. What they have to catch is a regression in kind — a
/// hold that stops holding, or a publish that goes back on a timer — and both of those cost
/// hundreds of milliseconds or whole seconds, not the tens that a loaded build agent costs.
/// </remarks>
public sealed class TabletLatencyTests
{
    private const int Iterations = 12;

    [Fact]
    public async Task AMovedMarkerReachesThePairedTabletWithoutWaitingForItsNextPoll()
    {
        await using var relay = await RelayMapTestHost.StartAsync();
        // The desktop is already showing a map when a tablet pairs, so the reader starts from one.
        await PublishAsync(relay, -1);
        var reader = new TabletReader(relay);
        await reader.PrimeAsync();

        var latencies = new List<double>(Iterations);
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var observed = reader.NextAsync();
            // The read is already held at this point. Nothing about this publish tells the tablet
            // to look; the relay wakes it. Timed from before the desktop's own POST, so what is
            // measured is the whole tablet leg rather than only the half after the relay has it.
            var started = Stopwatch.GetTimestamp();
            await PublishAsync(relay, iteration);
            await observed;
            latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        await reader.StopAsync();
        latencies.Sort();
        var median = latencies[latencies.Count / 2];
        var p95 = latencies[(int)Math.Min(latencies.Count - 1, Math.Ceiling(latencies.Count * 0.95) - 1)];

        // Thresholds set to catch a regression in kind rather than to pin a machine's speed: the
        // shape this replaced cost up to 1 s of publish throttle plus up to 1.5 s of poll, so
        // anything at or above a second is the old behaviour coming back. On a shared build box
        // running six test projects these have room to breathe and still fail that.
        Assert.True(median < 400, $"median {median:F0} ms; a held read should answer as soon as the desktop publishes");
        Assert.True(p95 < 1200, $"p95 {p95:F0} ms");
        // Every read returned because something changed, not because a timer went off. If the
        // hold stops holding this is the assertion that says so, whatever the machine's speed.
        Assert.Equal(Iterations, reader.ReadsThatCarriedAChange);
        Assert.Equal(Iterations + 1, reader.Reads);
    }

    [Fact]
    public async Task AReadThatAsksForNoHoldStillAnswersAtOnce()
    {
        // The compatibility rule, from the relay's side: an older tablet page names neither
        // parameter and must not be held for twenty seconds.
        await using var relay = await RelayMapTestHost.StartAsync();
        await PublishAsync(relay, 0);

        var started = Stopwatch.GetTimestamp();
        var response = await relay.GetAsync("v2/companion/relay/map", relay.Tablet);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"an unheld read took {elapsed.TotalMilliseconds:F0} ms");
        Assert.NotNull(response.Headers.GetValues(RelayCompanionRoutes.MapRevisionHeader).Single());
    }

    [Fact]
    public async Task AHoldEndsOnItsOwnWhenNothingHappens()
    {
        // A hold that never expires is a socket that never comes back. Asked for a second here
        // rather than the cap, because the cap is what the tablet asks for and the point is that
        // the caller's own shorter wait is honoured.
        await using var relay = await RelayMapTestHost.StartAsync();
        await PublishAsync(relay, 0);
        var current = relay.Surfaces.Revision;

        var started = Stopwatch.GetTimestamp();
        var response = await relay.GetAsync($"v2/companion/relay/map?since={current}&wait=1", relay.Tablet);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(750), $"the hold returned after {elapsed.TotalMilliseconds:F0} ms");
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"the hold overran at {elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(
            current.ToString(),
            response.Headers.GetValues(RelayCompanionRoutes.MapRevisionHeader).Single());
    }

    [Fact]
    public async Task AHeldReadIsReportedWhileItIsHeld()
    {
        // /health is the only way to see the bound being approached on a running relay.
        await using var relay = await RelayMapTestHost.StartAsync();
        await PublishAsync(relay, 0);
        var current = relay.Surfaces.Revision;

        var held = relay.GetAsync($"v2/companion/relay/map?since={current}&wait=15", relay.Tablet);
        var seen = 0;
        // Generous: the point is that a held read is counted while it is held, not how quickly a
        // loaded box gets the request onto the wire.
        for (var attempt = 0; attempt < 250 && seen == 0; attempt++)
        {
            seen = relay.Surfaces.WaitingCount;
            if (seen == 0)
            {
                await Task.Delay(20);
            }
        }

        await PublishAsync(relay, 1);
        await held;

        Assert.Equal(1, seen);
        Assert.Equal(0, relay.Surfaces.WaitingCount);
    }

    /// <summary>One publish of the desktop's map, with the marker somewhere new, over the real route.</summary>
    private static async Task PublishAsync(RelayMapTestHost relay, int step)
    {
        var response = await relay.PostAsync(
            "v2/companion/relay/map",
            relay.Owner,
            Encoding.UTF8.GetBytes($$$"""{"mapId":"customs","marker":{"x":{{{step}}},"y":{{{step}}}}}"""),
            "application/json");
        Assert.True(response.IsSuccessStatusCode);
    }

    /// <summary>
    /// The page's own read loop, in the shape the tablet actually runs it: hold on the revision it
    /// has, and read again the moment the answer comes back.
    /// </summary>
    private sealed class TabletReader(RelayMapTestHost relay)
    {
        private readonly CancellationTokenSource _stopping = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _loop;
        private long _revision;

        public int Reads { get; private set; }

        public int ReadsThatCarriedAChange { get; private set; }

        public async Task PrimeAsync()
        {
            // The first read cannot hold — there is nothing to hold against — so it is made before
            // anything is timed, exactly as the page makes it once after pairing.
            var response = await relay.GetAsync("v2/companion/relay/map", relay.Tablet);
            Reads++;
            _revision = response.IsSuccessStatusCode
                ? long.Parse(response.Headers.GetValues(RelayCompanionRoutes.MapRevisionHeader).Single())
                : 0;
            _loop = Task.Run(ReadLoopAsync);
        }

        public Task NextAsync() => _changed.Task;

        public async Task StopAsync()
        {
            await _stopping.CancelAsync();
            if (_loop is not null)
            {
                try
                {
                    await _loop;
                }
                catch (OperationCanceledException)
                {
                }
            }

            _stopping.Dispose();
        }

        private async Task ReadLoopAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                var response = await relay
                    .GetAsync($"v2/companion/relay/map?since={_revision}&wait=20", relay.Tablet, _stopping.Token)
                    .ConfigureAwait(false);
                Reads++;
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var revision = long.Parse(response.Headers.GetValues(RelayCompanionRoutes.MapRevisionHeader).Single());
                if (revision <= _revision)
                {
                    // The hold expired with nothing new. The page just asks again.
                    continue;
                }

                _revision = revision;
                ReadsThatCarriedAChange++;
                var woken = _changed;
                _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                woken.TrySetResult();
            }
        }
    }
}
