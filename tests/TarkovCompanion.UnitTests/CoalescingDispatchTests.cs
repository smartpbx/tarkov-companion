using TarkovCompanion.App.Services;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// One pass over the shell per burst of changes, rather than one per change.
/// </summary>
/// <remarks>
/// The runtime store raises Changed on every Update, and the shell posted one apply per event,
/// each carrying the snapshot captured when the event fired. A new session folder is read from
/// byte zero and yields every line it has, so that burst ran fourteen page view models and five
/// map calls once per line — applying snapshots already stale by the time they were applied, to
/// arrive at a state the last one would have reached on its own.
///
/// This is the part of that fix that can be wrong in a way nobody notices, which is why it has
/// a type of its own: a view model needing a real dispatcher cannot be built in a test, and the
/// logic would otherwise be four lines of interlocked state that nothing could check.
/// </remarks>
public sealed class CoalescingDispatchTests
{
    [Fact]
    public void A_burst_of_requests_becomes_one_pass()
    {
        var queue = new HeldContext();
        var passes = 0;
        var dispatch = new CoalescingDispatch(queue, () => passes++);

        for (var index = 0; index < 50; index++)
        {
            dispatch.Request();
        }

        Assert.Equal(0, passes);
        Assert.Equal(1, queue.Drain());
        Assert.Equal(1, passes);
    }

    [Fact]
    public void The_pass_reads_what_is_true_when_it_runs()
    {
        // The whole reason the work is a closure over the store rather than over a captured
        // snapshot. Fifty events, the last one wins, and nothing in between reaches the screen.
        var queue = new HeldContext();
        var latest = 0;
        var seen = -1;
        var dispatch = new CoalescingDispatch(queue, () => seen = latest);

        for (var index = 1; index <= 50; index++)
        {
            latest = index;
            dispatch.Request();
        }

        queue.Drain();

        Assert.Equal(50, seen);
    }

    [Fact]
    public void A_change_arriving_during_a_pass_is_not_lost()
    {
        // Why the flag is cleared before the work rather than after. Clearing afterwards lets a
        // change fall between the work reading the state and the flag being cleared, which is a
        // dropped update rather than a duplicated one — and much harder to notice.
        var queue = new HeldContext();
        var passes = 0;
        CoalescingDispatch? dispatch = null;
        dispatch = new CoalescingDispatch(queue, () =>
        {
            passes++;
            if (passes == 1)
            {
                // Something changed while the shell was mid-apply.
                dispatch!.Request();
            }
        });

        dispatch.Request();
        queue.Drain();

        Assert.Equal(2, passes);
    }

    [Fact]
    public void A_second_burst_after_the_first_has_run_gets_its_own_pass()
    {
        var queue = new HeldContext();
        var passes = 0;
        var dispatch = new CoalescingDispatch(queue, () => passes++);

        dispatch.Request();
        dispatch.Request();
        queue.Drain();
        dispatch.Request();
        queue.Drain();

        Assert.Equal(2, passes);
    }

    [Fact]
    public void Without_a_dispatcher_the_work_runs_where_it_was_asked_for()
    {
        // A test host and a headless run have no dispatcher, and the shell has to keep working
        // in both: the readouts are still correct on every change, they are merely correct
        // more often than they need to be.
        var passes = 0;
        var dispatch = new CoalescingDispatch(null, () => passes++);

        dispatch.Request();
        dispatch.Request();

        Assert.Equal(2, passes);
    }

    [Fact]
    public async Task Without_a_dispatcher_two_threads_never_run_the_work_at_once()
    {
        // The shell's save queue asks for a pass from the thread pool while the runtime store asks
        // from the thread that updated it. Run together, the pass that read the store first could
        // write the surface last, and a stale problem banner replaced the current one.
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var running = 0;
        var mostAtOnce = 0;
        var passes = 0;
        var dispatch = new CoalescingDispatch(null, () =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref mostAtOnce, now);
            if (Interlocked.Increment(ref passes) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(10));
            }

            Interlocked.Decrement(ref running);
        });

        var first = Task.Run(dispatch.Request);
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
        var second = Task.Run(dispatch.Request);

        // Unserialized, the second pass enters at once; give it every chance to.
        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref passes));

        releaseFirst.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, passes);
        Assert.Equal(1, mostAtOnce);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    /// <summary>A dispatcher that holds what is posted to it until it is asked to run it.</summary>
    /// <remarks>
    /// Holding rather than running is what makes the coalescing observable: on a real dispatcher
    /// the posts are drained by a message loop nobody in a test controls.
    /// </remarks>
    private sealed class HeldContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> _posted = [];

        public override void Post(SendOrPostCallback callback, object? state) => _posted.Add((callback, state));

        /// <summary>Runs everything that was posted, and says how many that was.</summary>
        public int Drain()
        {
            var ran = 0;
            // By index, because running one may post another — which is the case the third
            // test here is about.
            for (var index = 0; index < _posted.Count; index++)
            {
                var (callback, state) = _posted[index];
                callback(state);
                ran++;
            }

            _posted.Clear();
            return ran;
        }
    }
}
