namespace TarkovCompanion.App.Services;

/// <summary>
/// Runs one piece of work on the UI thread's next turn, however many times it was asked for
/// before that turn came, and never on the caller's own stack.
/// </summary>
/// <remarks>
/// <see cref="CoalescingDispatch"/> runs the work at once when the caller is already on the
/// dispatcher, which is right for "apply the latest snapshot" and wrong for "rebuild the map":
/// one screenshot changes the player marker, the trail, the group's marks, the raid phase and
/// the store's revision inside a single turn, each of them on the UI thread and each asking for a
/// rebuild. Measured on Customs that was twelve to twenty-nine scene rebuilds for one event, at
/// about 13 MB each. Deferring even the on-thread callers turns the whole burst into one.
///
/// The flag is cleared before the work runs, for the reason <see cref="CoalescingDispatch"/>
/// gives: a change that lands while the work is running queues one more pass rather than being
/// lost between the work reading its inputs and the flag going down.
///
/// With no dispatcher (a unit-test host, a headless run) the work runs at once, which is what
/// every caller of the thing this replaced saw.
/// </remarks>
public sealed class DeferredDispatch(SynchronizationContext? context, Action work)
{
    private readonly Action _work = work ?? throw new ArgumentNullException(nameof(work));
    private int _pending;

    public void Request()
    {
        if (context is null)
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

    private void Run()
    {
        Volatile.Write(ref _pending, 0);
        _work();
    }
}
