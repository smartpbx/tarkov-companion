using System.Globalization;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Strategy.Prior;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.App.ViewModels.V2.Now;

/// <summary>An exit the map knows, as far from the player as the last screenshot says.</summary>
/// <param name="IsOffered">A scan of the extract screen named it: offered in this raid.</param>
public sealed record NowExit(string Name, double? Metres, string? Compass, bool IsOffered, bool IsTransit = false);

/// <summary>One item of a loot verdict, as LAST SCAN lists it.</summary>
public sealed record NowLootRow(LootScanVerdict Verdict, string Name, string Reason, string Value)
{
    public string VerdictWord => Verdict switch
    {
        LootScanVerdict.Take => NowText.VerdictTake,
        LootScanVerdict.Swap => NowText.VerdictSwap,
        LootScanVerdict.Leave => NowText.VerdictLeave,
        _ => NowText.VerdictCheck,
    };

    public bool IsTake => Verdict == LootScanVerdict.Take;

    public bool IsSwap => Verdict == LootScanVerdict.Swap;

    public bool IsLeave => Verdict == LootScanVerdict.Leave;

    public bool IsCheck => Verdict == LootScanVerdict.Review;

    public bool HasReason => Reason.Length > 0;
}

/// <summary>The newest loot verdict, taken from the Loot page's own result when it arrived.</summary>
public sealed record NowLootVerdict(int Take, int Swap, int Leave, int Check, IReadOnlyList<NowLootRow> Rows, DateTimeOffset ReceivedUtc);

public enum NowTone
{
    Normal,

    /// <summary>Late raid: amber.</summary>
    Late,

    /// <summary>Past the time to leave for the exit: red.</summary>
    Urgent,
}

/// <summary>One squad row before it is bound: see <see cref="NowSquadRowViewModel"/>.</summary>
/// <param name="Signature">What a change of is worth a pulse: the member's state and area.</param>
/// <param name="Short">The distance alone where there is one, for SQUAD's one-line form.</param>
public sealed record NowSquadRow(string Name, string Where, string Short, string Age, bool CanPing, bool IsAway, string Signature);

/// <summary>
/// [#712 0-4] Everything the Now panel shows, worked out from one <see cref="Situation"/> at one moment.
/// </summary>
/// <remarks>
/// A pure projection so each phase's blocks can be tested without a window: the view model only
/// keeps the inputs and asks for a new state each second (the clock is an anchor, ADR 0022).
/// Every inferred fact says so in its own line: the clock's basis, a screenshot's age, an exit not
/// seen on the extract list, a squadmate's age. Nothing here prints a coordinate.
/// </remarks>
public sealed record NowPanelState
{
    /// <summary>At or under this, NOW turns amber and names the time to leave.</summary>
    public static readonly TimeSpan LateRaid = TimeSpan.FromMinutes(10);

    /// <summary>The run-through rule: under this much raid time a survival counts as a run-through.</summary>
    public static readonly TimeSpan RunThrough = TimeSpan.FromMinutes(7);

    /// <summary>Spare time on top of the walk by default; #712 0-6 makes it a setting (LeaveMarginSetting).</summary>
    public static readonly TimeSpan LeaveMargin = TimeSpan.FromMinutes(2);

    /// <summary>A screenshot older than this dims YOU: where you were, not where you are.</summary>
    public static readonly TimeSpan YouGoesStale = TimeSpan.FromMinutes(2);

    /// <summary>A verdict this fresh takes the panel over (the late-loot concept); older, it is one line.</summary>
    public static readonly TimeSpan VerdictFocus = TimeSpan.FromSeconds(90);

    /// <summary>At most this many verdict rows, so the panel never scrolls at 1080 high.</summary>
    public const int MaximumVerdictRows = 4;

    public SituationPhase Phase { get; init; }

    public NowTone Tone { get; init; }

    public bool IsLate => Tone != NowTone.Normal;

    public bool IsUrgent => Tone == NowTone.Urgent;

    public string NowHeading { get; init; } = NowText.HeadingNow;

    public string NowHeadline { get; init; } = string.Empty;

    public string NowDetail { get; init; } = string.Empty;

    public bool HasNowDetail => NowDetail.Length > 0;

    /// <summary>Run-through, or in a late raid the time to leave by.</summary>
    public string NowNote { get; init; } = string.Empty;

    public bool HasNowNote => NowNote.Length > 0;

    /// <summary>[#712 0-6] The leave line is a model (walk pace, last screenshot): its label, shown with it.</summary>
    public bool HasLeaveEstimate => IsLate && HasNowNote;

    public string LeaveEstimate => HasLeaveEstimate ? NowText.LeaveEstimate : string.Empty;

    /// <summary>[#712 0-6] YOU's side "wrong?" chip: the side the raid is being read as.</summary>
    public string YouSide { get; init; } = string.Empty;

    /// <summary>The run-through time has passed: the note carries a check.</summary>
    public bool NowNoteIsDone { get; init; }

    public bool ShowsYou { get; init; }

    public bool YouIsPlaced { get; init; }

    public string YouWhere { get; init; } = string.Empty;

    public string YouAge { get; init; } = string.Empty;

    public bool YouIsStale { get; init; }

    public string YouExit { get; init; } = string.Empty;

    public bool HasYouExit => YouExit.Length > 0;

    /// <summary>Set when the exit named was not seen on the extract list: nearest, not known offered.</summary>
    public string YouExitNote { get; init; } = string.Empty;

    public bool HasYouExitNote => YouExitNote.Length > 0;

    public bool ShowsSquad { get; init; }

    public bool HasSquad => Squad.Count > 0;

    public bool HasNoSquad => Squad.Count == 0;

    public IReadOnlyList<NowSquadRow> Squad { get; init; } = [];

    /// <summary>
    /// [#403] Where SQUAD's rows came from: squadmates' companions, or, with none sharing, the
    /// game's own party list (the same fallback the pre-raid brief makes).
    /// </summary>
    public string SquadSource { get; init; } = NowText.SquadSource;

    /// <summary>The party list's own "because" (how many ready, as of when), behind SQUAD's source label.</summary>
    public string SquadBecause { get; init; } = string.Empty;

    public bool HasSquadBecause => SquadBecause.Length > 0;

    /// <summary>One line for SQUAD while a fresh verdict has the room: "Geo 90 m E · Riley 60 m N".</summary>
    public string SquadLine { get; init; } = string.Empty;

    public bool ShowsNext { get; init; }

    public bool HasNext => NextLabel.Length > 0;

    public bool HasNoNext => NextLabel.Length == 0;

    public string NextLabel { get; init; } = string.Empty;

    public string NextDetail { get; init; } = string.Empty;

    public bool HasNextDetail => NextDetail.Length > 0;

    public string ThenLabel { get; init; } = string.Empty;

    public bool HasThen => ThenLabel.Length > 0;

    public bool ShowsScan { get; init; }

    public bool HasVerdict => Verdict is not null;

    public bool HasScanLine => ScanLine.Length > 0;

    public bool HasNoScan => !HasVerdict && !HasScanLine;

    public string ScanLine { get; init; } = string.Empty;

    public string ScanAge { get; init; } = string.Empty;

    public bool ShowsScanHint { get; init; }

    public NowLootVerdict? Verdict { get; init; }

    // "Take 2 · Swap 1 · Leave 1": each count after the first shown carries the separator.
    public string TakeText => Verdict is { Take: > 0 } verdict ? NowText.ScanTake(verdict.Take) : string.Empty;

    public string SwapText => Verdict is { Swap: > 0 } verdict ? Joined(NowText.ScanSwap(verdict.Swap), verdict.Take) : string.Empty;

    public string LeaveText => Verdict is { Leave: > 0 } verdict ? Joined(NowText.ScanLeave(verdict.Leave), verdict.Take + verdict.Swap) : string.Empty;

    public string CheckText => Verdict is { Check: > 0 } verdict
        ? Joined(NowText.ScanCheck(verdict.Check), verdict.Take + verdict.Swap + verdict.Leave)
        : string.Empty;

    /// <summary>LAST SCAN's empty line: "this raid" only means something in one.</summary>
    public string ScanNone => Phase == SituationPhase.InRaid ? NowText.ScanNone : NowText.ScanNoneOutOfRaid;

    /// <summary>A verdict younger than <see cref="VerdictFocus"/> in a raid: rows shown, YOU and SQUAD one line, NEXT away.</summary>
    public bool IsVerdictFocus { get; init; }

    public bool IsNotVerdictFocus => !IsVerdictFocus;

    public IReadOnlyList<NowLootRow> VerdictRows => IsVerdictFocus && Verdict is { } verdict ? verdict.Rows : [];

    /// <summary>The phase fact's own reason, from the situation itself: the "because" strip (ADR 0022).</summary>
    public string Because { get; init; } = string.Empty;

    public bool HasBecause => Because.Length > 0;

    public static NowPanelState Project(
        Situation situation,
        DateTimeOffset nowUtc,
        IReadOnlyList<NowExit>? exits = null,
        NowLootVerdict? verdict = null,
        TimeSpan? leaveMargin = null)
    {
        ArgumentNullException.ThrowIfNull(situation);
        var phase = situation.Phase.Value;
        var inRaid = phase == SituationPhase.InRaid;
        var raidStart = situation.Clock?.StartedUtc;
        var exit = inRaid ? ChooseExit(exits) : null;

        // A verdict from before this raid started is the last raid's container, not this one's.
        var shownVerdict = verdict is not null && (!inRaid || raidStart is null || verdict.ReceivedUtc >= raidStart)
            ? verdict
            : null;
        var focus = inRaid && shownVerdict is not null && nowUtc - shownVerdict.ReceivedUtc < VerdictFocus;

        // [#403] Companions' own shares first; with none, the game's party list, as the brief does.
        var party = situation.Squad.Count == 0 && situation.Party is { Members.Count: > 0 } game ? game : null;
        var squad = party is null
            ? situation.Squad.Select(member => Row(member, situation.Map?.Value, nowUtc)).ToArray()
            : party.Members.Select(member => Row(member, phase)).ToArray();

        var state = new NowPanelState
        {
            Phase = phase,
            // Nothing read yet has nothing to explain; NOW's own line already says so.
            Because = situation.Phase.Source == SituationSource.None ? string.Empty : situation.Phase.Because,
            ShowsYou = inRaid,
            ShowsSquad = squad.Length > 0 || inRaid,
            Squad = squad,
            SquadSource = party is null ? NowText.SquadSource : NowText.SquadFromGame,
            SquadBecause = party?.Because ?? string.Empty,
            ShowsNext = !focus && phase is SituationPhase.InRaid or SituationPhase.Matching or SituationPhase.Loading,
            ShowsScan = true,
            IsVerdictFocus = focus,
            YouSide = NowText.WrongSide(situation.Side?.Value ?? SituationSide.Unknown),
        };
        state = state with { SquadLine = JoinSquad(state.Squad) };
        state = WithNow(state, situation, nowUtc, exit, leaveMargin ?? LeaveMargin);
        state = WithYou(state, situation.You, nowUtc, exit);
        state = WithNext(state, situation.Next, situation.Then);
        return WithScan(state, situation.LastScan, shownVerdict, raidStart, nowUtc);
    }

    /// <summary>An offered exit first; else the nearest one, marked as not seen offered. Never a transit.</summary>
    internal static NowExit? ChooseExit(IReadOnlyList<NowExit>? exits)
    {
        if (exits is null)
        {
            return null;
        }

        var placed = exits.Where(exit => !exit.IsTransit && exit.Metres is { } metres && double.IsFinite(metres)).ToArray();
        return placed.Where(exit => exit.IsOffered).MinBy(exit => exit.Metres)
            ?? placed.MinBy(exit => exit.Metres);
    }

    /// <summary>"20:57", or "1:02:03" past an hour: a raid clock as the game writes it.</summary>
    internal static string Clock(TimeSpan time)
    {
        var value = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        return value.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalMinutes}:{value.Seconds:00}");
    }

    private static NowPanelState WithNow(NowPanelState state, Situation situation, DateTimeOffset nowUtc, NowExit? exit, TimeSpan margin)
    {
        var phase = situation.Phase.Value;
        if (phase != SituationPhase.InRaid)
        {
            var map = situation.Map?.Value is { } mapId ? MapDisplayName.FromId(mapId) : null;
            var spawning = phase == SituationPhase.Loading &&
                situation.Stages.Any(stage => stage.Kind is RaidPhaseMarkerKind.Spawning or RaidPhaseMarkerKind.Spawned);
            var headline = phase switch
            {
                SituationPhase.Loading when spawning => map is null ? NowText.Spawning : NowText.SpawningMap(map),
                SituationPhase.Loading when map is not null => NowText.LoadingMap(map),
                _ => NowText.Phase(phase),
            };
            var detail = phase switch
            {
                SituationPhase.Screen => NowText.ScreenLine(ScreenWord(situation.LastScan?.Kind)),
                // [#403] Matching and loading say when each step happened, from the game's own log lines.
                SituationPhase.Matching or SituationPhase.Loading when StageLine(situation.Stages) is { Length: > 0 } stages => stages,
                SituationPhase.Matching => string.Empty,
                _ => NowText.PhaseLine(phase, situation.Outcome?.Source == SituationSource.Screenshot),
            };
            return state with { NowHeadline = headline, NowDetail = detail };
        }

        if (situation.Clock is not { } clock)
        {
            return state with { NowHeadline = NowText.ClockUnknown, NowDetail = NowText.ClockNoStart };
        }

        var remaining = clock.RemainingAt(nowUtc);
        var elapsed = clock.ElapsedAt(nowUtc);
        var basis = clock.Basis switch
        {
            SituationClockBasis.Observed when clock.AnchorUtc is { } read => NowText.ClockObserved(NowText.Ago(Age(read, nowUtc))),
            SituationClockBasis.Counted => NowText.ClockCounted,
            SituationClockBasis.Elapsed => NowText.ClockElapsedBasis,
            _ => NowText.ClockNoStart,
        };
        state = state with
        {
            NowHeadline = remaining is { } left
                ? NowText.ClockLeft(Clock(left))
                : elapsed is { } since ? NowText.ClockElapsed(Clock(since)) : NowText.ClockUnknown,
            NowDetail = basis,
        };

        if (remaining is { } timeLeft && timeLeft <= LateRaid)
        {
            state = state with { NowHeading = NowText.HeadingLateRaid, Tone = NowTone.Late };
            if (exit?.Metres is { } metres)
            {
                var walk = Walk(metres);
                var leaveBy = LeaveBy(nowUtc, timeLeft, walk, margin);
                return leaveBy <= nowUtc
                    ? state with { Tone = NowTone.Urgent, NowNote = NowText.LeaveNow(exit.Name, walk) }
                    : state with { NowNote = NowText.LeaveBy(exit.Name, LocalTime.ShortTime(leaveBy), walk, (int)margin.TotalMinutes) };
            }

            return state;
        }

        if (elapsed is { } raidTime)
        {
            var mark = Clock(RunThrough);
            return raidTime < RunThrough
                ? state with { NowNote = NowText.RunThroughUntil(mark) }
                : state with { NowNote = NowText.RunThroughEnded(mark), NowNoteIsDone = true };
        }

        return state;
    }

    /// <summary>
    /// "Ready 14:02 · raid found 14:05 · map loaded 14:06": each step once, at its first line, in the
    /// order a raid goes through them. Queue steps G/H/I only say "matching", which the headline does.
    /// </summary>
    /// <remarks>
    /// Clock times rather than "N s ago", so the line does not change every second: the tablet's payload
    /// is sent on change only (TabletNowPanelBuilder).
    /// </remarks>
    internal static string StageLine(IReadOnlyList<RaidPhaseMarker> stages)
    {
        RaidPhaseMarkerKind[] order =
        [
            RaidPhaseMarkerKind.MatchingStarted,
            RaidPhaseMarkerKind.MatchingCompleted,
            RaidPhaseMarkerKind.LocationLoaded,
            RaidPhaseMarkerKind.Spawning,
            RaidPhaseMarkerKind.Spawned,
        ];
        var parts = order
            .Select(kind => stages.FirstOrDefault(stage => stage.Kind == kind))
            .OfType<RaidPhaseMarker>()
            .Select(stage => NowText.Stage(stage.Kind, LocalTime.ShortTime(stage.ObservedUtc)));
        return string.Join(" · ", parts);
    }

    private static NowPanelState WithYou(NowPanelState state, SituationYou? you, DateTimeOffset nowUtc, NowExit? exit)
    {
        if (!state.ShowsYou)
        {
            return state;
        }

        var exitText = exit?.Metres is { } metres
            ? (exit.IsOffered ? NowText.OfferedExit : (Func<string, string, int, string>)NowText.NearestExit)(
                exit.Name,
                Distance(metres, exit.Compass),
                Walk(metres))
            : string.Empty;
        var exitNote = exitText.Length > 0 && exit is { IsOffered: false } ? NowText.ExitUnconfirmed : string.Empty;
        if (you is null)
        {
            return state with
            {
                YouWhere = NowText.NoScreenshot,
                YouAge = NowText.NoScreenshotHint,
                YouExit = string.Empty,
            };
        }

        string[] parts =
        [
            you.AreaName ?? NowText.NoArea,
            .. you.FloorName is { Length: > 0 } floor ? [floor] : Array.Empty<string>(),
            .. you.Facing is { Length: > 0 } facing ? [NowText.Facing(facing)] : Array.Empty<string>(),
        ];
        var age = you.AgeAt(nowUtc);
        return state with
        {
            YouIsPlaced = true,
            YouWhere = string.Join(" · ", parts),
            YouAge = NowText.Ago(age),
            YouIsStale = age > YouGoesStale,
            YouExit = exitText,
            YouExitNote = exitNote,
        };
    }

    private static NowPanelState WithNext(NowPanelState state, SituationObjective? next, SituationObjective? then) =>
        !state.ShowsNext || next is null
            ? state
            : state with
            {
                NextLabel = next.Label,
                NextDetail = next.DistanceMetres is { } metres ? NowText.NextLeg(metres) : string.Empty,
                ThenLabel = then is null ? string.Empty : NowText.NextThen(then.Label),
            };

    private static NowPanelState WithScan(
        NowPanelState state,
        SituationScan? scan,
        NowLootVerdict? verdict,
        DateTimeOffset? raidStart,
        DateTimeOffset nowUtc)
    {
        var inRaid = state.Phase == SituationPhase.InRaid;
        if (verdict is not null && (scan is null || verdict.ReceivedUtc >= scan.ObservedUtc || scan.Kind == ScanContext.Container))
        {
            return state with { Verdict = verdict, ScanAge = NowText.Ago(Age(verdict.ReceivedUtc, nowUtc)) };
        }

        if (scan is not null && (!inRaid || raidStart is null || scan.ObservedUtc >= raidStart))
        {
            var line = scan.Kind switch
            {
                ScanContext.SingleItem when scan.Headline is { } item && scan.Action is { } action => NowText.ScanItem(item, action),
                ScanContext.SingleItem when scan.Headline is { } item => item,
                ScanContext.Container => NowText.ScanScreen(NowText.ScanContainer),
                ScanContext.ExtractList => NowText.ScanScreen(NowText.ScanExtractList),
                _ => NowText.ScanScreen(ScreenWord(scan.Kind)),
            };
            return state with { ScanLine = line, ScanAge = NowText.Ago(Age(scan.ObservedUtc, nowUtc)) };
        }

        return state with { ShowsScanHint = inRaid };
    }

    private static NowSquadRow Row(SituationSquadMember member, string? yourMap, DateTimeOffset nowUtc)
    {
        var distance = member.DistanceMetres is { } metres ? Distance(metres, member.Compass) : null;
        var where = member.State switch
        {
            SquadMemberState.InRaid when member.AreaName is { } area && distance is not null => NowText.SquadWhere(area, distance),
            SquadMemberState.InRaid when member.AreaName is { } area => area,
            SquadMemberState.InRaid when distance is not null => distance,
            SquadMemberState.InRaid when member.MapId is { } raidMap => NowText.SquadInRaidOn(MapDisplayName.FromId(raidMap)),
            SquadMemberState.InRaid => NowText.SquadNoPosition,
            SquadMemberState.OnAnotherMap when member.MapId is { } map && yourMap is not null => NowText.SquadOtherMap(MapDisplayName.FromId(map)),
            SquadMemberState.OnAnotherMap => NowText.SquadNotOnThisMap,
            SquadMemberState.Loading => NowText.SquadLoading,
            SquadMemberState.OutOfRaid => NowText.SquadOutOfRaid,
            SquadMemberState.Quiet when member.AreaName is { } area => NowText.SquadQuiet(area),
            SquadMemberState.Quiet => NowText.SquadQuietNoPlace,
            _ => NowText.SquadUnknown,
        };
        var placed = member.AreaName is not null || member.DistanceMetres is not null;
        var age = member.State is SquadMemberState.InRaid or SquadMemberState.Quiet && member.AgeAt(nowUtc) is { } seen
            ? NowText.Age(seen)
            : string.Empty;
        return new(
            member.Name,
            where,
            member.State == SquadMemberState.InRaid ? distance ?? where : where,
            age,
            CanPing: member.State == SquadMemberState.InRaid && placed && member.MapId is not null,
            IsAway: member.State is not SquadMemberState.InRaid,
            Signature: $"{member.State}|{member.AreaName}");
    }

    /// <summary>
    /// [#403] A party member the game's log named, with nobody's companion sharing: a name and, before
    /// the raid, whether they pressed Ready. No position, age or Ping: the log carries none.
    /// </summary>
    private static NowSquadRow Row(SituationPartyMember member, SituationPhase phase)
    {
        var beforeRaid = phase is SituationPhase.Unknown or SituationPhase.Menu or SituationPhase.Screen or SituationPhase.Matching;
        var where = beforeRaid && member.IsReady is { } ready
            ? ready ? NowText.PartyReady : NowText.PartyNotReady
            : NowText.PartyMember;
        return new(member.Name, where, where, string.Empty, CanPing: false, IsAway: false, Signature: $"party|{where}");
    }

    private static string JoinSquad(IReadOnlyList<NowSquadRow> rows) =>
        string.Join("  ·  ", rows.Select(row => $"{row.Name} {row.Short}"));

    private static string Distance(double metres, string? compass) =>
        $"{SpawnProximity.Describe(metres)} {compass}".TrimEnd();

    private static string ScreenWord(ScanContext? kind) => kind switch
    {
        ScanContext.FleaListings => NowText.ScreenFlea,
        ScanContext.QuestTasks => NowText.ScreenTasks,
        ScanContext.SingleItem => NowText.ScreenItem,
        ScanContext.Container => NowText.ScanContainer,
        ScanContext.ExtractList => NowText.ScanExtractList,
        _ => NowText.ScreenOther,
    };

    /// <summary>
    /// [#712 0-6] When to set off: the raid's end, less the walk and the margin. A model, not a
    /// reading: the walk is the careful pace over the straight line from the last screenshot.
    /// </summary>
    internal static DateTimeOffset LeaveBy(DateTimeOffset nowUtc, TimeSpan timeLeft, int walkMinutes, TimeSpan margin) =>
        nowUtc + timeLeft - TimeSpan.FromMinutes(walkMinutes) - (margin < TimeSpan.Zero ? TimeSpan.Zero : margin);

    /// <summary>Minutes on foot at the careful pace, the suggested routes' own allowance for obstacles.</summary>
    internal static int Walk(double metres) =>
        Math.Max(1, (int)Math.Ceiling(metres * TrafficRoute.ObstacleAllowance / TrafficRoute.CarefulPace / 60));

    private static string Joined(string text, int before) => before > 0 ? "· " + text : text;

    private static TimeSpan Age(DateTimeOffset at, DateTimeOffset nowUtc) => nowUtc > at ? nowUtc - at : TimeSpan.Zero;
}
