using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.App.Services.V2;

/// <summary>
/// [#712 0-11] The desktop's Now panel state, as the paired tablet's payload.
/// </summary>
/// <remarks>
/// Built from the same <see cref="NowPanelState"/> the desktop panel binds, so the tablet says what
/// the desk says, in the desk's language. Only the parts that tick are taken back to their anchors
/// (see <see cref="TabletNowPanel"/>): two builds a second apart with nothing new are equal, which
/// is what lets the publisher send on change only.
/// </remarks>
public static class TabletNowPanelBuilder
{
    /// <summary>The slot the tablet fills with the raid clock ("20:57").</summary>
    public const string ClockSlot = "{clock}";

    /// <summary>The slot the tablet fills with an age ("3 min ago").</summary>
    public const string AgoSlot = "{ago}";

    public static TabletNowPanel Build(NowPanelState state, Situation situation, Func<string, string?>? colourOf = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(situation);
        var inRaid = state.Phase == SituationPhase.InRaid;
        return new TabletNowPanel(
            TabletNowPanel.CurrentVersion,
            state.Phase.ToString(),
            situation.Map?.Value is { } map ? MapDisplayName.FromId(map) : null,
            state.Tone.ToString(),
            NowBlock(state, inRaid ? situation.Clock : null),
            You(state, situation.You),
            Squad(state, situation, colourOf),
            Next(state),
            Scan(state, situation.LastScan),
            state.Because.Length > 0 ? state.Because : null,
            new TabletNowWords(
                UiText.Get("Now.Age.Seconds"),
                UiText.Get("Now.Age.Minutes"),
                UiText.Get("Now.Age.Ago"),
                NowText.Ping,
                NowText.PingTip("{0}"))).Bounded();
    }

    private static TabletNowBlock NowBlock(NowPanelState state, SituationClock? clock)
    {
        var block = new TabletNowBlock(state.NowHeading, state.NowHeadline, null, state.NowDetail, null, state.NowNote, state.NowNoteIsDone);
        if (clock is null)
        {
            return block;
        }

        var counted = clock.Basis != SituationClockBasis.Observed;
        if (clock.RemainingAtAnchor is { } left && clock.AnchorUtc is { } anchor)
        {
            block = block with
            {
                Headline = NowText.ClockLeft(ClockSlot),
                Clock = new("left", anchor + left, clock.Basis.ToString(), counted),
            };
        }
        else if (clock.StartedUtc is { } started)
        {
            block = block with
            {
                Headline = NowText.ClockElapsed(ClockSlot),
                Clock = new("elapsed", started, clock.Basis.ToString(), counted),
            };
        }

        // The one basis that carries an age: "from your extract screen · 3 min ago".
        return clock.Basis == SituationClockBasis.Observed && clock.AnchorUtc is { } read
            ? block with { Detail = NowText.ClockObserved(AgoSlot), DetailSinceUtc = read }
            : block;
    }

    private static TabletNowYou? You(NowPanelState state, SituationYou? you) => !state.ShowsYou
        ? null
        : new TabletNowYou(
            NowText.HeadingYou,
            state.YouWhere,
            state.YouIsPlaced ? string.Empty : state.YouAge,
            state.YouIsPlaced ? you?.TakenUtc : null,
            state.YouIsStale,
            state.YouExit,
            state.YouExitNote);

    private static TabletNowSquad? Squad(NowPanelState state, Situation situation, Func<string, string?>? colourOf)
    {
        if (!state.ShowsSquad)
        {
            return null;
        }

        // NowPanelState keeps the situation's order, one row per member.
        var rows = state.Squad.Select((row, index) => new TabletNowSquadRow(
            row.Name,
            state.IsVerdictFocus ? row.Short : row.Where,
            row.Age.Length > 0 && index < situation.Squad.Count ? situation.Squad[index].PositionTakenUtc : null,
            row.CanPing,
            row.IsAway,
            colourOf?.Invoke(row.Name)));
        return new TabletNowSquad(NowText.HeadingSquad, [.. rows], NowText.SquadEmpty, NowText.SquadEmptyHint);
    }

    private static TabletNowNext? Next(NowPanelState state) => !state.ShowsNext
        ? null
        : new TabletNowNext(
            NowText.HeadingNext,
            state.NextLabel,
            state.NextDetail,
            state.ThenLabel,
            state.HasNext ? string.Empty : NowText.NextNoneHint);

    private static TabletNowScan Scan(NowPanelState state, SituationScan? scan)
    {
        if (state.Verdict is { } verdict)
        {
            var line = string.Join(' ', new[] { state.TakeText, state.SwapText, state.LeaveText, state.CheckText }.Where(part => part.Length > 0));
            return new TabletNowScan(
                NowText.HeadingLastScan,
                line,
                verdict.ReceivedUtc,
                string.Empty,
                [.. state.VerdictRows.Select(row => new TabletNowVerdictRow(row.Verdict.ToString(), row.VerdictWord, row.Name, row.Reason, row.Value))]);
        }

        return state.HasScanLine && scan is not null
            ? new TabletNowScan(NowText.HeadingLastScan, state.ScanLine, scan.ObservedUtc, string.Empty, [])
            : new TabletNowScan(NowText.HeadingLastScan, state.ScanNone, null, state.ShowsScanHint ? NowText.ScanNoneHint : string.Empty, []);
    }
}
