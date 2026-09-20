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
public sealed class DeferredDispatch
{
    private readonly Action _work;
    private readonly Action<Action>? _post;
    private int _pending;

    public DeferredDispatch(SynchronizationContext? context, Action work)
        : this(work, context is null ? null : run => context.Post(_ => run(), null))
    {
    }

    private DeferredDispatch(Action work, Action<Action>? post)
    {
        _work = work ?? throw new ArgumentNullException(nameof(work));
        _post = post;
    }

    /// <summary>
    /// The same coalescing, posted by a delegate rather than through a
    /// <see cref="SynchronizationContext"/>.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 45] For a caller that needs a priority the ambient context does not post
    /// at — the quest search runs behind queued input, see <see cref="UiThreadPost"/> — and so
    /// that wanting one does not mean writing a <see cref="SynchronizationContext"/> subclass,
    /// which reads like something installed on the thread even when it never is.
    /// A <see langword="null"/> <paramref name="post"/> runs the work inline, exactly as a
    /// <see langword="null"/> context does.
    /// </remarks>
    public static DeferredDispatch Posting(Action<Action>? post, Action work) => new(work, post);

    public void Request()
    {
        if (_post is null)
        {
            _work();
            return;
        }

        if (Interlocked.Exchange(ref _pending, 1) != 0)
        {
            return;
        }

        _post(Run);
    }

    private void Run()
    {
        Volatile.Write(ref _pending, 0);
        _work();
    }
}
