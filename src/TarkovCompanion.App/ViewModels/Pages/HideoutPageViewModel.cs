using System.Globalization;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.ViewModels;

public sealed record HideoutStationViewModel(
    string StationId,
    string Name,
    string Progress,
    int NextLevel,
    bool HasNextLevel,
    string Summary);

public sealed record HideoutRequirementViewModel(
    string ItemId,
    string ItemName,
    string Required,
    string Owned,
    string Remaining,
    bool IsSatisfied);

/// <summary>
/// Shows what each hideout station still needs, and how much of it the player has.
/// </summary>
/// <remarks>
/// Every data sync has always written hideout stations, levels and requirements into SQLite
/// and nothing ever read them back. Owned counts and built levels come from the local profile,
/// which the player maintains by hand; the companion never reads the game's inventory.
/// </remarks>
public sealed class HideoutPageViewModel : PageViewModel
{
    private readonly IRequirementCatalog _requirements;
    private readonly IPlayerProfileService _profileService;
    private readonly IItemRepository _itemRepository;
    private IReadOnlyList<HideoutStationViewModel> _stations = [];
    private IReadOnlyList<HideoutRequirementViewModel> _items = [];
    private HideoutStationViewModel? _selected;
    private string _status = "Loading the hideout catalog…";
    private string _detail = "Select a station to see what its next level needs.";

    public HideoutPageViewModel(
        IRequirementCatalog requirements,
        IPlayerProfileService profileService,
        IItemRepository itemRepository)
        : base("Hideout", "What each station still needs, against what you have", "Runtime state not loaded")
    {
        _requirements = requirements;
        _profileService = profileService;
        _itemRepository = itemRepository;
        RefreshCommand = new AsyncDelegateCommand(LoadAsync);
    }

    public AsyncDelegateCommand RefreshCommand { get; }

    public IReadOnlyList<HideoutStationViewModel> Stations
    {
        get => _stations;
        private set => SetProperty(ref _stations, value);
    }

    public IReadOnlyList<HideoutRequirementViewModel> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

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

    public HideoutStationViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value) && value is not null)
            {
                _ = ShowStationAsync(value, CancellationToken.None);
            }
        }
    }

    public void Apply(ApplicationRuntimeSnapshot snapshot) =>
        Evidence = $"{snapshot.Data.Availability} · {snapshot.Data.ItemCount:N0} cached items";

    public Task LoadAsync() => LoadAsync(CancellationToken.None);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Status = "Reading the hideout catalog…";
            var stations = await _requirements.GetStationsAsync(cancellationToken).ConfigureAwait(true);
            if (stations.Count == 0)
            {
                Stations = [];
                Items = [];
                Status = "No hideout data is cached yet. It arrives with the first successful data refresh.";
                return;
            }

            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            var requirements = await _requirements.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(true);
            var byStation = requirements
                .GroupBy(requirement => requirement.StationId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

            Stations = stations
                .Select(station => Describe(station, profile.HideoutStationLevels, byStation))
                .OrderByDescending(station => station.HasNextLevel)
                .ThenBy(station => station.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            Status = $"{Stations.Count} station(s). Built levels and owned items come from your local profile.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Stations = [];
            Items = [];
            Status = $"The hideout catalog could not be read: {exception.Message}";
        }
    }

    private async Task ShowStationAsync(HideoutStationViewModel station, CancellationToken cancellationToken)
    {
        try
        {
            if (!station.HasNextLevel)
            {
                Items = [];
                Detail = $"{station.Name} is already at its highest recorded level.";
                return;
            }

            Detail = $"Reading requirements for {station.Name} level {station.NextLevel}…";
            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            var requirements = await _requirements.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(true);
            var wanted = requirements
                .Where(requirement =>
                    string.Equals(requirement.StationId, station.StationId, StringComparison.OrdinalIgnoreCase) &&
                    requirement.TargetLevel == station.NextLevel)
                .ToArray();

            var rows = new List<HideoutRequirementViewModel>(wanted.Length);
            foreach (var requirement in wanted)
            {
                var item = await _itemRepository.GetAsync(requirement.ItemId, cancellationToken).ConfigureAwait(true);
                var owned = profile.OwnedItemCounts.GetValueOrDefault(requirement.ItemId);
                var remaining = Math.Max(0, requirement.Required - owned);
                rows.Add(new(
                    requirement.ItemId,
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
                ? $"{station.Name} level {station.NextLevel} needs no items."
                : outstanding == 0
                    ? $"{station.Name} level {station.NextLevel}: you have everything."
                    : $"{station.Name} level {station.NextLevel}: {outstanding} of {Items.Count} item(s) still needed.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Items = [];
            Detail = $"Those requirements could not be read: {exception.Message}";
        }
    }

    private static HideoutStationViewModel Describe(
        HideoutStationSummary station,
        IReadOnlyDictionary<string, int> builtLevels,
        IReadOnlyDictionary<string, HideoutItemRequirement[]> requirementsByStation)
    {
        var built = builtLevels.GetValueOrDefault(station.StationId);
        var maximum = station.Levels.Count == 0 ? 0 : station.Levels.Max();
        var next = station.Levels.Where(level => level > built).DefaultIfEmpty(0).Min();
        var hasNext = next > 0;
        var outstanding = hasNext && requirementsByStation.TryGetValue(station.StationId, out var all)
            ? all.Count(requirement => requirement.TargetLevel == next)
            : 0;

        return new(
            station.StationId,
            station.Name,
            maximum == 0 ? $"level {built}" : $"level {built} of {maximum}",
            next,
            hasNext,
            hasNext
                ? outstanding == 0 ? $"Level {next} needs no items." : $"Level {next} needs {outstanding} item(s)."
                : "Fully built.");
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
