using System.Diagnostics;
using Avalonia.Threading;

namespace TarkovCompanion.App.Services;

/// <summary>
/// <see cref="DeferredDispatch"/> for work that follows a stream of background events: coalesced,
/// run behind queued input, and no sooner than <see cref="Spacing"/> after it last started.
/// </summary>
/// <remarks>
/// [#453] The Raid map rebuilds its scene whenever the raid or the group changes. With three
/// squadmates sharing, the relay answered about three times a second, and each answer asked for a
/// rebuild at the default priority, which is above input: on Clayton's PC (build 2.0.1353) the
/// window then did not take a click for 5 to 237 seconds at a time. Posting at
/// <see cref="DispatcherPriority.Background"/> lets every queued click and key go first, and the
/// spacing puts a ceiling on how much of the interface thread a busy group can have, however fast
/// the events come. The first request after a quiet spell still runs on the next turn.
///
/// With no Avalonia dispatcher (a unit-test host, a headless run) the work runs at once, as it
/// does for <see cref="DeferredDispatch"/>.
/// </remarks>
public sealed class PacedDispatch
{
    /// <summary>The least time between two runs' starts while requests keep coming.</summary>
    public static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(250);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly Action _work;
    private readonly Action<Action>? _post;
    private readonly Action<TimeSpan, Action> _after;
    private readonly Func<TimeSpan> _now;
    private readonly TimeSpan _spacing;
    private TimeSpan? _lastStarted;
    private int _pending;

    /// <param name="post">Queues work for the interface thread; null runs every request inline.</param>
    /// <param name="after">Runs work on the interface thread after a delay; called on that thread.</param>
    internal PacedDispatch(Action work, Action<Action>? post, Action<TimeSpan, Action> after, Func<TimeSpan> now, TimeSpan spacing)
    {
        _work = work ?? throw new ArgumentNullException(nameof(work));
        _post = post;
        _after = after ?? throw new ArgumentNullException(nameof(after));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _spacing = spacing;
    }

    /// <summary>Paced on the Avalonia dispatcher when <paramref name="context"/> is Avalonia's, otherwise inline.</summary>
    public static PacedDispatch ForInterfaceThread(SynchronizationContext? context, Action work) => new(
        work,
        UiThreadPost.IsAvalonia(context) ? static run => Dispatcher.UIThread.Post(run, DispatcherPriority.Background) : null,
        static (delay, run) => DispatcherTimer.RunOnce(run, delay, DispatcherPriority.Background),
        static () => Clock.Elapsed,
        Spacing);

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

        _post(RunOrWait);
    }

    /// <summary>On the interface thread: now, or once the spacing since the last run has passed.</summary>
    private void RunOrWait()
    {
        var wait = _lastStarted is { } last ? _spacing - (_now() - last) : TimeSpan.Zero;
        if (wait > TimeSpan.Zero)
        {
            _after(wait, Run);
            return;
        }

        Run();
    }

    private void Run()
    {
        _lastStarted = _now();
        Volatile.Write(ref _pending, 0);
        _work();
    }
}
