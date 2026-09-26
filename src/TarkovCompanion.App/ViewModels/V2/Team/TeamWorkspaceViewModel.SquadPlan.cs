using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.ViewModels.V2.Team;

/// <summary>[#712 T7] A squad member's initial, in the colour their marker is drawn in.</summary>
public sealed record TeamMemberInitialViewModel(string Initial, IBrush Colour);

/// <summary>[#712 T7] One shared quest on the planned map, in the suggested order.</summary>
/// <param name="Members">"You, Geo": everyone who has it active.</param>
/// <param name="TogetherLabel">The first objective one trip does for several of them ("Mark the fuel tank").</param>
public sealed record TeamSharedQuestRowViewModel(
    string Order,
    string Name,
    string Members,
    string TogetherLabel,
    IReadOnlyList<TeamMemberInitialViewModel> Initials)
{
    public bool IsTogether => TogetherLabel.Length > 0;
}

/// <summary>[#712 T7] Another map with shared quests, which planning switches to.</summary>
public sealed record TeamPlanMapChipViewModel(string Label, bool IsSelected, ICommand SelectCommand);

/// <summary>[#712 T7] One line of a member's Loadout check: "keys" ticked, "no MS2000 Marker" crossed.</summary>
public sealed record TeamLoadoutChipViewModel(string Label, bool? Ok)
{
    public bool IsOk => Ok == true;

    public bool IsMissing => Ok == false;

    public bool IsUnknown => Ok is null;
}

/// <summary>[#712 T7] One member's row in the squad ready check, with where it came from.</summary>
/// <param name="Source">"shared by Geo's companion · 4 min ago", or "checked here · 1 min ago" for this player.</param>
public sealed record TeamReadyRowViewModel(
    string Name,
    IBrush? Colour,
    bool? Ready,
    string Level,
    IReadOnlyList<TeamLoadoutChipViewModel> Checks,
    string Source,
    string Empty)
{
    public bool HasColour => Colour is not null;

    public bool HasChecks => Checks.Count > 0;

    public bool HasEmpty => Empty.Length > 0;

    public bool HasLevel => Level.Length > 0;

    public bool IsReady => Ready == true;

    public bool IsNotReady => Ready == false;

    public int MissingCount => Checks.Count(check => check.IsMissing);

    /// <summary>Equal by what it shows, so an unchanged squad does not rebuild the list every second.</summary>
    public bool Equals(TeamReadyRowViewModel? other) =>
        other is not null && Name == other.Name && Ready == other.Ready && Level == other.Level &&
        Source == other.Source && Empty == other.Empty && Checks.SequenceEqual(other.Checks) &&
        (Colour as ISolidColorBrush)?.Color == (other.Colour as ISolidColorBrush)?.Color;

    public override int GetHashCode() => HashCode.Combine(Name, Ready, Level, Source);
}

/// <summary>
/// [#712 T7] Team's squad plan: the shared-task planner ("Tonight for the squad") and the ready check
/// from each member's own Loadout check.
/// </summary>
/// <remarks>
/// Every squad fact here is what a member's own companion sent: quest and objective ids (#780),
/// ready or not (#289), and their Loadout check and level. Nothing is read about anybody who does
/// not run the app, and nothing comes from this player's game log about the others.
/// </remarks>
public sealed partial class TeamWorkspaceViewModel
{
    private static readonly IBrush SelfColour = new SolidColorBrush(Color.Parse("#FF63D4DD"));

    private GroupReadyCheckShare? _readyCheck;
    private IMapDataService? _mapData;
    private Func<string, Task>? _openInRaid;
    private readonly Dictionary<string, string> _planMapNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _planMapNamesAsked = new(StringComparer.OrdinalIgnoreCase);
    private string? _chosenPlanMapId;
    private SharedReadiness? _ownReadiness;
    private bool _readingOwnReadiness;
    private bool _sharesReadyCheck = true;
    private ICommand? _putPlanOnRaidCommand;

    /// <summary>"My ready check": this player's own Loadout check and level go to the squad. On by default.</summary>
    public bool SharesReadyCheck
    {
        get => _sharesReadyCheck;
        set
        {
            if (SetProperty(ref _sharesReadyCheck, value))
            {
                SwitchFlipped();
                RefreshSquadPlan(_group);
            }
        }
    }

    /// <summary>The planned map, by quest catalog id; null with no shared quest anywhere.</summary>
    public string? PlanMapId { get; private set; }

    /// <summary>"Customs · 5 shared quests".</summary>
    public string PlanTitle { get; private set; } = string.Empty;

    public bool HasPlan => SharedQuestRows.Count > 0;

    public bool HasNoPlan => !HasPlan;

    public IReadOnlyList<TeamSharedQuestRowViewModel> SharedQuestRows { get; private set; } = [];

    /// <summary>The other maps with shared quests, and the planned one, to switch between.</summary>
    public IReadOnlyList<TeamPlanMapChipViewModel> PlanMapChips { get; private set; } = [];

    public bool HasPlanMapChips => PlanMapChips.Count > 1;

    /// <summary>Opens the Raid map on the planned map with the squad's stops on the objective route.</summary>
    public ICommand PutPlanOnRaidCommand => _putPlanOnRaidCommand ??= new AsyncDelegateCommand(PutPlanOnRaidAsync);

    /// <summary>One row per member: this player first, then each squadmate in the relay's order.</summary>
    public IReadOnlyList<TeamReadyRowViewModel> ReadyRows { get; private set; } = [];

    public bool HasReadyRows => ReadyRows.Count > 0;

    /// <summary>"2 of 3 ready · Riley is missing 1 item".</summary>
    public string ReadyCheckSummary { get; private set; } = string.Empty;

    public bool ReadyCheckHasMissing { get; private set; }

    /// <summary>
    /// Plan's "Open in Raid" for a quest map id: puts the player's own quests there on the map with
    /// their route. Set by the shell, which holds both workspaces.
    /// </summary>
    public void AttachOpenInRaid(Func<string, Task> openInRaid) => _openInRaid = openInRaid;

    private void AttachSquadPlan(GroupReadyCheckShare? readyCheck, IMapDataService? mapData)
    {
        _readyCheck = readyCheck;
        _mapData = mapData;
        if (readyCheck is not null)
        {
            readyCheck.Changed += () => _dispatch(() =>
            {
                _ownReadiness = null;
                RefreshSquadPlan(_group);
            });
        }
    }

    /// <summary>Rebuilds the plan and the ready rows; runs on every <see cref="Apply"/> and quest change.</summary>
    private void RefreshSquadPlan(GroupSnapshot group)
    {
        try
        {
            RefreshPlan();
            RefreshReadyRows(group);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An extra on the page: a fault here must not take the Team workspace with it.
            CrashLog.Write("workspace-fault/team-plan", exception.ToString());
        }
    }

    private void RefreshPlan()
    {
        var picture = _squadQuests?.Picture ?? SquadQuestPicture.Empty;
        var maps = SquadTaskPlanner.Plan(picture);
        foreach (var map in maps)
        {
            AskMapName(map.MapId);
        }

        var shown = ChoosePlanMap(maps, _chosenPlanMapId, CurrentRaidMapName(), MapName);
        var mapId = shown?.MapId;
        if (!string.Equals(mapId, PlanMapId, StringComparison.OrdinalIgnoreCase))
        {
            PlanMapId = mapId;
            OnPropertyChanged(nameof(PlanMapId));
            // The ready check is made for the map the squad is planning.
            _readyCheck?.SetMap(mapId);
        }

        var rows = shown is null ? [] : BuildSharedQuestRows(shown, ColourFor);
        var title = shown is null ? string.Empty : TeamPlanText.Title(MapName(shown.MapId), shown.Quests.Count);
        IReadOnlyList<TeamPlanMapChipViewModel> chips =
        [
            .. maps.Select(map => new TeamPlanMapChipViewModel(
                TeamPlanText.MapCount(MapName(map.MapId), map.Quests.Count),
                string.Equals(map.MapId, mapId, StringComparison.OrdinalIgnoreCase),
                new DelegateCommand(() => ChoosePlanMap(map.MapId)))),
        ];
        if (title != PlanTitle || !SameRows(rows, SharedQuestRows) || !chips.Select(chip => (chip.Label, chip.IsSelected)).SequenceEqual(PlanMapChips.Select(chip => (chip.Label, chip.IsSelected))))
        {
            PlanTitle = title;
            SharedQuestRows = rows;
            PlanMapChips = chips;
            OnPropertyChanged(nameof(PlanTitle));
            OnPropertyChanged(nameof(SharedQuestRows));
            OnPropertyChanged(nameof(HasPlan));
            OnPropertyChanged(nameof(HasNoPlan));
            OnPropertyChanged(nameof(PlanMapChips));
            OnPropertyChanged(nameof(HasPlanMapChips));
        }
    }

    private static bool SameRows(IReadOnlyList<TeamSharedQuestRowViewModel> left, IReadOnlyList<TeamSharedQuestRowViewModel> right) =>
        left.Select(row => (row.Order, row.Name, row.Members, row.TogetherLabel))
            .SequenceEqual(right.Select(row => (row.Order, row.Name, row.Members, row.TogetherLabel)));

    private void ChoosePlanMap(string mapId)
    {
        _chosenPlanMapId = mapId;
        RefreshSquadPlan(_group);
    }

    /// <summary>
    /// The map the plan shows: the one the player picked here, else the Raid map on screen when the
    /// squad shares a quest there, else the map with the most shared quests.
    /// </summary>
    internal static SquadPlanMap? ChoosePlanMap(
        IReadOnlyList<SquadPlanMap> maps,
        string? chosen,
        string? raidMapName,
        Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(nameOf);
        return maps.FirstOrDefault(map => string.Equals(map.MapId, chosen, StringComparison.OrdinalIgnoreCase))
            ?? (raidMapName is { Length: > 0 }
                ? maps.FirstOrDefault(map => string.Equals(nameOf(map.MapId), raidMapName, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? maps.FirstOrDefault();
    }

    /// <summary>The planner's rows, numbered in its suggested order.</summary>
    internal static IReadOnlyList<TeamSharedQuestRowViewModel> BuildSharedQuestRows(SquadPlanMap map, Func<string, IBrush> colourFor)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(colourFor);
        return
        [
            .. map.Quests.Select(quest => new TeamSharedQuestRowViewModel(
                quest.Order.ToString(CultureInfo.CurrentCulture),
                quest.Name,
                string.Join(", ", quest.Members),
                quest.Objectives.FirstOrDefault(objective => objective.Together)?.Description ?? string.Empty,
                [.. quest.Members.Select(name => new TeamMemberInitialViewModel(Initial(name), colourFor(name)))])),
        ];
    }

    private static string Initial(string name) =>
        name.Trim() is { Length: > 0 } trimmed ? trimmed[..1].ToUpper(CultureInfo.CurrentCulture) : "?";

    private IBrush ColourFor(string name) =>
        name == TeamText.You || _raidCockpit?.SquadColorFor(name) is not { } hex || !Color.TryParse(hex, out var colour)
            ? SelfColour
            : new SolidColorBrush(colour);

    private string? CurrentRaidMapName() => _raidCockpit?.SelectedMap?.Name;

    private string MapName(string mapId) => _planMapNames.TryGetValue(mapId, out var name) ? name : mapId;

    /// <summary>Names a quest map id once, from the synced maps table, then plans again.</summary>
    private void AskMapName(string mapId)
    {
        if (_mapData is null || !_planMapNamesAsked.Add(mapId))
        {
            return;
        }

        _ = NameMapAsync(_mapData, mapId);
    }

    private async Task NameMapAsync(IMapDataService maps, string mapId)
    {
        try
        {
            var map = await maps.GetAsync(mapId, CancellationToken.None).ConfigureAwait(false);
            if (map is { Name.Length: > 0 })
            {
                _dispatch(() =>
                {
                    _planMapNames[mapId] = map.Name;
                    RefreshSquadPlan(_group);
                });
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The id stands in for the name.
            CrashLog.Write("workspace-fault/team-plan", $"map name {mapId}: {exception.Message}");
        }
    }

    private async Task PutPlanOnRaidAsync()
    {
        if (PlanMapId is not { } mapId)
        {
            return;
        }

        if (_raidCockpit is { } raid)
        {
            // The squad's open objectives on the map, and on the objective route with the player's own.
            raid.ShowSquadObjectives = true;
            raid.RouteSquadStops = true;
        }

        if (_openInRaid is not null)
        {
            await _openInRaid(mapId).ConfigureAwait(true);
        }
        else
        {
            _navigate?.Invoke(V2Routes.Raid);
        }
    }

    private void RefreshReadyRows(GroupSnapshot group)
    {
        if (_readyCheck is not null && _sharesReadyCheck && _ownReadiness is null && !_readingOwnReadiness)
        {
            _readingOwnReadiness = true;
            _ = ReadOwnReadinessAsync(_readyCheck);
        }

        var now = _clock.GetUtcNow();
        var own = _sharesReadyCheck ? _ownReadiness : null;
        var rows = group.IsSharing || group.Members.Count > 0
            ? BuildReadyRows(SelfName(), MyStatus.Ready, own, now, group.Members, ColourFor)
            : [];
        var ready = (MyStatus.Ready == true ? 1 : 0) + group.Members.Count(member => member.Ready == true);
        var summary = rows.Count == 0 ? string.Empty : DescribeReadyCheck(ready, rows);
        var missing = rows.Any(row => row.MissingCount > 0);
        if (!rows.SequenceEqual(ReadyRows) || summary != ReadyCheckSummary || missing != ReadyCheckHasMissing)
        {
            ReadyRows = rows;
            ReadyCheckSummary = summary;
            ReadyCheckHasMissing = missing;
            OnPropertyChanged(nameof(ReadyRows));
            OnPropertyChanged(nameof(HasReadyRows));
            OnPropertyChanged(nameof(ReadyCheckSummary));
            OnPropertyChanged(nameof(ReadyCheckHasMissing));
        }
    }

    private string SelfName() => DisplayName.Trim() is { Length: > 0 } name ? name : TeamText.You;

    private async Task ReadOwnReadinessAsync(GroupReadyCheckShare share)
    {
        try
        {
            var readiness = await share.GetAsync(CancellationToken.None).ConfigureAwait(false);
            _dispatch(() =>
            {
                _ownReadiness = readiness;
                _readingOwnReadiness = false;
                RefreshReadyRows(_group);
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _dispatch(() => _readingOwnReadiness = false);
            CrashLog.Write("workspace-fault/team-plan", $"own ready check: {exception.Message}");
        }
    }

    /// <summary>This player's row first, then one per squadmate, each only what their companion shared.</summary>
    internal static IReadOnlyList<TeamReadyRowViewModel> BuildReadyRows(
        string selfName,
        bool? selfReady,
        SharedReadiness? own,
        DateTimeOffset nowUtc,
        IReadOnlyList<GroupMemberView> members,
        Func<string, IBrush> colourFor)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(colourFor);
        var rows = new List<TeamReadyRowViewModel>(members.Count + 1);
        var ownItems = own?.Check?.Items ?? [];
        rows.Add(new(
            selfName,
            SelfColour,
            selfReady,
            own?.Level is { } ownLevel ? TeamPlanText.Level(ownLevel) : string.Empty,
            Chips(ownItems),
            own?.Check is { } check ? TeamPlanText.CheckedHere(Ago(nowUtc - check.CheckedUtc)) : string.Empty,
            own?.Check is null ? TeamPlanText.NothingShared : ownItems.Count == 0 ? TeamPlanText.NothingToCheck : string.Empty));
        foreach (var member in members)
        {
            var items = member.LoadoutCheck?.Items ?? [];
            // The check's own age, plus how long since the relay last heard from them.
            var age = member.LoadoutCheck?.Age is { } checkAge ? checkAge + (member.Since ?? TimeSpan.Zero) : (TimeSpan?)null;
            var shared = member.LoadoutCheck is not null || member.Level is not null;
            rows.Add(new(
                member.Name,
                colourFor(member.Name),
                member.Ready,
                member.Level is { } level ? TeamPlanText.Level(level) : string.Empty,
                Chips(items),
                shared ? TeamPlanText.SharedBy(member.Name, age is { } known ? Ago(known) : TeamText.JustNow) : string.Empty,
                member.LoadoutCheck is null
                    ? (shared ? string.Empty : TeamPlanText.NothingShared)
                    : items.Count == 0 ? TeamPlanText.NothingToCheck : string.Empty));
        }

        return rows;
    }

    private static IReadOnlyList<TeamLoadoutChipViewModel> Chips(IReadOnlyList<LoadoutCheckItem> items) =>
    [
        .. items.Select(item => new TeamLoadoutChipViewModel(
            item.Ok switch
            {
                true => TeamPlanText.Kind(item.Kind),
                false => TeamPlanText.Missing(item.Missing ?? TeamPlanText.Kind(item.Kind)),
                null => TeamPlanText.NotScanned(TeamPlanText.Kind(item.Kind)),
            },
            item.Ok)),
    ];

    private static string Ago(TimeSpan elapsed) =>
        TeamText.Ago(UnitText.Duration(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed));

    /// <summary>"2 of 3 ready · Riley is missing 1 item"; "… · Nothing missing" when every check is complete.</summary>
    internal static string DescribeReadyCheck(int ready, IReadOnlyList<TeamReadyRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var missing = rows.Where(row => row.MissingCount > 0).ToArray();
        var checkedAny = rows.Any(row => row.HasChecks);
        var tail = missing.Length > 0
            ? string.Join(" · ", missing.Select(row => TeamPlanText.MemberMissing(row.Name, row.MissingCount)))
            : checkedAny ? TeamPlanText.AllThere : string.Empty;
        var head = TeamPlanText.Summary(ready, rows.Count);
        return tail.Length == 0 ? head : $"{head} · {tail}";
    }
}
