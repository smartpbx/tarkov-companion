using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>One station, at whatever level the profile says it is built to.</summary>
public sealed class HideoutStationRowViewModel : BindableViewModel
{
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

    public ICommand SelectCommand { get; }
}

/// <summary>One item a station's next level still needs.</summary>
public sealed record HideoutRequirementRowViewModel(
    string ItemName,
    string Required,
    string Owned,
    string Remaining,
    bool IsSatisfied);

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
        IItemRepository itemRepository)
    {
        _requirements = requirements ?? throw new ArgumentNullException(nameof(requirements));
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
        RefreshCommand = new AsyncDelegateCommand(RefreshAsync);
    }

    public AsyncDelegateCommand RefreshCommand { get; }

    public IReadOnlyList<HideoutStationRowViewModel> Stations
    {
        get => _stations;
        private set => SetProperty(ref _stations, value);
    }

    public bool HasStations => Stations.Count > 0;

    public IReadOnlyList<HideoutRequirementRowViewModel> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    public bool HasSelection => _selected is not null;

    public string SelectedStationName => _selected?.Name ?? string.Empty;

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
                Status = "No hideout data cached yet.";
                return;
            }

            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            _ownedItemCounts = profile.OwnedItemCounts;
            _allRequirements = await _requirements.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(true);
            var byStation = _allRequirements
                .GroupBy(requirement => requirement.StationId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

            var selectedStationId = _selected?.StationId;
            Stations = stations
                .Select(station => Describe(station, profile.HideoutStationLevels, byStation))
                .OrderByDescending(station => station.HasNextLevel && !station.CanBuildNow)
                .ThenByDescending(station => station.HasNextLevel)
                .ThenBy(station => station.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var buildable = Stations.Count(station => station.HasNextLevel && station.CanBuildNow);
            Status = $"{Stations.Count} station(s) · {buildable} ready to build now";

            var reselect = Stations.FirstOrDefault(station => station.StationId == selectedStationId);
            if (reselect is not null)
            {
                await SelectAsync(reselect, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                _selected = null;
                Items = [];
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedStationName));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Stations = [];
            Items = [];
            Status = $"Unavailable · {exception.Message}";
        }
    }

    internal async Task SelectAsync(HideoutStationRowViewModel station, CancellationToken cancellationToken)
    {
        _selected = station;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedStationName));
        if (!station.HasNextLevel)
        {
            Items = [];
            Detail = $"{station.Name} · already at its highest level";
            return;
        }

        try
        {
            var wanted = _allRequirements
                .Where(requirement =>
                    string.Equals(requirement.StationId, station.StationId, StringComparison.OrdinalIgnoreCase) &&
                    requirement.TargetLevel == station.NextLevel)
                .ToArray();
            var rows = new List<HideoutRequirementRowViewModel>(wanted.Length);
            foreach (var requirement in wanted)
            {
                var item = await _itemRepository.GetAsync(requirement.ItemId, cancellationToken).ConfigureAwait(true);
                var owned = _ownedItemCounts.GetValueOrDefault(requirement.ItemId);
                var remaining = Math.Max(0, requirement.Required - owned);
                rows.Add(new(
                    item?.Name ?? requirement.ItemId,
                    Count(requirement.Required),
                    Count(owned),
                    remaining == 0 ? "Complete" : Count(remaining),
                    remaining == 0));
            }

            Items = rows
                .OrderBy(row => row.IsSatisfied)
                .ThenBy(row => row.ItemName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var outstanding = Items.Count(row => !row.IsSatisfied);
            Detail = Items.Count == 0
                ? $"{station.Name} level {station.NextLevel} · no items needed"
                : outstanding == 0
                    ? $"{station.Name} level {station.NextLevel} · you have everything"
                    : $"{station.Name} level {station.NextLevel} · {outstanding} of {Items.Count} still needed";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Items = [];
            Detail = $"Unreadable · {exception.Message}";
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

    private HideoutStationRowViewModel Describe(
        HideoutStationSummary station,
        IReadOnlyDictionary<string, int> builtLevels,
        IReadOnlyDictionary<string, HideoutItemRequirement[]> requirementsByStation)
    {
        var built = builtLevels.GetValueOrDefault(station.StationId);
        var next = station.Levels.Where(level => level > built).DefaultIfEmpty(0).Min();
        var hasNext = next > 0;
        var maximum = station.Levels.Count == 0 ? 0 : station.Levels.Max();
        var nextLevelRequirements = hasNext && requirementsByStation.TryGetValue(station.StationId, out var all)
            ? all.Where(requirement => requirement.TargetLevel == next).ToArray()
            : [];
        var missing = nextLevelRequirements
            .Count(requirement => _ownedItemCounts.GetValueOrDefault(requirement.ItemId) < requirement.Required);

        return new(
            station.StationId,
            station.Name,
            maximum == 0 ? $"Level {built}" : $"Level {built} of {maximum}",
            hasNext,
            next,
            canBuildNow: hasNext && missing == 0,
            missingItemCount: missing,
            select: stationId =>
            {
                var found = Stations.FirstOrDefault(row => row.StationId == stationId);
                Select(found);
            });
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
