using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>One station, at whatever level the profile says it is built to.</summary>
public sealed class HideoutStationRowViewModel : BindableViewModel
{
    private bool _isSelected;

    internal HideoutStationRowViewModel(
        string stationId,
        string name,
        string currentLevelLabel,
        bool hasNextLevel,
        int nextLevel,
        bool canBuildNow,
        int missingItemCount,
        Action<string> select)
    {
        StationId = stationId;
        Name = name;
        CurrentLevelLabel = currentLevelLabel;
        HasNextLevel = hasNextLevel;
        NextLevel = nextLevel;
        CanBuildNow = canBuildNow;
        MissingItemCount = missingItemCount;
        SelectCommand = new DelegateCommand(() => select(stationId));
    }

    public string StationId { get; }

    public string Name { get; }

    public string CurrentLevelLabel { get; }

    public bool HasNextLevel { get; }

    public int NextLevel { get; }

    public bool CanBuildNow { get; }

    public int MissingItemCount { get; }

    public string NextLevelSummary => !HasNextLevel
        ? "Fully built."
        : CanBuildNow
            ? $"Level {NextLevel} · you have everything"
            : $"Level {NextLevel} · missing {MissingItemCount} item(s)";

    /// <summary>The short state chip on the station card: ready, how much is missing, or maxed.</summary>
    public string StateLabel => !HasNextLevel
        ? "Max level"
        : CanBuildNow
            ? "Ready"
            : $"{MissingItemCount} missing";

    public bool IsReady => HasNextLevel && CanBuildNow;

    /// <summary>The level the profile says is built, which the stepper edits.</summary>
    public int BuiltLevel { get; init; }

    /// <summary>The highest level the station has.</summary>
    public int MaximumLevel { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public ICommand SelectCommand { get; }
}

/// <summary>One item a station's next level still needs.</summary>
public sealed record HideoutRequirementRowViewModel(
    string ItemName,
    string Required,
    string Owned,
    string Remaining,
    bool IsSatisfied)
{
    /// <summary>"2 / 5": owned against required, the requirement row's right-hand figure.</summary>
    public string ProgressLabel => $"{Owned} / {Required}";

    /// <summary>The cheapest barter, where one beats buying the item and the player's loyalty allows it; empty otherwise.</summary>
    public string CheapestRoute { get; init; } = string.Empty;

    public bool HasCheapestRoute => CheapestRoute.Length > 0;
}

/// <summary>One item still short across the next level of every station, with the totals behind it.</summary>
public sealed record HideoutRollupRowViewModel(string ItemName, int Need, int Have)
{
    public int Remaining => Math.Max(0, Need - Have);

    public string ProgressLabel => $"{Have:N0} / {Need:N0}";
}

/// <summary>
/// V2 Hideout section for the Plan route's Hideout child: current level, what the next level
/// needs, and what can be built now versus what is still missing — read from the same requirement
/// catalog and profile the V1 Hideout page already uses. No crafts/barters engine (#307).
/// </summary>
public sealed class HideoutWorkspaceViewModel : BindableViewModel
{
    private readonly IRequirementCatalog _requirements;
    private readonly IPlayerProfileService _profileService;
    private readonly IItemRepository _itemRepository;
    private readonly IBarterCatalog? _barters;
    private readonly ITraderCatalog? _traders;
    private IReadOnlyDictionary<string, int> _traderLevels = new Dictionary<string, int>(StringComparer.Ordinal);
    private IReadOnlyList<HideoutRollupRowViewModel> _rollup = [];
    private IReadOnlyDictionary<string, int> _ownedItemCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    private IReadOnlyList<HideoutItemRequirement> _allRequirements = [];
    private IReadOnlyList<HideoutStationRowViewModel> _stations = [];
    private IReadOnlyList<HideoutRequirementRowViewModel> _items = [];
    private HideoutStationRowViewModel? _selected;
    private string _status = "Loading the hideout catalog…";
    private string _detail = "Pick a station";

    public HideoutWorkspaceViewModel(
        IRequirementCatalog requirements,
        IPlayerProfileService profileService,
        IItemRepository itemRepository,
        // Package 28: the cheapest-barter line V1's Hideout page carries. Optional, so a
        // composition without the barter catalog simply has no route lines.
        IBarterCatalog? barters = null,
        ITraderCatalog? traders = null)
    {
        _barters = barters;
        _traders = traders;
        _requirements = requirements ?? throw new ArgumentNullException(nameof(requirements));
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
        RefreshCommand = new AsyncDelegateCommand(RefreshAsync);
    }

    public AsyncDelegateCommand RefreshCommand { get; }

    public IReadOnlyList<HideoutStationRowViewModel> Stations
    {
        get => _stations;
        private set
        {
            if (SetProperty(ref _stations, value))
            {
                OnPropertyChanged(nameof(HasStations));
                OnPropertyChanged(nameof(StationCountLabel));
            }
        }
    }

    public bool HasStations => Stations.Count > 0;

    public IReadOnlyList<HideoutRequirementRowViewModel> Items
    {
        get => _items;
        private set
        {
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(HasItems));
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public string SelectedStationName => _selected?.Name ?? string.Empty;

    /// <summary>"Level 1 of 3", under the selected station's name.</summary>
    public string SelectedLevelLabel => _selected?.CurrentLevelLabel ?? string.Empty;

    public bool HasItems => Items.Count > 0;

    /// <summary>Whether the selected station has a level above the one built.</summary>
    public bool CanRaiseLevel => _selected is { HasNextLevel: true };

    public bool CanLowerLevel => _selected is { BuiltLevel: > 0 };

    public ICommand RaiseLevelCommand => _raiseLevel ??= new AsyncDelegateCommand(() => ChangeLevelAsync(1));

    public ICommand LowerLevelCommand => _lowerLevel ??= new AsyncDelegateCommand(() => ChangeLevelAsync(-1));

    private ICommand? _raiseLevel;
    private ICommand? _lowerLevel;

    /// <summary>Items still short for the next level of every station, each counted once across them.</summary>
    public IReadOnlyList<HideoutRollupRowViewModel> Rollup
    {
        get => _rollup;
        private set
        {
            if (SetProperty(ref _rollup, value))
            {
                OnPropertyChanged(nameof(HasRollup));
                OnPropertyChanged(nameof(RollupHeading));
            }
        }
    }

    public bool HasRollup => _rollup.Count > 0;

    public string RollupHeading => _rollup.Count == 1 ? "1 item still needed" : $"{_rollup.Count:N0} items still needed";

    /// <summary>"26 stations", the station list's heading figure.</summary>
    public string StationCountLabel => Stations.Count == 1 ? "1 station" : $"{Stations.Count:N0} stations";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public Task LoadAsync() => RefreshAsync(CancellationToken.None);

    public Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stations = await _requirements.GetStationsAsync(cancellationToken).ConfigureAwait(true);
            if (stations.Count == 0)
            {
                Stations = [];
                Items = [];
                Rollup = [];
                _selected = null;
                OnPropertyChanged(nameof(HasSelection));
                Status = "No hideout data cached yet.";
                return;
            }

            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            _ownedItemCounts = profile.OwnedItemCounts;
            _traderLevels = profile.TraderLevels;
            _allRequirements = await _requirements.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(true);
            var plans = HideoutPlanner.Plan(stations, profile.HideoutStationLevels, _allRequirements, _ownedItemCounts);

            var selectedStationId = _selected?.StationId;
            Stations = plans
                .Select(Describe)
                .OrderByDescending(station => station.HasNextLevel && !station.CanBuildNow)
                .ThenByDescending(station => station.HasNextLevel)
                .ThenBy(station => station.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var buildable = Stations.Count(station => station.HasNextLevel && station.CanBuildNow);
            Status = $"{StationCountLabel} · {buildable} ready to build now";
            Rollup = await OffInterfaceThread.Run(() => BuildRollupAsync(plans, cancellationToken), cancellationToken).ConfigureAwait(true);

            // The detail pane is the page's primary content, so something is always selected
            // once stations exist: the previous choice if it survived, else the first station.
            var reselect = Stations.FirstOrDefault(station => station.StationId == selectedStationId)
                ?? Stations[0];
            await SelectAsync(reselect, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Raw exception text is diagnostics, not page copy; see PlanWorkspaceViewModel.
            Stations = [];
            Items = [];
            Status = "Hideout data isn't available yet.";
            WorkspaceFault.Record("hideout", "refresh", exception);
        }
    }

    internal async Task SelectAsync(HideoutStationRowViewModel station, CancellationToken cancellationToken)
    {
        if (_selected is not null)
        {
            _selected.IsSelected = false;
        }

        _selected = station;
        station.IsSelected = true;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedStationName));
        OnPropertyChanged(nameof(SelectedLevelLabel));
        OnPropertyChanged(nameof(CanRaiseLevel));
        OnPropertyChanged(nameof(CanLowerLevel));
        if (!station.HasNextLevel)
        {
            Items = [];
            Detail = "Already at its highest level.";
            return;
        }

        try
        {
            var wanted = _allRequirements
                .Where(requirement =>
                    string.Equals(requirement.StationId, station.StationId, StringComparison.OrdinalIgnoreCase) &&
                    requirement.TargetLevel == station.NextLevel)
                .ToArray();
            // Off the interface thread as a whole. Routing prices every input of every barter in
            // the catalog, one query each, before it can compare them, and the rows then look up
            // a name apiece; none of it touches anything the view is bound to.
            var ownedCounts = _ownedItemCounts;
            var traderLevels = _traderLevels;
            var rows = await OffInterfaceThread.Run(
                async () =>
                {
                    var routes = await HideoutBarterRoutes
                        .ComputeAsync(_barters, _traders, _itemRepository, wanted, traderLevels, cancellationToken)
                        .ConfigureAwait(false);
                    var built = new List<HideoutRequirementRowViewModel>(wanted.Length);
                    foreach (var requirement in wanted)
                    {
                        var item = await _itemRepository.GetAsync(requirement.ItemId, cancellationToken).ConfigureAwait(false);
                        var owned = ownedCounts.GetValueOrDefault(requirement.ItemId);
                        var remaining = Math.Max(0, requirement.Required - owned);
                        built.Add(new(
                            item?.Name ?? requirement.ItemId,
                            Count(requirement.Required),
                            Count(owned),
                            remaining == 0 ? "Complete" : Count(remaining),
                            remaining == 0)
                        {
                            CheapestRoute = routes.GetValueOrDefault(requirement.ItemId, string.Empty),
                        });
                    }

                    return built;
                },
                cancellationToken).ConfigureAwait(true);

            Items = rows
                .OrderBy(row => row.IsSatisfied)
                .ThenBy(row => row.ItemName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var outstanding = Items.Count(row => !row.IsSatisfied);
            Detail = Items.Count == 0
                ? $"Level {station.NextLevel} needs no items."
                : outstanding == 0
                    ? $"Level {station.NextLevel} · you have everything"
                    : $"Level {station.NextLevel} · {outstanding} of {Items.Count} still needed";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Items = [];
            Detail = "Requirements aren't available yet.";
            WorkspaceFault.Record("hideout", "read requirements", exception);
        }
    }

    public void Select(HideoutStationRowViewModel? station)
    {
        if (station is null)
        {
            return;
        }

        _ = SelectAsync(station, CancellationToken.None);
    }

    private Task ChangeLevelAsync(int direction) =>
        _selected is { } row ? SetBuiltLevelAsync(row.StationId, row.BuiltLevel + direction) : Task.CompletedTask;

    /// <summary>
    /// Records the level a station has been built to, and re-reads everything, because a station
    /// at a different level has different requirements rather than the same ones with another number.
    /// </summary>
    /// <remarks>
    /// Clamped to what the catalog says the station has: the profile is a file somebody can open,
    /// and a station at level 9 of 3 would make the "next level" arithmetic name one that does not exist.
    /// </remarks>
    internal async Task SetBuiltLevelAsync(string stationId, int level)
    {
        var row = Stations.FirstOrDefault(station => string.Equals(station.StationId, stationId, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return;
        }

        var wanted = Math.Clamp(level, 0, row.MaximumLevel);
        if (wanted == row.BuiltLevel)
        {
            return;
        }

        try
        {
            var profile = await _profileService.GetActiveAsync(CancellationToken.None).ConfigureAwait(true);
            var levels = new Dictionary<string, int>(profile.HideoutStationLevels, StringComparer.OrdinalIgnoreCase)
            {
                [stationId] = wanted,
            };
            await _profileService
                .SaveAsync(profile with { HideoutStationLevels = levels, UpdatedUtc = DateTimeOffset.UtcNow }, CancellationToken.None)
                .ConfigureAwait(true);
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Station level not saved · {exception.Message}";
        }
    }

    /// <summary>What is still short across every station's next level, named; the sums are <see cref="HideoutPlanner.Shortfall"/>.</summary>
    private async Task<IReadOnlyList<HideoutRollupRowViewModel>> BuildRollupAsync(
        IReadOnlyList<HideoutStationPlan> plans,
        CancellationToken cancellationToken)
    {
        var shortfalls = HideoutPlanner.Shortfall(plans, _ownedItemCounts);
        var rows = new List<HideoutRollupRowViewModel>(shortfalls.Count);
        foreach (var shortfall in shortfalls)
        {
            var item = await _itemRepository.GetAsync(shortfall.ItemId, cancellationToken).ConfigureAwait(true);
            rows.Add(new(item?.Name ?? shortfall.ItemId, shortfall.Need, shortfall.Have));
        }

        return
        [
            .. rows
                .OrderByDescending(row => row.Remaining)
                .ThenBy(row => row.ItemName, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    private HideoutStationRowViewModel Describe(HideoutStationPlan plan) => new(
        plan.StationId,
        plan.Name,
        plan.MaximumLevel == 0 ? $"Level {plan.BuiltLevel}" : $"Level {plan.BuiltLevel} of {plan.MaximumLevel}",
        plan.HasNextLevel,
        plan.NextLevel,
        canBuildNow: plan.CanBuildNow,
        missingItemCount: plan.MissingItemCount,
        select: stationId =>
        {
            var found = Stations.FirstOrDefault(row => row.StationId == stationId);
            Select(found);
        })
    {
        BuiltLevel = plan.BuiltLevel,
        MaximumLevel = plan.MaximumLevel,
    };

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
