using System.Globalization;
using TarkovCompanion.App.Localization;
using System.Windows.Input;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>One upgrade in the order it has to be built.</summary>
public sealed record HideoutUpgradeStepRowViewModel(string Order, string Title, string State, string AlsoNeeds, bool IsReady)
{
    public bool HasAlsoNeeds => AlsoNeeds.Length > 0;

    public string GateLabel => HasAlsoNeeds ? PlanText.HideoutGate(AlsoNeeds) : string.Empty;

    public string MissingItems { get; init; } = string.Empty;

    public bool HasMissingItems => MissingItems.Length > 0;

    public TimeSpan? ConstructionTime { get; init; }

    public string DurationLabel => ConstructionTime is { } duration
        ? PlanText.HideoutBuildTime(FormatDuration(duration))
        : PlanText.HideoutBuildTimeUnavailable;

    public string LearnReason => HasAlsoNeeds ? PlanText.HideoutLearnGate(AlsoNeeds) : PlanText.HideoutLearnItems(State);

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return PlanText.HideoutInstant;
        }

        if (duration.Days > 0)
        {
            return duration.Hours > 0 ? PlanText.HideoutDaysHours(duration.Days, duration.Hours) : PlanText.HideoutDays(duration.Days);
        }

        if (duration.Hours > 0)
        {
            return duration.Minutes > 0 ? PlanText.HideoutHoursMinutes(duration.Hours, duration.Minutes) : PlanText.HideoutHours(duration.Hours);
        }

        return duration.Minutes > 0 ? PlanText.HideoutMinutes(duration.Minutes) : PlanText.HideoutSeconds(Math.Max(1, duration.Seconds));
    }
}

/// <summary>One line of the merged shopping list.</summary>
public sealed record HideoutShoppingRowViewModel(string ItemName, string ProgressLabel, string PriceLabel, string CostLabel)
{
    public bool HasCost => CostLabel.Length > 0;
}

/// <summary>Which upgrades the shopping list covers.</summary>
public sealed class HideoutScopeChipViewModel : BindableViewModel
{
    private bool _isSelected;

    internal HideoutScopeChipViewModel(int count, string label, Action<HideoutScopeChipViewModel> select)
    {
        Count = count;
        Label = label;
        SelectCommand = new DelegateCommand(() => select(this));
    }

    /// <summary>How many next upgrades, or 0 for the path to the chosen station level.</summary>
    public int Count { get; }

    public string Label { get; }

    public string AutomationId => Count switch
    {
        0 => "v2-hideout-scope-path",
        < 0 => "v2-hideout-scope-every",
        _ => $"v2-hideout-scope-{Count}",
    };

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// The several-level half of the Hideout page (#307): the upgrades that come next, the order a
/// chosen station level has to be built in, and one shopping list with prices across either. The
/// ordering and the merging are <see cref="HideoutUpgradePlanner"/>'s; this only names items,
/// prices them and words the rows, off the interface thread.
/// </summary>
public sealed class HideoutUpgradePlanViewModel : BindableViewModel
{
    private readonly IItemRepository _items;
    private readonly IHideoutPrerequisiteCatalog? _prerequisiteCatalog;
    private IReadOnlyList<HideoutStationSummary> _stations = [];
    private IReadOnlyDictionary<string, int> _built = new Dictionary<string, int>(StringComparer.Ordinal);
    private IReadOnlyList<HideoutItemRequirement> _requirements = [];
    private IReadOnlyDictionary<string, int> _owned = new Dictionary<string, int>(StringComparer.Ordinal);
    private HideoutPrerequisites _prerequisites = HideoutPrerequisites.None;
    private HideoutStationSummary? _target;
    private int _targetLevel;
    private IReadOnlyList<HideoutUpgradeStepRowViewModel> _next = [];
    private IReadOnlyList<HideoutUpgradeStepRowViewModel> _path = [];
    private IReadOnlyList<HideoutShoppingRowViewModel> _shopping = [];
    private string _shoppingHeading = string.Empty;
    private string _shoppingTotal = string.Empty;
    private int _version;

    /// <summary>The scope that covers each station's next level, startable or not.</summary>
    private const int EveryStation = -1;

    private const string RoublesItemId = "5449016a4bdc2d6f028b456f";

    public HideoutUpgradePlanViewModel(IItemRepository items, IHideoutPrerequisiteCatalog? prerequisites)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _prerequisiteCatalog = prerequisites;
        Scopes =
        [
            new(3, PlanText.HideoutNextCount(3), SelectScope),
            new(5, PlanText.HideoutNextCount(5), SelectScope),
            new(10, PlanText.HideoutNextCount(10), SelectScope),
            new(0, PlanText.HideoutPath, SelectScope),
            new(EveryStation, PlanText.HideoutEveryStation, SelectScope),
        ];
        Scopes[1].IsSelected = true;
        RaiseTargetCommand = new DelegateCommand(() => MoveTarget(1));
        LowerTargetCommand = new DelegateCommand(() => MoveTarget(-1));
    }

    public IReadOnlyList<HideoutScopeChipViewModel> Scopes { get; }

    public ICommand RaiseTargetCommand { get; }

    public ICommand LowerTargetCommand { get; }

    public IReadOnlyList<HideoutUpgradeStepRowViewModel> NextUpgrades
    {
        get => _next;
        private set
        {
            SetProperty(ref _next, value);
            OnPropertyChanged(nameof(HasNextUpgrades));
            OnPropertyChanged(nameof(NextSummary));
        }
    }

    public bool HasNextUpgrades => _next.Count > 0;

    public string NextSummary => DurationSummary(_next);

    public IReadOnlyList<HideoutUpgradeStepRowViewModel> PathSteps
    {
        get => _path;
        private set
        {
            SetProperty(ref _path, value);
            OnPropertyChanged(nameof(HasPath));
            OnPropertyChanged(nameof(PathHeading));
            OnPropertyChanged(nameof(PathSummary));
            OnPropertyChanged(nameof(CanRaiseTarget));
            OnPropertyChanged(nameof(CanLowerTarget));
        }
    }

    public bool HasPath => _target is not null && _targetLevel > 0;

    public string PathHeading => _target is null || _targetLevel == 0
        ? PlanText.HideoutPath
        : PlanText.HideoutPathToLevel(_targetLevel);

    public string PathSummary => _path.Count == 0
        ? PlanText.HideoutAlreadyBuilt
        : PlanText.HideoutPathSummary(DurationSummary(_path), PlannerVersions.Label(PlannerVersions.HideoutPath));

    public bool CanRaiseTarget => _target is not null && _target.Levels.Any(level => level > _targetLevel);

    public bool CanLowerTarget => _target is not null &&
        _target.Levels.Any(level => level < _targetLevel && level > _built.GetValueOrDefault(_target.StationId));

    public IReadOnlyList<HideoutShoppingRowViewModel> Shopping
    {
        get => _shopping;
        private set
        {
            SetProperty(ref _shopping, value);
            OnPropertyChanged(nameof(HasShopping));
        }
    }

    public bool HasShopping => _shopping.Count > 0;

    public string ShoppingHeading
    {
        get => _shoppingHeading;
        private set => SetProperty(ref _shoppingHeading, value);
    }

    public string ShoppingTotal
    {
        get => _shoppingTotal;
        private set => SetProperty(ref _shoppingTotal, value);
    }

    /// <summary>Takes what the page just read. The selected station becomes the path's target.</summary>
    public async Task UpdateAsync(
        IReadOnlyList<HideoutStationSummary> stations,
        IReadOnlyDictionary<string, int> built,
        IReadOnlyList<HideoutItemRequirement> requirements,
        IReadOnlyDictionary<string, int> owned,
        string? selectedStationId,
        CancellationToken cancellationToken)
    {
        _stations = stations;
        _built = built;
        _requirements = requirements;
        _owned = owned;
        if (_prerequisiteCatalog is not null)
        {
            _prerequisites = await OffInterfaceThread
                .Run(() => _prerequisiteCatalog.GetAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(true);
        }

        SetTarget(selectedStationId, keepLevel: false);
        await RebuildAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The player picked another station: the path now leads to its highest level.</summary>
    public Task TargetAsync(string? stationId, CancellationToken cancellationToken)
    {
        SetTarget(stationId, keepLevel: false);
        return RebuildAsync(cancellationToken);
    }

    private void SetTarget(string? stationId, bool keepLevel)
    {
        var target = _stations.FirstOrDefault(station =>
            string.Equals(station.StationId, stationId, StringComparison.OrdinalIgnoreCase));
        if (!keepLevel || !ReferenceEquals(target, _target))
        {
            _targetLevel = target is { Levels.Count: > 0 } ? target.Levels.Max() : 0;
        }

        _target = target;
    }

    private void MoveTarget(int direction)
    {
        if (_target is null)
        {
            return;
        }

        var built = _built.GetValueOrDefault(_target.StationId);
        var next = direction > 0
            ? _target.Levels.Where(level => level > _targetLevel).DefaultIfEmpty(_targetLevel).Min()
            : _target.Levels.Where(level => level < _targetLevel && level > built).DefaultIfEmpty(_targetLevel).Max();
        if (next != _targetLevel)
        {
            _targetLevel = next;
            _ = RebuildAsync(CancellationToken.None);
        }
    }

    private void SelectScope(HideoutScopeChipViewModel scope)
    {
        foreach (var chip in Scopes)
        {
            chip.IsSelected = ReferenceEquals(chip, scope);
        }

        _ = RebuildAsync(CancellationToken.None);
    }

    private async Task RebuildAsync(CancellationToken cancellationToken)
    {
        var version = ++_version;
        var scope = Scopes.First(chip => chip.IsSelected);
        var (stations, built, requirements, owned, prerequisites) = (_stations, _built, _requirements, _owned, _prerequisites);
        var (target, targetLevel) = (_target, _targetLevel);
        try
        {
            var result = await OffInterfaceThread.Run(
                async () =>
                {
                    var upcoming = HideoutUpgradePlanner.NextUpgrades(
                        stations, built, requirements, prerequisites, owned, Math.Max(scope.Count, 10));
                    var path = target is null
                        ? []
                        : HideoutUpgradePlanner.CriticalPath(
                            stations, built, requirements, prerequisites, owned, target.StationId, targetLevel);
                    IReadOnlyList<HideoutUpgradeStep> covered = scope.Count switch
                    {
                        0 => path,
                        EveryStation => HideoutUpgradePlanner.EveryNextLevel(stations, built, requirements, prerequisites, owned),
                        _ => [.. upcoming.Take(scope.Count)],
                    };
                    var lines = HideoutUpgradePlanner.ShoppingList(covered, owned);
                    var itemNames = new Dictionary<string, string>(StringComparer.Ordinal);
                    var nextRows = await DescribeAsync(
                        upcoming.Take(scope.Count <= 0 ? 5 : scope.Count), itemNames, cancellationToken).ConfigureAwait(false);
                    var pathRows = await DescribeAsync(path, itemNames, cancellationToken).ConfigureAwait(false);
                    var rows = new List<(HideoutShoppingRowViewModel Row, long Cost, int Remaining)>(lines.Count);
                    long total = 0;
                    var unpriced = 0;
                    foreach (var line in lines)
                    {
                        var itemName = await ItemNameAsync(line.ItemId, itemNames, cancellationToken).ConfigureAwait(false);
                        var price = await _items.GetPriceAsync(line.ItemId, cancellationToken).ConfigureAwait(false);
                        // Roubles are their own price; the other currencies have none on the flea.
                        var isRoubles = string.Equals(line.ItemId, RoublesItemId, StringComparison.Ordinal);
                        var each = isRoubles ? 1 : price?.Average24HourRoubles ?? price?.FleaPriceRoubles;
                        var cost = each is { } known ? known * line.Remaining : 0;
                        total += cost;
                        unpriced += each is null ? 1 : 0;
                        rows.Add((new(
                            itemName,
                            line.Have is { } have ? $"{Count(have)} / {Count(line.Need)}" : $"? / {Count(line.Need)}",
                            isRoubles ? PlanText.HideoutCash : each is { } priced ? PlanText.HideoutEach(Roubles(priced)) : PlanText.HideoutNoFleaPrice,
                            each is null ? string.Empty : Roubles(cost)), cost, line.Remaining));
                    }

                    var anyUnknown = lines.Any(line => line.Have is null);
                    var toBuy = anyUnknown ? PlanText.HideoutUpToToBuy(Roubles(total)) : PlanText.HideoutAboutToBuy(Roubles(total));
                    var totalLabel = lines.Count == 0
                        ? PlanText.HideoutNothingLeftToBuy
                        : unpriced > 0 ? PlanText.HideoutTotalUnpriced(toBuy, unpriced) : toBuy;
                    return (
                        Next: nextRows,
                        Path: pathRows,
                        Shopping: (IReadOnlyList<HideoutShoppingRowViewModel>)[.. rows
                            .OrderByDescending(row => row.Cost)
                            .ThenBy(row => row.Row.ItemName, StringComparer.CurrentCultureIgnoreCase)
                            .Select(row => row.Row)],
                        Total: totalLabel);
                },
                cancellationToken).ConfigureAwait(true);
            if (version != _version)
            {
                return;
            }

            NextUpgrades = result.Next;
            PathSteps = result.Path;
            Shopping = result.Shopping;
            ShoppingTotal = result.Total;
            ShoppingHeading = scope.Count switch
            {
                0 => target is null ? PlanText.HideoutShoppingList : PlanText.HideoutShoppingListFor(target.Name, targetLevel),
                EveryStation => PlanText.HideoutShoppingListEveryStation,
                _ => PlanText.HideoutShoppingListNext(result.Next.Count),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WorkspaceFault.Record("hideout", "plan upgrades", exception);
        }
    }

    private async Task<IReadOnlyList<HideoutUpgradeStepRowViewModel>> DescribeAsync(
        IEnumerable<HideoutUpgradeStep> steps,
        Dictionary<string, string> itemNames,
        CancellationToken cancellationToken)
    {
        var rows = new List<HideoutUpgradeStepRowViewModel>();
        var available = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var missing = new List<string>();
            var missingCount = 0;
            var unknownCount = 0;
            foreach (var need in step.Needs)
            {
                var name = await ItemNameAsync(need.ItemId, itemNames, cancellationToken).ConfigureAwait(false);
                if (!available.TryGetValue(need.ItemId, out var held))
                {
                    held = need.Owned;
                }

                if (held is { } owned)
                {
                    var remaining = Math.Max(0, need.Required - owned);
                    available[need.ItemId] = Math.Max(0, owned - need.Required);
                    if (remaining > 0)
                    {
                        missingCount++;
                        missing.Add(PlanText.HideoutMissingItem(name, Count(remaining)));
                    }
                }
                else
                {
                    unknownCount++;
                    available[need.ItemId] = null;
                    missing.Add(PlanText.HideoutMissingItemToCheck(name, Count(need.Required)));
                }
            }

            rows.Add(new(
                (rows.Count + 1).ToString(CultureInfo.CurrentCulture),
                PlanText.HideoutStepTitle(step.Name, step.Level),
                (missingCount, unknownCount) switch
                {
                    (0, 0) => step.Needs.Count == 0 ? PlanText.HideoutNoItems : PlanText.HideoutHaveItAll,
                    (var shortCount, 0) => PlanText.HideoutShort(shortCount),
                    (0, var unknown) => PlanText.HideoutToCheck(unknown),
                    var (shortCount, unknown) => PlanText.HideoutShortAndToCheck(shortCount, unknown),
                },
                string.Join(" · ", step.AlsoNeeds),
                missingCount == 0 && unknownCount == 0)
            {
                ConstructionTime = step.ConstructionTime,
                MissingItems = missing.Count == 0 ? string.Empty : PlanText.HideoutMissingList(string.Join(", ", missing)),
            });
        }

        return rows;
    }

    private async Task<string> ItemNameAsync(
        string itemId,
        Dictionary<string, string> itemNames,
        CancellationToken cancellationToken)
    {
        if (itemNames.TryGetValue(itemId, out var name))
        {
            return name;
        }

        var item = await _items.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
        name = item?.Name ?? itemId;
        itemNames[itemId] = name;
        return name;
    }

    private static string DurationSummary(IReadOnlyList<HideoutUpgradeStepRowViewModel> rows)
    {
        var count = PlanText.HideoutUpgradeCount(rows.Count);
        var known = TimeSpan.FromTicks(rows.Sum(row => row.ConstructionTime?.Ticks ?? 0));
        var unknown = rows.Count(row => row.ConstructionTime is null);
        return unknown switch
        {
            0 => PlanText.HideoutDurationTotal(count, HideoutUpgradeStepRowViewModel.FormatDuration(known)),
            _ when known == TimeSpan.Zero => PlanText.HideoutBuildTimesUnavailable(unknown, count),
            _ => PlanText.HideoutDurationKnown(count, HideoutUpgradeStepRowViewModel.FormatDuration(known), unknown),
        };
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string Roubles(long value) => UnitText.RoublesShort(value, shortFrom: 10_000);
}
