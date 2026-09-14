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
    string Summary)
{
    /// <summary>What the profile says is built, which is what the stepper edits.</summary>
    /// <remarks>
    /// An init property rather than another positional parameter. Held as
    /// <see cref="decimal"/> because that is what a spinner binds.
    /// </remarks>
    public decimal BuiltLevel { get; init; }

    /// <summary>The highest level this station has, so the stepper cannot go past it.</summary>
    public decimal MaximumLevel { get; init; }

    /// <summary>Whether there is anything to step through.</summary>
    public bool CanSetLevel => MaximumLevel > 0;
}

public sealed record HideoutRequirementViewModel(
    string ItemId,
    string ItemName,
    string Required,
    string Owned,
    string Remaining,
    bool IsSatisfied)
{
    /// <summary>The cheapest way to get one, where there is a cheaper one than the flea.</summary>
    /// <remarks>
    /// An init property rather than a seventh positional parameter. Empty where nothing beats
    /// buying it, where nothing has priced the inputs, or where the only cheaper barter needs
    /// loyalty this player does not have — all three of which are "no answer" rather than
    /// "buy it", and a row that said "buy it" in the second case would be making that up.
    /// </remarks>
    public string CheapestRoute { get; init; } = string.Empty;

    public bool HasCheapestRoute => CheapestRoute.Length > 0;
}

/// <summary>
/// Shows what each hideout station still needs, and how much of it the player has.
/// </summary>
/// <remarks>
/// Every data sync has always written hideout stations, levels and requirements into SQLite
/// and nothing ever read them back. Owned counts and built levels come from the local profile,
/// which the player maintains by hand; the companion never reads the game's inventory.
///
/// "Maintains by hand" was until now a claim with nothing behind it. HideoutStationLevels is
/// written by nothing anywhere in the application, so every station printed "level 0 of N" under
/// a line saying the levels came from the player's profile — true, and useless, because the
/// profile had no way to hold anything else. The stepper on each row is the hands.
/// </remarks>
public sealed class HideoutPageViewModel : PageViewModel
{
    private int? _knownItemCount;

    private readonly IRequirementCatalog _requirements;
    private readonly IPlayerProfileService _profileService;
    private readonly IItemRepository _itemRepository;
    private readonly IBarterCatalog? _barters;
    private readonly ITraderCatalog? _traders;
    private IReadOnlyList<HideoutStationViewModel> _stations = [];
    private IReadOnlyDictionary<string, int> _stationLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<HideoutRequirementViewModel> _items = [];
    private HideoutStationViewModel? _selected;
    private string _status = "Loading the hideout catalog…";
    private string _detail = "Pick a station";

    public HideoutPageViewModel(
        IRequirementCatalog requirements,
        IPlayerProfileService profileService,
        IItemRepository itemRepository,
        // Optional so every composition that builds this by hand keeps working. Without it the
        // rows lose their route line, which is what they had before it existed.
        IBarterCatalog? barters = null,
        ITraderCatalog? traders = null)
        : base("Hideout", "What each station still needs", "Runtime state not loaded")
    {
        _requirements = requirements;
        _profileService = profileService;
        _itemRepository = itemRepository;
        _barters = barters;
        _traders = traders;
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

    /// <summary>Takes what the sync produced, rebuilding when the catalog actually changed.</summary>
    /// <remarks>
    /// The count is the change signal, not merely a zero check. On a fresh install this page
    /// loads from an empty cache, the sync then fills it, and nothing here noticed: the old
    /// code acted only on ItemCount == 0, so 0 -> N did nothing and the page went on saying it
    /// had nothing cached until somebody pressed Reload. Copied from LoadoutPageViewModel,
    /// which is the one page that had it right.
    ///
    /// Fire and forget, because Apply is called from the shell's state pass and must not block
    /// it on a database read.
    /// </remarks>
    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        Evidence = $"{snapshot.Data.Availability} · {snapshot.Data.ItemCount:N0} cached items";
        if (_knownItemCount == snapshot.Data.ItemCount)
        {
            return;
        }

        _knownItemCount = snapshot.Data.ItemCount;
        if (snapshot.Data.ItemCount == 0)
        {
            Stations = [];
            Items = [];
            Status = snapshot.Data.Detail;
            return;
        }

        _ = LoadAsync();
    }

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
                Status = "No hideout data cached yet";
                return;
            }

            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            var requirements = await _requirements.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(true);
            var byStation = requirements
                .GroupBy(requirement => requirement.StationId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

            _stationLevels = profile.HideoutStationLevels;
            Stations = stations
                .Select(station => Describe(station, profile.HideoutStationLevels, byStation))
                .OrderByDescending(station => station.HasNextLevel)
                .ThenBy(station => station.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var built = Stations.Count(station => station.BuiltLevel > 0);
        Status = built == 0
            ? $"{Stations.Count} stations · set the level you have each one built to"
            : $"{Stations.Count} stations · {built} built · levels and stock from your profile";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Stations = [];
            Items = [];
            Status = $"Unreadable · {exception.Message}";
        }
    }

    private async Task ShowStationAsync(HideoutStationViewModel station, CancellationToken cancellationToken)
    {
        try
        {
            if (!station.HasNextLevel)
            {
                Items = [];
                Detail = $"{station.Name} · already at its highest level";
                return;
            }

            Detail = $"Reading {station.Name} level {station.NextLevel}…";
            var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(true);
            var requirements = await _requirements.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(true);
            var wanted = requirements
                .Where(requirement =>
                    string.Equals(requirement.StationId, station.StationId, StringComparison.OrdinalIgnoreCase) &&
                    requirement.TargetLevel == station.NextLevel)
                .ToArray();

            var routes = await RoutesAsync(wanted, profile.TraderLevels, cancellationToken).ConfigureAwait(true);
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
                    remaining == 0)
                {
                    CheapestRoute = routes.GetValueOrDefault(requirement.ItemId, string.Empty),
                });
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

    /// <summary>
    /// Records the level a station has been built to, and re-reads the page.
    /// </summary>
    /// <remarks>
    /// The whole page is measured against these: which level is next, which items it needs, and
    /// how many of them are outstanding. So a change reloads rather than editing the row in
    /// place — a station moved from level 0 to 3 has different requirements, not the same ones
    /// with a different number beside them.
    ///
    /// Clamped to what the catalog says the station has. A spinner is bounded in the view, but
    /// the profile is a file somebody can open, and a station at level 9 of 3 would make the
    /// "next level" arithmetic produce a level that does not exist.
    /// </remarks>
    public async Task SetStationLevelAsync(string stationId, decimal level)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            return;
        }

        var station = Stations.FirstOrDefault(row =>
            string.Equals(row.StationId, stationId, StringComparison.OrdinalIgnoreCase));
        if (station is null)
        {
            return;
        }

        var wanted = (int)Math.Clamp(level, 0, station.MaximumLevel);
        if (wanted == _stationLevels.GetValueOrDefault(stationId))
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
                .SaveAsync(
                    profile with { HideoutStationLevels = levels, UpdatedUtc = DateTimeOffset.UtcNow },
                    CancellationToken.None)
                .ConfigureAwait(true);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = $"Station level not saved · {exception.Message}";
        }
    }

    /// <summary>
    /// The cheapest barter for each wanted item, where one beats buying it.
    /// </summary>
    /// <remarks>
    /// 789 barters are rewritten on every sync and no query had ever read one. This is the half
    /// of that the trader-level stepper unblocked: every barter's loyalty requirement was
    /// unanswerable while TraderLevels was written by nothing, so a route line would have had
    /// to either ignore the requirement — offering routes the player cannot take — or assume
    /// level 1 and hide most of them.
    ///
    /// Silent on every failure. No barter catalog, no prices, an unpriced input, a route that
    /// is not actually cheaper, or loyalty the player does not have: all of those are "no
    /// answer", and the row simply does not carry a route line. A line saying "buy it" when the
    /// truth is "nothing here could work it out" would be making something up.
    /// </remarks>
    private async Task<Dictionary<string, string>> RoutesAsync(
        IReadOnlyList<HideoutItemRequirement> wanted,
        IReadOnlyDictionary<string, int> traderLevels,
        CancellationToken cancellationToken)
    {
        var routes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_barters is null)
        {
            return routes;
        }

        try
        {
            var barters = await _barters.GetAsync(cancellationToken).ConfigureAwait(true);
            if (barters.Count == 0)
            {
                return routes;
            }

            var traderNames = _traders is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : await _traders.GetNamesAsync(cancellationToken).ConfigureAwait(true);

            // Prices are read once per item and remembered, because a barter's inputs are
            // frequently the inputs of other barters and the wanted list repeats items across
            // levels.
            var prices = new Dictionary<string, long?>(StringComparer.Ordinal);
            async Task<long?> PriceAsync(string itemId)
            {
                if (!prices.TryGetValue(itemId, out var known))
                {
                    var snapshot = await _itemRepository.GetPriceAsync(itemId, cancellationToken).ConfigureAwait(true);
                    prices[itemId] = known = snapshot?.FleaPriceRoubles;
                }

                return known;
            }

            // Warmed before routing, because the routing itself is synchronous: it has to
            // compare every barter against every other and cannot await inside that.
            foreach (var itemId in barters
                .SelectMany(barter => barter.Wants.Select(want => want.ItemId))
                .Concat(wanted.Select(requirement => requirement.ItemId))
                .Distinct(StringComparer.Ordinal))
            {
                await PriceAsync(itemId).ConfigureAwait(true);
            }

            foreach (var requirement in wanted.DistinctBy(item => item.ItemId, StringComparer.Ordinal))
            {
                var route = BarterRouting.Cheapest(
                    requirement.ItemId,
                    barters,
                    traderLevels,
                    itemId => prices.GetValueOrDefault(itemId));
                if (route is null)
                {
                    continue;
                }

                // Only where it actually beats buying one. A barter that costs more than the
                // flea price is a route, not a cheaper route, and putting it on the row would
                // be advice to spend more.
                var flea = prices.GetValueOrDefault(requirement.ItemId);
                if (flea is not { } buying || route.PerItem >= buying)
                {
                    continue;
                }

                var trader = route.TraderId is { Length: > 0 } id
                    ? traderNames.GetValueOrDefault(id, id)
                    : "a trader";
                routes[requirement.ItemId] = string.Create(
                    CultureInfo.CurrentCulture,
                    $"Barter from {trader} costs about {route.PerItem:N0} ₽ each · flea {buying:N0} ₽");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The rest of the page is unaffected; the rows simply lose their route line.
            routes.Clear();
        }

        return routes;
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
                : "Fully built.")
        {
            BuiltLevel = built,
            MaximumLevel = maximum,
        };
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
