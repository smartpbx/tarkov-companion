using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.Application.Services.Situations;

/// <summary>The objective route the player opened on the Raid map, as the planner ordered it.</summary>
/// <param name="StartLabel">Where the route was measured from ("your screenshot", a spawn).</param>
public sealed record SituationPlan(
    string MapId,
    string StartLabel,
    IReadOnlyList<ObjectiveRouteStep> Steps,
    DateTimeOffset PlannedUtc);

/// <summary>
/// Folds the existing inputs into one <see cref="Situation"/>: the rules of ADR 0022 in one place.
/// </summary>
/// <remarks>
/// <para>
/// Not thread-safe; <see cref="SituationService"/> serialises every call. It holds only what the
/// inputs do not: which raid edge was seen when, the four loading markers, the last scan and plan,
/// and any outcome somebody reported. Everything else is read from the runtime snapshot at fold time,
/// so the raid rules stay in <see cref="RaidStateService"/> and are not repeated here.
/// </para>
/// <para>
/// Order is by arrival, not by time stamp. A PC clock that steps back four hours mid-raid (#891)
/// leaves every stamp after the step earlier than the ones before it, so "is this marker after the
/// raid ended" is answered with an arrival counter.
/// </para>
/// </remarks>
public sealed class SituationFolder(ISituationPlaces? places = null)
{
    /// <summary>How long the post-raid question stays up before the phase falls back to the menu.</summary>
    public static readonly TimeSpan PostRaidHolds = TimeSpan.FromMinutes(10);

    /// <summary>A queue longer than this without a raid is taken as abandoned.</summary>
    public static readonly TimeSpan MatchingHolds = TimeSpan.FromMinutes(15);

    /// <summary>How long a screenshot of a between-raid screen decides the phase.</summary>
    public static readonly TimeSpan ScreenHolds = TimeSpan.FromMinutes(3);

    private const int MaximumMarkers = 32;
    private static readonly string[] CompassPoints = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    private readonly ISituationPlaces? _places = places;
    private readonly List<(RaidPhaseMarker Marker, long Sequence)> _markers = [];
    private readonly Dictionary<Guid, SituationFact<SituationOutcome>> _outcomes = [];
    private long _sequence;
    private ApplicationRuntimeSnapshot? _runtime;
    private Guid? _raidId;
    private long _raidStartSequence = -1;
    private long _endSequence = -1;
    private DateTimeOffset? _endUtc;
    private ScanOutcome? _scan;
    private long _scanSequence = -1;
    private SituationPlan? _plan;

    public void Observe(ApplicationRuntimeSnapshot runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var raid = runtime.Raid;
        var previous = _runtime?.Raid;
        var wasInRaid = previous?.State is RaidLifecycleState.InRaid or RaidLifecycleState.LoadingRaid;
        var isInRaid = raid.State is RaidLifecycleState.InRaid or RaidLifecycleState.LoadingRaid;
        if (wasInRaid && !isInRaid)
        {
            _endSequence = ++_sequence;
            _endUtc = raid.UpdatedUtc;
        }
        else if (isInRaid && raid.RaidId is { } id && id != _raidId)
        {
            // A raid that began while the last was still running ended that one (#568).
            if (previous is { State: RaidLifecycleState.InRaid, RaidId: { } running } && running != id)
            {
                _endSequence = ++_sequence;
                _endUtc = raid.StartedUtc ?? raid.UpdatedUtc;
            }

            _raidStartSequence = ++_sequence;
        }

        _raidId = raid.RaidId ?? _raidId;
        _runtime = runtime;
    }

    public void Observe(RaidPhaseMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        _markers.Add((marker, ++_sequence));
        if (_markers.Count > MaximumMarkers)
        {
            _markers.RemoveAt(0);
        }
    }

    public void Observe(ScanOutcome scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        _scan = scan;
        _scanSequence = ++_sequence;
    }

    public void SetPlan(SituationPlan? plan) => _plan = plan;

    /// <summary>How a raid ended, from the one-tap question or a photographed summary screen.</summary>
    public void ReportOutcome(Guid raidId, SituationFact<SituationOutcome> outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _outcomes[raidId] = outcome;
    }

    /// <summary>Moves every held wall time by a PC clock jump, as <see cref="RaidSnapshotClockShift"/> does for the raid.</summary>
    public void RebaseClock(TimeSpan jump)
    {
        for (var index = 0; index < _markers.Count; index++)
        {
            var (marker, sequence) = _markers[index];
            _markers[index] = (marker with { ObservedUtc = marker.ObservedUtc + jump }, sequence);
        }

        _endUtc += jump;
        if (_scan is { } scan)
        {
            _scan = scan with { ObservedUtc = scan.ObservedUtc + jump };
        }
    }

    public Situation Fold(DateTimeOffset nowUtc, long version)
    {
        var runtime = _runtime;
        var raid = runtime?.Raid;
        var phase = Phase(runtime, nowUtc);
        var inRaidPhase = phase.Value is SituationPhase.Matching or SituationPhase.Loading or SituationPhase.InRaid
            or SituationPhase.PostRaid or SituationPhase.Dead or SituationPhase.Extracted;
        var raidFacts = inRaidPhase && raid is not null;
        var mapId = raidFacts ? raid!.MapId : null;
        var you = phase.Value == SituationPhase.InRaid && raid is not null ? You(raid, mapId) : null;

        return new Situation(version, nowUtc, phase)
        {
            RaidId = raidFacts ? raid!.RaidId : null,
            Map = mapId is null ? null : MapFact(raid!, mapId),
            Side = raidFacts ? SideFact(raid!) : null,
            Outcome = raidFacts && phase.Value is SituationPhase.PostRaid or SituationPhase.Dead or SituationPhase.Extracted
                ? OutcomeFact(raid!)
                : null,
            Profile = runtime?.Profile is { } profile
                ? new(
                    $"{profile.Name} · {profile.GameMode}",
                    Confidence.Certain,
                    SituationSource.Profile,
                    profile.UpdatedUtc,
                    "The companion profile in use.")
                : null,
            Clock = phase.Value == SituationPhase.InRaid && raid is not null ? Clock(raid, nowUtc) : null,
            You = you,
            Squad = runtime is null ? [] : Squad(runtime.Group, mapId, you),
            Next = Objective(mapId, phase.Value, 0),
            Then = Objective(mapId, phase.Value, 1),
            LastScan = LastScan(phase.Value),
            Stages = phase.Value is SituationPhase.Matching or SituationPhase.Loading or SituationPhase.InRaid
                ? Attempt(_markers.Where(entry => entry.Sequence > _endSequence).ToArray())
                : [],
            Party = runtime is null ? null : Party(runtime.Squad),
        };
    }

    /// <summary>
    /// The player's own party as the game's group notifications state it, ready or not (#403). Only
    /// members the game named; readiness only where a notification said it.
    /// </summary>
    private static SituationParty? Party(SquadSnapshot squad)
    {
        if (!squad.HasMembers)
        {
            return null;
        }

        var members = squad.Members
            .Select(member => new SituationPartyMember(member.Nickname ?? "Squadmate", member.IsReady, member.IsLeader == true))
            .ToArray();
        var ready = members.Count(member => member.IsReady == true);
        return new(members, ready, squad.UpdatedUtc,
            $"The game's group notifications, last at {Clock(squad.UpdatedUtc)}: {ready} of {members.Length} ready.");
    }

    private SituationFact<SituationPhase> Phase(ApplicationRuntimeSnapshot? runtime, DateTimeOffset nowUtc)
    {
        if (runtime is null)
        {
            return Situation.Initial.Phase;
        }

        var raid = runtime.Raid;
        var cycle = _markers.Where(entry => entry.Sequence > _endSequence).ToArray();
        var attempt = Attempt(cycle);
        var last = Furthest(attempt);
        // Fixed per rule rather than copied from the last log line, whose confidence moves with
        // every line and would give the situation a new version each time.
        var confidence = new Confidence(0.9);
        switch (raid.State)
        {
            // The game enters "in raid" on the profileStatus line, about a minute before the player
            // can move; until GameStarted the loading screen is still up.
            case RaidLifecycleState.InRaid when last is not null && last.Kind != RaidPhaseMarkerKind.GameStarted:
                return Fact(SituationPhase.Loading, confidence, SituationSource.GameLog, last.ObservedUtc,
                    $"The game confirmed the raid; it has not started yet (last line: {Describe(last.Kind)} at {Clock(last.ObservedUtc)}).");
            case RaidLifecycleState.InRaid:
                return InRaidFact(raid, cycle);
            case RaidLifecycleState.LoadingRaid when last is not null && IsQueue(last.Kind):
                return Fact(SituationPhase.Matching, confidence, SituationSource.GameLog, ReadyAt(attempt),
                    $"You pressed Ready at {Clock(ReadyAt(attempt))} and the game is looking for a raid.");
            case RaidLifecycleState.LoadingRaid:
                return Fact(SituationPhase.Loading, confidence, SituationSource.GameLog, last?.ObservedUtc ?? raid.UpdatedUtc,
                    last is null
                        ? "The game is loading a raid."
                        : $"The game found a raid and is loading it ({Describe(last.Kind)} at {Clock(last.ObservedUtc)}).");
        }

        // Out of a raid. A queue started since the last raid ended comes first: it is what the
        // player is doing now.
        if (last is not null && IsQueue(last.Kind) && nowUtc - ReadyAt(attempt) < MatchingHolds)
        {
            return Fact(SituationPhase.Matching, new Confidence(0.9), SituationSource.GameLog, ReadyAt(attempt),
                $"You pressed Ready at {Clock(ReadyAt(attempt))} and the game is looking for a raid.");
        }

        if (runtime.Squad.MatchStartedUtc is { } groupStart
            && (_endUtc is null || groupStart > _endUtc)
            && (raid.StartedUtc is null || groupStart > raid.StartedUtc)
            && nowUtc - groupStart < MatchingHolds)
        {
            return Fact(SituationPhase.Matching, new Confidence(0.85), SituationSource.GameLog, groupStart,
                $"Your group started matching at {Clock(groupStart)}.");
        }

        if (_scan is { } scan && _scanSequence > _endSequence && IsBetweenRaidScreen(scan.Context)
            && nowUtc - scan.ObservedUtc < ScreenHolds)
        {
            return Fact(SituationPhase.Screen, new Confidence(0.8), SituationSource.Screenshot, scan.ObservedUtc,
                $"Your screenshot at {Clock(scan.ObservedUtc)} showed {DescribeScreen(scan.Context)}.");
        }

        if (raid.State == RaidLifecycleState.PostRaid)
        {
            var ended = _endUtc ?? raid.UpdatedUtc;
            if (nowUtc - ended < PostRaidHolds)
            {
                if (raid.RaidId is { } id && _outcomes.TryGetValue(id, out var outcome) && outcome.Value != SituationOutcome.Unknown)
                {
                    var died = outcome.Value == SituationOutcome.Died;
                    return Fact(died ? SituationPhase.Dead : SituationPhase.Extracted, outcome.Confidence, outcome.Source,
                        outcome.ObservedUtc, outcome.Because);
                }

                return Fact(SituationPhase.PostRaid, confidence, SituationSource.GameLog, ended,
                    $"The game reported the raid over at {Clock(ended)}. The log never says how it ended.");
            }

            return Fact(SituationPhase.Menu, new Confidence(0.7), SituationSource.GameLog, ended,
                $"The last raid ended at {Clock(ended)}, and nothing since says you are in another.", inferred: true);
        }

        // The log has no "in the menu" line: before the first raid, watching the logs and seeing
        // no raid is what the menu looks like.
        if (raid.State == RaidLifecycleState.Unknown)
        {
            return runtime.Observation.IsWatchingLogs
                ? Fact(SituationPhase.Menu, new Confidence(0.6), SituationSource.GameLog, DateTimeOffset.UnixEpoch,
                    "Watching the game's logs, and no raid in them yet.", inferred: true)
                : Situation.Initial.Phase;
        }

        return Fact(SituationPhase.Menu, confidence, SituationSource.GameLog, raid.UpdatedUtc,
            "The game is running and you are not in a raid.");
    }

    private SituationFact<SituationPhase> InRaidFact(RaidSnapshot raid, (RaidPhaseMarker Marker, long Sequence)[] cycle)
    {
        if (cycle.LastOrDefault(entry => entry.Marker.Kind == RaidPhaseMarkerKind.GameStarted).Marker is { } started)
        {
            return Fact(SituationPhase.InRaid, new Confidence(0.95), SituationSource.GameLog, started.ObservedUtc,
                $"The game started the raid at {Clock(started.ObservedUtc)}.");
        }

        // A raid known only from a screenshot name is real evidence but not the log's word.
        return raid.RaidKey is null && raid.StartedByEventId is null && raid.LastKnownPosition is { } position
            ? Fact(SituationPhase.InRaid, new Confidence(0.8), SituationSource.Screenshot, position.Timestamp,
                $"Your screenshot at {Clock(position.Timestamp)} carries a raid position.")
            : Fact(SituationPhase.InRaid, new Confidence(0.9), SituationSource.GameLog, raid.StartedUtc ?? raid.UpdatedUtc,
                raid.StartedUtc is { } at ? $"The game confirmed the raid at {Clock(at)}." : "The game log says you are in a raid.");
    }

    private SituationFact<string> MapFact(RaidSnapshot raid, string mapId)
    {
        var name = _places?.MapName(mapId) ?? mapId;
        return raid.IsManualMapOverride
            ? new(mapId, Confidence.Certain, SituationSource.Player, raid.StartedUtc ?? raid.UpdatedUtc, $"You picked {name}.")
            : new(mapId, new Confidence(0.9), SituationSource.GameLog, raid.StartedUtc ?? raid.UpdatedUtc, $"The game log named {name}.");
    }

    private static SituationFact<SituationSide>? SideFact(RaidSnapshot raid)
    {
        var side = raid.Side?.Trim().ToLowerInvariant() switch
        {
            "pmc" or "usec" or "bear" => SituationSide.Pmc,
            "scav" or "savage" => SituationSide.Scav,
            _ => SituationSide.Unknown,
        };
        if (side == SituationSide.Unknown)
        {
            return new(SituationSide.Unknown, Confidence.Unknown, SituationSource.None, raid.UpdatedUtc,
                "Nothing in the log has said which side this raid is.");
        }

        // A Transfer end proves a scav run; which profile ran the raid is an inference (EFT_LOG_FACTS.md).
        var proven = raid.SideBasis?.Contains("Transfer", StringComparison.Ordinal) == true;
        return new(side, new Confidence(proven ? 0.99 : 0.9), SituationSource.GameLog, raid.StartedUtc ?? raid.UpdatedUtc,
            raid.SideBasis ?? "From the game log.", IsInferred: !proven);
    }

    private SituationFact<SituationOutcome> OutcomeFact(RaidSnapshot raid) =>
        raid.RaidId is { } id && _outcomes.TryGetValue(id, out var outcome)
            ? outcome
            : new(SituationOutcome.Unknown, Confidence.Unknown, SituationSource.None, _endUtc ?? raid.UpdatedUtc,
                "The game does not write how a raid ended.");

    private SituationClock Clock(RaidSnapshot raid, DateTimeOffset nowUtc)
    {
        var observed = raid.RaidClock is { } reading && raid.RaidClockReadUtc is { } read ? (reading, read) : ((TimeSpan, DateTimeOffset)?)null;
        var length = raid.MapId is { } map ? _places?.RaidLength(map, raid.Side) : null;
        var resolved = RaidTimer.ResolveForRaid(observed, raid.StartedUtc, length, raid.Side, startSetByHand: false, nowUtc);
        return resolved.Basis switch
        {
            RaidTimeBasis.Observed => new(SituationClockBasis.Observed, observed!.Value.Item1, observed.Value.Item2, raid.StartedUtc,
                $"Read off your extract screen at {Clock(observed.Value.Item2)}."),
            RaidTimeBasis.Counted => new(SituationClockBasis.Counted, length, raid.StartedUtc, raid.StartedUtc,
                $"Counted from the raid start at {Clock(raid.StartedUtc!.Value)} and the map's {length!.Value.TotalMinutes:0}-minute length."),
            _ when raid.StartedUtc is { } started => new(SituationClockBasis.Elapsed, null, null, started,
                RaidTimer.CanCountFromStart(raid.Side)
                    ? $"The map's length is not known, so this counts up from {Clock(started)}."
                    : $"A scav joins a raid already running; screenshot the extract list for time left. Counting up from {Clock(started)}."),
            _ => new(SituationClockBasis.Unknown, null, null, null, "No raid start and no extract screen yet."),
        };
    }

    private SituationYou? You(RaidSnapshot raid, string? mapId)
    {
        if (raid.LastKnownPosition is not { } position)
        {
            return null;
        }

        var place = mapId is null ? new SituationPlace(null, null) : _places?.Describe(mapId, position.Position) ?? new(null, null);
        return new(
            place.AreaName,
            place.FloorName,
            Facing(position.HeadingDegrees),
            position.HeadingDegrees,
            position.Position,
            position.Timestamp,
            $"From your screenshot at {Clock(position.Timestamp)}.");
    }

    private IReadOnlyList<SituationSquadMember> Squad(GroupSnapshot group, string? mapId, SituationYou? you)
    {
        if (!group.IsSharing || group.Members.Count == 0)
        {
            return [];
        }

        var rows = new List<SituationSquadMember>(group.Members.Count);
        foreach (var member in group.Members)
        {
            DateTimeOffset? taken = member.PositionAgeNow is { } age
                ? TruncateToSecond(group.UpdatedUtc - age)
                : null;
            var sameMap = mapId is not null && string.Equals(member.MapId, mapId, StringComparison.OrdinalIgnoreCase);
            var state = member.HasGoneQuiet
                ? SquadMemberState.Quiet
                : member.RaidState switch
                {
                    RaidLifecycleState.InRaid when mapId is not null && member.MapId is not null && !sameMap => SquadMemberState.OnAnotherMap,
                    RaidLifecycleState.InRaid => SquadMemberState.InRaid,
                    RaidLifecycleState.LoadingRaid => SquadMemberState.Loading,
                    RaidLifecycleState.Menu or RaidLifecycleState.PostRaid or RaidLifecycleState.LauncherOrGameDetected => SquadMemberState.OutOfRaid,
                    _ => SquadMemberState.Unknown,
                };
            var area = member.Position is { } at && member.MapId is { } theirMap
                ? _places?.Describe(theirMap, at).AreaName
                : null;
            var measurable = you is not null && sameMap && member.Position is not null;
            rows.Add(new(
                member.Name,
                state,
                member.MapId,
                area,
                measurable ? Math.Round(SpawnProximity.Distance(you!.Position, member.Position!.Value)) : null,
                measurable ? SpawnProximity.Compass(you!.Position, member.Position!.Value) : null,
                taken,
                taken is { } when
                    ? $"Shared by {member.Name}'s companion from their screenshot at {Clock(when)}."
                    : $"Shared by {member.Name}'s companion."));
        }

        return rows;
    }

    private SituationObjective? Objective(string? mapId, SituationPhase phase, int index)
    {
        if (_plan is not { } plan || mapId is null
            || phase is not (SituationPhase.Matching or SituationPhase.Loading or SituationPhase.InRaid)
            || !string.Equals(plan.MapId, mapId, StringComparison.OrdinalIgnoreCase)
            || plan.Steps.Count <= index)
        {
            return null;
        }

        var step = plan.Steps[index];
        return new(
            step.ObjectiveId,
            step.Label,
            index == 0 && double.IsFinite(step.LegDistanceMetres) ? Math.Round(step.LegDistanceMetres) : null,
            plan.MapId,
            plan.PlannedUtc,
            index == 0
                ? $"Stop {step.Number} of the objective route you opened, measured from {plan.StartLabel}."
                : $"Stop {step.Number} of the objective route you opened.");
    }

    private SituationScan? LastScan(SituationPhase phase)
    {
        if (_scan is not { } scan || (phase == SituationPhase.InRaid && _scanSequence < _raidStartSequence))
        {
            return null;
        }

        var selected = scan.Recognition.Selected;
        var count = scan.Container?.Items.Count ?? (selected is null ? 0 : 1);
        return new(
            scan.Context,
            selected?.DisplayName,
            count,
            scan.Recommendation?.Action.ToString(),
            scan.ObservedUtc,
            $"Your screenshot at {Clock(scan.ObservedUtc)} ({DescribeScreen(scan.Context)}).");
    }

    private static bool IsBetweenRaidScreen(ScanContext context) =>
        context is ScanContext.FleaListings or ScanContext.QuestTasks or ScanContext.SingleItem;

    private static string DescribeScreen(ScanContext context) => context switch
    {
        ScanContext.FleaListings => "the flea market",
        ScanContext.QuestTasks => "the TASKS screen",
        ScanContext.SingleItem => "an item",
        ScanContext.Container => "an open container",
        ScanContext.ExtractList => "the extract list",
        _ => "a screen the companion does not know",
    };

    private static string Describe(RaidPhaseMarkerKind kind) => kind switch
    {
        RaidPhaseMarkerKind.MatchingStarted or RaidPhaseMarkerKind.MatchingStep => "matching",
        RaidPhaseMarkerKind.MatchingCompleted => "matched",
        RaidPhaseMarkerKind.LocationLoaded => "map loaded",
        RaidPhaseMarkerKind.Spawning => "spawning",
        RaidPhaseMarkerKind.Spawned => "spawned",
        _ => "game started",
    };

    private static bool IsQueue(RaidPhaseMarkerKind kind) =>
        kind is RaidPhaseMarkerKind.MatchingStarted or RaidPhaseMarkerKind.MatchingStep;

    /// <summary>How far into a raid each marker is. The log does not keep them in this order (#403).</summary>
    private static int Rank(RaidPhaseMarkerKind kind) => kind switch
    {
        RaidPhaseMarkerKind.MatchingStarted or RaidPhaseMarkerKind.MatchingStep => 0,
        RaidPhaseMarkerKind.MatchingCompleted => 1,
        RaidPhaseMarkerKind.LocationLoaded => 2,
        RaidPhaseMarkerKind.Spawning => 3,
        RaidPhaseMarkerKind.Spawned => 4,
        _ => 5,
    };

    /// <summary>This raid attempt's markers: from the latest Ready on, so a queue left and re-entered starts over.</summary>
    private static RaidPhaseMarker[] Attempt((RaidPhaseMarker Marker, long Sequence)[] cycle)
    {
        var start = Array.FindLastIndex(cycle, entry => entry.Marker.Kind == RaidPhaseMarkerKind.MatchingStarted);
        return cycle[Math.Max(start, 0)..].Select(entry => entry.Marker).ToArray();
    }

    /// <summary>
    /// The furthest stage reached, not the last line: 1.1.5.1 wrote a queue step after MatchingCompleted
    /// (#403), and taking the last line would put a loading raid back in the queue.
    /// </summary>
    private static RaidPhaseMarker? Furthest(RaidPhaseMarker[] attempt)
    {
        RaidPhaseMarker? furthest = null;
        foreach (var marker in attempt)
        {
            if (furthest is null || Rank(marker.Kind) >= Rank(furthest.Kind))
            {
                furthest = marker;
            }
        }

        return furthest;
    }

    private static DateTimeOffset ReadyAt(RaidPhaseMarker[] attempt) =>
        attempt.FirstOrDefault(marker => marker.Kind == RaidPhaseMarkerKind.MatchingStarted)?.ObservedUtc
        ?? attempt.First().ObservedUtc;

    /// <summary>A heading as one of eight compass points, with the map's convention (0 is north).</summary>
    public static string? Facing(double headingDegrees) =>
        double.IsFinite(headingDegrees)
            ? CompassPoints[(int)Math.Round((((headingDegrees % 360) + 360) % 360) / 45) % 8]
            : null;

    private static SituationFact<SituationPhase> Fact(
        SituationPhase phase,
        Confidence confidence,
        SituationSource source,
        DateTimeOffset observedUtc,
        string because,
        bool inferred = false) =>
        new(phase, confidence, source, observedUtc, because, inferred);

    private static string Clock(DateTimeOffset utc) => LocalTime.Time(utc);

    private static DateTimeOffset TruncateToSecond(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Offset);
}
