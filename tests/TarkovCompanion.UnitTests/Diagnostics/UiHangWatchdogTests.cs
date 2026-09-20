using System.Collections.Concurrent;
using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.UnitTests.Diagnostics;

/// <summary>
/// #453: the hang watchdog, against a dispatcher that really is blocked.
/// </summary>
/// <remarks>
/// The dispatcher here is one thread draining a queue, which is what a dispatcher is. Blocking it
/// means blocking that thread on an event, so the heartbeat is genuinely queued behind work that
/// has not returned — not a flag the watchdog is told to read as "busy".
/// </remarks>
public sealed class UiHangWatchdogTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public void ABlockedDispatcherIsRecordedOnceWithWhatWasRunningAndAgainWhenItRecovers()
    {
        using var dispatcher = new OneThreadDispatcher();
        var records = new ConcurrentQueue<(string Category, string Message)>();
        using var hung = new ManualResetEventSlim(false);
        using var recovered = new ManualResetEventSlim(false);
        using var watchdog = new UiHangWatchdog(
            dispatcher.Post,
            () => "route '#/plan'; last load 'plan' (still running)",
            (category, message) =>
            {
                records.Enqueue((category, message));
                (category == "ui-hang" ? hung : recovered).Set();
            },
            Threshold,
            Interval);
        watchdog.Start();

        using var release = new ManualResetEventSlim(false);
        dispatcher.Post(() => release.Wait(TimeSpan.FromSeconds(30)));

        Assert.True(hung.Wait(TimeSpan.FromSeconds(10)), "a dispatcher blocked past the threshold was never reported");
        // Still blocked: several more checks pass and none of them may report it a second time.
        Thread.Sleep(Threshold);
        Assert.Single(records);
        var (category, message) = records.First();
        Assert.Equal("ui-hang", category);
        Assert.Contains("has not answered for", message, StringComparison.Ordinal);
        Assert.Contains("route '#/plan'", message, StringComparison.Ordinal);
        Assert.Contains("last load 'plan'", message, StringComparison.Ordinal);

        release.Set();
        Assert.True(recovered.Wait(TimeSpan.FromSeconds(10)), "the recovery was never reported");
        Assert.Equal(["ui-hang", "ui-hang-recovered"], records.Select(record => record.Category));
        Assert.Contains("answered again after", records.Last().Message, StringComparison.Ordinal);
        Assert.Equal(1, watchdog.HangsRecorded);
    }

    [Fact]
    public void ADispatcherThatKeepsAnsweringIsNeverReported()
    {
        using var dispatcher = new OneThreadDispatcher();
        var records = new ConcurrentQueue<string>();
        using var watchdog = new UiHangWatchdog(
            dispatcher.Post,
            () => "idle",
            (category, _) => records.Enqueue(category),
            Threshold,
            Interval);
        watchdog.Start();

        // Busy, but in turns far shorter than the threshold, for several thresholds' worth of time.
        var until = DateTime.UtcNow + (Threshold * 4);
        while (DateTime.UtcNow < until)
        {
            dispatcher.Post(() => Thread.Sleep(10));
            Thread.Sleep(15);
        }

        Assert.Empty(records);
    }

    [Fact]
    public void ADispatcherThatRefusesTheHeartbeatStopsTheWatchdogQuietly()
    {
        var unhandled = new ConcurrentQueue<object>();
        UnhandledExceptionEventHandler handler = (_, arguments) => unhandled.Enqueue(arguments.ExceptionObject);
        AppDomain.CurrentDomain.UnhandledException += handler;
        try
        {
            var posts = 0;
            using var watchdog = new UiHangWatchdog(
                _ =>
                {
                    Interlocked.Increment(ref posts);
                    throw new InvalidOperationException("The dispatcher has shut down.");
                },
                () => "idle",
                (_, _) => { },
                Threshold,
                Interval);
            watchdog.Start();
            Thread.Sleep(Threshold);

            // It tried once, was refused, and stopped: no retry storm, and nothing escaped the thread.
            Assert.Equal(1, Volatile.Read(ref posts));
            Assert.Empty(unhandled);
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= handler;
        }
    }

    /// <summary>One thread running whatever is posted to it, in order.</summary>
    private sealed class OneThreadDispatcher : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = [];
        private readonly Thread _thread;

        public OneThreadDispatcher()
        {
            _thread = new Thread(() =>
            {
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    work();
                }
            })
            {
                IsBackground = true,
                Name = "stand-in dispatcher",
            };
            _thread.Start();
        }

        public void Post(Action work) => _queue.Add(work);

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }
    }
}
