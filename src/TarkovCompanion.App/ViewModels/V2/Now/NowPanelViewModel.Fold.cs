namespace TarkovCompanion.App.ViewModels.V2.Now;

/// <summary>
/// [#712 0-7] How far the Now panel has folded to fit its room without scrolling.
/// </summary>
/// <remarks>
/// The epic's rule is that the panel never scrolls. At 100% text in a 1080-high window every
/// block fits; with four squadmates and long place names, a maximised window (1009 high) or 125%
/// and 150% text it did not, and the blocks were clipped, SQUAD's last rows, NEXT and LAST SCAN
/// cut off the bottom with their labels. NowPanelView measures the blocks and raises the fold
/// one step at a time until they fit, each step giving up the least useful thing still shown:
/// 1 the hints and NEXT's second stop; 2 YOU and NEXT in smaller type, and each verdict row's
/// reason; 3 SQUAD in one line (the fresh-verdict form); 4 NEXT, and verdict rows past the second;
/// 5 an empty LAST SCAN, and More past one line; 6 SQUAD's line past two lines, YOU's exit line (the
/// late-raid line still names it), "wrong?" and verdict rows past the first; 7 the verdict's rows
/// (its counts stay, and a tap opens Loot) and SQUAD's line past one. A label that says a fact is a guess is never folded away while
/// its fact shows (NowPanelGlanceTests holds this).
/// </remarks>
public sealed partial class NowPanelViewModel
{
    public const int MaximumFold = 7;

    private int _fold;

    /// <summary>0 shows everything; set by the view while it measures.</summary>
    public int Fold
    {
        get => _fold;
        set
        {
            if (SetProperty(ref _fold, Math.Clamp(value, 0, MaximumFold)))
            {
                RaiseFold();
            }
        }
    }

    public bool ShowsHints => _fold < 1;

    /// <summary>YOU's place and exit in body type: a fresh verdict has the room, or the panel is folded.</summary>
    public bool IsYouCompact => State.IsVerdictFocus || _fold >= 2;

    public bool ShowsYouExit => State.HasYouExit && State.IsNotVerdictFocus && _fold < 6;

    public bool ShowsWrong => State.YouIsPlaced && State.IsNotVerdictFocus && _fold < 6;

    /// <summary>The fresh verdict's rows: without their reasons from fold 2, then two, one and none.</summary>
    public IReadOnlyList<NowLootRow> VerdictRows => _fold switch
    {
        < 2 => State.VerdictRows,
        _ => [.. State.VerdictRows.Take(_fold switch { >= 7 => 0, 6 => 1, >= 4 => 2, _ => int.MaxValue })
            .Select(row => row with { Reason = string.Empty })],
    };

    public int SquadLineLines => _fold switch
    {
        >= 7 => 1,
        >= 6 => 2,
        _ => 0,
    };

    public int MoreLines => _fold >= 5 ? 1 : 0;

    /// <summary>The late-raid line names an exit the extract list never showed, and YOU's own note for it is hidden.</summary>
    public bool ShowsLeaveExitNote => State.IsLate && State.HasNowNote && State.HasYouExitNote && !ShowsYouExit;

    public int NextLabelLines => _fold >= 2 ? 1 : 2;

    public bool ShowsNextThen => State.HasThen && ShowsHints;

    public bool ShowsSquadLine => State.HasSquad && (State.IsVerdictFocus || _fold >= 3);

    public bool ShowsSquadRows => State.IsNotVerdictFocus && _fold < 3;

    public bool ShowsNextBlock => State.ShowsNext && _fold < 4;

    public bool ShowsScanBlock => State.ShowsScan && State.IsNotVerdictFocus && !(_fold >= 5 && State.HasNoScan);

    public bool ShowsScanHint => State.ShowsScanHint && ShowsHints;

    private void RaiseFold()
    {
        OnPropertyChanged(nameof(ShowsHints));
        OnPropertyChanged(nameof(IsYouCompact));
        OnPropertyChanged(nameof(ShowsYouExit));
        OnPropertyChanged(nameof(ShowsWrong));
        OnPropertyChanged(nameof(VerdictRows));
        OnPropertyChanged(nameof(SquadLineLines));
        OnPropertyChanged(nameof(MoreLines));
        OnPropertyChanged(nameof(ShowsLeaveExitNote));
        OnPropertyChanged(nameof(NextLabelLines));
        OnPropertyChanged(nameof(ShowsNextThen));
        OnPropertyChanged(nameof(ShowsSquadLine));
        OnPropertyChanged(nameof(ShowsSquadRows));
        OnPropertyChanged(nameof(ShowsNextBlock));
        OnPropertyChanged(nameof(ShowsScanBlock));
        OnPropertyChanged(nameof(ShowsScanHint));
    }
}
