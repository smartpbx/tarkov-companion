using System.Diagnostics;
using System.Globalization;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Notices when the interface thread stops answering, and says so in the crash log.
/// </summary>
/// <remarks>
/// "The app freezes" (#453) was reported twice and reproduced never, because a frozen window
/// leaves nothing behind: no exception, no log line, and the thread that could describe the
/// problem is the one that is stuck. This watches from outside. A background thread posts a
/// heartbeat to the dispatcher at the priority input is delivered at; if the heartbeat has not run
/// after <see cref="Threshold"/>, the dispatcher has not delivered a click for that long either,
/// and one record goes to <see cref="CrashLog"/> naming the route, how long it has been, and what
/// <see cref="UiActivity"/> last saw start. A second record is written when the heartbeat finally
/// runs, with the full length of the freeze. A freeze that never ends leaves the first record, and
/// the next launch's "previous run died" beside it.
///
/// It is built not to become the problem. It owns one thread that sleeps between checks, holds no
/// lock the interface thread takes, never waits on the dispatcher, and catches everything: a
/// watchdog that cannot post or cannot write stops watching rather than taking the process with
/// it. One heartbeat is outstanding at a time, so a long freeze queues one delegate, not hundreds.
/// </remarks>
public sealed class UiHangWatchdog : IDisposable
{
    /// <summary>How long the dispatcher may leave a heartbeat unanswered before it is a hang.</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(5);

    private readonly Action<Action> _post;
    private readonly Func<string> _describe;
    private readonly Action<string, string> _write;
    private readonly TimeSpan _interval;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private long _postedTimestamp;
    private long _servicedTimestamp;
    private int _outstanding;
    private int _disposed;

    /// <param name="post">Queues work for the interface thread without waiting for it.</param>
    /// <param name="describe">What the interface was last doing; called from the watchdog's thread.</param>
    /// <param name="write">Where a record goes: a category and a message.</param>
    public UiHangWatchdog(
        Action<Action> post,
        Func<string> describe,
        Action<string, string> write,
        TimeSpan? threshold = null,
        TimeSpan? interval = null)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _describe = describe ?? throw new ArgumentNullException(nameof(describe));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        Threshold = threshold ?? DefaultThreshold;
        _interval = interval ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Threshold, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_interval, TimeSpan.Zero);
        _thread = new Thread(Watch)
        {
            IsBackground = true,
            Name = "tarkov-companion-ui-hang-watchdog",
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public TimeSpan Threshold { get; }

    /// <summary>How many hangs have been recorded since this started.</summary>
    public int HangsRecorded { get; private set; }

    public void Start() => _thread.Start();

    /// <summary>The watchdog the running application uses: the real dispatcher, the real log.</summary>
    /// <remarks>
    /// Posted at input priority, deliberately. Await continuations run above it, so a dispatcher
    /// kept busy by a stream of them would answer a heartbeat posted among them at once and still
    /// not deliver a click for ten seconds. A heartbeat that waits where input waits measures the
    /// thing the player experiences.
    /// </remarks>
    public static UiHangWatchdog ForApplication(TimeSpan? threshold = null, TimeSpan? interval = null) => new(
        work => Avalonia.Threading.Dispatcher.UIThread.Post(work, Avalonia.Threading.DispatcherPriority.Input),
        UiActivity.DescribeWithSteps,
        static (category, message) =>
        {
            CrashLog.Write(category, message);
            // A breadcrumb as well, so that if the freeze ends with the process being killed, the
            // next launch's account of the previous run ends with "it was frozen".
            CrashBreadcrumbs.Drop(category, message);
        },
        threshold,
        interval);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Set();
    }

    private void Watch()
    {
        var reported = false;
        var lastCheck = Stopwatch.GetTimestamp();
        try
        {
            while (!_stop.Wait(_interval))
            {
                // A check that arrives seconds late means this thread was not running either:
                // the machine slept, or the whole process was starved. The dispatcher was not
                // singled out, so the clock on any outstanding heartbeat starts again rather than
                // reporting a lid closed overnight as an eight-hour freeze.
                var sinceLastCheck = Stopwatch.GetElapsedTime(lastCheck);
                lastCheck = Stopwatch.GetTimestamp();
                if (sinceLastCheck > _interval + _interval + TimeSpan.FromSeconds(2) && !reported)
                {
                    Volatile.Write(ref _postedTimestamp, lastCheck);
                    continue;
                }

                if (Volatile.Read(ref _outstanding) == 0)
                {
                    if (reported)
                    {
                        reported = false;
                        var frozenFor = Stopwatch.GetElapsedTime(
                            Volatile.Read(ref _postedTimestamp),
                            Volatile.Read(ref _servicedTimestamp));
                        _write("ui-hang-recovered", string.Create(
                            CultureInfo.InvariantCulture,
                            $"The interface answered again after {frozenFor.TotalSeconds:0.0} s. Now: {_describe()}"));
                    }

                    Volatile.Write(ref _postedTimestamp, Stopwatch.GetTimestamp());
                    Volatile.Write(ref _outstanding, 1);
                    _post(Serviced);
                    continue;
                }

                var waited = Stopwatch.GetElapsedTime(Volatile.Read(ref _postedTimestamp));
                if (!reported && waited >= Threshold)
                {
                    reported = true;
                    HangsRecorded++;
                    _write("ui-hang", string.Create(
                        CultureInfo.InvariantCulture,
                        $"The interface has not answered for {waited.TotalSeconds:0.0} s. {_describe()}"));
                }
            }
        }
        catch (Exception)
        {
            // Whatever it was — a dispatcher that has shut down, a log that cannot be written —
            // the right response from a diagnostic is to stop, not to be the crash.
        }
    }

    private void Serviced()
    {
        Volatile.Write(ref _servicedTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _outstanding, 0);
    }
}
