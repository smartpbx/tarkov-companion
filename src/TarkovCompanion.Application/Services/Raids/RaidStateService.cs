using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>A raid state whose transitions can be worked out privately and made current later.</summary>
/// <remarks>
/// The durable raid-history path must not change the raid everyone reads until the record of
/// that change has been accepted. Applying evidence to the live state first, and then failing
/// to store it, left the state service holding a raid the record never heard of — the next
/// observation built on it, and its start was never written.
/// </remarks>
public interface IStagedRaidStateService : IRaidStateService
{
    /// <summary>A private working copy that starts from <see cref="IRaidStateService.Current"/>.</summary>
    IRaidStateService Stage();

    /// <summary>Makes a working copy from <see cref="Stage"/> the current state.</summary>
    /// <remarks>The caller serializes transitions; a commit replaces whatever is current.</remarks>
    void Commit(IRaidStateService stage);
}

public sealed class RaidStateService(bool developerMode = false) : IStagedRaidStateService
{
    private readonly bool _developerMode = developerMode;

    private readonly RaidStateService? _origin;

    private RaidStateService(RaidStateService origin)
        : this(origin._developerMode)
    {
        Current = origin.Current;
        _origin = origin;
    }

    /// <inheritdoc />
    public IRaidStateService Stage() => new RaidStateService(this);

    /// <inheritdoc />
    public void Commit(IRaidStateService stage)
    {
        if (stage is not RaidStateService staged || !ReferenceEquals(staged._origin, this))
        {
            throw new ArgumentException("Only a working copy staged from this raid state can be committed.", nameof(stage));
        }

        Current = staged.Current;
    }

    /// <summary>[#799] Moves every held wall time by a clock jump; see <see cref="RaidSnapshotClockShift"/>.</summary>
    public RaidSnapshot RebaseClock(TimeSpan jump)
    {
        Current = RaidSnapshotClockShift.Shift(Current, jump);
        return Current;
    }

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

        // The end of some other raid is not the end of this one. Both ids are the game's own, so
        // a mismatch is a fact rather than a guess; without either id the end is believed as before.
        if (suggested == RaidLifecycleState.PostRaid
            && Current.State == RaidLifecycleState.InRaid
            && RaidIdentity.DifferentRaid(Current.RaidKey, evidence.RaidKey))
        {
            return Current;
        }

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
        //
        // And the game's own short id outranks both (#568). The same id is the same raid, however
        // the line arrived: a reconnect after the game died writes it into a new log folder, and
        // that is the raid continuing, not another one. A different id is another raid, whatever
        // this still believes about the last one, because the game process can die mid-raid and
        // then no end is ever written. One such raid used to hold every raid after it.
        var sameRaidByKey = RaidIdentity.SameRaid(Current.RaidKey, evidence.RaidKey);
        var anotherRaid = Current.State == RaidLifecycleState.InRaid
            && suggested == RaidLifecycleState.InRaid
            && RaidIdentity.IsAnotherRaid(Current, evidence);
        var repeatOfThisRaid = sameRaidByKey
            || (evidence.EventId is { Length: > 0 }
                && string.Equals(evidence.EventId, Current.StartedByEventId, StringComparison.Ordinal));
        var enteringRaid = targetState == RaidLifecycleState.InRaid
            && (Current.State != RaidLifecycleState.InRaid
                || (evidence.StartsNewRaid && !repeatOfThisRaid)
                || anotherRaid);
        var clearingRaid = targetState == RaidLifecycleState.Menu;
        var isManual = evidence.Kind == RaidEvidenceKind.ManualOverride && evidence.MapId is not null
            ? true
            : enteringNewRaid || clearingRaid
                ? false
                : Current.IsManualMapOverride;
        // A screenshot gap wide enough to start a new raid (see MaximumPositionGap) carries no
        // map of its own, and letting the fallback keep the old raid's map would show a raid on
        // a map it was never confirmed to be on. Log evidence that starts a new raid this way
        // always carries its own MapId, so it never reaches the fallback and is unaffected.
        var mapId = Current.IsManualMapOverride && evidence.Kind != RaidEvidenceKind.ManualOverride && !enteringNewRaid
            ? Current.MapId
            : evidence.MapId ?? (clearingRaid || anotherRaid || (evidence.StartsNewRaid && !repeatOfThisRaid) ? null : Current.MapId);
        // Lines from a later launch of the game than the raid's own are not the raid doing
        // anything, unless they are the raid itself coming back (a reconnect carries its id).
        var isThisRaidsActivity = !RaidIdentity.IsLaterSession(Current.LogSession, evidence.LogSession) || sameRaidByKey;

        Current = Current with
        {
            RaidId = enteringRaid ? Guid.NewGuid() : clearingRaid || enteringNewRaid ? null : Current.RaidId,
            StartedByEventId = enteringRaid
                ? evidence.EventId
                : clearingRaid || enteringNewRaid ? null : Current.StartedByEventId,
            RaidKey = enteringRaid
                ? evidence.RaidKey
                : clearingRaid || enteringNewRaid ? null : Current.RaidKey ?? evidence.RaidKey,
            LogSession = enteringRaid
                ? evidence.LogSession
                : clearingRaid || enteringNewRaid
                    ? null
                    : isThisRaidsActivity ? evidence.LogSession ?? Current.LogSession : Current.LogSession,
            LastActivityUtc = enteringRaid
                ? observedUtc
                : clearingRaid || enteringNewRaid
                    ? null
                    : isThisRaidsActivity && Current.State == RaidLifecycleState.InRaid && !evidence.EndsUnreported
                        ? LaterOf(Current.LastActivityUtc, observedUtc)
                        : Current.LastActivityUtc,
            State = targetState,
            MapId = mapId,
            StartedUtc = enteringRaid
                ? evidence.RaidStartedUtc ?? observedUtc
                : clearingRaid || enteringNewRaid ? null : Current.StartedUtc,
            // Shown to the player as how recently the raid was seen, so it never runs
            // backwards even when the clock behind it does.
            UpdatedUtc = Later(observedUtc),
            Confidence = evidence.Confidence,
            LastKnownPosition = enteringRaid || enteringNewRaid || clearingRaid ? null : Current.LastKnownPosition,
            // The trail belongs to the raid it was walked in, so a new one starts empty.
            PositionTrail = enteringRaid || enteringNewRaid || clearingRaid ? [] : Current.PositionTrail,
            // Belongs to the raid it was scanned in, exactly like the trail. This used to be
            // cleared only on enteringNewRaid, not enteringRaid, so a raid confirmed to have
            // begun directly (an extract-list bootstrap, never having passed through
            // LoadingRaid) inherited the extract scan of whatever raid the player was in
            // before: a leftover "PMC exfil confirmed" line carried into a scav run.
            ActiveExtracts = enteringRaid || enteringNewRaid || clearingRaid ? [] : Current.ActiveExtracts,
            // A clock belongs to the raid it was read in, exactly like the trail.
            RaidClock = enteringRaid || enteringNewRaid || clearingRaid ? null : Current.RaidClock,
            RaidClockReadUtc = enteringRaid || enteringNewRaid || clearingRaid ? null : Current.RaidClockReadUtc,
            // And so does what the display said. A bar read in the last raid describes a body
            // that raid is over for.
            Hud = enteringRaid || enteringNewRaid || clearingRaid ? null : Current.Hud,
            // These describe one reading of one screen and are otherwise only ever replaced,
            // never cleared, by ApplyExtracts -- so without this a raid that began before the
            // player rescanned the extract list kept reading the previous raid's unmatched
            // lines and transit offers.
            ExtractLinesNotMatched = enteringRaid || enteringNewRaid || clearingRaid ? [] : Current.ExtractLinesNotMatched,
            Transits = enteringRaid || enteringNewRaid || clearingRaid ? [] : Current.Transits,
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
    /// How long a gap between two screenshots can be before they cannot be the same raid.
    /// </summary>
    /// <remarks>
    /// Without a log line to say where one raid ends and the next begins, screenshots alone
    /// have to. This reuses <see cref="RaidResume"/>'s own bound on how long a raid can run,
    /// because a gap wider than that cannot be a pause inside one raid. Without it, a
    /// companion that cannot read the game's logs never leaves <see cref="RaidLifecycleState.InRaid"/>
    /// on its own, so every later screenshot only ever extended the trail it already had —
    /// including the screenshot backlog replayed on startup, which joined every raid in it,
    /// however many days apart, into one walk.
    /// </remarks>
    private static readonly TimeSpan MaximumPositionGap = RaidResume.LongestRaid + RaidResume.Margin;

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

        var gapFromLast = Current.LastKnownPosition is { } last
            ? observedUtc - last.Timestamp.ToUniversalTime()
            : (TimeSpan?)null;
        if (Current.State != RaidLifecycleState.InRaid || gapFromLast > MaximumPositionGap)
        {
            Apply(new RaidEvidence(
                RaidEvidenceKind.ScreenshotFilename,
                observedUtc,
                null,
                RaidLifecycleState.InRaid,
                new Confidence(0.80),
                "A normal screenshot filename provides last-known raid position evidence.")
            {
                // Set even when a raid is already running: without it, a gap this wide while
                // still InRaid would not requalify as enteringRaid below, and the new raid
                // would inherit the old one's id, start time and trail instead of starting
                // its own.
                StartsNewRaid = true,
            });
        }

        Current = Current with
        {
            LastKnownPosition = position,
            PositionTrail = Extend(Current.PositionTrail, position),
            LastActivityUtc = LaterOf(Current.LastActivityUtc, observedUtc),
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

        // Bootstrapping from this screen proves only that a raid is running, not when it began:
        // unlike a log confirmation line, an extract-list observation carries no start time. The
        // Apply below still stamps StartedUtc = observedUtc on the enteringRaid transition, which
        // is corrected a few lines down whenever this same screen also gives a real clock
        // reading. Without one, that guess reads as "the raid just began", and RaidTimer counts
        // down from the map's full length instead of admitting the start time is unknown — the
        // defect a player photographing the extract list mid-raid actually saw.
        var bootstrapping = Current.State != RaidLifecycleState.InRaid;
        if (bootstrapping)
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
            LastActivityUtc = LaterOf(Current.LastActivityUtc, observedUtc),
            Confidence = new Confidence(Math.Max(Current.Confidence.Value, 0.85)),
            // A start time this call itself invented and cannot correct with a real reading is
            // worse than admitting it is unknown: see the remark above.
            StartedUtc = bootstrapping && raidClock is null ? null : Current.StartedUtc,
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
    private static DateTimeOffset LaterOf(DateTimeOffset? known, DateTimeOffset observedUtc) =>
        known is { } value && value > observedUtc ? value : observedUtc;

    private DateTimeOffset Later(DateTimeOffset observedUtc) =>
        observedUtc > Current.UpdatedUtc ? observedUtc : Current.UpdatedUtc;
}
