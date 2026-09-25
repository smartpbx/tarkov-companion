namespace TarkovCompanion.App.Services;

/// <summary>
/// Runs one piece of work on the UI thread, however many times it was asked for.
/// </summary>
/// <remarks>
/// The runtime store raises Changed on every Update, and the shell posted one apply per event,
/// each carrying the snapshot captured when the event fired. So a burst — a new session folder
/// is read from byte zero and yields every line it has — ran fourteen page view models and five
/// map calls once per line, applying snapshots that were already stale by the time they were
/// applied, to arrive at a state the last one would have reached on its own.
///
/// One pending post per burst, and the work reads the current state rather than closing over a
/// captured one, so what lands on screen is what is true when it lands rather than what was true
/// when the event fired.
///
/// Its own type because the alternative is four lines of interlocked state inside a view model
/// that cannot be built without a dispatcher, which means the one part of it that can be wrong
/// in a way nobody notices is the one part nothing can check.
/// </remarks>
public sealed class CoalescingDispatch(SynchronizationContext? context, Action work)
{
    private readonly Action _work = work ?? throw new ArgumentNullException(nameof(work));
    private readonly Lock _inline = new();
    private int _pending;

    /// <summary>
    /// Asks for the work to run, soon, once.
    /// </summary>
    /// <remarks>
    /// Runs it straight away when there is no dispatcher, or when the caller is already on it:
    /// a test host and a headless run have no dispatcher, and posting to the thread you are
    /// already on would delay the work for no reason.
    ///
    /// Without a dispatcher the work is also run one caller at a time. The shell's save queue
    /// finishes on the thread pool and asks for a pass from there, while the runtime store asks
    /// from whichever thread updated it. Two passes at once each read the store and then wrote
    /// the surface, so the one that read first could write last: a stale "offline" surface
    /// replaced the recovered one, and a dismissed banner came back or a new one stayed hidden.
    /// That failed A_global_problem_is_one_dismissible_line... three times on 2026-09-25. One at
    /// a time, whichever pass runs last reads the latest state, which is all the dispatcher
    /// guaranteed. The lock is re-entrant, so a pass that asks for another runs it inline as
    /// before.
    /// </remarks>
    public void Request()
    {
        if (context is null)
        {
            lock (_inline)
            {
                _work();
            }

            return;
        }

        if (ReferenceEquals(SynchronizationContext.Current, context))
        {
            _work();
            return;
        }

        if (Interlocked.Exchange(ref _pending, 1) != 0)
        {
            return;
        }

        context.Post(_ => Run(), null);
    }

    /// <summary>
    /// Clears the flag before running, deliberately.
    /// </summary>
    /// <remarks>
    /// A change arriving while the work is running then queues a fresh post, which is one extra
    /// pass. Clearing afterwards would let that change fall between the work reading the state
    /// and the flag being cleared, and be lost until something else happened to change — which
    /// is a dropped update rather than a duplicated one, and much harder to notice.
    /// </remarks>
    private void Run()
    {
        Volatile.Write(ref _pending, 0);
        _work();
    }
}
