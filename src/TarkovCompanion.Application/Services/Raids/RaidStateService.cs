using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

public sealed class RaidStateService(bool developerMode = false) : IRaidStateService
{
    private readonly bool _developerMode = developerMode;

    public RaidSnapshot Current { get; private set; } = new(
        null,
        RaidLifecycleState.Unknown,
        null,
        null,
        DateTimeOffset.UnixEpoch,
        Confidence.Unknown,
        null,
        [],
        false);

    public RaidSnapshot Apply(RaidEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Kind == RaidEvidenceKind.Simulator && !_developerMode)
        {
            return Current;
        }

        // Evidence is applied in the order it arrives, and is never refused for being older
        // than the raid clock.
        //
        // It used to be, and that made raid tracking depend on the system clock only ever
        // moving forward. A clock that steps backwards, which a time-zone correction or an
        // NTP resync will do, then silently discarded every piece of raid evidence until real
        // time caught up: on a four-hour step, four hours of raids. Nothing surfaced, because
        // discarding evidence is how the guard was supposed to behave.
        //
        // The guard was protecting against reordering that cannot happen here. Log evidence
        // reaches this from a single sequential loop, so arrival order is the log's own order,
        // and the streams that genuinely can arrive out of order carry their own comparisons:
        // a screenshot is judged against the last screenshot, not against this clock.
        var observedUtc = evidence.ObservedUtc.ToUniversalTime();
        var suggested = evidence.SuggestedState ?? Current.State;

        // A raid does not go back to loading. The game interleaves loading-ish and in-raid-ish
        // lines throughout a raid, and reading them one at a time flipped the state between
        // the two on every line: nine transitions in two seconds was measured on a live
        // machine, five of them inside one second and some 200 microseconds apart. Anything
        // that fires on entering a state fired repeatedly, and the state the raid came to rest
        // in was decided by which line happened to end a buffer rather than by the game.
        //
        // Loading is reachable from not being in a raid, which is the only time it means
        // anything. A genuinely new raid that begins while this still believes the last one is
        // running is corrected by its own confirmation line, which carries the map and is
        // authoritative in a way that a loading marker is not.
        var targetState = suggested == RaidLifecycleState.LoadingRaid
            && Current.State == RaidLifecycleState.InRaid
                ? RaidLifecycleState.InRaid
                : suggested;
        var enteringNewRaid = targetState == RaidLifecycleState.LoadingRaid
            && Current.State != RaidLifecycleState.LoadingRaid;
        // A raid begins either because the state changed into one, or because the game said so
        // while this still believed the previous raid was running. Without the second case, a
        // raid that starts before the last one was seen to end inherits its map, its start time
        // and its trail, which is worse than having no raid at all.
        // The same confirmation arrives twice, because every notification is written into two
        // log files. Beginning a raid on its id rather than on its content means the second
        // copy is recognised as the event that already started this raid, instead of throwing
        // away the identity, start time and trail it just created.
        var repeatOfThisRaid = evidence.EventId is { Length: > 0 }
            && string.Equals(evidence.EventId, Current.StartedByEventId, StringComparison.Ordinal);
        var enteringRaid = targetState == RaidLifecycleState.InRaid
            && (Current.State != RaidLifecycleState.InRaid || (evidence.StartsNewRaid && !repeatOfThisRaid));
        var clearingRaid = targetState == RaidLifecycleState.Menu;
        var isManual = evidence.Kind == RaidEvidenceKind.ManualOverride && evidence.MapId is not null
            ? true
            : enteringNewRaid || clearingRaid
                ? false
                : Current.IsManualMapOverride;
        var mapId = Current.IsManualMapOverride && evidence.Kind != RaidEvidenceKind.ManualOverride && !enteringNewRaid
            ? Current.MapId
            : evidence.MapId ?? (clearingRaid ? null : Current.MapId);

        Current = Current with
        {
            RaidId = enteringRaid ? Guid.NewGuid() : clearingRaid || enteringNewRaid ? null : Current.RaidId,
            StartedByEventId = enteringRaid
                ? evidence.EventId
                : clearingRaid || enteringNewRaid ? null : Current.StartedByEventId,
            State = targetState,
            MapId = mapId,
            StartedUtc = enteringRaid ? observedUtc : clearingRaid || enteringNewRaid ? null : Current.StartedUtc,
            // Shown to the player as how recently the raid was seen, so it never runs
            // backwards even when the clock behind it does.
            UpdatedUtc = Later(observedUtc),
            Confidence = evidence.Confidence,
            LastKnownPosition = enteringRaid || enteringNewRaid || clearingRaid ? null : Current.LastKnownPosition,
            // The trail belongs to the raid it was walked in, so a new one starts empty.
            PositionTrail = enteringRaid || enteringNewRaid || clearingRaid ? [] : Current.PositionTrail,
            ActiveExtracts = enteringNewRaid || clearingRaid ? [] : Current.ActiveExtracts,
            // A clock belongs to the raid it was read in, exactly like the trail.
            RaidClock = enteringRaid || enteringNewRaid || clearingRaid ? null : Current.RaidClock,
            RaidClockReadUtc = enteringRaid || enteringNewRaid || clearingRaid ? null : Current.RaidClockReadUtc,
            // And so does what the display said. A bar read in the last raid describes a body
            // that raid is over for.
            Hud = enteringRaid || enteringNewRaid || clearingRaid ? null : Current.Hud,
            IsManualMapOverride = isManual,
            // A raid keeps the side it started with; evidence that cannot tell does not
            // overwrite what an earlier, better-informed line already established. The basis
            // moves with the value so the two can never describe different things.
            Side = evidence.Side ?? (clearingRaid || enteringNewRaid ? null : Current.Side),
            SideBasis = evidence.Side is not null
                ? evidence.SideBasis
                : clearingRaid || enteringNewRaid ? null : Current.SideBasis,
        };

        return Current;
    }

    /// <summary>
    /// Takes over a raid a previous run of the companion was already recording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This service is memory, so a companion restarted mid-raid recovers the raid from the
    /// log and then gives it a new identity: a new id, a start time of whenever the restart
    /// happened, and an empty trail drawn over screenshots that are already on disk. The row
    /// the previous run opened stays open for ever and History shows it as in progress.
    /// </para>
    /// <para>
    /// Whether this raid is that raid is not a question this service can answer — it is a
    /// question about what was recorded — so the answer is handed in rather than worked out
    /// here. What this does is make the two the same raid.
    /// </para>
    /// <para>
    /// Refused unless a raid is running. Adopting into the menu would attach a finished raid's
    /// identity to no raid at all, and the next one to start would inherit it.
    /// </para>
    /// </remarks>
    public RaidSnapshot Adopt(Guid raidId, DateTimeOffset? startedUtc, IReadOnlyList<ScreenshotPosition> trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        if (Current.State is not (RaidLifecycleState.InRaid or RaidLifecycleState.LoadingRaid))
        {
            return Current;
        }

        Current = Current with
        {
            RaidId = raidId,
            // The recorded start where there is one. A row with no start time is still this
            // raid; it just cannot say when it began, and inventing a time from the restart
            // would date the raid to the moment the companion came back.
            StartedUtc = startedUtc ?? Current.StartedUtc,
            PositionTrail = trail,
            // The last place recorded is where the player is until they photograph themselves
            // again, which is the same claim the trail already makes about every other point.
            LastKnownPosition = trail.Count > 0 ? trail[^1] : Current.LastKnownPosition,
        };
        return Current;
    }

    /// <summary>
    /// Records where the player was, from a screenshot the player chose to take.
    /// </summary>
    /// <remarks>
    /// Ordering is judged against the last position and not against the raid's own clock. A
    /// live raid writes log lines every few seconds, so the raid clock is almost always ahead
    /// of the screenshot that just landed; comparing against it silently discarded every
    /// position taken during a raid, which is the only time positions exist. A screenshot
    /// older than one already recorded is still refused, which is what the guard was for.
    /// </remarks>
    public RaidSnapshot ApplyPosition(ScreenshotPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        var observedUtc = position.Timestamp.ToUniversalTime();
        if (Current.LastKnownPosition is { } previous && observedUtc < previous.Timestamp.ToUniversalTime())
        {
            return Current;
        }

        if (Current.State != RaidLifecycleState.InRaid)
        {
            Apply(new RaidEvidence(
                RaidEvidenceKind.ScreenshotFilename,
                observedUtc,
                null,
                RaidLifecycleState.InRaid,
                new Confidence(0.80),
                "A normal screenshot filename provides last-known raid position evidence."));
        }

        Current = Current with
        {
            LastKnownPosition = position,
            PositionTrail = Extend(Current.PositionTrail, position),
            // The raid clock only ever moves forward. A screenshot that is genuinely older
            // than the last log line records its position without rewinding the raid.
            UpdatedUtc = Later(observedUtc),
            Confidence = new Confidence(Math.Max(Current.Confidence.Value, 0.80)),
        };
        return Current;
    }

    /// <summary>How many points of a raid's trail are kept.</summary>
    /// <remarks>
    /// A player takes a handful of screenshots in a raid, not hundreds, so this is a guard
    /// against something unexpected rather than a limit anyone will meet. The oldest go first,
    /// because where somebody is heading matters more than where they started.
    /// </remarks>
    private const int MaximumTrailPoints = 240;

    private static IReadOnlyList<ScreenshotPosition> Extend(
        IReadOnlyList<ScreenshotPosition> trail,
        ScreenshotPosition position)
    {
        // The same screenshot read twice is one place the player stood, not two.
        if (trail.Count > 0 && string.Equals(trail[^1].Filename, position.Filename, StringComparison.OrdinalIgnoreCase))
        {
            return trail;
        }

        var extended = new List<ScreenshotPosition>(trail) { position };
        return extended.Count > MaximumTrailPoints
            ? extended.Skip(extended.Count - MaximumTrailPoints).ToArray()
            : extended;
    }

    public RaidSnapshot ApplyExtracts(
        IReadOnlyList<ActiveExtract> extracts,
        DateTimeOffset observedUtc,
        TimeSpan? raidClock = null,
        IReadOnlyList<string>? linesNotMatched = null,
        IReadOnlyList<string>? transits = null)
    {
        ArgumentNullException.ThrowIfNull(extracts);
        observedUtc = observedUtc.ToUniversalTime();

        if (Current.State != RaidLifecycleState.InRaid)
        {
            Apply(new RaidEvidence(
                RaidEvidenceKind.ManualOverride,
                observedUtc,
                null,
                RaidLifecycleState.InRaid,
                new Confidence(0.85),
                "An extract-list observation provides raid-state evidence."));
        }

        Current = Current with
        {
            ActiveExtracts = extracts.ToArray(),
            UpdatedUtc = Later(observedUtc),
            Confidence = new Confidence(Math.Max(Current.Confidence.Value, 0.85)),
            // Kept only when this screenshot carried one. A scan that could not read the clock
            // must not erase the last one that could.
            RaidClock = raidClock ?? Current.RaidClock,
            RaidClockReadUtc = raidClock is null ? Current.RaidClockReadUtc : observedUtc,
            // Replaced rather than merged: these describe one reading of one screen, and
            // carrying last screen's leftovers forward would say the scan failed on lines it
            // never saw.
            ExtractLinesNotMatched = linesNotMatched ?? [],
            Transits = transits ?? [],
        };
        return Current;
    }

    /// <summary>
    /// The later of an observation's time and the raid clock, so the clock never rewinds.
    /// </summary>
    /// <remarks>
    /// The raid clock is read by the player as how recently anything was seen. Letting it move
    /// backwards would show a raid updating in the future and then ageing, which is worse than
    /// a clock that pauses. Nothing is discarded to achieve this; only what is displayed is
    /// held steady.
    /// </remarks>
    private DateTimeOffset Later(DateTimeOffset observedUtc) =>
        observedUtc > Current.UpdatedUtc ? observedUtc : Current.UpdatedUtc;
}
