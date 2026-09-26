using System.Text.Json;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Recognition;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.Application.Services.Situations;

public sealed class SituationChangedEventArgs(Situation previous, Situation current) : EventArgs
{
    public Situation Previous { get; } = previous;

    public Situation Current { get; } = current;
}

/// <summary>
/// The one situation every V3 surface reads (ADR 0022, epic #712 T1).
/// </summary>
/// <remarks>
/// <para>
/// Folds the runtime snapshot (raid state from the log and screenshots, the log's party, the relay
/// squad, the profile), the four loading markers, the newest scan, the opened objective route and
/// any reported outcome into one immutable <see cref="Situation"/>. The rules are in
/// <see cref="SituationFolder"/>; this class serialises the inputs, keeps the version and the
/// transition log, and publishes changes.
/// </para>
/// <para>
/// <see cref="Situation.Version"/> rises only when something in the situation changed. The raid
/// clock is an anchor rather than a number for exactly this reason, and a periodic refresh, which
/// is what moves the phase on when time alone passes (post-raid back to the menu), publishes
/// nothing when nothing moved.
/// </para>
/// </remarks>
public sealed class SituationService : IObservable<Situation>, IDisposable
{
    /// <summary>How often time alone is allowed to change the situation.</summary>
    public static readonly TimeSpan DefaultRefresh = TimeSpan.FromSeconds(5);

    private const int MaximumTransitions = 64;
    private static readonly JsonSerializerOptions SignatureOptions = new();

    private readonly IRuntimeStateStore? _runtime;
    private readonly TimeProvider _time;
    private readonly ISituationPlaces? _places;
    private readonly LatestScanResultPublisher? _scans;
    private readonly ILogger<SituationService>? _logger;
    private readonly SituationFolder _folder;
    private readonly Lock _gate = new();
    private readonly object _notificationGate = new();
    private readonly List<IObserver<Situation>> _observers = [];
    private readonly List<SituationTransition> _transitions = [];
    private readonly ITimer? _timer;
    private string _signature = string.Empty;
    private bool _disposed;
    private bool _applying;

    public SituationService(
        IRuntimeStateStore? runtime,
        TimeProvider? time = null,
        ISituationPlaces? places = null,
        LatestScanResultPublisher? scans = null,
        ILogger<SituationService>? logger = null,
        TimeSpan? refresh = null)
    {
        _runtime = runtime;
        _time = time ?? TimeProvider.System;
        _places = places;
        _scans = scans;
        _logger = logger;
        _folder = new SituationFolder(places);
        Current = Situation.Initial;
        if (_runtime is not null)
        {
            _runtime.Changed += RuntimeChanged;
            _folder.Observe(_runtime.Current);
        }

        if (_places is not null)
        {
            _places.Changed += PlacesChanged;
        }

        if (_scans is not null)
        {
            _scans.Published += ScanPublished;
        }

        var period = refresh ?? DefaultRefresh;
        if (period > TimeSpan.Zero)
        {
            _timer = _time.CreateTimer(_ => Refresh(), null, period, period);
        }

        Refresh();
    }

    /// <summary>Raised once per new version, in version order, never under the fold's lock.</summary>
    public event EventHandler<SituationChangedEventArgs>? Changed;

    public Situation Current { get; private set; }

    /// <summary>The newest phase changes with their "because", newest last.</summary>
    public IReadOnlyList<SituationTransition> Transitions
    {
        get
        {
            lock (_gate)
            {
                return [.. _transitions];
            }
        }
    }

    public void Observe(RaidPhaseMarker marker) => Apply(folder => folder.Observe(marker));

    public void Observe(ScanOutcome scan) => Apply(folder => folder.Observe(scan));

    public void SetPlan(SituationPlan? plan) => Apply(folder => folder.SetPlan(plan));

    public void ReportOutcome(Guid raidId, SituationFact<SituationOutcome> outcome) =>
        Apply(folder => folder.ReportOutcome(raidId, outcome));

    /// <summary>Moves the held times by a PC clock jump (#891), alongside the raid state's own rebase.</summary>
    public void RebaseClock(TimeSpan jump) => Apply(folder => folder.RebaseClock(jump));

    /// <summary>Folds again with nothing new, so time alone can move the phase on.</summary>
    public void Refresh() => Apply(static _ => { });

    public IDisposable Subscribe(IObserver<Situation> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_notificationGate)
        {
            _observers.Add(observer);
            observer.OnNext(Current);
        }

        return new Unsubscriber(this, observer);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        if (_runtime is not null)
        {
            _runtime.Changed -= RuntimeChanged;
        }

        if (_places is not null)
        {
            _places.Changed -= PlacesChanged;
        }

        if (_scans is not null)
        {
            _scans.Published -= ScanPublished;
        }

        lock (_notificationGate)
        {
            foreach (var observer in _observers.ToArray())
            {
                observer.OnCompleted();
            }

            _observers.Clear();
        }
    }

    private void RuntimeChanged(object? sender, EventArgs e)
    {
        if (_runtime is { } runtime)
        {
            var snapshot = runtime.Current;
            Apply(folder => folder.Observe(snapshot));
        }
    }

    private void PlacesChanged(object? sender, EventArgs e) => Refresh();

    private void ScanPublished(ScanOutcome outcome) => Observe(outcome);

    private void Apply(Action<SituationFolder> input)
    {
        if (_disposed)
        {
            return;
        }

        // One gate orders publication, a second guards the fold: a later input cannot overtake an
        // earlier one's notification, and no handler ever runs while the fold is locked.
        lock (_notificationGate)
        {
            // Same-thread re-entry (a subscriber or a places answer calling back in) only feeds the
            // folder, and the outer call folds again afterwards: one change is never published
            // twice, and a nested change is never published before the one that caused it.
            lock (_gate)
            {
                input(_folder);
            }

            if (_applying)
            {
                return;
            }

            _applying = true;
            try
            {
                while (true)
                {
                    SituationChangedEventArgs? change;
                    lock (_gate)
                    {
                        change = FoldLocked();
                    }

                    if (change is null)
                    {
                        return;
                    }

                    Publish(change);
                }
            }
            finally
            {
                _applying = false;
            }
        }
    }

    private void Publish(SituationChangedEventArgs change)
    {
        try
        {
            Changed?.Invoke(this, change);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger?.LogWarning(exception, "A situation subscriber failed on version {Version}.", change.Current.Version);
        }

        foreach (var observer in _observers.ToArray())
        {
            try
            {
                observer.OnNext(change.Current);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger?.LogWarning(exception, "A situation observer failed on version {Version}.", change.Current.Version);
            }
        }
    }

    private SituationChangedEventArgs? FoldLocked()
    {
        var previous = Current;
        var candidate = _folder.Fold(_time.GetUtcNow(), previous.Version);
        var signature = Signature(candidate);
        if (string.Equals(signature, _signature, StringComparison.Ordinal))
        {
            return null;
        }

        _signature = signature;
        var current = candidate with { Version = previous.Version + 1 };
        Current = current;
        if (current.Phase.Value != previous.Phase.Value)
        {
            var transition = new SituationTransition(
                current.Version,
                current.ComputedUtc,
                previous.Phase.Value,
                current.Phase.Value,
                current.Phase.Because);
            _transitions.Add(transition);
            if (_transitions.Count > MaximumTransitions)
            {
                _transitions.RemoveAt(0);
            }

            _logger?.LogInformation(
                "Situation {Version}: {From} -> {To} because {Because} (source {Source}, confidence {Confidence:0.00}).",
                current.Version,
                previous.Phase.Value,
                current.Phase.Value,
                current.Phase.Because,
                current.Phase.Source,
                current.Phase.Confidence.Value);
        }

        return new(previous, current);
    }

    /// <summary>Everything but the version and the moment of folding, so equal situations compare equal.</summary>
    private static string Signature(Situation situation) =>
        JsonSerializer.Serialize(situation with { Version = 0, ComputedUtc = DateTimeOffset.UnixEpoch }, SignatureOptions);

    private sealed class Unsubscriber(SituationService owner, IObserver<Situation> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._notificationGate)
            {
                owner._observers.Remove(observer);
            }
        }
    }
}
