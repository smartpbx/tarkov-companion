using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.RaidPerfHarness;

/// <summary>What one kind of event cost each time it arrived.</summary>
internal sealed class EventProbe(string name)
{
    public string Name { get; } = name;

    /// <summary>Bytes allocated anywhere in the process from publishing the event until the UI settled.</summary>
    public Samples AllocatedBytes { get; } = new();

    /// <summary>How long the publishing thread was held by it.</summary>
    public Samples PublishMs { get; } = new();

    /// <summary>How long the UI thread then spent running jobs and drawing before it went quiet.</summary>
    public Samples UiMs { get; } = new();

    public Samples Rebuilds { get; } = new();

    /// <summary>The share of <see cref="UiMs"/> spent running dispatcher jobs (bindings, layout, scene present).</summary>
    public Samples JobsMs { get; } = new();

    /// <summary>The share spent drawing the frame afterwards.</summary>
    public Samples FrameMs { get; } = new();

    /// <summary>How many of the plan's marker, line and label view models were new after the event.</summary>
    public Samples NewViewModels { get; } = new();
}

/// <summary>
/// Feeds a running raid into the real services at the real cadence: the player's screenshots,
/// the log lines a tailer turns into evidence, and the group's exchange with the relay.
/// </summary>
/// <remarks>
/// Every event is published from a thread-pool thread, because that is where the application's
/// own publishers run (the observation loop, the group worker, the outbox pump); publishing from
/// the UI thread would hide anything that only breaks or only costs when it is not there. Nothing
/// is sent to the game or to a relay: the exchange is the snapshot the group service would have
/// published after a round trip, put through the same store.
///
/// Cadences are in raid seconds and divided by <c>accelerate</c> to get wall time, so a stress run
/// can deliver a 40-minute raid's worth of events in a fraction of it. The clock the application
/// keeps for itself (the one-second DispatcherTimer) does not speed up, which is why an
/// accelerated run answers "what accumulates" and only a real-time one answers "how busy".
/// </remarks>
internal sealed class RaidDriver
{
    public const double ExchangeSeconds = 5;
    public const double LogSeconds = 3;
    public const double ScreenshotSeconds = 30;

    private readonly HarnessHost _host;
    private readonly RaidScript _script;
    private readonly RaidActivityCoordinator _coordinator;
    private readonly IRuntimeStateStore _store;
    private readonly Random _random = new(20260918);
    private readonly int _members;
    private readonly double _accelerate;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double[] _nextShot;
    private double _nextExchange;
    private double _nextLog;
    private int _ordinal;
    private int _rebuilds;

    public RaidDriver(HarnessHost host, RaidScript script, int members, double accelerate)
    {
        _host = host;
        _script = script;
        _members = Math.Max(1, members);
        _accelerate = accelerate;
        _coordinator = host.Services.GetRequiredService<RaidActivityCoordinator>();
        _store = host.Services.GetRequiredService<IRuntimeStateStore>();
        _nextShot = [.. Enumerable.Range(0, _members).Select(who => who * (ScreenshotSeconds / _members) / accelerate)];
        host.Raid.SceneRebuilt += (_, _) => Interlocked.Increment(ref _rebuilds);
        Exchange = new("group exchange (5 s)");
        Screenshot = new("screenshot (30 s)");
        Log = new("log evidence (3 s)");
    }

    public EventProbe Exchange { get; }

    public EventProbe Screenshot { get; }

    public EventProbe Log { get; }

    public int Rebuilds => Volatile.Read(ref _rebuilds);

    public double RaidSeconds => _clock.Elapsed.TotalSeconds * _accelerate;

    /// <summary>Enters the raid the way the log does, and gives the map a first position.</summary>
    public void Begin()
    {
        var now = DateTimeOffset.UtcNow;
        Run(() => _coordinator.ApplyEvidenceAsync(
            new RaidEvidence(RaidEvidenceKind.LogLine, now, _script.MapId, RaidLifecycleState.InRaid, new(0.95), "Raid started")
            {
                Side = "PMC",
            },
            CancellationToken.None));
        Settle();
    }

    /// <summary>Fires whatever is due. Returns how many events it delivered.</summary>
    public int Step(bool settle = true)
    {
        var wall = _clock.Elapsed.TotalSeconds;
        var fired = 0;
        for (var who = 0; who < _members; who++)
        {
            if (wall < _nextShot[who])
            {
                continue;
            }

            _nextShot[who] += ScreenshotSeconds / _accelerate;
            fired++;
            var ordinal = ++_ordinal;
            var shot = _script.Shot(who, RaidSeconds, DateTimeOffset.UtcNow, ordinal);
            _script.Remember(who, shot);
            if (who == 0)
            {
                Measure(Screenshot, settle, () =>
                {
                    Run(() => _coordinator.ApplyPositionAsync(shot, CancellationToken.None));
                    // What the observation service does the moment a name arrives.
                    Run(() =>
                    {
                        _store.Update(current => current with { RecentScreenshotNames = [$"####-##-##[##-##]_{ordinal}.png", .. current.RecentScreenshotNames.Take(2)] });
                        return Task.CompletedTask;
                    });
                });
            }
        }

        if (wall >= _nextLog)
        {
            _nextLog += LogSeconds / _accelerate;
            fired++;
            Measure(Log, settle, () => Run(() => _coordinator.ApplyEvidenceAsync(
                new RaidEvidence(RaidEvidenceKind.LogLine, DateTimeOffset.UtcNow, _script.MapId, RaidLifecycleState.InRaid, new(0.9), "log line")
                {
                    Side = "PMC",
                },
                CancellationToken.None)));
        }

        if (wall >= _nextExchange)
        {
            _nextExchange += ExchangeSeconds / _accelerate;
            fired++;
            Measure(Exchange, settle, () =>
            {
                var group = _script.Exchange(DateTimeOffset.UtcNow, RaidSeconds, _random);
                Run(() =>
                {
                    _store.Update(current => current with { Group = group });
                    return Task.CompletedTask;
                });
            });
        }

        return fired;
    }

    /// <summary>Makes the next <see cref="Step"/> deliver this kind of event, whatever the cadence says.</summary>
    public void MakeDue(string kind)
    {
        switch (kind)
        {
            case "exchange":
                _nextExchange = 0;
                break;
            case "log":
                _nextLog = 0;
                break;
            case "screenshot":
                _nextShot[0] = 0;
                break;
        }
    }

    /// <summary>Runs the dispatcher until the UI has nothing left to do about the last event.</summary>
    public double Settle()
    {
        var ui = Stopwatch.StartNew();
        ui.Reset();
        var quiet = 0;
        var seen = Rebuilds;
        var deadline = Stopwatch.StartNew();
        while (quiet < 3 && deadline.ElapsedMilliseconds < 3000)
        {
            ui.Start();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            ui.Stop();
            if (Rebuilds == seen)
            {
                quiet++;
            }
            else
            {
                quiet = 0;
                seen = Rebuilds;
            }

            Thread.Sleep(2);
        }

        return ui.Elapsed.TotalMilliseconds;
    }

    private void Measure(EventProbe probe, bool settle, Action publish)
    {
        var allocated = GC.GetTotalAllocatedBytes(false);
        var rebuilds = Rebuilds;
        var beforeObjects = ViewModelIdentities();
        var watch = Stopwatch.StartNew();
        publish();
        var published = watch.Elapsed.TotalMilliseconds;
        if (!settle)
        {
            // A pan in progress: the event lands and the UI deals with it in its own time, which
            // is exactly the interference being measured, so nothing here waits for it.
            probe.PublishMs.Add(published);
            return;
        }

        var jobs = Settle();
        var ui = jobs;
        var frame = 0.0;
        // The compositor only draws what changed. A scene that was presented is a change, so it
        // is drawn; an event that left the scene alone costs no frame, which is the point.
        if (Rebuilds != rebuilds)
        {
            frame = _host.Frame();
            ui += frame;
        }

        probe.NewViewModels.Add(ViewModelIdentities().Count(item => !beforeObjects.Contains(item)));
        probe.JobsMs.Add(jobs);
        probe.FrameMs.Add(frame);

        probe.AllocatedBytes.Add(GC.GetTotalAllocatedBytes(false) - allocated);
        probe.PublishMs.Add(published);
        probe.UiMs.Add(ui);
        probe.Rebuilds.Add(Rebuilds - rebuilds);
    }

    private HashSet<object> ViewModelIdentities()
    {
        var renderer = _host.Raid.Renderer;
        var set = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (renderer is null)
        {
            return set;
        }

        foreach (var item in renderer.SpatialObjects)
        {
            set.Add(item);
        }

        foreach (var item in renderer.GeometryObjects)
        {
            set.Add(item);
        }

        foreach (var item in renderer.LabelObjects)
        {
            set.Add(item);
        }

        return set;
    }

    /// <summary>Runs on a pool thread and waits, so the caller is held exactly as long as the publisher was.</summary>
    private static void Run(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

    private static void Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();
}
