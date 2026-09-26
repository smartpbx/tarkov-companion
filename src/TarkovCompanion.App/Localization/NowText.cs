using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.App.Localization;

/// <summary>[#712 0-4] Every word the Raid page's Now panel shows (#314 recipe: Now.* in the tables).</summary>
/// <remarks>
/// The situation's own "because" lines are English diagnostics (ADR 0022); the panel says each fact
/// from its kind instead, through these. Area, map, extract, item and player names are data.
/// </remarks>
public static class NowText
{
    public static string PanelName => UiText.Get("Now.PanelName");
    public static string HeadingNow => UiText.Get("Now.Heading.Now");
    public static string HeadingLateRaid => UiText.Get("Now.Heading.LateRaid");
    public static string HeadingYou => UiText.Get("Now.Heading.You");
    public static string HeadingSquad => UiText.Get("Now.Heading.Squad");
    public static string HeadingNext => UiText.Get("Now.Heading.Next");
    public static string HeadingLastScan => UiText.Get("Now.Heading.LastScan");

    public static string ClockLeft(string time) => UiText.Format("Now.Clock.Left", time);
    public static string ClockElapsed(string time) => UiText.Format("Now.Clock.Elapsed", time);
    public static string ClockUnknown => UiText.Get("Now.Clock.Unknown");
    public static string ClockObserved(string age) => UiText.Format("Now.Clock.Observed", age);
    public static string ClockCounted => UiText.Get("Now.Clock.Counted");
    public static string ClockElapsedBasis => UiText.Get("Now.Clock.ElapsedBasis");
    public static string ClockNoStart => UiText.Get("Now.Clock.NoStart");
    public static string RunThroughUntil(string time) => UiText.Format("Now.RunThrough.Until", time);
    public static string RunThroughEnded(string time) => UiText.Format("Now.RunThrough.Ended", time);
    public static string LeaveBy(string exit, string time, int walk, int margin) => UiText.Format("Now.LeaveBy", exit, time, walk, margin);
    public static string LeaveNow(string exit, int walk) => UiText.Format("Now.LeaveNow", exit, walk);

    public static string Phase(SituationPhase phase) => UiText.Get(phase switch
    {
        SituationPhase.Menu => "Now.Phase.Menu",
        SituationPhase.Screen => "Now.Phase.Screen",
        SituationPhase.Matching => "Now.Phase.Matching",
        SituationPhase.Loading => "Now.Phase.Loading",
        SituationPhase.Dead => "Now.Phase.Dead",
        SituationPhase.Extracted => "Now.Phase.Extracted",
        SituationPhase.PostRaid or SituationPhase.InRaid => "Now.Phase.PostRaid",
        _ => "Now.Phase.Unknown",
    });

    public static string LoadingMap(string map) => UiText.Format("Now.Phase.LoadingMap", map);
    /// <summary>The NOW block's second line out of a raid; a reported ending says who reported it.</summary>
    public static string PhaseLine(SituationPhase phase, bool fromScreen = false) => UiText.Get(phase switch
    {
        SituationPhase.Menu => "Now.PhaseLine.Menu",
        SituationPhase.Matching => "Now.PhaseLine.Matching",
        SituationPhase.Loading => "Now.PhaseLine.Loading",
        SituationPhase.PostRaid or SituationPhase.InRaid => "Now.PhaseLine.PostRaid",
        SituationPhase.Dead or SituationPhase.Extracted => fromScreen ? "Now.PhaseLine.ReportedScreen" : "Now.PhaseLine.Reported",
        _ => "Now.PhaseLine.Unknown",
    });

    public static string ScreenLine(string screen) => UiText.Format("Now.PhaseLine.Screen", screen);
    public static string ScreenFlea => UiText.Get("Now.Screen.Flea");
    public static string ScreenTasks => UiText.Get("Now.Screen.Tasks");
    public static string ScreenItem => UiText.Get("Now.Screen.Item");
    public static string ScreenOther => UiText.Get("Now.Screen.Other");

    public static string Facing(string compass) => UiText.Format("Now.You.Facing", compass);
    public static string NoArea => UiText.Get("Now.You.NoArea");
    public static string NoScreenshot => UiText.Get("Now.You.NoScreenshot");
    public static string NoScreenshotHint => UiText.Get("Now.You.NoScreenshotHint");
    public static string OfferedExit(string exit, string where, int minutes) => UiText.Format("Now.You.OfferedExit", exit, where, minutes);
    public static string NearestExit(string exit, string where, int minutes) => UiText.Format("Now.You.NearestExit", exit, where, minutes);
    public static string ExitUnconfirmed => UiText.Get("Now.You.ExitUnconfirmed");
    public static string Wrong => UiText.Get("Now.You.Wrong");
    public static string WrongDetail => UiText.Get("Now.You.WrongDetail");

    /// <summary>"12 s" or "3 min": an age without "ago", for a squad row's right-hand column.</summary>
    public static string Age(TimeSpan age) => age < TimeSpan.FromMinutes(1)
        ? UiText.Format("Now.Age.Seconds", (int)Math.Max(0, age.TotalSeconds))
        : UiText.Format("Now.Age.Minutes", (int)age.TotalMinutes);

    public static string Ago(TimeSpan age) => UiText.Format("Now.Age.Ago", Age(age));

    public static string SquadSource => UiText.Get("Now.Squad.Source");
    public static string SquadWhere(string area, string distance) => UiText.Format("Now.Squad.Where", area, distance);
    public static string SquadNoPosition => UiText.Get("Now.Squad.NoPosition");
    public static string SquadInRaidOn(string map) => UiText.Format("Now.Squad.InRaidOn", map);
    public static string SquadOtherMap(string map) => UiText.Format("Now.Squad.OtherMap", map);
    public static string SquadNotOnThisMap => UiText.Get("Now.Squad.NotOnThisMap");
    public static string SquadOutOfRaid => UiText.Get("Now.Squad.OutOfRaid");
    public static string SquadLoading => UiText.Get("Now.Squad.Loading");
    public static string SquadQuiet(string area) => UiText.Format("Now.Squad.Quiet", area);
    public static string SquadQuietNoPlace => UiText.Get("Now.Squad.QuietNoPlace");
    public static string SquadUnknown => UiText.Get("Now.Squad.Unknown");
    public static string SquadEmpty => UiText.Get("Now.Squad.Empty");
    public static string SquadEmptyHint => UiText.Get("Now.Squad.EmptyHint");
    public static string Ping => UiText.Get("Now.Squad.Ping");
    public static string PingTip(string member) => UiText.Format("Now.Squad.PingTip", member);

    public static string NextOnRoute => UiText.Get("Now.Next.OnRoute");
    public static string NextLeg(double metres) => UiText.Format("Now.Next.Leg", Math.Round(metres));
    public static string NextThen(string label) => UiText.Format("Now.Next.Then", label);
    public static string NextNone => UiText.Get("Now.Next.None");
    public static string NextNoneHint => UiText.Get("Now.Next.NoneHint");

    public static string ScanNone => UiText.Get("Now.Scan.None");
    public static string ScanNoneOutOfRaid => UiText.Get("Now.Scan.NoneOutOfRaid");
    public static string ScanNoneHint => UiText.Get("Now.Scan.NoneHint");
    public static string ScanTake(int count) => UiText.Format("Now.Scan.Take", count);
    public static string ScanSwap(int count) => UiText.Format("Now.Scan.Swap", count);
    public static string ScanLeave(int count) => UiText.Format("Now.Scan.Leave", count);
    public static string ScanCheck(int count) => UiText.Format("Now.Scan.Check", count);
    public static string VerdictTake => UiText.Get("Now.Scan.VerdictTake");
    public static string VerdictSwap => UiText.Get("Now.Scan.VerdictSwap");
    public static string VerdictLeave => UiText.Get("Now.Scan.VerdictLeave");
    public static string VerdictCheck => UiText.Get("Now.Scan.VerdictCheck");
    public static string OpenLoot => UiText.Get("Now.Scan.OpenLoot");
    public static string ScanScreen(string what) => UiText.Format("Now.Scan.Screen", what);
    public static string ScanItem(string item, string action) => UiText.Format("Now.Scan.Item", item, action);
    public static string ScanContainer => UiText.Get("Now.Scan.Container");
    public static string ScanExtractList => UiText.Get("Now.Scan.ExtractList");

    public static string More => UiText.Get("Now.More");
    public static string MoreTip => UiText.Get("Now.MoreTip");
    public static string Back => UiText.Get("Now.Back");
}
