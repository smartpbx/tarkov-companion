using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Recognition;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.Application.Services.Sound;

/// <summary>
/// Turns what the companion already knows into cues (#712 T5): the situation's raid clock and
/// phase, the relay's squad marks, and each finished scan. Each trigger fires once per event.
/// </summary>
/// <remarks>
/// <para>
/// The raid clock is a count (ADR 0022: started plus the map's length), so the deadline is only
/// ever a tone, never a spoken number that could be taken for the game's own clock. The squad cue
/// is for marks made by squadmates' own companions; nothing is said about anyone else.
/// </para>
/// <para>
/// Marks and scans already present when sound starts are remembered, not played: switching the
/// app on mid-raid should not replay the last ten minutes.
/// </para>
/// </remarks>
public sealed class SoundCueTriggers : IDisposable
{
    /// <summary>When "time is running out" sounds, on the counted clock.</summary>
    public static readonly TimeSpan ApproachingAt = TimeSpan.FromMinutes(5);

    /// <summary>How often the clock is looked at between situation changes.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly ISoundCues _sound;
    private readonly ISoundLines _lines;
    private readonly TimeProvider _time;
    private readonly SituationService? _situation;
    private readonly IRuntimeStateStore? _runtime;
    private readonly LatestScanResultPublisher? _scans;
    private readonly Func<string?> _selfName;
    private readonly Func<string, CancellationToken, Task<string?>>? _shortName;
    private readonly ITimer? _timer;
    private readonly Lock _gate = new();
    private readonly HashSet<long> _seenMarks = [];
    private readonly HashSet<Guid> _seenScans = [];
    private Guid? _approachingRaid;
    private Guid? _reachedRaid;
    private Guid? _askedRaid;
    private bool _marksSeeded;

    public SoundCueTriggers(
        ISoundCues sound,
        ISoundLines lines,
        TimeProvider? time = null,
        SituationService? situation = null,
        IRuntimeStateStore? runtime = null,
        LatestScanResultPublisher? scans = null,
        Func<string?>? selfName = null,
        Func<string, CancellationToken, Task<string?>>? shortName = null,
        bool startTimer = true)
    {
        _sound = sound ?? throw new ArgumentNullException(nameof(sound));
        _lines = lines ?? throw new ArgumentNullException(nameof(lines));
        _time = time ?? TimeProvider.System;
        _situation = situation;
        _runtime = runtime;
        _scans = scans;
        _selfName = selfName ?? (() => null);
        _shortName = shortName;
        if (situation is not null)
        {
            SeedRaid(situation.Current);
            situation.Changed += SituationChanged;
            if (startTimer)
            {
                _timer = _time.CreateTimer(_ => CheckClock(situation.Current, _time.GetUtcNow()), null, Tick, Tick);
            }
        }

        if (runtime is not null)
        {
            ObserveGroup(runtime.Current.Group);
            runtime.Changed += RuntimeChanged;
        }

        if (scans is not null)
        {
            if (scans.Current is { } current)
            {
                _seenScans.Add(current.ScanId);
            }

            scans.Published += ScanPublished;
        }
    }

    /// <summary>The deadline and outcome cues for one situation at <paramref name="nowUtc"/>.</summary>
    public void CheckClock(Situation situation, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(situation);
        SoundCue? cue = null;
        lock (_gate)
        {
            if (situation.RaidId is { } raid)
            {
                if (situation.Phase.Value == SituationPhase.InRaid
                    && situation.Clock?.RemainingAt(nowUtc) is { } left
                    && situation.Clock.Basis is SituationClockBasis.Counted or SituationClockBasis.Observed)
                {
                    if (left <= TimeSpan.Zero && _reachedRaid != raid)
                    {
                        _reachedRaid = raid;
                        _approachingRaid = raid;
                        cue = SoundCue.ExtractReached;
                    }
                    else if (left > TimeSpan.Zero && left <= ApproachingAt && _approachingRaid != raid)
                    {
                        _approachingRaid = raid;
                        cue = SoundCue.ExtractApproaching;
                    }
                }
                else if (situation.Phase.Value == SituationPhase.PostRaid && situation.Outcome is null && _askedRaid != raid)
                {
                    _askedRaid = raid;
                    cue = SoundCue.OutcomeQuestion;
                }
            }
        }

        if (cue is { } play)
        {
            _sound.Play(play);
        }
    }

    /// <summary>One cue for any number of new marks from squadmates in one relay update.</summary>
    public void ObserveGroup(GroupSnapshot group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var fresh = false;
        lock (_gate)
        {
            if (!group.IsSharing)
            {
                return;
            }

            var self = _selfName();
            var ids = group.Pings.Select(ping => (ping.Id, ping.By))
                .Concat(group.Waypoints.Select(waypoint => (waypoint.Id, waypoint.By)));
            foreach (var (id, by) in ids)
            {
                if (_seenMarks.Add(id) && _marksSeeded
                    && !string.Equals(by?.Trim(), self?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    fresh = true;
                }
            }

            _marksSeeded = true;
        }

        if (fresh)
        {
            _sound.Play(SoundCue.SquadPing);
        }
    }

    /// <summary>Speaks (or ticks) once per finished loot scan.</summary>
    public async Task ObserveScanAsync(ScanOutcome scan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        lock (_gate)
        {
            if (!_seenScans.Add(scan.ScanId))
            {
                return;
            }
        }

        string? shortName = null;
        if (scan.Recognition.Selected is { } item && _shortName is not null)
        {
            try
            {
                shortName = await _shortName(item.CanonicalId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
            {
                // The full name is still a name; a catalog that cannot shorten it costs nothing.
            }
        }

        if (LootVerdictLine.From(scan, shortName) is { } line)
        {
            _sound.Play(SoundCue.LootVerdict, _lines.LootVerdict(line));
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        if (_situation is not null)
        {
            _situation.Changed -= SituationChanged;
        }

        if (_runtime is not null)
        {
            _runtime.Changed -= RuntimeChanged;
        }

        if (_scans is not null)
        {
            _scans.Published -= ScanPublished;
        }
    }

    /// <summary>A raid already under way, or already asked about, when sound starts plays nothing for what has passed.</summary>
    private void SeedRaid(Situation situation)
    {
        if (situation.RaidId is not { } raid)
        {
            return;
        }

        if (situation.Phase.Value == SituationPhase.PostRaid)
        {
            _askedRaid = raid;
        }

        var left = situation.Clock?.RemainingAt(_time.GetUtcNow());
        if (left is { } remaining && remaining <= ApproachingAt)
        {
            _approachingRaid = raid;
            if (remaining <= TimeSpan.Zero)
            {
                _reachedRaid = raid;
            }
        }
    }

    private void SituationChanged(object? sender, SituationChangedEventArgs eventArgs) =>
        CheckClock(eventArgs.Current, _time.GetUtcNow());

    private void RuntimeChanged(object? sender, EventArgs eventArgs)
    {
        if (_runtime is { } runtime)
        {
            ObserveGroup(runtime.Current.Group);
        }
    }

    private void ScanPublished(ScanOutcome scan) => _ = ObserveScanSafelyAsync(scan);

    private async Task ObserveScanSafelyAsync(ScanOutcome scan)
    {
        try
        {
            await ObserveScanAsync(scan).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Sound is a courtesy; a failure here must never reach the scan pipeline that raised the event.
        }
    }
}
