using Avalonia.Threading;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Shell;
using TarkovCompanion.App.Services.Updates;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels;

public abstract class BindableViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class DelegateCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}

/// <summary>A command that acts on the row it was invoked from.</summary>
/// <remarks>
/// <see cref="DelegateCommand"/> discards its parameter, which is right for a button that means
/// one thing and wrong for a button inside a list, where which row it sat in is the whole of
/// what it meant.
/// </remarks>
public sealed class ParameterCommand<T>(Action<T?> execute) : ICommand
    where T : class
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter as T);
}

public sealed class AsyncDelegateCommand(Func<Task> execute) : ICommand
{
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning;

    public async void Execute(object? parameter) => await ExecuteAsync().ConfigureAwait(true);

    public async Task ExecuteAsync()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute().ConfigureAwait(true);
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class NavigationItem : BindableViewModel
{
    private bool _isSelected;
    private bool _hasNotice;

    internal NavigationItem(string name, string glyph, PageViewModel page, Action<NavigationItem> select)
    {
        Name = name;
        Glyph = glyph;
        Page = page;
        SelectCommand = new DelegateCommand(() => select(this));
    }

    public string Name { get; }

    /// <summary>
    /// Whether a hairline is drawn above this row.
    /// </summary>
    /// <remarks>
    /// Fourteen rows in one ungrouped run, nine of them between-raid reference pages, is a list
    /// somebody reads from the top every time because nothing in it says where to start looking.
    /// Three rules and the rest follows: during a raid, between raids, and the record.
    ///
    /// A hairline rather than a heading, because a heading is a row that cannot be clicked and
    /// the rail is short of height, not of labels. It also survives the rail collapsing to
    /// glyphs, where a heading could not.
    /// </remarks>
    public bool StartsGroup { get; init; }

    /// <summary>
    /// The icon, as path geometry rather than a character.
    /// </summary>
    /// <remarks>
    /// These were Unicode characters picked by eye, from completely different blocks: a map
    /// symbol, a dingbat, an arrow, a rouble sign. Each block resolves to a different fallback
    /// font, so every row rendered at its own size, weight, stroke and baseline, and the rail
    /// looked ragged for a reason nobody could point at. Keys was tiny, Loadout was a dense
    /// block, Flea was a letter.
    ///
    /// Drawn here they share one grid, one stroke width and one cap style, because they are one
    /// set rather than fourteen characters that happened to be available.
    /// </remarks>
    public string Glyph { get; }

    public PageViewModel Page { get; }

    public ICommand SelectCommand { get; }

    /// <summary>
    /// Whether this destination is asking to be visited.
    /// </summary>
    /// <remarks>
    /// One mark, on the rail, so it is visible from whatever page somebody is on. Putting the
    /// news inside the page it concerns means the only way to learn there is news is to go
    /// looking for it, which is the state this replaces: a new build could sit in the feed for
    /// days because nobody opened Settings.
    /// </remarks>
    public bool HasNotice
    {
        get => _hasNotice;
        set => SetProperty(ref _hasNotice, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}

public sealed record StatusChip(string Label, string Value, string Evidence, string Color)
{
    /// <summary>
    /// Whether the chip belongs to the raid readout rather than the health strip.
    /// </summary>
    /// <remarks>
    /// Map, raid and position are what a player glances at mid-raid. Whether the game is
    /// being observed, how old the data is and whether a scan is possible only matter when
    /// they are wrong, so the shell draws those smaller and lets their colour do the talking.
    /// </remarks>
    public bool IsPrimary { get; init; }
}

/// <summary>
/// Says what the game is doing, in the words a player would use.
/// </summary>
/// <remarks>
/// These used to reach the interface through ToString(), so the readout a player glances at
/// mid-raid said "InRaid", "PostRaid" and, best of all, "LauncherOrGameDetected". Those are
/// names for the code's benefit. Nobody playing the game calls it that, and the longest one
/// appears exactly when somebody is first wondering whether the companion is working.
/// </remarks>
public static class RaidStateText
{
    public static string Describe(RaidLifecycleState state) => state switch
    {
        RaidLifecycleState.InRaid => "In raid",
        RaidLifecycleState.LoadingRaid => "Loading into a raid",
        RaidLifecycleState.PostRaid => "Raid over",
        RaidLifecycleState.Menu => "In the menus",
        RaidLifecycleState.LauncherOrGameDetected => "Game running",
        _ => "Not observed",
    };
}

public abstract class PageViewModel(string title, string description, string evidence) : BindableViewModel
{
    /// <summary>
    /// Whether the shell draws its title block above this page.
    /// </summary>
    /// <remarks>
    /// A page that is mostly one large picture wants the height more than it wants a heading
    /// that repeats what the picture already says. The map page names the current map on the
    /// map itself, so the shell's copy of it was eighty pixels spent saying it twice.
    /// </remarks>
    public virtual bool ShowsPageHeader => true;

    private string _title = title;
    private string _description = description;
    private string _evidence = evidence;

    public string Title
    {
        get => _title;
        protected set => SetProperty(ref _title, value);
    }

    public string Description
    {
        get => _description;
        protected set => SetProperty(ref _description, value);
    }

    public string Evidence
    {
        get => _evidence;
        protected set => SetProperty(ref _evidence, value);
    }
}

/// <summary>One extract this raid is offering, as a row.</summary>
/// <param name="Name">What the game calls it.</param>
/// <param name="Confidence">How sure the reading was, because a poor match is worth a second look.</param>
/// <param name="Evidence">Which engine read it and when, for when the reading turns out wrong.</param>
public sealed record ActiveExtractViewModel(string Name, string Confidence, string Evidence);

/// <summary>One of the two bars the game draws in the corner of the screen.</summary>
/// <remarks>
/// Named by the colour a player can see. What each bar measures has not been established, and
/// calling one "stamina" would be a guess printed as a fact — while somebody looking at their
/// own screen knows which is which by looking at it.
/// </remarks>
/// <param name="Kind">"Blue" or "Green", as the player sees them.</param>
/// <param name="Detail">How full, against the longest seen this raid.</param>
public sealed record HudBarViewModel(string Kind, string Detail);

/// <summary>
/// One map the group could queue, and why.
/// </summary>
/// <remarks>
/// The counts are the whole row. "You: 3 (1 pinned) · Geo: 2" is a sentence somebody reads
/// once and then says out loud, which is what this replaces: four people reading their quest
/// lists to each other before every session.
///
/// Named per person rather than totalled, because a map where one person has five and nobody
/// else has any is a different night from one where four people have two each, and a single
/// number cannot tell them apart.
/// </remarks>
public sealed class TonightMapViewModel
{
    public TonightMapViewModel(string mapName, TonightMapRow row, Func<Task> show)
    {
        MapName = mapName;
        Row = row;
        Detail = Describe(row);
        ShowCommand = new AsyncDelegateCommand(show);
    }

    /// <summary>What the map is called here, rather than the id the catalog keys it by.</summary>
    public string MapName { get; }

    public TonightMapRow Row { get; }

    /// <summary>Who has what here, in the order a person would say it.</summary>
    public string Detail { get; }

    /// <summary>Quests across the group, which is the number the list is ordered by.</summary>
    public int Total => Row.Total;

    /// <summary>Switches the map to this one and lights its objectives.</summary>
    public AsyncDelegateCommand ShowCommand { get; }

    /// <summary>
    /// You first, then everyone else, and nobody with nothing to do here.
    /// </summary>
    /// <remarks>
    /// Your own count leads because it is the one the reader can act on alone. The pinned
    /// count rides along in brackets rather than as its own column: it is you saying which of
    /// your own quests matter, and it only means anything beside the count it is part of.
    /// </remarks>
    private static string Describe(TonightMapRow row)
    {
        var parts = new List<string>();
        if (row.Yours > 0)
        {
            parts.Add(row.YoursPinned > 0
                ? string.Create(CultureInfo.CurrentCulture, $"You: {row.Yours} ({row.YoursPinned} pinned)")
                : string.Create(CultureInfo.CurrentCulture, $"You: {row.Yours}"));
        }

        parts.AddRange(row.Others.Select(other =>
            string.Create(CultureInfo.CurrentCulture, $"{other.Name}: {other.Count}")));
        return string.Join(" · ", parts);
    }
}

public sealed class RaidPageViewModel : PageViewModel
{
    /// <inheritdoc />
    /// <remarks>
    /// The map is the page. Everything the header would have said is either on the map or in
    /// the status chips along the top of the window.
    /// </remarks>
    public override bool ShowsPageHeader => false;

    private readonly IRaidHistoryService? _raidHistoryService;
    private readonly List<string> _scannedThisRaid = [];
    private string _raidState = "No raid evidence";
    private string _position = "No last-known position";
    private IReadOnlyList<ActiveExtractViewModel> _extracts = [];
    private string _extractsNotMatched = string.Empty;
    private string _transits = string.Empty;
    private RaidSummaryViewModel? _summary;
    private DateTimeOffset? _summaryShownUtc;
    private RaidSnapshot? _lastInRaid;
    private string? _lastInRaidMode;
    private RaidLifecycleState _previousState = RaidLifecycleState.Unknown;

    /// <summary>How long the summary stays up before it closes on its own.</summary>
    /// <remarks>
    /// Reported as "it just stays open right now": today it only closes on its dismiss button,
    /// Escape, or the next raid starting. Fifteen minutes is well past the ninety seconds or so
    /// somebody actually reads it in, so this only ever fires on a card genuinely left open.
    /// </remarks>
    private static readonly TimeSpan SummaryAutoDismissAfter = TimeSpan.FromMinutes(15);
    private Guid? _summaryRaidId;
    private Guid? _scanRaidId;
    private DateTimeOffset _lastScanObservedUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Creates the raid page.
    /// </summary>
    /// <remarks>
    /// The raid history service is optional because the summary does not depend on it: every
    /// line is built from state the page has already observed. History is read afterwards
    /// only to confirm the finished raid reached the local database, so when no service is
    /// supplied that one line says the history was not read and the rest is unaffected.
    /// </remarks>
    private readonly IMapDataService? _maps;
    /// <summary>Raid lengths already looked up, keyed by map and side together.</summary>
    /// <remarks>
    /// By map alone, it could only ever hold one of the two durations, and what it held was the
    /// PMC one whatever side the player was on.
    /// </remarks>
    private readonly Dictionary<string, TimeSpan?> _raidLengths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last raid we were told about, so a tick has something to recompute from.</summary>
    private RaidSnapshot? _lastRaid;
    private IReadOnlyList<HudBarViewModel> _hudBars = [];
    private string _hudDetail = string.Empty;
    private readonly IItemRepository? _items;
    private readonly Func<string, string>? _nameTask;
    private readonly Func<IReadOnlyList<QuestSummaryReadModel>>? _questBoard;
    private IReadOnlyList<TonightMapViewModel> _tonight = [];
    private string _tonightSignature = string.Empty;
    private string _timeLeft = "Unknown";
    private string _timeLeftDetail = "No raid in progress";

    /// <summary>Walking a finished raid back across the map, when one has been opened.</summary>
    public RaidReplayViewModel Replay { get; }

    /// <summary>How long is left, as the game would draw it.</summary>
    public string TimeLeft
    {
        get => _timeLeft;
        private set => SetProperty(ref _timeLeft, value);
    }

    /// <summary>
    /// Whether that was read off a screenshot or counted from the start.
    /// </summary>
    /// <remarks>
    /// The two are not equal claims. A number read off the extract list is the game's own; a
    /// count from the raid's confirmation against the map's stated length is an estimate that
    /// drifts, and presenting one as the other would be a claim this cannot make.
    /// </remarks>
    public string TimeLeftDetail
    {
        get => _timeLeftDetail;
        private set => SetProperty(ref _timeLeftDetail, value);
    }

    public RaidPageViewModel(
        MapViewModel map,
        IRaidHistoryService? raidHistoryService = null,
        // Optional only so the compositions that build this by hand keep working; without it
        // the timer falls back to whatever a screenshot last showed.
        IMapDataService? maps = null,
        // Names the items a raid's record only holds ids for. Without it the flea line reads
        // as hexadecimal, which is the defect the trader ids had until they were resolved.
        IItemRepository? items = null,
        // Names a task the same way. A function rather than the quest service, because the
        // shell already holds a loaded quest board and a second read of the catalog here would
        // be a second answer to a question already answered.
        Func<string, string>? nameTask = null,
        // The quest board the shell already holds, for ranking tonight's maps by where the
        // group's quests overlap. A function rather than the quest service for the reason the
        // name lookup above is one: the board is loaded and reading the catalog again here
        // would be a second answer to a question already answered.
        Func<IReadOnlyList<QuestSummaryReadModel>>? questBoard = null)
        : base("Raid reference", "The map, and where your screenshots put you", "No raid evidence")
    {
        Map = map;
        _raidHistoryService = raidHistoryService;
        _maps = maps;
        _items = items;
        _nameTask = nameTask;
        _questBoard = questBoard;
        Replay = new(map);
        DismissSummaryCommand = new DelegateCommand(DismissSummary);
    }

    public MapViewModel Map { get; }

    /// <summary>
    /// The raid that has just finished, or null when none has finished this session.
    /// </summary>
    /// <remarks>
    /// Deliberately sticky. It is set on the single transition out of a raid and then left
    /// alone: the snapshots that follow a raid end arrive seconds apart, and clearing on any
    /// of them would make the summary flash past before it could be read. The player
    /// dismisses it, or the next finished raid replaces it.
    /// </remarks>
    public RaidSummaryViewModel? Summary
    {
        get => _summary;
        private set
        {
            if (SetProperty(ref _summary, value))
            {
                OnPropertyChanged(nameof(HasSummary));
            }
        }
    }

    /// <summary>Whether a finished raid is available to show.</summary>
    public bool HasSummary => Summary is not null;

    /// <summary>
    /// Puts the summary away.
    /// </summary>
    /// <remarks>
    /// The summary sits above the live raid panels, so without a way to dismiss it the panel
    /// would still be covering the top of the page during the next raid.
    /// </remarks>
    public DelegateCommand DismissSummaryCommand { get; }

    public string RaidState
    {
        get => _raidState;
        private set => SetProperty(ref _raidState, value);
    }

    public string Position
    {
        get => _position;
        private set => SetProperty(ref _position, value);
    }

    /// <summary>
    /// The extracts this raid is offering, one per row.
    /// </summary>
    /// <remarks>
    /// Reported as "extracts were detected but they format pretty useless for a human", with a
    /// screenshot of this:
    ///
    /// <code>
    /// Bridge V-Ex (99%, ocr:tesseract-5.5.1-wrapper-5.5.2; map=woods; status=Active;
    /// observedUtc=2026-09-13T03:12:22.8604023+00:00), Power Line Passage (Flare) (81%, ...)
    /// </code>
    ///
    /// That is the recognition's own diagnostic, printed verbatim and joined with commas into
    /// a paragraph. It says which engine read it and at what instant, neither of which is a
    /// thing anybody reads while deciding where to leave from. The name is, and the confidence
    /// is, because a 60% match is worth a second look before running across a map.
    ///
    /// The diagnostic is kept on the row as a tooltip rather than deleted. It is exactly what
    /// is wanted when a recognised extract turns out to be the wrong one.
    /// </remarks>
    public IReadOnlyList<ActiveExtractViewModel> Extracts
    {
        get => _extracts;
        private set
        {
            SetProperty(ref _extracts, value);
            OnPropertyChanged(nameof(HasExtracts));
        }
    }

    public bool HasExtracts => Extracts.Count > 0;

    /// <summary>
    /// What the last extract scan read off that screen and could not place.
    /// </summary>
    /// <remarks>
    /// A scan that matches one exit out of eight and a screen that has one exit on it both
    /// read "Extracts: one". Saying what else was on the screen is the difference between a
    /// report of a bug and a report that is its own diagnosis, and it cost a round trip to a
    /// machine with the screenshots on it to establish that once already.
    /// </remarks>
    public string ExtractsNotMatched
    {
        get => _extractsNotMatched;
        private set
        {
            SetProperty(ref _extractsNotMatched, value);
            OnPropertyChanged(nameof(HasExtractsNotMatched));
        }
    }

    public bool HasExtractsNotMatched => ExtractsNotMatched.Length > 0;

    /// <summary>
    /// The ways to another map this screen offered, which no extract catalog contains.
    /// </summary>
    public string Transits
    {
        get => _transits;
        private set
        {
            SetProperty(ref _transits, value);
            OnPropertyChanged(nameof(HasTransits));
        }
    }

    public bool HasTransits => Transits.Length > 0;

    public void Apply(ApplicationRuntimeSnapshot snapshot, DateTimeOffset nowUtc)
    {
        var raid = snapshot.Raid;
        Title = raid.MapId is null ? "Raid reference" : $"{raid.MapId} raid";
        RaidState = raid.State == RaidLifecycleState.Unknown
            ? "No raid state has been observed."
            : $"{RaidStateText.Describe(raid.State)} · {FormatAge(raid.UpdatedUtc, nowUtc)}";
        Position = raid.LastKnownPosition is null
            ? "No position yet"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"X {raid.LastKnownPosition.Position.X:F1}, Y {raid.LastKnownPosition.Position.Y:F1}, Z {raid.LastKnownPosition.Position.Z:F1} · screenshot {FormatAge(raid.LastKnownPosition.Timestamp, nowUtc)}");
        // The active-extracts panel is only about the raid running right now. Without this
        // gate it kept showing the just-finished raid's confirmed exits and transit offers
        // straight through PostRaid, on top of a summary card describing that same raid.
        var isInRaid = raid.State == RaidLifecycleState.InRaid;
        Extracts = !isInRaid
            ? []
            : raid.ActiveExtracts
                .Select(extract => new ActiveExtractViewModel(
                    extract.Name,
                    string.Create(CultureInfo.CurrentCulture, $"{extract.Confidence.Value:P0} sure"),
                    extract.Source))
                .ToArray();
        ExtractsNotMatched = !isInRaid
            ? string.Empty
            : raid.ExtractLinesNotMatched.Count switch
            {
                0 => string.Empty,
                1 => $"1 line on that screen was not matched to an exit: {raid.ExtractLinesNotMatched[0]}",
                var count => string.Create(
                    CultureInfo.CurrentCulture,
                    $"{count} lines on that screen were not matched to an exit: ") +
                    string.Join(" · ", raid.ExtractLinesNotMatched.Take(12)),
            };
        Transits = !isInRaid || raid.Transits.Count == 0
            ? string.Empty
            : "Transits offered: " + string.Join(" · ", raid.Transits);
        _lastRaid = raid;
        UpdateTimeLeft(raid, nowUtc);
        UpdateHud(raid, nowUtc);
        Evidence = raid.UpdatedUtc == DateTimeOffset.UnixEpoch
            ? "No raid evidence"
            : $"{raid.Confidence.Value:P0} confidence · observed {FormatAge(raid.UpdatedUtc, nowUtc)}";
        UpdateTonight(snapshot);
        ObserveLifecycle(snapshot, nowUtc);
    }

    /// <summary>
    /// Which map the group should queue, while there is still time to choose one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only in the menu. It is the decision made before a raid and it is not a decision any
    /// more once one has started, so a card offering to switch the map mid-raid would be in
    /// the way of the map the player is actually standing on.
    /// </para>
    /// <para>
    /// Ranked on every tick and the rows are replaced only when the answer changes. The
    /// snapshot arrives every few seconds and the rows carry commands; swapping them each time
    /// would rebuild the list under anybody in the middle of clicking a row. The ranking
    /// itself is a walk of the quest board, which is cheap enough to do rather than cache and
    /// then have to work out when the cache is wrong.
    /// </para>
    /// </remarks>
    private void UpdateTonight(ApplicationRuntimeSnapshot snapshot)
    {
        if (_questBoard is null)
        {
            return;
        }

        var rows = Choose(
            snapshot.Raid.State,
            _questBoard(),
            snapshot.Group.Members,
            Map.Locations,
            ShowTonightMapAsync);
        var signature = string.Join("|", rows.Select(row => $"{row.MapName}={row.Detail}"));
        if (signature == _tonightSignature)
        {
            return;
        }

        _tonightSignature = signature;
        Tonight = rows;
    }

    /// <summary>
    /// Which maps to offer, given what the group is working on and where the player is.
    /// </summary>
    /// <remarks>
    /// A static seam, the way the group panel beside the map has one: this is the whole of the
    /// decision, and the alternative is a test that stands up a map view model to read three
    /// lines of text out of it.
    /// </remarks>
    /// <param name="state">Where the player is, because this is only a question in the menu.</param>
    /// <param name="board">Every task the local catalog knows, with this profile's progress.</param>
    /// <param name="members">The rest of the group, as they last described themselves.</param>
    /// <param name="locations">The maps this companion can actually show.</param>
    /// <param name="show">What a row does when it is clicked.</param>
    public static IReadOnlyList<TonightMapViewModel> Choose(
        RaidLifecycleState state,
        IReadOnlyList<QuestSummaryReadModel> board,
        IReadOnlyList<GroupMemberView> members,
        IReadOnlyList<MapLocation> locations,
        Func<string, Task> show)
    {
        if (state != RaidLifecycleState.Menu)
        {
            return [];
        }

        var rows = new List<TonightMapViewModel>();
        foreach (var row in TonightMaps.Rank(board, members))
        {
            // A map this catalog cannot name is a map this card cannot switch to, and a row
            // that does nothing when clicked is worse than a row that is not there.
            var location = locations.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, row.MapId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.SourceId, row.MapId, StringComparison.OrdinalIgnoreCase));
            if (location is null)
            {
                continue;
            }

            var mapId = location.Id;
            rows.Add(new(location.Name, row, () => show(mapId)));
        }

        return rows;
    }

    /// <summary>Switches to that map and lights the objectives on it.</summary>
    /// <remarks>
    /// Both halves, because the card's claim is "there are four things to do here" and
    /// switching to a map with the quest layer off would answer it with an empty map. The
    /// layer is left on afterwards: it was turned on by somebody asking to see quests.
    /// </remarks>
    private async Task ShowTonightMapAsync(string mapId)
    {
        try
        {
            await Map.FollowRaidAsync(mapId).ConfigureAwait(true);
            Map.ToggleOverlay(MapOverlayKind.QuestObjectives, true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Map.ReportInteractionFailure(exception.Message);
        }
    }

    /// <summary>
    /// Where the group's quests overlap, best map first.
    /// </summary>
    /// <remarks>
    /// Empty outside the menu and empty when nothing overlaps, which includes playing alone
    /// with no quests on any map this companion can place.
    /// </remarks>
    public IReadOnlyList<TonightMapViewModel> Tonight
    {
        get => _tonight;
        private set
        {
            if (SetProperty(ref _tonight, value))
            {
                OnPropertyChanged(nameof(HasTonight));
            }
        }
    }

    public bool HasTonight => Tonight.Count > 0;

    /// <summary>
    /// What the game's own display said, as the last screenshot showed it.
    /// </summary>
    /// <remarks>
    /// Read on every frame, turned into one evidence string, and dropped: HudBar.Fraction had
    /// no production caller anywhere. So a player who photographed themselves at a quarter of
    /// something had handed the number over and been told nothing.
    ///
    /// Named by colour, because what each bar measures has not been established — the code that
    /// reads them says so outright, and a player looking at their own screen knows which is
    /// which by looking at it. Calling one "stamina" would be a guess printed as a fact.
    /// </remarks>
    public IReadOnlyList<HudBarViewModel> HudBars
    {
        get => _hudBars;
        private set
        {
            SetProperty(ref _hudBars, value);
            OnPropertyChanged(nameof(HasHudBars));
        }
    }

    public bool HasHudBars => _hudBars.Count > 0;

    /// <summary>Whether the display was in the frame, and how old the frame is.</summary>
    public string HudDetail
    {
        get => _hudDetail;
        private set => SetProperty(ref _hudDetail, value);
    }

    public bool HasHudDetail => _hudDetail.Length > 0;

    private void UpdateHud(RaidSnapshot raid, DateTimeOffset nowUtc)
    {
        if (raid.Hud is not { } hud)
        {
            HudBars = [];
            HudDetail = string.Empty;
            return;
        }

        HudBars = hud.IsPresent
            ? [.. hud.Bars.Select(bar => new HudBarViewModel(bar.Kind, Describe(bar)))]
            : [];

        // The age is on the reading rather than on each bar. Bars move continuously, so a
        // reading four minutes old is a claim about four minutes ago, and showing it without
        // saying so is the difference between a readout and a guess.
        HudDetail = hud.IsPresent
            ? $"From your screenshot {FormatAge(hud.ReadUtc, nowUtc)}"
            : $"{hud.Detail} · {FormatAge(hud.ReadUtc, nowUtc)}";
    }

    /// <summary>
    /// How full one bar is, against the longest it has been this raid.
    /// </summary>
    /// <remarks>
    /// The first reading has nothing to compare against and says so, rather than reading
    /// "100%" — which would be right only by accident, and wrong in the one case that matters:
    /// the first screenshot somebody takes after running themselves empty.
    /// </remarks>
    private static string Describe(RaidHudBar bar) => bar.Fraction is { } fraction
        ? string.Create(CultureInfo.CurrentCulture, $"{fraction:P0} of the longest seen this raid")
        : "the longest seen this raid so far";

    /// <summary>
    /// How long is left, from whichever source has the better claim.
    /// </summary>
    /// <remarks>
    /// The map's length is looked up once per map and remembered, because this runs on every
    /// runtime snapshot and a map's raid length does not change between them.
    /// </remarks>
    /// <summary>
    /// Recomputes how long is left, from a fresh now and the last raid we were told about.
    /// </summary>
    /// <remarks>
    /// Nothing in this application ticks: Apply runs only when the runtime store changes, and
    /// between events that can be minutes. So "Time left" sat frozen at whatever it said when
    /// the last screenshot landed, which is a readout that is wrong more often than it is
    /// right and cannot be told apart from one that is working.
    ///
    /// Only the time-sensitive part is redone. Re-applying the whole snapshot every second
    /// would run fourteen page view models and five map calls to move one clock.
    /// </remarks>
    public void Tick(DateTimeOffset nowUtc)
    {
        if (Summary is not null &&
            _summaryShownUtc is { } shownUtc &&
            nowUtc - shownUtc >= SummaryAutoDismissAfter)
        {
            DismissSummary();
        }

        if (_lastRaid is { } raid)
        {
            UpdateTimeLeft(raid, nowUtc);
            // The age on the display reading moves with the clock beside it, for the same
            // reason: a reading whose age is frozen is a reading that looks current.
            UpdateHud(raid, nowUtc);
        }
    }

    private void UpdateTimeLeft(RaidSnapshot raid, DateTimeOffset nowUtc)
    {
        if (raid.State != RaidLifecycleState.InRaid)
        {
            TimeLeft = "Unknown";
            TimeLeftDetail = "No raid in progress";
            return;
        }

        var remaining = RaidTimer.Resolve(
            raid.RaidClock is { } clock && raid.RaidClockReadUtc is { } readUtc ? (clock, readUtc) : null,
            raid.StartedUtc,
            LengthFor(raid),
            nowUtc);
        TimeLeft = remaining.Display;
        TimeLeftDetail = remaining.Detail;
    }

    /// <summary>
    /// How long a raid on this map runs, for the side it is being run as.
    /// </summary>
    /// <remarks>
    /// The catalog states a PMC length and, where it knows one, a scav length. A scav raid is
    /// the shorter of the two and using the PMC length for it would promise time nobody has.
    /// </remarks>
    private TimeSpan? LengthFor(RaidSnapshot raid)
    {
        if (raid.MapId is not { Length: > 0 } mapId || _maps is null)
        {
            return null;
        }

        // Side included, because the answer differs by it. A scav raid counted from the PMC
        // length promises time nobody has: on Customs that is forty minutes against about
        // twenty-five.
        var key = $"{mapId}|{raid.Side ?? string.Empty}";
        if (!_raidLengths.TryGetValue(key, out var length))
        {
            // Remembered before the lookup returns, so a snapshot every second does not start
            // a query every second while the first one is still running.
            _raidLengths[key] = null;
            _ = RememberLengthAsync(key, mapId, raid.Side);
            return null;
        }

        return length;
    }

    private async Task RememberLengthAsync(string key, string mapId, string? side)
    {
        try
        {
            var map = await _maps!.GetAsync(mapId, CancellationToken.None).ConfigureAwait(true);
            _raidLengths[key] = RaidTimer.LengthFor(side, map?.PmcRaidDuration, map?.ScavRaidDuration);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A length nobody could read means the timer counts nothing rather than counting
            // down from a number that was invented.
            _raidLengths[key] = null;
        }
    }

    /// <summary>
    /// Notices that a raid has ended and builds the summary of it.
    /// </summary>
    /// <remarks>
    /// The end of a raid is the single edge from InRaid to PostRaid or Menu. A transfer to
    /// another map keeps the state at InRaid by design, so no edge occurs and no summary is
    /// produced for a raid that is still being played.
    ///
    /// The details of the raid are taken from the last snapshot seen while it was running,
    /// not from the snapshot that ends it: returning to the menu clears the map, the start
    /// time and the last-known position out of the raid state.
    /// </remarks>
    private void ObserveLifecycle(ApplicationRuntimeSnapshot snapshot, DateTimeOffset nowUtc)
    {
        var raid = snapshot.Raid;
        TrackScans(snapshot);

        // The next raid starting closes the summary of the one before it, so a stale card is
        // never still open once there is a fresh raid to look at. A transfer keeps the state
        // at InRaid throughout, so it is not this edge and never closes anything.
        var wasNotRunning = _previousState is not (RaidLifecycleState.LoadingRaid or RaidLifecycleState.InRaid);
        var isNowRunning = raid.State is RaidLifecycleState.LoadingRaid or RaidLifecycleState.InRaid;
        if (wasNotRunning && isNowRunning && Summary is not null)
        {
            DismissSummary();
        }

        if (raid.State == RaidLifecycleState.InRaid)
        {
            _lastInRaid = raid;
            _lastInRaidMode = snapshot.Profile?.GameMode.ToString();
        }

        var finished = _previousState == RaidLifecycleState.InRaid
            && raid.State is RaidLifecycleState.PostRaid or RaidLifecycleState.Menu;
        _previousState = raid.State;
        if (!finished || _lastInRaid is not { } lastInRaid)
        {
            return;
        }

        // Cleared immediately so a further Menu snapshot after PostRaid cannot rebuild the
        // same summary and reset the history line that is already being filled in.
        _lastInRaid = null;
        _summaryRaidId = lastInRaid.RaidId;
        _summaryShownUtc = nowUtc;
        Summary = RaidSummaryViewModel.Create(
            ResolveMapName(lastInRaid.MapId),
            lastInRaid.StartedUtc,
            raid.UpdatedUtc,
            _lastInRaidMode ?? snapshot.Profile?.GameMode.ToString(),
            // The ending notification is preferred over the raid's own record, because a
            // transfer establishes the side outright and only arrives on the line that ends
            // the raid. The raid's own record is the fallback for every other ending.
            raid.Side ?? lastInRaid.Side,
            _scannedThisRaid.ToArray(),
            lastInRaid.LastKnownPosition,
            _raidHistoryService is null ? "Not read" : "Checking…");
        if (_raidHistoryService is not null && lastInRaid.RaidId is { } raidId)
        {
            // Deliberately not awaited: the summary is already complete and correct, and the
            // snapshot handler that called this runs on the UI thread.
            _ = ConfirmAgainstHistoryAsync(raidId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Remembers what the player scanned while this raid was open.
    /// </summary>
    /// <remarks>
    /// These are exactly the scans the raid coordinator writes to raid history, which records
    /// a scan event whenever a raid id is open. The history service exposes no way to read
    /// those events back, so the page counts the same scans as they pass through the runtime
    /// snapshot instead of querying for them.
    ///
    /// A new raid takes the scan already on screen as its baseline, so an item looked up in
    /// the menu beforehand is never attributed to the raid that follows it.
    /// </remarks>
    private void TrackScans(ApplicationRuntimeSnapshot snapshot)
    {
        if (snapshot.Raid.RaidId is not { } raidId)
        {
            return;
        }

        if (_scanRaidId != raidId)
        {
            _scanRaidId = raidId;
            _scannedThisRaid.Clear();
            _lastScanObservedUtc = snapshot.Scan.ObservedUtc;
            return;
        }

        var scan = snapshot.Scan;
        if (!scan.Succeeded
            || scan.ObservedUtc <= _lastScanObservedUtc
            || (snapshot.Raid.StartedUtc is { } startedUtc && scan.ObservedUtc < startedUtc))
        {
            return;
        }

        _lastScanObservedUtc = scan.ObservedUtc;
        _scannedThisRaid.Add(scan.ItemName ?? "Unnamed item");
    }

    /// <summary>
    /// Confirms that the finished raid reached the local history database.
    /// </summary>
    /// <remarks>
    /// This is the one part of the summary that cannot be answered from observed state, and
    /// it is worth answering: it is the difference between a raid the player can still export
    /// tomorrow and one that was only ever on screen. A failure is reported in the summary
    /// rather than thrown, because the rest of the summary remains true regardless.
    /// </remarks>
    private async Task ConfirmAgainstHistoryAsync(Guid raidId, CancellationToken cancellationToken)
    {
        if (_raidHistoryService is null)
        {
            return;
        }

        string history;
        var quests = string.Empty;
        var sales = string.Empty;
        try
        {
            var raids = await _raidHistoryService.ListAsync(cancellationToken).ConfigureAwait(true);
            var entry = raids.FirstOrDefault(candidate => candidate.Id == raidId);
            history = entry is null
                ? "Not saved"
                : entry.EndedUtc is { } endedUtc
                    ? string.Create(CultureInfo.CurrentCulture, $"Saved · closed {endedUtc.ToLocalTime():g}")
                    : "Saved · no end time";

            // Out of the raid's own record rather than counted as they went past. Counting
            // works only while the application that saw them is still running; this is the
            // answer a raid opened from History a week later would get.
            quests = await DescribeQuestsAsync(raidId, cancellationToken).ConfigureAwait(true);
            sales = await DescribeSalesAsync(raidId, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            history = $"Unreadable · {exception.Message}";
        }

        // A raid that finished while this was running has already replaced the summary, and
        // the older answer must not overwrite the newer one.
        if (_summaryRaidId == raidId && Summary is { } summary)
        {
            Summary = summary with { History = history, Quests = quests, Sales = sales };
        }
    }

    /// <summary>
    /// The quests the game announced during one raid, named where the catalog knows them.
    /// </summary>
    /// <remarks>
    /// Only hand-ins and failures. A quest starting during a raid is the game catching up with
    /// a decision made in the menu, and listing it under "what happened in that raid" would
    /// credit the raid with something it did not do.
    /// </remarks>
    private async Task<string> DescribeQuestsAsync(Guid raidId, CancellationToken cancellationToken)
    {
        var payloads = await _raidHistoryService!
            .ListEventPayloadsAsync(raidId, "quest", cancellationToken)
            .ConfigureAwait(true);
        var named = new List<string>();
        foreach (var payload in payloads)
        {
            if (Read<QuestStatusObservation>(payload) is not { } quest ||
                quest.State is not (RecordedTaskState.Completed or RecordedTaskState.Failed))
            {
                continue;
            }

            var name = _nameTask?.Invoke(quest.TaskId) ?? quest.TaskId;
            var line = quest.State == RecordedTaskState.Failed ? $"{name} (failed)" : name;
            if (!named.Contains(line, StringComparer.Ordinal))
            {
                named.Add(line);
            }
        }

        return named.Count == 0 ? string.Empty : "Handed in: " + string.Join(" · ", named);
    }

    /// <summary>What sold on the flea while one raid was open.</summary>
    /// <remarks>
    /// Counted per item rather than per offer, because two offers for the same thing is one
    /// sentence a player would say: "two Salewas went".
    /// </remarks>
    private async Task<string> DescribeSalesAsync(Guid raidId, CancellationToken cancellationToken)
    {
        var payloads = await _raidHistoryService!
            .ListEventPayloadsAsync(raidId, "sale", cancellationToken)
            .ConfigureAwait(true);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            if (Read<FleaSaleObservation>(payload) is not { HandbookItemId: { Length: > 0 } itemId } sale)
            {
                continue;
            }

            counts[itemId] = counts.GetValueOrDefault(itemId) + Math.Max(1, sale.Count);
        }

        if (counts.Count == 0)
        {
            return string.Empty;
        }

        // Named from the catalog rather than from the Quests page's own cache, which only holds
        // items looked up for a selected quest — so a sold item would almost always have come
        // out as its id. That is the defect the trader ids had until #194 and the Bring lines
        // had until they were given a lookup of their own.
        var named = new List<string>(counts.Count);
        foreach (var (itemId, count) in counts)
        {
            var item = _items is null
                ? null
                : await _items.GetAsync(itemId, cancellationToken).ConfigureAwait(true);
            named.Add($"{count}× {item?.Name ?? itemId}");
        }

        return "Sold: " + string.Join(" · ", named);
    }

    /// <summary>
    /// Reads one stored payload, or nothing rather than failing the whole summary.
    /// </summary>
    /// <remarks>
    /// A row that will not parse costs its own line. The summary appears when a raid ends and
    /// is the one moment somebody is definitely looking at the application, so it is the worst
    /// possible place to raise an exception over a malformed row.
    /// </remarks>
    private static T? Read<T>(string payload)
        where T : class
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Names the map the way the map catalogue does, falling back to the logged token.
    /// </summary>
    /// <remarks>
    /// The game logs tokens such as "bigmap" rather than display names, and the catalogue is
    /// only loaded once maps have synced, so the raw token has to remain an acceptable answer.
    /// </remarks>
    private string ResolveMapName(string? mapId) => string.IsNullOrWhiteSpace(mapId)
        ? "Unknown map"
        : Map.Locations.FirstOrDefault(location =>
            string.Equals(location.Id, mapId, StringComparison.OrdinalIgnoreCase))?.Name ?? mapId;

    private void DismissSummary()
    {
        _summaryRaidId = null;
        _summaryShownUtc = null;
        Summary = null;
    }

    private static string FormatAge(DateTimeOffset observedUtc, DateTimeOffset nowUtc)
    {
        var age = nowUtc - observedUtc;
        return age < TimeSpan.Zero ? "timestamp is in the future" : $"{Math.Max(0, (int)age.TotalSeconds)}s ago";
    }
}

public sealed record ItemSearchResultViewModel(
    string Id,
    string Name,
    string ShortName,
    string Category,
    string Dimensions,
    string Match,
    string Price,
    string PriceSource,
    string Updated,
    long? BestValueRoubles = null,
    long? ValuePerSlotRoubles = null,
    double Score = 0,
    string MatchedText = "",
    string Size = "");

public sealed class ItemsPageViewModel : PageViewModel
{
    private readonly IItemSearchService _searchService;
    private readonly IItemRepository _itemRepository;
    private string _searchQuery = string.Empty;
    /// <summary>What the status line says when there is nothing wrong and nothing searched.</summary>
    private const string ReadyToSearch = "Search by name or short name";

    private const int DefaultResultLimit = 12;

    private bool _showingNoData;
    private string _searchStatus = ReadyToSearch;
    private IReadOnlyList<ItemSearchResultViewModel> _results = [];
    private ApplicationRuntimeSnapshot? _snapshot;

    public ItemsPageViewModel(IItemSearchService searchService, IItemRepository itemRepository)
        : base("Items", "Search the normalized local json.tarkov.dev cache", "Runtime state not loaded")
    {
        _searchService = searchService;
        _itemRepository = itemRepository;
        SearchCommand = new AsyncDelegateCommand(SearchAsync);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, value);
    }

    public IReadOnlyList<ItemSearchResultViewModel> Results
    {
        get => _results;
        private set => SetProperty(ref _results, value);
    }

    public AsyncDelegateCommand SearchCommand { get; }

    /// <summary>How many hits a search keeps; V1's cards fit a dozen, V2's list scrolls and asks for more.</summary>
    public int ResultLimit { get; set; } = DefaultResultLimit;

    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        _snapshot = snapshot;
        Evidence = $"{snapshot.Data.Availability} · {snapshot.Data.ItemCount:N0} cached items";
        if (snapshot.Data.ItemCount == 0)
        {
            Results = [];
            _showingNoData = true;
            SearchStatus = snapshot.Data.Detail;
        }
        else if (_showingNoData)
        {
            // Said before the item cache had loaded and never taken back, so the page insisted
            // no data was available while the header counted five thousand cached items.
            _showingNoData = false;
            SearchStatus = ReadyToSearch;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_snapshot?.IsDemoMode == true)
        {
            SearchQuery = "Graphics Card";
            await SearchAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public Task SearchAsync() => SearchAsync(CancellationToken.None);

    public async Task SearchAsync(CancellationToken cancellationToken)
    {
        if (_snapshot?.Data.ItemCount is null or 0)
        {
            Results = [];
            SearchStatus = _snapshot?.Data.Detail ?? "Runtime state is not loaded.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            Results = [];
            SearchStatus = "Enter an item name or short name.";
            return;
        }

        try
        {
            SearchStatus = "Searching the local item cache…";
            var hits = await _searchService.SearchAsync(SearchQuery, Math.Max(1, ResultLimit), cancellationToken).ConfigureAwait(true);
            var results = new List<ItemSearchResultViewModel>(hits.Count);
            foreach (var hit in hits)
            {
                var price = await _itemRepository.GetPriceAsync(hit.Item.Id, cancellationToken).ConfigureAwait(true);
                var bestValue = price?.BestEconomicValue ?? 0;
                var channel = price?.BestSaleChannel.ToString() ?? "Unavailable";
                results.Add(new(
                    hit.Item.Id,
                    hit.Item.Name,
                    hit.Item.ShortName,
                    hit.Item.Category.ToString(),
                    $"{hit.Item.Dimensions.Width} × {hit.Item.Dimensions.Height} · {hit.Item.Dimensions.Slots} slot(s)",
                    $"{hit.Score:P0} · matched {hit.MatchedText}",
                    bestValue > 0 ? $"{bestValue:N0} ₽ · {hit.Item.ValuePerSlot(price!):N0} ₽ / slot" : "Price unavailable",
                    channel,
                    $"json.tarkov.dev · {hit.Item.Provenance.SourceUpdatedUtc?.ToUniversalTime():u}",
                    bestValue > 0 ? bestValue : null,
                    bestValue > 0 ? hit.Item.ValuePerSlot(price!) : null,
                    hit.Score,
                    hit.MatchedText,
                    $"{hit.Item.Dimensions.Width}×{hit.Item.Dimensions.Height}"));
            }

            Results = results;
            SearchStatus = results.Count == 0
                ? "No local item matched that query."
                : $"{results.Count} results";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Results = [];
            SearchStatus = $"Item search failed: {exception.Message}";
        }
    }
}

/// <summary>One scan already on disk, rendered as finished text.</summary>
/// <param name="Name">What it resolved to, or what it thought it saw.</param>
/// <param name="When">When it was read, in the player's own clock.</param>
/// <param name="Detail">Confidence, context, and the recommendation or the reason there was none.</param>
public sealed record ScanHistoryRowViewModel(string Name, string When, string Detail);

public sealed class ScannerPageViewModel : PageViewModel
{
    /// <summary>How many past scans are worth a glance. More than this is an export, not a page.</summary>
    private const int HistoryDepth = 25;

    private readonly IRuntimeScanUseCase _scanUseCase;
    private readonly IScanHistoryService _history;
    private IReadOnlyList<ScanHistoryRowViewModel> _history_rows = [];
    private string _historyStatus = "Loading earlier scans…";
    private DateTimeOffset _lastHistoryScanUtc = DateTimeOffset.MinValue;
    private string _itemName = "No item scanned";
    private string _value = "Unavailable";
    private string _recommendation = "No recommendation without observed evidence.";
    private string _confidence = "Unavailable";
    private string _source = "No capture source";
    private string _detail = "Nothing has been scanned yet.";
    private bool _hasResult;

    public ScannerPageViewModel(IRuntimeScanUseCase scanUseCase, IScanHistoryService history)
        : base("Scanner", "Read the last screenshot you took", "Runtime state not loaded")
    {
        _scanUseCase = scanUseCase;
        _history = history;
        ScanCommand = new AsyncDelegateCommand(ScanAsync);
        RefreshHistoryCommand = new AsyncDelegateCommand(LoadHistoryAsync);
    }

    public AsyncDelegateCommand ScanCommand { get; }

    public AsyncDelegateCommand RefreshHistoryCommand { get; }

    /// <summary>
    /// Every scan this installation has made, most recent first.
    /// </summary>
    /// <remarks>
    /// These rows have been written to the database since the first migration and nothing has
    /// ever read one back, so a player could scan all evening and the application could not
    /// show them a single thing they had scanned.
    /// </remarks>
    public IReadOnlyList<ScanHistoryRowViewModel> History
    {
        get => _history_rows;
        private set
        {
            if (SetProperty(ref _history_rows, value))
            {
                OnPropertyChanged(nameof(HasHistory));
            }
        }
    }

    public bool HasHistory => _history_rows.Count > 0;

    public string HistoryStatus
    {
        get => _historyStatus;
        private set => SetProperty(ref _historyStatus, value);
    }

    public async Task LoadHistoryAsync()
    {
        try
        {
            var entries = await _history.GetRecentAsync(HistoryDepth, CancellationToken.None).ConfigureAwait(true);
            History = entries.Select(Describe).ToArray();
            HistoryStatus = entries.Count switch
            {
                0 => "Nothing scanned yet on this machine.",
                1 => "1 earlier scan",
                var count => string.Create(CultureInfo.CurrentCulture, $"{count} most recent scans"),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The history is an extra. A database that will not answer must not cost the
            // player the scan they just took, which is the thing above it on the page.
            History = [];
            HistoryStatus = $"Earlier scans could not be read: {exception.Message}";
        }
    }

    private static ScanHistoryRowViewModel Describe(ScanHistoryEntry entry) => new(
        entry.Name,
        entry.ObservedUtc == DateTimeOffset.UnixEpoch
            ? "at an unrecorded time"
            : string.Create(CultureInfo.CurrentCulture, $"{entry.ObservedUtc.ToLocalTime():g}"),
        string.Join(
            " · ",
            new[]
            {
                entry.Confidence.Value.ToString("P0", CultureInfo.CurrentCulture) + " confidence",
                entry.Context.ToString(),
                entry.Recommendation ?? entry.DiagnosticCode ?? "No recommendation was produced",
            }));

    public string ItemName
    {
        get => _itemName;
        private set => SetProperty(ref _itemName, value);
    }

    public string Value
    {
        get => _value;
        private set => SetProperty(ref _value, value);
    }

    public string Recommendation
    {
        get => _recommendation;
        private set => SetProperty(ref _recommendation, value);
    }

    public string Confidence
    {
        get => _confidence;
        private set => SetProperty(ref _confidence, value);
    }

    public string Source
    {
        get => _source;
        private set => SetProperty(ref _source, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    /// <summary>
    /// Whether the last scan produced something to read.
    /// </summary>
    /// <remarks>
    /// The page at rest and the page after a scan want different layouts. Before anything has
    /// been scanned, a readout whose every row says "unavailable" tells the player less than
    /// one line naming the shortcut does, and it repeated the same sentence four times.
    /// </remarks>
    public bool HasResult
    {
        get => _hasResult;
        private set
        {
            if (SetProperty(ref _hasResult, value))
            {
                OnPropertyChanged(nameof(HasNoResult));
            }
        }
    }

    public bool HasNoResult => !HasResult;

    public Task ScanAsync() => ScanAsync(CancellationToken.None);

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _scanUseCase.ExecuteAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Detail = $"Scan failed: {exception.Message}";
        }
    }

    public void Apply(ScanExecutionResult scan)
    {
        // A new scan is exactly the thing that adds a row, and it is the only thing that does,
        // so the list is re-read then rather than on every runtime tick. The snapshot is
        // restated several times a second in a raid and the database has no need to hear it.
        if (scan.Succeeded && scan.ObservedUtc != _lastHistoryScanUtc)
        {
            _lastHistoryScanUtc = scan.ObservedUtc;
            _ = LoadHistoryAsync();
        }

        HasResult = scan.Succeeded;
        ItemName = scan.Succeeded ? scan.ItemName ?? "Unnamed item" : "No item scanned";
        Value = scan.Succeeded && scan.ValueRoubles is { } value
            ? $"{value:N0} ₽ · {scan.ValuePerSlotRoubles.GetValueOrDefault():N0} ₽ / slot"
            : "Unavailable";
        Recommendation = scan.Succeeded
            ? scan.Recommendation ?? "No recommendation was produced."
            : "No recommendation without observed evidence.";
        Confidence = scan.Succeeded ? scan.Confidence.Value.ToString("P0", CultureInfo.CurrentCulture) : "Unavailable";
        Source = scan.Source;
        Detail = scan.Detail;
        Evidence = scan.Succeeded
            ? $"{Confidence} confidence · {scan.Source} · {scan.ObservedUtc.ToLocalTime():T}"
            : scan.Detail;
    }
}

/// <summary>A raid somebody has asked to watch again, with the path it was walked.</summary>
public sealed record RaidReplayRequest(string Title, IReadOnlyList<ScreenshotPosition> Positions);

public sealed record RaidHistoryEntryViewModel(
    string Id,
    string MapId,
    string Map,
    string Mode,
    string Started,
    string Ended,
    string Outcome,
    string Notes)
{
    /// <summary>
    /// How far the raid went, from the screenshots taken during it.
    /// </summary>
    /// <remarks>
    /// Every position has been written to the database on every scan since the first raid and
    /// nothing ever read one back, so a raid's path survived a restart on disk and vanished
    /// from the screen. This is the first thing to read them.
    ///
    /// Distance rather than a count, because "nine screenshots" says how often somebody
    /// photographed something and "1.4 km" says what the raid was.
    /// </remarks>
    public string Path { get; init; } = "No screenshots";

    /// <summary>Watches this raid again on the map.</summary>
    public ICommand? ReplayCommand { get; init; }

    public bool CanReplay => ReplayCommand is not null;
}

public sealed class HistoryPageViewModel : PageViewModel
{
    private readonly IRaidHistoryService _raidHistoryService;
    private readonly Func<string?, string> _nameOfMap;
    private IReadOnlyList<RaidHistoryEntryViewModel> _entries = [];
    private string _status = "History has not been loaded.";
    private int _namedMapCount = -1;

    /// <param name="nameOfMap">
    /// Turns a stored map token into the name the chooser above the map uses.
    /// </param>
    /// <remarks>
    /// Passed in rather than resolved here because the map catalog belongs to the map, and
    /// History is loaded before it: every row read "streets-of-tarkov" and "ground-zero-21"
    /// where it meant Streets of Tarkov and Ground Zero. Two names for one place reads as two
    /// places.
    /// </remarks>
    public HistoryPageViewModel(IRaidHistoryService raidHistoryService, Func<string?, string> nameOfMap)
        : base("History", "Every raid the companion has seen", "Runtime state not loaded")
    {
        _raidHistoryService = raidHistoryService;
        _nameOfMap = nameOfMap;
        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
    }

    /// <summary>
    /// Renames the rows once the map catalog has arrived.
    /// </summary>
    /// <remarks>
    /// The catalog loads last, so a History opened before it holds tokens. Rather than reload
    /// every raid from the database, the rows are rewritten in place from what they already
    /// carry. Called with the catalog's size, which is the only change signal there is.
    /// </remarks>
    public void RenameMaps(int knownMapCount)
    {
        if (_namedMapCount == knownMapCount || Entries.Count == 0)
        {
            return;
        }

        _namedMapCount = knownMapCount;
        Entries = Entries
            .Select(entry => entry with { Map = _nameOfMap(entry.MapId) })
            .ToArray();
    }

    public IReadOnlyList<RaidHistoryEntryViewModel> Entries
    {
        get => _entries;
        private set
        {
            if (SetProperty(ref _entries, value))
            {
                OnPropertyChanged(nameof(HasNoEntries));
            }
        }
    }

    /// <summary>Whether there is nothing here yet, which is every fresh install.</summary>
    public bool HasNoEntries => Entries.Count == 0;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public AsyncDelegateCommand RefreshCommand { get; }

    /// <summary>Raised when somebody asks to watch a raid again.</summary>
    /// <remarks>
    /// An event rather than a reference to the map, because the History page's business is
    /// history. Where the replay is drawn is the shell's decision and it is the only thing
    /// that can also move the player to the page holding the map.
    /// </remarks>
    public event EventHandler<RaidReplayRequest>? ReplayRequested;

    internal async Task ReplayAsync(RaidHistoryEntryViewModel entry)
    {
        try
        {
            if (!Guid.TryParse(entry.Id, out var raidId))
            {
                return;
            }

            var positions = await _raidHistoryService
                .ListPositionsAsync(raidId, CancellationToken.None)
                .ConfigureAwait(true);
            if (positions.Count == 0)
            {
                Status = $"{entry.Map} has no screenshots to replay";
                return;
            }

            ReplayRequested?.Invoke(this, new($"{entry.Map} · {entry.Started}", positions));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"That raid could not be replayed · {exception.Message}";
        }
    }

    public Task LoadAsync() => LoadAsync(CancellationToken.None);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var raids = await _raidHistoryService.ListAsync(cancellationToken).ConfigureAwait(true);
            var entries = new List<RaidHistoryEntryViewModel>(raids.Count);
            foreach (var raid in raids)
            {
                var positions = await _raidHistoryService
                    .ListPositionsAsync(raid.Id, cancellationToken)
                    .ConfigureAwait(true);
                var entry = new RaidHistoryEntryViewModel(
                    raid.Id.ToString("D"),
                    raid.MapId ?? string.Empty,
                    _nameOfMap(raid.MapId),
                    raid.Mode,
                    raid.StartedUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Unknown",
                    raid.EndedUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "In progress",
                    raid.Outcome ?? "Not recorded",
                    raid.Notes ?? "No notes")
                {
                    Path = DescribePath(positions),
                };

                // Built after the row, so the command closes over this raid rather than over
                // whichever row the list happened to end with.
                entries.Add(positions.Count == 0
                    ? entry
                    : entry with { ReplayCommand = new AsyncDelegateCommand(() => ReplayAsync(entry)) });
            }

            Entries = entries;
            // Said once here rather than on every row. EFT_LOG_FACTS.md records that the game
            // never writes an outcome, so the Outcome column read "Not recorded" on every raid
            // for ever — a column whose only possible value is the absence of a value. The
            // field stays on RaidHistoryEntry in case the game ever starts saying.
            Status = Entries.Count == 0
                ? "No raids recorded yet."
                : $"{Entries.Count} raid{(Entries.Count == 1 ? "" : "s")}. The game never records whether you survived, so no outcome is shown.";
            // Where the rows came from and how many, and nothing that belongs in a sentence.
            //
            // This was $"SQLite · {Status}", so the chip was the body text with a prefix on it
            // — then trimmed with an ellipsis because it did not fit, leaving the page carrying
            // one sentence and one truncated copy of the same sentence. Reported with a
            // screenshot of exactly that.
            Evidence = $"SQLite · {Entries.Count} entries";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Entries = [];
            Status = $"Raid history unavailable: {exception.Message}";
            Evidence = "SQLite · unavailable";
        }
    }

    /// <summary>
    /// How far the raid went, in a line.
    /// </summary>
    /// <remarks>
    /// Straight lines between screenshots, so this is a floor rather than a measurement: the
    /// player walked at least this far and almost certainly further. Two screenshots minutes
    /// apart say nothing about what happened between them, and pretending otherwise would be
    /// the same mistake the position trail already refuses to make by drawing itself dotted.
    /// </remarks>
    private static string DescribePath(IReadOnlyList<ScreenshotPosition> positions)
    {
        if (positions.Count == 0)
        {
            return "No screenshots";
        }

        if (positions.Count == 1)
        {
            return "1 screenshot";
        }

        var metres = 0d;
        for (var index = 1; index < positions.Count; index++)
        {
            var from = positions[index - 1].Position;
            var to = positions[index].Position;
            var dx = to.X - from.X;
            var dz = to.Z - from.Z;
            metres += Math.Sqrt((dx * dx) + (dz * dz));
        }

        // Branched before the call rather than inside it: a conditional between two
        // interpolations is two strings, and the culture-aware overload wants a handler.
        return metres >= 1000
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"{positions.Count} screenshots · at least {metres / 1000:F1} km")
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{positions.Count} screenshots · at least {metres:F0} m");
    }
}

public sealed class SettingsPageViewModel : PageViewModel
{
    /// <summary>
    /// Updating the application from inside it, rather than by hand.
    /// </summary>
    /// <remarks>
    /// Every build reached its machine tonight because a person downloaded an artifact,
    /// checked a hash and swapped a folder. That is a great deal of ceremony to change one
    /// line, and it means a fix only arrives while somebody is awake to carry it.
    ///
    /// Only builds that passed Windows verification are ever offered, and the download is
    /// refused unless its checksum matches what was published, so the shortcut does not come
    /// at the cost of the checks.
    /// </remarks>

    private readonly ApplicationStartupCoordinator _startupCoordinator;
    private readonly IScreenshotRetentionStore _retentionSettings;
    private readonly IRecycleBin _recycleBin;
    private ScreenshotRetentionSettings _retention = ScreenshotRetentionSettings.Default;
    private string _retentionStatus = "Reading how long screenshots are kept…";
    private readonly VelopackUpdateGateway? _updates;
    private string _updateStatus = "Updates have not been checked this session.";
    private string _installedBuild = "Build unknown";
    private bool _isBusyWithUpdate;
    private bool _canDownloadUpdate;
    private bool _canRestartForUpdate;
    private string _dataStatus = "Runtime state not loaded";
    private string _profileContext = "Profile unavailable";
    private string _scanProvider = "Unavailable";
    private ApplicationRuntimeSnapshot? _snapshot;
    private string _diagnosticsStatus = "Nothing copied yet.";

    /// <summary>What happened the last time somebody asked for the diagnostics.</summary>
    public string DiagnosticsStatus
    {
        get => _diagnosticsStatus;
        private set => SetProperty(ref _diagnosticsStatus, value);
    }

    /// <summary>Where this application writes its own log, as opposed to where the game does.</summary>
    /// <remarks>
    /// Settings had a "Logs" box and it is the *game's* log folder, which is the right thing
    /// in that section and the wrong answer to "where do I find yours".
    /// </remarks>
    public string CompanionLogPath => CrashLog.FilePath ?? "not started yet";

    /// <summary>
    /// Puts a description of this installation on the clipboard, ready to paste.
    /// </summary>
    /// <remarks>
    /// Two players in one evening appeared in their group's member list and never on its map,
    /// and both times the diagnosis had to be assembled by somebody else asking questions
    /// across Discord. This is so the answer can be sent by the person who has the problem,
    /// in one action, without them having to find anything.
    ///
    /// SAFETY.md governs what it may contain. SupportBundle projects the snapshot into a closed
    /// schema of categories, booleans, and capped counts; it never opens the log or renders a
    /// screenshot name, coordinate, path, credential, identity, or free-form runtime detail.
    /// The screenshot compatibility count is enough to settle the case above without exporting
    /// the evidence that produced it. Relay-side arbitrary-body validation remains owned by #310.
    /// </remarks>
    public async Task CopyDiagnosticsAsync(Func<string, Task> toClipboard)
    {
        ArgumentNullException.ThrowIfNull(toClipboard);
        if (_snapshot is not { } snapshot)
        {
            DiagnosticsStatus = "Nothing to describe yet; the application is still starting.";
            return;
        }

        try
        {
            var report = SupportBundle.Describe(
                snapshot,
                snapshot.RecentScreenshotNames,
                CrashLog.FilePath);
            await toClipboard(report).ConfigureAwait(true);
            DiagnosticsStatus = string.Create(
                CultureInfo.CurrentCulture,
                $"Copied · {report.Length:N0} characters · paste it wherever you are being helped.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DiagnosticsStatus = $"Could not copy: {exception.Message}";
        }
    }

    /// <summary>
    /// The quest page, so its once-a-wipe setup can live here instead of above the board.
    /// </summary>
    /// <remarks>
    /// The exchange and the TarkovTracker import cost about 180 px above the first quest on
    /// the page used most between raids, for things touched once a wipe. They are bound
    /// through rather than copied, because the exchange has to move *with* its preview and its
    /// confirm and undo: ADR 0004 and SAFETY.md both rely on an import being reviewed before
    /// it is applied, and splitting the review from the action is the one way to move this
    /// wrongly.
    ///
    /// Assigned after construction because the shell builds Settings before Quests.
    /// </remarks>
    public required QuestsPageViewModel Quests { get; init; }

    public SettingsPageViewModel(
        ApplicationStartupCoordinator startupCoordinator,
        RuntimeOptions options,
        AppDataPaths paths,
        AppCommandLine commandLine,
        IOcrEngineStatus ocrStatus,
        IScreenshotRetentionStore retentionSettings,
        IRecycleBin recycleBin,
        VelopackUpdateGateway? updates = null,
        // Optional so a composition without stored settings still builds a Settings page,
        // which is what the tests that construct this by hand rely on.
        IEftPathOverrideStore? gameFolders = null,
        RaidObservationService? observation = null)
        : base("Settings & diagnostics", "Runtime configuration and a manual data refresh", "Not loaded")
    {
        ArgumentNullException.ThrowIfNull(ocrStatus);
        ArgumentNullException.ThrowIfNull(retentionSettings);
        ArgumentNullException.ThrowIfNull(recycleBin);
        _startupCoordinator = startupCoordinator;
        _retentionSettings = retentionSettings;
        _recycleBin = recycleBin;
        _updates = updates;
        _gameFolders = gameFolders;
        _observation = observation;
        CheckForUpdateCommand = new AsyncDelegateCommand(CheckForUpdateAsync);
        CopyDiagnosticsCommand = new AsyncDelegateCommand(() => CopyDiagnosticsAsync(Clipboard));
        ReportProblemCommand = new AsyncDelegateCommand(ReportProblemAsync);
        DownloadUpdateCommand = new AsyncDelegateCommand(DownloadUpdateAsync);
        RestartForUpdateCommand = new DelegateCommand(RestartForUpdate);
        if (_updates is not null)
        {
            _installedBuild = _updates.InstalledBuild;
            if (!_updates.IsInstalled)
            {
                // Not the same sentence as InstalledBuild directly above it, which already
                // says "Running from a folder, not installed". Said twice it was a fact
                // repeated; said once with what to do about it, it is an answer.
                _updateStatus = "Only an installed build updates itself. Run the installer once and this keeps itself current.";
            }
        }
        // The engine explains exactly why it is unavailable - a missing Visual C++ runtime
        // reads very differently from an unsupported architecture - but until now only the
        // headless self-test ever read that reason, so the user saw a bare "Unavailable".
        RecognitionProvider = ocrStatus.Availability.IsAvailable
            ? $"Available · {ocrStatus.Availability.Provider}"
            : $"Unavailable · {ocrStatus.Availability.Provider} · {ocrStatus.Availability.Reason ?? "No reason was reported."}";
        // The runtime warning belongs to one engine and not the other. Windows has its own OCR
        // and needs no redistributable, so telling somebody running on it to go and install
        // one sends them after a problem they do not have.
        RecognitionNeedsRuntime = ocrStatus.Availability.Provider.StartsWith("tesseract", StringComparison.OrdinalIgnoreCase);
        IsOffline = options.IsOffline;
        DatabasePath = Path.Combine(paths.Database, "tarkov-companion.db");
        DiagnosticChannel = commandLine.DeveloperMode && !string.IsNullOrWhiteSpace(commandLine.DiagnosticChannelPath)
            ? "Requested"
            : "Off · needs developer mode and a path";
        SyncCommand = new AsyncDelegateCommand(SyncAsync);
        ToggleScreenshotTidyingCommand = new AsyncDelegateCommand(ToggleScreenshotTidyingAsync);
        ChooseRetentionCommand = new AsyncDelegateCommand(ChooseRetentionAsync);
        SaveGameFoldersCommand = new AsyncDelegateCommand(SaveGameFoldersAsync);
        _ = LoadRetentionAsync();
        _ = LoadGameFoldersAsync();
    }

    private readonly IEftPathOverrideStore? _gameFolders;
    private readonly RaidObservationService? _observation;
    private string _screenshotFolder = string.Empty;
    private string _logFolder = string.Empty;
    private string _gameFolderStatus = "Found on their own";
    private string _watchedFolders = "Looking for the game…";

    /// <summary>
    /// Where the game keeps its screenshots, when the companion cannot work it out.
    /// </summary>
    /// <remarks>
    /// It guesses from the install, from Documents, from Pictures, and from wherever OneDrive
    /// says it has put those. That covers the arrangements we know of and will never cover all
    /// of them: the first person to install this who does not use OneDrive had no screenshots
    /// detected and no way at all to say where they were.
    ///
    /// Left blank, the guessing stands.
    /// </remarks>
    public string ScreenshotFolder
    {
        get => _screenshotFolder;
        set => SetProperty(ref _screenshotFolder, value);
    }

    /// <summary>Where the game writes one log folder per launch.</summary>
    public string LogFolder
    {
        get => _logFolder;
        set => SetProperty(ref _logFolder, value);
    }

    public string GameFolderStatus
    {
        get => _gameFolderStatus;
        private set => SetProperty(ref _gameFolderStatus, value);
    }

    /// <summary>The folders actually being watched right now, which is the answer to "is it on".</summary>
    public string WatchedFolders
    {
        get => _watchedFolders;
        private set => SetProperty(ref _watchedFolders, value);
    }

    public AsyncDelegateCommand SaveGameFoldersCommand { get; }

    private async Task LoadGameFoldersAsync()
    {
        if (_gameFolders is null)
        {
            return;
        }

        try
        {
            var stored = await _gameFolders.GetAsync(CancellationToken.None).ConfigureAwait(true);
            ScreenshotFolder = stored.ScreenshotRoot ?? string.Empty;
            LogFolder = stored.LogRoot ?? string.Empty;
            GameFolderStatus = stored.IsEmpty ? "Found on their own" : "Set by you";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            GameFolderStatus = $"Unreadable · {exception.Message}";
        }
    }

    private async Task SaveGameFoldersAsync()
    {
        if (_gameFolders is null)
        {
            return;
        }

        try
        {
            var chosen = new EftPathOverrides(ScreenshotFolder, LogFolder).Normalized();
            await _gameFolders.SaveAsync(chosen, CancellationToken.None).ConfigureAwait(true);
            // Discovery only runs between watching sessions, and a session runs until it is
            // cancelled. Without this the folder just typed would do nothing until the next
            // launch, which reads as the setting being ignored.
            _observation?.Rediscover();
            GameFolderStatus = Describe(chosen);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            GameFolderStatus = $"Not saved · {exception.Message}";
        }
    }

    /// <summary>
    /// Says whether what was typed is going to be used, before the player goes looking.
    /// </summary>
    /// <remarks>
    /// A folder that is not there is ignored rather than honoured, so that a typo leaves the
    /// guessing working instead of leaving the companion watching nothing. Somebody who has
    /// just typed one has to be told that, or the setting looks broken rather than skipped.
    /// </remarks>
    private static string Describe(EftPathOverrides chosen)
    {
        if (chosen.IsEmpty)
        {
            return "Cleared · found on their own again";
        }

        var missing = new[] { chosen.ScreenshotRoot, chosen.LogRoot }
            .Where(path => path is { Length: > 0 } && !Directory.Exists(path))
            .ToArray();
        return missing.Length == 0
            ? "Saved · watching starts within a minute"
            : $"Saved, but not there: {string.Join(", ", missing)}";
    }

    public bool IsOffline { get; }

    public AsyncDelegateCommand CheckForUpdateCommand { get; }

    public AsyncDelegateCommand DownloadUpdateCommand { get; }

    public DelegateCommand RestartForUpdateCommand { get; }

    public AsyncDelegateCommand CopyDiagnosticsCommand { get; }

    public AsyncDelegateCommand ReportProblemCommand { get; }

    /// <summary>
    /// How a report reaches the relay, supplied by the shell.
    /// </summary>
    /// <remarks>
    /// A function rather than the group service, so this page does not acquire a dependency on
    /// sharing in order to describe itself, and so the sending can be replaced in a test.
    /// Returns the sentence to show the player.
    /// </remarks>
    public required Func<string, CancellationToken, Task<string>> SendReport { get; init; }

    /// <summary>
    /// Sends the diagnostics to the relay, which files an issue and hands back the link.
    /// </summary>
    /// <remarks>
    /// The same text Copy diagnostics produces, sent rather than pasted, so the person with
    /// the problem does not have to find somebody to paste it to.
    ///
    /// Copy diagnostics stays, and every failure here points back at it: a relay that cannot
    /// be reached is one of the problems somebody might be reporting, and a report button that
    /// only worked when nothing was wrong would be worth very little.
    /// </remarks>
    public async Task ReportProblemAsync()
    {
        if (_snapshot is not { } snapshot)
        {
            DiagnosticsStatus = "Nothing to describe yet; the application is still starting.";
            return;
        }

        DiagnosticsStatus = "Sending…";
        try
        {
            var report = SupportBundle.Describe(snapshot, snapshot.RecentScreenshotNames, CrashLog.FilePath);
            DiagnosticsStatus = await SendReport(report, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DiagnosticsStatus = $"Could not send: {exception.Message}. Use Copy diagnostics instead.";
        }
    }

    /// <summary>
    /// How text reaches the clipboard, replaced in tests.
    /// </summary>
    /// <remarks>
    /// Assigned by the view, because a clipboard belongs to a window and a view model that
    /// reached for one would be a view model that cannot be tested.
    /// </remarks>
    public Func<string, Task> Clipboard { get; set; } = _ => Task.CompletedTask;

    /// <summary>Which build is running, so a report of a bug can name it.</summary>
    public string InstalledBuild
    {
        get => _installedBuild;
        private set => SetProperty(ref _installedBuild, value);
    }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    /// <summary>Raised when a build starts or stops waiting, so the rail can mark itself.</summary>
    public event EventHandler<bool>? UpdateWaitingChanged;

    /// <summary>Whether an offered build has not yet been fetched.</summary>
    public bool CanDownloadUpdate
    {
        get => _canDownloadUpdate;
        private set
        {
            if (SetProperty(ref _canDownloadUpdate, value))
            {
                UpdateWaitingChanged?.Invoke(this, value || CanRestartForUpdate);
            }
        }
    }

    /// <summary>Whether a build is unpacked and waiting for the application to close.</summary>
    public bool CanRestartForUpdate
    {
        get => _canRestartForUpdate;
        private set
        {
            if (SetProperty(ref _canRestartForUpdate, value))
            {
                UpdateWaitingChanged?.Invoke(this, value || CanDownloadUpdate);
            }
        }
    }

    /// <summary>
    /// Looks for a newer build, on a timer, without anybody asking.
    /// </summary>
    /// <remarks>
    /// The only way to learn a build existed was to open this page and press a button, so one
    /// could sit in the feed for days. This checks shortly after launch and every few hours
    /// after that.
    ///
    /// Delayed rather than immediate at startup because it is a network call and startup is
    /// already doing the work somebody is waiting on. Hours rather than minutes because builds
    /// land a few times a day at most, and the check exists to be noticed between raids rather
    /// than during one.
    /// </remarks>
    public async Task WatchForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (_updates is null || !_updates.IsInstalled)
        {
            return;
        }

        var delay = TimeSpan.FromMinutes(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromHours(4);
            if (IsBusyWithUpdate || CanRestartForUpdate)
            {
                continue;
            }

            Apply(await _updates.CheckAsync(cancellationToken).ConfigureAwait(true));
        }
    }

    public bool IsBusyWithUpdate
    {
        get => _isBusyWithUpdate;
        private set
        {
            if (SetProperty(ref _isBusyWithUpdate, value))
            {
                OnPropertyChanged(nameof(IsUpdateIdle));
                OnPropertyChanged(nameof(CanCheckForUpdate));
            }
        }
    }

    public bool IsUpdateIdle => !IsBusyWithUpdate;

    /// <summary>
    /// Whether checking for an update can do anything.
    /// </summary>
    /// <remarks>
    /// A build running from an extracted folder has no installation to replace, so the check
    /// can only come back and say so. The page offered the button anyway, directly under a line
    /// already saying it could not update itself, and pressing it printed that same sentence a
    /// third time. A control that cannot act is better switched off than left to explain
    /// itself afterwards.
    /// </remarks>
    public bool CanCheckForUpdate => _updates is { IsInstalled: true } && IsUpdateIdle;

    public bool SupportsUpdates => _updates is not null;

    private async Task CheckForUpdateAsync()
    {
        if (_updates is null || IsBusyWithUpdate)
        {
            return;
        }

        IsBusyWithUpdate = true;
        CanDownloadUpdate = false;
        UpdateStatus = "Checking for a newer build…";
        try
        {
            Apply(await _updates.CheckAsync(CancellationToken.None).ConfigureAwait(true));
        }
        finally
        {
            IsBusyWithUpdate = false;
        }
    }

    private async Task DownloadUpdateAsync()
    {
        if (_updates is null || IsBusyWithUpdate)
        {
            return;
        }

        IsBusyWithUpdate = true;
        CanDownloadUpdate = false;
        UpdateStatus = "Downloading…";
        try
        {
            Apply(await _updates.DownloadAsync(CancellationToken.None).ConfigureAwait(true));
        }
        finally
        {
            IsBusyWithUpdate = false;
        }
    }

    private void Apply(UpdateProgress progress)
    {
        UpdateStatus = progress.Status;
        CanDownloadUpdate = progress.CanDownload;
        CanRestartForUpdate = progress.CanApply;
    }

    /// <summary>
    /// Installs and reopens. Normally does not return.
    /// </summary>
    /// <remarks>
    /// Closing is no longer this application's job. The updater replaces the files and starts
    /// the new build itself, which is the part the previous hand-written swap script could
    /// never do reliably: it had to wait for a process to release a folder that a file-sync
    /// client was holding open, and on a real machine that never happened.
    /// </remarks>
    private void RestartForUpdate()
    {
        if (_updates is null)
        {
            return;
        }

        try
        {
            CanRestartForUpdate = false;
            UpdateStatus = "Installing · it will close and reopen";
            _updates.ApplyAndRestart();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            UpdateStatus = $"The update could not be started: {exception.Message}";
            CanRestartForUpdate = true;
        }
    }

    /// <summary>
    /// Tidying the game's screenshot folder, which is the one folder this application empties.
    /// </summary>
    /// <remarks>
    /// Every screenshot is the player's own file, taken deliberately, and the companion only
    /// reads them. Tidying them is therefore stated plainly on this page rather than done
    /// quietly: what will go, when it will go, and where it goes so it can be got back.
    ///
    /// A day is the shortest span that still covers an evening's play and the following
    /// morning, which is when somebody actually goes looking for the screenshot they meant to
    /// keep.
    /// </remarks>
    public AsyncDelegateCommand ToggleScreenshotTidyingCommand { get; }

    public AsyncDelegateCommand ChooseRetentionCommand { get; }

    /// <summary>Whether the folder is swept at all.</summary>
    public bool TidiesScreenshots => _retention.IsEnabled;

    public string ScreenshotTidyingLabel => TidiesScreenshots ? "Stop tidying" : "Start tidying";

    /// <summary>How long screenshots are kept, in the player's words rather than in hours.</summary>
    public string RetentionDisplay => _retention.SafeRetentionHours switch
    {
        24 => "24 hours",
        72 => "3 days",
        168 => "7 days",
        var hours when hours % 24 == 0 => $"{hours / 24} days",
        var hours => $"{hours} hours",
    };

    public string RetentionStatus
    {
        get => _retentionStatus;
        private set => SetProperty(ref _retentionStatus, value);
    }

    /// <summary>Whether tidying can happen at all on this machine.</summary>
    public bool CanTidyScreenshots => _recycleBin.IsAvailable;

    private async Task LoadRetentionAsync()
    {
        try
        {
            // Constructed and called before ApplicationStartupCoordinator.InitializeAsync has
            // necessarily applied migrations, so this used to query retention_policies too
            // early on a fresh data folder — an unobserved SqliteException, since the caller
            // discards this task. Wait for the same gate the data refresh and profile bootstrap
            // use, and catch broadly below so a future early-query bug logs instead of crashing
            // the process from the finalizer thread.
            await _startupCoordinator.DatabaseReadyAsync(CancellationToken.None).ConfigureAwait(true);
            ApplyRetention(await _retentionSettings.GetAsync(CancellationToken.None).ConfigureAwait(true));
            RetentionStatus = DescribeRetention();
        }
        catch (Exception exception)
        {
            RetentionStatus = $"Unreadable · {exception.Message}";
        }
    }

    private Task ToggleScreenshotTidyingAsync() =>
        SaveRetentionAsync(_retention with { IsEnabled = !_retention.IsEnabled });

    /// <summary>Moves to the next span, which is a button rather than a list because there are four.</summary>
    private Task ChooseRetentionAsync() => SaveRetentionAsync(_retention with
    {
        RetentionHours = _retention.SafeRetentionHours switch
        {
            24 => 72,
            72 => 168,
            168 => 720,
            _ => 24,
        },
    });

    private async Task SaveRetentionAsync(ScreenshotRetentionSettings settings)
    {
        try
        {
            await _retentionSettings.SaveAsync(settings, CancellationToken.None).ConfigureAwait(true);
            ApplyRetention(settings);
            RetentionStatus = DescribeRetention();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RetentionStatus = $"Not saved · {exception.Message}";
        }
    }

    private void ApplyRetention(ScreenshotRetentionSettings settings)
    {
        _retention = settings;
        OnPropertyChanged(nameof(TidiesScreenshots));
        OnPropertyChanged(nameof(ScreenshotTidyingLabel));
        OnPropertyChanged(nameof(RetentionDisplay));
    }

    private string DescribeRetention()
    {
        if (!_recycleBin.IsAvailable)
        {
            return "No recycle bin here, so nothing is tidied";
        }

        return _retention.IsEnabled
            ? $"Older than {RetentionDisplay} go to the recycle bin · the newest is always kept"
            : "Left alone";
    }

    public string RecognitionProvider { get; }

    public string DatabasePath { get; }

    public string DiagnosticChannel { get; }

    /// <summary>Whether the engine in use is the one that needs a Visual C++ redistributable.</summary>
    public bool RecognitionNeedsRuntime { get; }

    public AsyncDelegateCommand SyncCommand { get; }

    public string DataStatus
    {
        get => _dataStatus;
        private set => SetProperty(ref _dataStatus, value);
    }

    public string ProfileContext
    {
        get => _profileContext;
        private set => SetProperty(ref _profileContext, value);
    }

    public string ScanProvider
    {
        get => _scanProvider;
        private set => SetProperty(ref _scanProvider, value);
    }

    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        _snapshot = snapshot;
        DataStatus = $"{snapshot.Data.Availability} · {snapshot.Data.ItemCount:N0} items · {snapshot.Data.Detail}";
        WatchedFolders = snapshot.Observation switch
        {
            { ScreenshotRoot: { Length: > 0 } shots, LogRoot: { Length: > 0 } logs } =>
                $"Screenshots {shots} · logs {logs}",
            { ScreenshotRoot: { Length: > 0 } shots } => $"Screenshots {shots} · no log folder",
            { LogRoot: { Length: > 0 } logs } => $"Logs {logs} · no screenshot folder",
            _ => "Nothing found yet",
        };
        ProfileContext = snapshot.Profile is null
            ? "Profile unavailable"
            : $"{snapshot.Profile.Name} · level {snapshot.Profile.Level} · {snapshot.Profile.GameMode}";
        ScanProvider = snapshot.Scan.IsAvailable
            ? snapshot.Scan.Succeeded ? $"Last result: {snapshot.Scan.Source}" : snapshot.Scan.Detail
            : snapshot.Scan.Detail;
        Evidence = snapshot.DatabaseReady ? "Persistent database initialized" : "Database not initialized";
    }

    public async Task SyncAsync()
    {
        try
        {
            await _startupCoordinator.RefreshAsync(force: true, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DataStatus = $"Refresh failed: {exception.Message}";
        }
    }
}

public sealed class MainWindowViewModel : BindableViewModel, IDisposable
{
    /// <summary>Cancelled when the window goes, so background watchers stop with it.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    private readonly GroupSessionService _group;
    private readonly IShellLayoutStore? _layoutStore;

    /// <summary>One apply per burst, on the UI thread, reading whatever is current when it runs.</summary>
    private readonly CoalescingDispatch _apply;
    private readonly IRuntimeStateStore _stateStore;
    private readonly ApplicationStartupCoordinator _startupCoordinator;
    private readonly RuntimeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly SynchronizationContext? _synchronizationContext;

    // The chip palette mirrors Themes/InstrumentStyles.axaml. Grey is the resting state, and
    // each accent is reserved for one meaning so a glance at the bar reads the same way every
    // time: sage is live, cyan is a place, ochre is a value or a warning, coral is a fault.
    private const string RestingColor = "#8F9BA6";
    private const string InkColor = "#C6D0D8";
    private const string CyanColor = "#56B8C6";
    private const string OchreColor = "#C6A15B";
    private const string SageColor = "#77B895";
    private const string CoralColor = "#DF6A62";
    private string? _followedMapId;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private PageViewModel _currentPage;
    private V2ShellViewModel? _previewShell;
    private IReadOnlyList<StatusChip> _status = [];
    private string _modeLabel = string.Empty;
    private string _lastScanName = "No item scanned";
    private bool _hasScan;
    private string _lastScanValue = "Unavailable";
    private string _lastScanAdvice = "No recommendation without observed evidence.";
    private string _lastScanEvidence = "No scan evidence";
    private bool _initialized;
    private bool _disposed;
    private bool _isRailCollapsed;
    private ShellLayout _layout = ShellLayout.Default;


    /// <summary>The one-second tick, where there is a dispatcher to run it on.</summary>
    private DispatcherTimer? _clock;

    public MainWindowViewModel(
        IRuntimeStateStore stateStore,
        ApplicationStartupCoordinator startupCoordinator,
        IItemSearchService itemSearchService,
        IItemRepository itemRepository,
        IPriceHistoryService priceHistoryService,
        IRequirementCatalog requirementCatalog,
        IItemFactCatalog itemFactCatalog,
        IQuestProgressService questProgress,
        IBarterCatalog barters,
        ITraderCatalog traderCatalog,
        IEventCatalog eventCatalog,
        IEventAuthoring eventAuthoring,
        IEventTrackerService eventTracker,
        IPlayerProfileService profileService,
        IRaidHistoryService raidHistoryService,
        IShellLayoutStore layoutStore,
        IMapDataService maps,
        IRuntimeScanUseCase scanUseCase,
        IScanHistoryService scanHistory,
        IOcrEngineStatus ocrStatus,
        IGroupSettingsStore groupSettings,
        GroupSessionService group,
        IScreenshotRetentionStore retentionSettings,
        IEftPathOverrideStore gameFolders,
        RaidObservationService observation,
        IRecycleBin recycleBin,
        VelopackUpdateGateway updates,
        RuntimeOptions options,
        AppDataPaths paths,
        AppCommandLine commandLine,
        MapViewModel map,
        QuestsPageViewModel quests,
        TimeProvider timeProvider,
        ILogger<MainWindowViewModel> logger)
    {
        _group = group;
        _layoutStore = layoutStore;
        _stateStore = stateStore;
        _startupCoordinator = startupCoordinator;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        var synchronizationContext = SynchronizationContext.Current;
        _synchronizationContext = synchronizationContext?.GetType().Namespace?
            .StartsWith("Avalonia", StringComparison.Ordinal) == true
                ? synchronizationContext
                : null;

        Map = map;
        Raid = new(map, raidHistoryService, maps, itemRepository, quests.NameOfTask, quests.BoardTasks);
        Scanner = new(scanUseCase, scanHistory);
        Items = new(itemSearchService, itemRepository);
        Quests = quests;
        History = new(raidHistoryService, ResolveMapName);
        Flea = new(itemSearchService, itemRepository, priceHistoryService);
        Hideout = new(requirementCatalog, profileService, itemRepository, barters, traderCatalog);
        Settings = new(
            startupCoordinator,
            options,
            paths,
            commandLine,
            ocrStatus,
            retentionSettings,
            recycleBin,
            updates,
            gameFolders,
            observation)
        {
            // The quest exchange and the TarkovTracker import are rendered on Settings now,
            // bound through this, so they stop costing 180 px above the quest board.
            Quests = quests,
            // The relay does the filing, because a token on every player's disk is not a thing
            // to arrange, and the group session is the one component that already holds the key.
            SendReport = group.ReportProblemAsync,
        };
        Ammo = new(itemFactCatalog, itemRepository);
        Keys = new(itemFactCatalog, itemRepository, questProgress, maps);
        Loadout = new(itemFactCatalog, itemSearchService, itemRepository);
        Events = new(eventCatalog, eventTracker, itemRepository, eventAuthoring);
        Squad = new(itemRepository);
        Group = new(groupSettings);

        Navigation =
        [
            CreateNavigation("Raid", "M8,1.5 V4.5 M8,11.5 V14.5 M1.5,8 H4.5 M11.5,8 H14.5 M8,5.2 A2.8,2.8 0 1 1 7.99,5.2 Z", Raid),
            CreateNavigation("Squad", "M5.5,7 A2,2 0 1 1 5.49,7 Z M10.5,7 A2,2 0 1 1 10.49,7 Z M2,13.5 C2,11 3.6,10 5.5,10 C7.4,10 9,11 9,13.5 M9.6,10.1 C12,10.1 14,11.1 14,13.5", Squad),
            CreateNavigation("Group", "M2.5,6 H11 M9,3.5 L11.5,6 L9,8.5 M13.5,10.5 H5 M7,8 L4.5,10.5 L7,13", Group),
            CreateNavigation("Scanner", "M2,5 V2.5 H4.5 M11.5,2.5 H14 V5 M14,11.5 V14 H11.5 M4.5,14 H2 V11.5 M2.5,8 H13.5", Scanner),
            CreateNavigation("Items", "M8,2 L14,5.2 V10.8 L8,14 L2,10.8 V5.2 Z M2,5.2 L8,8.4 L14,5.2 M8,8.4 V14", Items, startsGroup: true),
            CreateNavigation("Ammo", "M8,1.5 C10,4 10.5,6 10.5,8.5 H5.5 C5.5,6 6,4 8,1.5 Z M5.5,8.5 H10.5 V12 H5.5 Z M5.5,12 H10.5 V14.5 H5.5 Z", Ammo),
            CreateNavigation("Keys", "M6,10 A3,3 0 1 1 5.99,10 Z M8.1,8.2 L13.5,2.8 M11.5,4.8 L13,6.3 M12.6,3.7 L14,5.1", Keys),
            CreateNavigation("Flea", "M2.5,8.5 L8.5,2.5 H13.5 V7.5 L7.5,13.5 Z M11,5 A0.9,0.9 0 1 1 10.99,5 Z", Flea),
            CreateNavigation("Quests", "M3,8.5 L6.5,12 L13,4", Quests),
            CreateNavigation("Hideout", "M2,7.5 L8,2 L14,7.5 M3.6,6.4 V14 H12.4 V6.4 M6.6,14 V9.5 H9.4 V14", Hideout),
            CreateNavigation("Events", "M4,14 V2 M4,2.6 H12.5 L10.4,5.8 L12.5,9 H4", Events),
            CreateNavigation("Loadout", "M2.5,2.5 H13.5 V13.5 H2.5 Z M2.5,8 H13.5 M8,2.5 V13.5", Loadout),
            CreateNavigation("History", "M8,1.5 A6.5,6.5 0 1 1 2.4,4.8 M2.4,4.8 V1.8 M2.4,4.8 H5.4 M8,4.5 V8.5 L11,10.2", History, startsGroup: true),
            CreateNavigation("Settings", "M2.5,4.5 H13.5 M2.5,8 H13.5 M2.5,11.5 H13.5 M6,2.9 V6.1 M10.5,6.4 V9.6 M5,9.9 V13.1", Settings, startsGroup: true),
        ];

        _currentPage = Navigation[0].Page;
        Navigation[0].IsSelected = true;

        // The rail carries the news, so it is visible from whatever page somebody is on.
        var settingsItem = Navigation.First(item => ReferenceEquals(item.Page, Settings));
        Settings.UpdateWaitingChanged += (_, waiting) => settingsItem.HasNotice = waiting;

        // The map knows where somebody clicked; the group session knows how to tell anybody.
        Map.GroupMarkRequested += (_, request) => _ = MarkForGroupAsync(request);
        Map.GroupMarkRemoveRequested += (_, id) => _ = RemoveGroupMarkAsync(id);
        Map.GroupMarksClearRequested += (_, reachedOnly) => _ = ClearGroupMarksAsync(reachedOnly);
        // The History page asks; the shell decides where a replay is drawn and is the only
        // thing that can also move the player to the page holding the map.
        History.ReplayRequested += (_, request) =>
        {
            Raid.Replay.Open(request.Title, request.Positions);
            if (Navigation.FirstOrDefault(item => ReferenceEquals(item.Page, Raid)) is { } destination)
            {
                Select(destination);
            }
        };

        _apply = new(_synchronizationContext, () => ApplySnapshot(_stateStore.Current));
        _stateStore.Changed += RuntimeStateChanged;
        ApplySnapshot(_stateStore.Current);
        StartClock();
    }

    /// <summary>
    /// Starts the one thing in this application that ticks.
    /// </summary>
    /// <remarks>
    /// Everything here is driven by the runtime store, which raises Changed when the game
    /// writes a log line or the player takes a screenshot — and between those it can be minutes.
    /// So every "how long ago" and every countdown on screen was frozen at whatever it read when
    /// the last event landed. That was survivable while the only clock was twenty pixels deep in
    /// a sidebar; it is not survivable with "Time left" at reading size along the top, where a
    /// frozen readout is indistinguishable from a working one.
    ///
    /// A second, because that is the resolution of the thing being shown. Only the clock and the
    /// chips are redone: re-applying the whole snapshot would run fourteen page view models and
    /// five map calls to move one number.
    ///
    /// Nothing starts where there is no dispatcher — a test host, a headless run — and nothing
    /// depends on it having started. The chips are still correct on every snapshot; they are
    /// merely correct less often.
    /// </remarks>
    private void StartClock()
    {
        if (_synchronizationContext is null)
        {
            return;
        }

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        _clock.Start();
    }

    private void Tick()
    {
        if (_disposed)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var snapshot = _stateStore.Current;
        Raid.Tick(now);
        UpdateStatus(snapshot, now);
    }

    /// <summary>
    /// Sends a mark and says what became of it.
    /// </summary>
    /// <remarks>
    /// One code path draws every mark, including your own, so nothing appears until the next
    /// exchange brings it back. That gap reads as the gesture not working unless something
    /// says otherwise, which is how it was reported.
    /// </remarks>
    private async Task MarkForGroupAsync(GroupMarkRequest request)
    {
        var sent = await _group
            .MarkAsync(request.MapId, request.Position, request.Label, request.IsPing, CancellationToken.None)
            .ConfigureAwait(true);
        Map.ReportMark(request.IsPing, sent);
    }

    /// <summary>
    /// Takes a mark off the group's map and says whether it went.
    /// </summary>
    /// <remarks>
    /// Nothing is removed locally, the same rule as marking. The next exchange brings back a
    /// room without it, so one code path draws every mark and a remover never sees a map the
    /// group does not have — which also means a failed removal quietly leaves the mark where
    /// it was rather than taking it off one screen and nobody else's.
    /// </remarks>
    private async Task RemoveGroupMarkAsync(long id)
    {
        var removed = await _group.RemoveMarkAsync(id, CancellationToken.None).ConfigureAwait(true);
        Map.ReportMarkRemoved(removed);
    }

    /// <summary>Clears the group's marks on the open map, either the reached ones or all.</summary>
    private async Task ClearGroupMarksAsync(bool reachedOnly)
    {
        var cleared = await _group
            .ClearMarksAsync(Map.SelectedLocation?.Id, reachedOnly, CancellationToken.None)
            .ConfigureAwait(true);
        Map.ReportMarksCleared(reachedOnly, cleared);
    }

    public bool IsDemoMode => _options.DemoMode;

    public IReadOnlyList<NavigationItem> Navigation { get; }

    public FleaPageViewModel Flea { get; }

    public HideoutPageViewModel Hideout { get; }

    public AmmoPageViewModel Ammo { get; }

    public KeysPageViewModel Keys { get; }

    public LoadoutPageViewModel Loadout { get; }

    public EventsPageViewModel Events { get; }

    public SquadPageViewModel Squad { get; }

    public GroupPageViewModel Group { get; }

    public IReadOnlyList<StatusChip> Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(PrimaryStatus));
                OnPropertyChanged(nameof(SecondaryStatus));
            }
        }
    }

    /// <summary>The raid readout: map, raid state and position.</summary>
    public IReadOnlyList<StatusChip> PrimaryStatus => Status.Where(chip => chip.IsPrimary).ToArray();

    /// <summary>The health strip: observation, data freshness and scan readiness.</summary>
    public IReadOnlyList<StatusChip> SecondaryStatus => Status.Where(chip => !chip.IsPrimary).ToArray();

    public PageViewModel CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public MapViewModel Map { get; }

    public RaidPageViewModel Raid { get; }

    public ScannerPageViewModel Scanner { get; }

    public ItemsPageViewModel Items { get; }

    public QuestsPageViewModel Quests { get; }

    public HistoryPageViewModel History { get; }

    public SettingsPageViewModel Settings { get; }

    public string ModeLabel
    {
        get => _modeLabel;
        private set => SetProperty(ref _modeLabel, value);
    }

    /// <summary>The optional process-start preview shell; V1 remains the default when this is null.</summary>
    public V2ShellViewModel? PreviewShell
    {
        get => _previewShell;
        set
        {
            if (ReferenceEquals(_previewShell, value))
            {
                return;
            }

            if (_previewShell is not null)
            {
                _previewShell.PropertyChanged -= PreviewShellPropertyChanged;
            }

            if (SetProperty(ref _previewShell, value))
            {
                if (_previewShell is not null)
                {
                    _previewShell.PropertyChanged += PreviewShellPropertyChanged;
                }

                OnPropertyChanged(nameof(IsLegacyShell));
                OnPropertyChanged(nameof(IsPreviewShell));
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public bool IsLegacyShell => PreviewShell is null;

    public bool IsPreviewShell => PreviewShell is not null;

    public string WindowTitle => PreviewShell?.Title ?? "Tarkov Companion";

    private void PreviewShellPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is null or nameof(V2ShellViewModel.Title))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    public string LastScanName
    {
        get => _lastScanName;
        private set => SetProperty(ref _lastScanName, value);
    }

    /// <summary>
    /// Whether a scan has actually produced a result to read.
    /// </summary>
    /// <remarks>
    /// Drives the scan readout between its one-line resting state and its full one. Before the
    /// first scan the detailed readout has nothing in it but the words "no item scanned", and
    /// the height it occupies is worth more to the map.
    /// </remarks>
    public bool HasScan
    {
        get => _hasScan;
        private set
        {
            if (SetProperty(ref _hasScan, value))
            {
                OnPropertyChanged(nameof(HasNoScan));
            }
        }
    }

    public bool HasNoScan => !HasScan;

    public string LastScanValue
    {
        get => _lastScanValue;
        private set => SetProperty(ref _lastScanValue, value);
    }

    public string LastScanAdvice
    {
        get => _lastScanAdvice;
        private set => SetProperty(ref _lastScanAdvice, value);
    }

    public string LastScanEvidence
    {
        get => _lastScanEvidence;
        private set => SetProperty(ref _lastScanEvidence, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_initialized)
            {
                return;
            }

            await Task.Run(
                    () => _startupCoordinator.InitializeAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);
            ApplySnapshot(_stateStore.Current);
            await Items.InitializeAsync(cancellationToken).ConfigureAwait(true);
            await Quests.InitializeAsync(cancellationToken).ConfigureAwait(true);
            await History.LoadAsync(cancellationToken).ConfigureAwait(true);
            await Scanner.LoadHistoryAsync().ConfigureAwait(true);
            await Hideout.LoadAsync(cancellationToken).ConfigureAwait(true);
            await Ammo.LoadAsync(cancellationToken).ConfigureAwait(true);
            await Keys.LoadAsync(cancellationToken).ConfigureAwait(true);
            await Events.LoadAsync(cancellationToken).ConfigureAwait(true);
            _startupCoordinator.BeginBackgroundRefresh();
            // Fire and forget, deliberately. Looking for a newer build must never be something
            // startup waits on, and a check that fails is not worth reporting at launch: the
            // gateway already reports a failure next to the button for anyone who goes looking.
            _ = Settings.WatchForUpdatesAsync(_lifetime.Token);
            await Group.InitializeAsync(cancellationToken).ConfigureAwait(true);
            _initialized = true;
            await Map.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Application startup failed.");
            _stateStore.Update(current => current with
            {
                DatabaseReady = false,
                Data = current.Data with
                {
                    Availability = DataAvailability.Error,
                    Detail = $"Application startup failed: {exception.Message}",
                },
            });
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    /// <summary>
    /// Goes to the page at <paramref name="index"/>, counting from zero.
    /// </summary>
    /// <remarks>
    /// By position rather than by name, because the shortcut is a digit and the digit is the
    /// position. Out of range does nothing: fourteen pages and ten digits means four pages the
    /// keyboard cannot reach, and a digit that silently went to the wrong one would be worse
    /// than a digit that goes nowhere.
    /// </remarks>
    public bool NavigateTo(int index)
    {
        if (index < 0 || index >= Navigation.Count)
        {
            return false;
        }

        Select(Navigation[index]);
        return true;
    }

    /// <summary>
    /// Undoes the most recent thing Escape could undo, one press at a time.
    /// </summary>
    /// <remarks>
    /// In the order somebody would expect to get out of them: the selection they just made,
    /// then the replay they opened, then the summary that appeared by itself. One press does
    /// one of these, because Escape clearing three things at once is Escape losing two of them
    /// for somebody who wanted the first.
    ///
    /// Returns whether anything was actually closed, so a press with nothing to undo is left
    /// for whatever else wants it rather than being swallowed.
    /// </remarks>
    public bool Dismiss()
    {
        if (Map.SelectedMarkers.Count > 0)
        {
            Map.ClearSelection();
            return true;
        }

        if (Raid.Replay.IsOpen)
        {
            Raid.Replay.Close();
            return true;
        }

        if (Raid.HasSummary)
        {
            Raid.DismissSummaryCommand.Execute(null);
            return true;
        }

        return false;
    }

    public bool Navigate(string pageName)
    {
        var target = Navigation.FirstOrDefault(item => string.Equals(item.Name, pageName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        Select(target);
        return true;
    }

    /// <summary>
    /// Whether the rail is a glyph column rather than a list of names.
    /// </summary>
    /// <remarks>
    /// 188 pixels of rail on a companion that shares a 3840×1080 screen with the game is 188
    /// pixels the map does not have, and at the window's minimum width the map is 584. Collapsed
    /// it is 48: the glyphs stay, the notice dots stay, and the names move to the tooltips.
    ///
    /// Not automatic. A rail that collapsed itself at some width would be a rail that changed
    /// shape while somebody was aiming at it.
    /// </remarks>
    public bool IsRailCollapsed
    {
        get => _isRailCollapsed;
        private set
        {
            if (SetProperty(ref _isRailCollapsed, value))
            {
                OnPropertyChanged(nameof(RailWidth));
                OnPropertyChanged(nameof(RailToggleLabel));
            }
        }
    }

    /// <summary>How wide the rail is drawn, which is the only thing the layout reads.</summary>
    public double RailWidth => IsRailCollapsed ? 48 : 188;

    /// <summary>A chevron pointing the way the next press moves it.</summary>
    public string RailToggleLabel => IsRailCollapsed ? "\u203a" : "\u2039";

    /// <summary>
    /// How large everything is drawn, as a multiple of the size it was designed at.
    /// </summary>
    /// <remarks>
    /// Seven fixed pixel sizes in the type scale, every column fixed, and no LayoutTransform
    /// anywhere: the only lever anybody had was Windows scaling, which scales the game on the
    /// same machine. This is the companion's own.
    ///
    /// One transform on the root, so nothing else has to know. Two honest caveats: a ComboBox
    /// dropdown and a tooltip are separate top-level windows and stay at their designed size,
    /// and the map's own pointer maths is unaffected because it already reads positions through
    /// the transforms above it.
    /// </remarks>
    public double InterfaceScale
    {
        get => _layout.Scale;
        private set
        {
            var wanted = ShellLayout.NearestScale(value);
            if (_layout.Scale.Equals(wanted))
            {
                return;
            }

            _layout = _layout with { Scale = wanted };
            OnPropertyChanged();
            OnPropertyChanged(nameof(InterfaceScaleLabel));
            _ = SaveLayoutAsync();
        }
    }

    /// <summary>The size as somebody would say it, for the Settings row.</summary>
    public string InterfaceScaleLabel => InterfaceScale.ToString("P0", CultureInfo.CurrentCulture);

    /// <summary>Steps one size larger or smaller, stopping at the ends.</summary>
    public void StepInterfaceScale(int direction) =>
        InterfaceScale = ShellLayout.StepScale(_layout.Scale, direction);

    /// <summary>Back to the size everything was designed at.</summary>
    public void ResetInterfaceScale() => InterfaceScale = 1;

    /// <summary>Collapses or expands the rail, and remembers which.</summary>
    public void ToggleRail()
    {
        IsRailCollapsed = !IsRailCollapsed;
        _ = SaveLayoutAsync();
    }

    /// <summary>
    /// Records where the window is, so the next launch opens there.
    /// </summary>
    /// <remarks>
    /// Called by the window when it is moved, resized or closed. Bounds are only stored while
    /// the window is in its normal state: a maximized window's bounds are the screen, and
    /// saving those would leave somebody who un-maximized once with a window the size of their
    /// monitor for ever.
    /// </remarks>
    public void RecordBounds(double width, double height, double left, double top, bool isMaximized)
    {
        _layout = isMaximized
            ? _layout with { IsMaximized = true }
            : _layout with
            {
                Width = width,
                Height = height,
                Left = left,
                Top = top,
                IsMaximized = false,
                IsRailCollapsed = IsRailCollapsed,
            };
        _ = SaveLayoutAsync();
    }

    /// <summary>
    /// The layout to open at, already moved back onto a screen that exists.
    /// </summary>
    /// <remarks>
    /// Somebody who left the companion on a second monitor and then unplugged it would
    /// otherwise get a window in empty space with no title bar to drag it back by, which is the
    /// one failure that cannot be recovered from inside the application.
    /// </remarks>
    public async Task<ShellLayout> LoadLayoutAsync(IReadOnlyList<ScreenBounds> screens)
    {
        if (_layoutStore is null)
        {
            return ShellLayout.Default;
        }

        try
        {
            _layout = (await _layoutStore.GetAsync(_lifetime.Token).ConfigureAwait(true)).ClampTo(screens);
            IsRailCollapsed = _layout.IsRailCollapsed;
            OnPropertyChanged(nameof(InterfaceScale));
            OnPropertyChanged(nameof(InterfaceScaleLabel));
            return _layout;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The window opens where it always used to, which is a recoverable answer.
            return ShellLayout.Default;
        }
    }

    private async Task SaveLayoutAsync()
    {
        if (_layoutStore is null)
        {
            return;
        }

        try
        {
            await _layoutStore
                .SaveAsync(_layout with { IsRailCollapsed = IsRailCollapsed }, _lifetime.Token)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A window position that could not be written is not worth telling anybody about.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _clock?.Stop();
        _clock = null;
        if (_previewShell is not null)
        {
            _previewShell.PropertyChanged -= PreviewShellPropertyChanged;
        }
        _stateStore.Changed -= RuntimeStateChanged;
        // Stops the update watcher, which otherwise outlives the window it reports to and
        // keeps making network calls for a process on its way out.
        _lifetime.Cancel();
        _lifetime.Dispose();
        _initializationLock.Dispose();
    }

    /// <summary>
    /// Runs a scan from the global shortcut.
    /// </summary>
    /// <remarks>
    /// The event arrives on the hotkey service's own message-pump thread, so the work is
    /// posted to the UI thread. The whole point of the shortcut is that the player is still
    /// in the game, so this also brings the Scanner page forward on the second monitor
    /// without anyone having to alt-tab and click.
    /// </remarks>
    private void FollowRaidMap(ApplicationRuntimeSnapshot snapshot)
    {
        var mapId = snapshot.Raid.MapId;
        if (string.IsNullOrWhiteSpace(mapId) ||
            snapshot.Raid.IsManualMapOverride ||
            string.Equals(mapId, _followedMapId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _followedMapId = mapId;
        _ = FollowRaidMapAsync(mapId);
    }

    private async Task FollowRaidMapAsync(string mapId)
    {
        try
        {
            await Map.FollowRaidAsync(mapId).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not follow the raid onto {MapId}.", mapId);
        }
    }

    private NavigationItem CreateNavigation(string name, string glyph, PageViewModel page, bool startsGroup = false) =>
        new(name, glyph, page, Select) { StartsGroup = startsGroup };

    private void Select(NavigationItem selected)
    {
        foreach (var item in Navigation)
        {
            item.IsSelected = ReferenceEquals(item, selected);
        }

        CurrentPage = selected.Page;
    }

    /// <summary>
    /// Applies the newest snapshot once, however many changes arrived while it was waiting.
    /// </summary>
    /// <remarks>
    /// Through <see cref="CoalescingDispatch"/>, which reads Current when it runs rather than
    /// closing over the snapshot the event carried — so what lands on screen is what is true
    /// when it lands rather than what was true when the event fired.
    /// </remarks>
    private void RuntimeStateChanged(object? sender, EventArgs eventArgs) => _apply.Request();

    /// <summary>
    /// The name the map chooser shows for a stored token, or the token if nothing knows.
    /// </summary>
    /// <remarks>
    /// The same lookup RaidPageViewModel has always done for the raid summary. It lived only
    /// there, so the status-bar chip and every History row printed "streets-of-tarkov" and
    /// "ground-zero-21" while the chooser above the map printed the names — two names for one
    /// place, which reads as two places.
    ///
    /// The token remains an acceptable answer: the map catalog loads last, and a name nobody
    /// has yet is better shown as the id than as "Unknown".
    /// </remarks>
    private string ResolveMapName(string? mapId) => string.IsNullOrWhiteSpace(mapId)
        ? "Unknown map"
        : Map.Locations.FirstOrDefault(location =>
            string.Equals(location.Id, mapId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(location.SourceId, mapId, StringComparison.OrdinalIgnoreCase))?.Name ?? mapId;

    private void ApplySnapshot(ApplicationRuntimeSnapshot snapshot)
    {
        var now = _timeProvider.GetUtcNow();
        ModeLabel = snapshot.IsDemoMode
            ? "Deterministic local fixture · no live game access"
            : snapshot.IsOffline
                ? "Offline"
                : string.Empty;
        // The map catalog loads after History does, so rows opened before it holds tokens
        // rather than names. Cheap: it returns immediately unless the catalog actually grew.
        History.RenameMaps(Map.Locations.Count);
        // Before the status bar, which now prints the clock the raid page works out. Two
        // places computing the same countdown would eventually disagree about it.
        Raid.Apply(snapshot, now);
        UpdateStatus(snapshot, now);
        Scanner.Apply(snapshot.Scan);
        Items.Apply(snapshot);
        Flea.Apply(snapshot);
        Hideout.Apply(snapshot);
        Ammo.Apply(snapshot);
        Keys.Apply(snapshot);
        Loadout.Apply(snapshot);
        Events.Apply(snapshot);
        Squad.Apply(snapshot);
        Group.Apply(snapshot);
        Quests.ApplyRuntime(snapshot);
        Settings.Apply(snapshot);
        FollowRaidMap(snapshot);
        // The map's own marker comes straight off the raid snapshot, so a screenshot taken
        // mid-raid appears without the player having to touch the map page. Held back once the
        // raid is not running: the trail, the "where the others started" panel and the spawn
        // lines built from it are about the raid that just finished, not something to leave
        // drawn through PostRaid on top of its own summary card.
        var isInRaid = snapshot.Raid.State == RaidLifecycleState.InRaid;
        Map.ShowPlayer(
            isInRaid ? snapshot.Raid.LastKnownPosition : null,
            isInRaid ? snapshot.Raid.PositionTrail : []);
        // The extracts a raid actually offers come from the player scanning the list, so the
        // map can mark them out from the ten it knows the map has. Same gate: an exit marked
        // "offered" by a raid that is over reads as still offered by the one that follows it.
        Map.ShowActiveExtracts(isInRaid ? snapshot.Raid.ActiveExtracts : []);
        // A PMC exit is not a worse option for a scav, it is not an option, so the map stops
        // drawing the ones this raid cannot use.
        Map.ShowSide(snapshot.Raid.Side);
        Map.ShowGroup(snapshot.Group.Members);
        Map.ShowGroupMarks(snapshot.Group.Waypoints, snapshot.Group.Pings);
        HasScan = snapshot.Scan.Succeeded;
        LastScanName = snapshot.Scan.Succeeded ? snapshot.Scan.ItemName ?? "Unnamed item" : "No item scanned";
        LastScanValue = snapshot.Scan.Succeeded && snapshot.Scan.ValueRoubles is { } value
            ? $"{value:N0} ₽ · {snapshot.Scan.ValuePerSlotRoubles.GetValueOrDefault():N0} ₽ per slot"
            : "Unavailable";
        LastScanAdvice = snapshot.Scan.Succeeded
            ? snapshot.Scan.Recommendation ?? "No recommendation was produced."
            : "No recommendation without observed evidence.";
        LastScanEvidence = snapshot.Scan.Detail;
    }

    /// <summary>
    /// Builds the six chips along the top of the window.
    /// </summary>
    /// <remarks>
    /// A chip is grey until it has something to say. Four of the six used to carry their
    /// accent colour permanently, so at rest the bar was as colourful as it ever got and
    /// nothing changing could stand out. Now colour means an observation: sage for something
    /// live, cyan for a place, ochre for a value or a warning, coral for a fault.
    /// </remarks>
    /// <param name="mapName">
    /// Resolved by the caller, because naming a map needs the catalog and this is static.
    /// </param>
    /// <summary>Rebuilds the chips, from the raid page's own reading of the clock.</summary>
    private void UpdateStatus(ApplicationRuntimeSnapshot snapshot, DateTimeOffset nowUtc) => Status = CreateStatus(
        snapshot,
        nowUtc,
        ResolveMapName(snapshot.Raid.MapId),
        Map.Area,
        Raid.TimeLeft,
        Raid.TimeLeftDetail);

    private static IReadOnlyList<StatusChip> CreateStatus(
        ApplicationRuntimeSnapshot snapshot,
        DateTimeOffset nowUtc,
        string mapName,
        string areaName,
        string timeLeft,
        string timeLeftDetail)
    {
        var raid = snapshot.Raid;
        var observation = snapshot.Observation;
        var dataAge = snapshot.Data.UpdatedUtc is { } updated
            ? FormatAge(updated, nowUtc)
            : "never synced";
        var position = raid.LastKnownPosition;
        var scan = snapshot.Scan;
        return
        [
            // The EFT chip used to be hardcoded to "Not observed" whatever was happening.
            // It now reports what the observation service is actually doing.
            new(
                "EFT",
                snapshot.IsDemoMode
                    ? "Demo fixture"
                    : observation.IsObserving ? "Observing" : observation.IsSupported ? "Not found" : "Unsupported",
                snapshot.IsDemoMode ? "No live game access" : observation.Detail,
                observation.IsObserving ? SageColor : observation.IsSupported || snapshot.IsDemoMode ? RestingColor : OchreColor),
            // The evidence line used to be a confidence percentage about the map id, which is
            // a number about our own certainty rather than about the raid. What a player wants
            // from this chip once they know the map is where on it they are, which the map
            // page already works out and nothing else was showing.
            new(
                "Map",
                raid.MapId is null ? "Unknown" : mapName,
                raid.MapId is null
                    ? observation.IsWatchingLogs ? "Waiting for a raid to start" : "No current raid evidence"
                    : position is null
                        ? $"{raid.Confidence.Value:P0} · {FormatAge(raid.UpdatedUtc, nowUtc)}"
                        : $"{areaName} · screenshot {FormatAge(position.Timestamp, nowUtc)}",
                raid.MapId is null ? RestingColor : CyanColor)
            {
                IsPrimary = true,
            },
            new(
                "Raid",
                RaidStateText.Describe(raid.State),
                raid.StartedUtc is null ? "No active session" : $"Started {raid.StartedUtc.Value.ToLocalTime():T}",
                raid.State switch
                {
                    RaidLifecycleState.InRaid => SageColor,
                    RaidLifecycleState.Unknown => RestingColor,
                    _ => InkColor,
                })
            {
                IsPrimary = true,
            },
            // Was "Position", printing raw world coordinates at reading size. Nobody reads
            // world coordinates: they are two numbers whose only use is being handed to
            // something that draws a map, and the map is already drawing it.
            //
            // Time left is the thing a player actually glances up for, and it existed only
            // twenty pixels deep in the Raid sidebar. The coordinates are kept in the tooltip,
            // where they are wanted exactly when something has gone wrong with the marker.
            new(
                "Time left",
                timeLeft,
                position is null
                    ? timeLeftDetail
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"{timeLeftDetail} · at {position.Position.X:F0}, {position.Position.Z:F0}"),
                raid.State == RaidLifecycleState.InRaid ? CyanColor : RestingColor)
            {
                IsPrimary = true,
            },
            new(
                "Data",
                snapshot.Data.Availability.ToString(),
                $"{snapshot.Data.ItemCount:N0} items · {dataAge}",
                snapshot.Data.Availability switch
                {
                    DataAvailability.Error => CoralColor,
                    DataAvailability.Unavailable => OchreColor,
                    _ => RestingColor,
                }),
            new(
                "Scan",
                scan.IsAvailable ? scan.Succeeded ? "Observed" : "Ready" : "Unavailable",
                scan.Detail,
                scan.IsAvailable ? scan.Succeeded ? OchreColor : RestingColor : OchreColor),
        ];
    }

    private static string FormatAge(DateTimeOffset observedUtc, DateTimeOffset nowUtc)
    {
        var age = nowUtc - observedUtc;
        if (age < TimeSpan.Zero)
        {
            return "future timestamp";
        }

        return age < TimeSpan.FromMinutes(1)
            ? $"{Math.Max(0, (int)age.TotalSeconds)}s ago"
            : age < TimeSpan.FromHours(1)
                ? $"{(int)age.TotalMinutes}m ago"
                : $"{(int)age.TotalHours}h ago";
    }
}
