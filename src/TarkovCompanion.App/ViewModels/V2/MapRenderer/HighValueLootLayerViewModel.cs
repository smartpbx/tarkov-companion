using System.Windows.Input;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

/// <summary>The desktop projection kept beside the canonical high-value scene objects.</summary>
/// <remarks>
/// Loot-specific facts do not fit in the shared scene object's compact detail string. Keeping
/// the typed result beside the scene prevents the desktop from parsing prose or manufacturing
/// values, floors, or positions. Filter changes are requests to the owner, which rebuilds the
/// application result before publishing a new scene and panel together.
/// </remarks>
public sealed class HighValueLootLayerViewModel : BindableViewModel
{
    public const int PageSize = 50;
    public const int MaximumRenderedFilterOptions = 12;

    private const int MaximumFilterOptionsRead = 256;
    private const int MaximumFilterOptionLength = 256;

    private readonly MapSceneRendererPresentation _presentation;
    private readonly Action<HighValueLootLayerFilterState> _requestFilter;
    private readonly Action<HighValueLootEntry> _select;
    private readonly Action _requestRefresh;
    private HighValueLootLayerResult _result;
    private HighValueLootLayerFilterState _filterState;
    private IReadOnlyList<string> _availableCategories;
    private IReadOnlyList<string> _availableFloors;
    private bool _categoryOptionsTruncated;
    private bool _floorOptionsTruncated;
    private bool _isLayerVisible;
    private int _pageIndex;

    public HighValueLootLayerViewModel(
        HighValueLootLayerResult result,
        HighValueLootLayerFilterState filterState,
        IReadOnlyList<string>? availableCategories,
        IReadOnlyList<string>? availableFloors,
        bool isLayerVisible,
        MapSceneRendererPresentation presentation,
        Action<HighValueLootLayerFilterState> requestFilter,
        Action<HighValueLootEntry> select,
        Action? requestRefresh = null)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
        _filterState = filterState ?? throw new ArgumentNullException(nameof(filterState));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _requestFilter = requestFilter ?? throw new ArgumentNullException(nameof(requestFilter));
        _select = select ?? throw new ArgumentNullException(nameof(select));
        _requestRefresh = requestRefresh ?? (() => { });
        RefreshCommand = new DelegateCommand(() => _requestRefresh());
        (_availableCategories, _categoryOptionsTruncated) = NormalizeOptions(
            availableCategories ?? CategoriesFrom(result),
            ActiveSingleCategory(filterState));
        (_availableFloors, _floorOptionsTruncated) = NormalizeOptions(
            availableFloors ?? [],
            ActiveFloor(filterState));
        _isLayerVisible = isLayerVisible;
        PreviousPageCommand = new DelegateCommand(() => ChangePage(-1));
        NextPageCommand = new DelegateCommand(() => ChangePage(1));
        Rebuild();
    }

    public string Heading => Text("Map.Loot.Heading");
    public string FilterHeading => Text("Map.Loot.Filters");
    public string Legend => _result.CompactLegend;
    /// <summary>Short, visible context beside the loot switch in the Raid Layers menu.</summary>
    public string LayerMenuStatus
    {
        get
        {
            if (_result.Status.Completeness is ResultCompleteness.Unknown or ResultCompleteness.Unavailable)
            {
                return $"No loot data for {MapName(_result.MapId)}";
            }

            if (_result.Status.Completeness != ResultCompleteness.Partial)
            {
                return "Complete";
            }

            if (_result.Diagnostics.Any(diagnostic => diagnostic.Code == "snapshot.transform-stale"))
            {
                return "Partial · map changed";
            }

            var incomplete = _result.Diagnostics.Count(diagnostic => diagnostic.AffectsCompleteness);
            return incomplete > 0
                ? $"Partial · {Number(incomplete)} incomplete"
                : "Partial · source coverage";
        }
    }
    public string StateMessage { get; private set; } = string.Empty;
    public string FreshnessMessage { get; private set; } = string.Empty;
    public bool HasFreshnessMessage => !string.IsNullOrWhiteSpace(FreshnessMessage);
    public string CoverageLabel { get; private set; } = string.Empty;
    public bool HasCoverage => !string.IsNullOrWhiteSpace(CoverageLabel);
    public bool HasEntries => Rows.Count > 0;
    public bool ShowsEmpty => _isLayerVisible && !HasEntries;
    public string EmptyMessage => Text("Map.Loot.Empty");
    /// <summary>
    /// True when there is no last-known-good loot-spawn snapshot to draw at all (as opposed to
    /// one that is present but filtered to nothing). [Issue 563] "High-value loot only" checks
    /// this before hiding anything else, and the Raid page uses it to surface a Refresh action.
    /// </summary>
    public bool IsUnavailable => _result.Status.Completeness is ResultCompleteness.Unknown or ResultCompleteness.Unavailable;
    /// <summary>
    /// "No loot spawn data yet · &lt;reason&gt;", independent of whether the layer switch itself
    /// is on. [Issue 563] The preset turns this layer on as one of its steps, so a notice fired
    /// from the preset button before that step lands must not say "off" when the real reason is
    /// that there is nothing published to switch on.
    /// </summary>
    public string NoDataMessage
    {
        get
        {
            // [#292] Local only is a state, said the way Data & Privacy says it, not an error.
            var reason = _result.Diagnostics.Count == 0
                ? Text("Map.Loot.NoDataReasonUnknown")
                : _result.Diagnostics[0].Code == LootSpawnRefreshCodes.LocalOnly
                    ? TarkovCompanion.App.Localization.SetupText.NetworkState(TarkovCompanion.Core.Network.NetworkVerdict.LocalOnly)
                    : _result.Diagnostics[0].Explanation;
            return Format("Map.Loot.NoDataYet", reason);
        }
    }
    public ICommand RefreshCommand { get; }
    public string ValueBasisLabel => Text("Map.Loot.ValueBasis");
    public string ValueThresholdLabel => Text("Map.Loot.ValueThreshold");
    public string MinimumTierLabel => Text("Map.Loot.MinimumTier");
    public string ProfileRelevanceLabel => Text("Map.Loot.ProfileRelevance");
    public string CategoryLabel => Text("Map.Loot.Category");
    public string FloorLabel => Text("Map.Loot.Floor");
    public bool HasCategoryOptionsNotice => _categoryOptionsTruncated;
    public bool HasFloorOptionsNotice => _floorOptionsTruncated;
    public string CategoryOptionsNotice => Text("Map.Loot.MoreCategories");
    public string FloorOptionsNotice => Text("Map.Loot.MoreFloors");
    public bool HasMultipleCategoryFilter => _filterState.Filter.Categories.Count > 1;
    public string MultipleCategoryFilterMessage => HasMultipleCategoryFilter
        ? Format("Map.Loot.MultipleCategories", Number(_filterState.Filter.Categories.Count))
        : string.Empty;
    public string PreviousPageLabel => Text("Map.Action.Previous");
    public string NextPageLabel => Text("Map.Action.Next");
    public IReadOnlyList<HighValueLootFilterChoiceViewModel> ValueBasisChoices { get; private set; } = [];
    public IReadOnlyList<HighValueLootFilterChoiceViewModel> CompactValueBasisChoices { get; private set; } = [];
    public IReadOnlyList<HighValueLootFilterChoiceViewModel> ValueThresholdChoices { get; private set; } = [];
    public IReadOnlyList<HighValueLootFilterChoiceViewModel> MinimumTierChoices { get; private set; } = [];
    public IReadOnlyList<HighValueLootFilterChoiceViewModel> ProfileRelevanceChoices { get; private set; } = [];
    public IReadOnlyList<HighValueLootFilterChoiceViewModel> CategoryChoices { get; private set; } = [];
    public IReadOnlyList<HighValueLootFilterChoiceViewModel> FloorChoices { get; private set; } = [];
    public IReadOnlyList<HighValueLootEntryViewModel> Rows { get; private set; } = [];
    public IReadOnlyList<HighValueLootEntry> AllEntries => _result.Entries;
    public HighValueLootLayerFilterState FilterState => _filterState;
    public bool HasPerSlotValues =>
        _filterState.Filter.ValueBasis == LootSpawnValueBasis.ValuePerSquare ||
        _result.Entries.Any(entry => entry.MaximumValuePerSquare is not null);
    public string CompactFilterLabel => Format(
        "Map.Loot.CompactFilter",
        DescribeThreshold(_filterState.Filter.EffectiveMinimumValueRoubles),
        _filterState.Filter.ValueBasis == LootSpawnValueBasis.ValuePerSquare
            ? Text("Map.Loot.PerSlot")
            : Text("Map.Loot.PerItem"));
    public IReadOnlySet<MapSceneObjectId> VisibleObjectIds { get; private set; } = new HashSet<MapSceneObjectId>();

    /// <summary>The spawns the current filter would draw with the layer on, whether it is on or not.</summary>
    /// <remarks>
    /// [#677] The Layers menu counts the loot layer from this. Counted from
    /// <see cref="VisibleObjectIds"/>, which is empty while the layer is off, the switch read
    /// "nothing to show" and could not be turned on; only the gem preset could show loot.
    /// </remarks>
    public IReadOnlySet<MapSceneObjectId> ShowableObjectIds { get; private set; } = new HashSet<MapSceneObjectId>();
    public int FilteredCount { get; private set; }
    public int PageCount => FilteredCount == 0 ? 0 : (FilteredCount + PageSize - 1) / PageSize;
    public int PageNumber => PageCount == 0 ? 0 : _pageIndex + 1;
    public string PageLabel => Format("Map.Loot.Page", PageNumber, PageCount, FilteredCount);
    public bool CanGoToPreviousPage => _pageIndex > 0;
    public bool CanGoToNextPage => _pageIndex + 1 < PageCount;
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }

    public event Action? ProjectionChanged;

    /// <summary>
    /// Raised when the player asks to make a spawn a waypoint. The layer does not own waypoints;
    /// the host that does (the Raid cockpit) places one and this only says which spawn.
    /// </summary>
    public event Action<HighValueLootEntry>? WaypointRequested;

    internal void RequestWaypoint(HighValueLootEntry entry) => WaypointRequested?.Invoke(entry);

    public void SetLayerVisibility(bool isVisible, bool notifyProjection = true)
    {
        if (_isLayerVisible == isVisible)
        {
            return;
        }

        _isLayerVisible = isVisible;
        _pageIndex = 0;
        Rebuild();
        RaiseAllChanged();
        if (notifyProjection)
        {
            ProjectionChanged?.Invoke();
        }
    }

    public void Present(
        HighValueLootLayerResult result,
        HighValueLootLayerFilterState filterState,
        IReadOnlyList<string>? availableCategories,
        IReadOnlyList<string>? availableFloors)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(filterState);
        // [#453] The Raid map presents again on every change it draws (a squadmate moving, a
        // screenshot), and each one used to send a player reading page 3 back to page 1. Only a
        // different map or a different filter is a different list; BuildRows clamps the page if
        // the same list got shorter.
        var sameList = string.Equals(result.MapId, _result.MapId, StringComparison.OrdinalIgnoreCase)
            && (ReferenceEquals(filterState, _filterState) || filterState == _filterState);
        var sameResult = sameList && ReferenceEquals(result, _result);
        var previousCategories = _availableCategories;
        var previousFloors = _availableFloors;
        var previousTruncation = (_categoryOptionsTruncated, _floorOptionsTruncated);
        _result = result;
        _filterState = filterState;
        var categoriesWereTruncated = _categoryOptionsTruncated;
        var floorsWereTruncated = _floorOptionsTruncated;
        (_availableCategories, _categoryOptionsTruncated) = NormalizeOptions(
            availableCategories ?? _availableCategories,
            ActiveSingleCategory(filterState));
        _categoryOptionsTruncated |= availableCategories is null && categoriesWereTruncated;
        (_availableFloors, _floorOptionsTruncated) = NormalizeOptions(
            availableFloors ?? _availableFloors,
            ActiveFloor(filterState));
        _floorOptionsTruncated |= availableFloors is null && floorsWereTruncated;
        // [#657] The same result under the same filter and options draws the same rows and the
        // same markers. The runtime source hands back the very same result while nothing it was
        // built from has changed, so a squadmate moving no longer rebuilds the list, and the
        // ProjectionChanged it raised no longer rebuilt every loot marker a second time.
        if (sameResult &&
            previousCategories.SequenceEqual(_availableCategories, StringComparer.Ordinal) &&
            previousFloors.SequenceEqual(_availableFloors, StringComparer.Ordinal) &&
            previousTruncation == (_categoryOptionsTruncated, _floorOptionsTruncated))
        {
            return;
        }

        if (!sameList)
        {
            _pageIndex = 0;
        }

        Rebuild();
        RaiseAllChanged();
        ProjectionChanged?.Invoke();
    }

    private void ChangePage(int direction)
    {
        var next = Math.Clamp(_pageIndex + direction, 0, Math.Max(0, PageCount - 1));
        if (next == _pageIndex)
        {
            return;
        }

        _pageIndex = next;
        BuildRows();
        RaisePageChanged();
    }

    private void Request(HighValueLootLayerFilterState state) => _requestFilter(state);

    private void Rebuild()
    {
        StateMessage = DescribeState();
        FreshnessMessage = DescribeFreshness();
        CoverageLabel = _result.Coverage is { } coverage
            ? Format(
                "Map.Loot.Coverage",
                Number(coverage.Positioned),
                Number(coverage.Published),
                Number(coverage.FloorResolved),
                Number(coverage.Unresolved))
            : string.Empty;
        BuildChoices();
        BuildRows();
    }

    private void BuildChoices()
    {
        ValueBasisChoices = Enum.GetValues<LootSpawnValueBasis>()
            .Select(basis => Choice(
                $"basis-{basis}",
                DescribeBasis(basis),
                _filterState.Filter.ValueBasis == basis,
                () => Request(_filterState.WithFilter(CloneFilter(valueBasis: basis)))))
            .ToArray();
        CompactValueBasisChoices = new[]
            {
                (Basis: LootSpawnValueBasis.BestNet, Label: Text("Map.Loot.PerItem")),
                (Basis: LootSpawnValueBasis.ValuePerSquare, Label: Text("Map.Loot.PerSlot")),
            }
            .Where(choice => choice.Basis != LootSpawnValueBasis.ValuePerSquare || HasPerSlotValues)
            .Select(choice => Choice(
                $"compact-basis-{choice.Basis}",
                choice.Label,
                choice.Basis == LootSpawnValueBasis.ValuePerSquare
                    ? _filterState.Filter.ValueBasis == LootSpawnValueBasis.ValuePerSquare
                    : _filterState.Filter.ValueBasis != LootSpawnValueBasis.ValuePerSquare,
                () => Request(_filterState.WithFilter(CloneFilter(valueBasis: choice.Basis)))))
            .ToArray();
        ValueThresholdChoices = new long[] { 0, 50_000, 100_000, 250_000, 500_000 }
            .Select(threshold => Choice(
                threshold == 0 ? "threshold-any" : $"threshold-{threshold}",
                DescribeThreshold(threshold),
                _filterState.Filter.EffectiveMinimumValueRoubles == threshold,
                () => Request(_filterState.WithFilter(CloneFilter(
                    minimumValueRoubles: threshold,
                    replaceMinimumValue: true)))))
            .ToArray();
        MinimumTierChoices = new[]
            {
                LootSpawnValueTier.Qualifying,
                LootSpawnValueTier.Moderate,
                LootSpawnValueTier.High,
                LootSpawnValueTier.Exceptional,
            }
            .Select(tier => Choice(
                $"tier-{tier}",
                DescribeTier(tier),
                _filterState.MinimumTier == tier,
                () => Request(_filterState with { MinimumTier = tier })))
            .ToArray();
        ProfileRelevanceChoices =
        [
            Choice(
                "profile-on",
                Text("Map.Loot.ProfileIncluded"),
                _filterState.Filter.IncludeProfileRelevant,
                () => Request(_filterState.WithFilter(CloneFilter(includeProfileRelevant: true)))),
            Choice(
                "profile-off",
                Text("Map.Loot.ProfileExcluded"),
                !_filterState.Filter.IncludeProfileRelevant,
                () => Request(_filterState.WithFilter(CloneFilter(includeProfileRelevant: false)))),
        ];
        CategoryChoices = new[] { string.Empty }.Concat(_availableCategories)
            .Select(category => Choice(
                string.IsNullOrEmpty(category) ? "category-filter-any" : $"category-{category}",
                string.IsNullOrEmpty(category) ? Text("Map.Loot.AllCategories") : category,
                string.IsNullOrEmpty(category)
                    ? _filterState.Filter.Categories.Count == 0
                    : _filterState.Filter.Categories.Count == 1 &&
                      string.Equals(_filterState.Filter.Categories[0], category, StringComparison.OrdinalIgnoreCase),
                () => Request(_filterState.WithFilter(CloneFilter(
                    categories: string.IsNullOrEmpty(category) ? [] : [category])))))
            .ToArray();
        FloorChoices = new[] { string.Empty }.Concat(_availableFloors)
            .Select(floor => Choice(
                string.IsNullOrEmpty(floor) ? "floor-filter-any" : $"floor-{floor}",
                string.IsNullOrEmpty(floor) ? Text("Map.Loot.AllFloors") : floor,
                string.Equals(_filterState.Filter.FloorId ?? string.Empty, floor, StringComparison.OrdinalIgnoreCase),
                () => Request(_filterState.WithFilter(CloneFilter(
                    floorId: string.IsNullOrEmpty(floor) ? null : floor,
                    replaceFloor: true)))))
            .ToArray();
    }

    private void BuildRows()
    {
        var filtered = _result.Entries
            .Where(entry => TierRank(entry.Tier) >= TierRank(_filterState.MinimumTier))
            .OrderByDescending(entry => TierSortRank(entry.Tier))
            .ThenByDescending(entry => RankingValue(entry, _filterState.Filter.ValueBasis))
            .ThenBy(entry => entry.Spawn.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Spawn.SpawnId, StringComparer.Ordinal)
            .ToArray();
        ShowableObjectIds = filtered
            .Where(entry => entry.SceneObjectId is not null)
            .Select(entry => entry.SceneObjectId!.Value)
            .ToHashSet();
        if (!_isLayerVisible)
        {
            FilteredCount = 0;
            Rows = [];
            VisibleObjectIds = new HashSet<MapSceneObjectId>();
            return;
        }

        FilteredCount = filtered.Length;
        var pageCount = FilteredCount == 0 ? 0 : (FilteredCount + PageSize - 1) / PageSize;
        _pageIndex = pageCount == 0 ? 0 : Math.Clamp(_pageIndex, 0, pageCount - 1);
        Rows = filtered
            .Skip(_pageIndex * PageSize)
            .Take(PageSize)
            .Select(entry => new HighValueLootEntryViewModel(
                entry,
                _filterState.Filter,
                _presentation,
                () => _select(entry),
                () => RequestWaypoint(entry)))
            .ToArray();
        VisibleObjectIds = ShowableObjectIds;
    }

    private HighValueLootFilter CloneFilter(
        LootSpawnValueBasis? valueBasis = null,
        bool? includeProfileRelevant = null,
        string? floorId = null,
        bool replaceFloor = false,
        IReadOnlyList<string>? categories = null,
        long? minimumValueRoubles = null,
        bool replaceMinimumValue = false) => new(
        valueBasis ?? _filterState.Filter.ValueBasis,
        _filterState.Filter.Thresholds,
        _filterState.Filter.MaximumPriceAge,
        _filterState.Filter.MaximumSourceAge,
        _filterState.Filter.MinimumConfidence,
        includeProfileRelevant ?? _filterState.Filter.IncludeProfileRelevant,
        replaceFloor ? floorId : _filterState.Filter.FloorId,
        _filterState.Filter.ItemIds,
        categories ?? _filterState.Filter.Categories,
        replaceMinimumValue ? minimumValueRoubles : _filterState.Filter.MinimumValueRoubles);

    private HighValueLootFilterChoiceViewModel Choice(
        string id,
        string label,
        bool isSelected,
        Action select) => new(id, label, isSelected, select);

    private string DescribeState()
    {
        if (!_isLayerVisible)
        {
            return Text("Map.Loot.LayerOff");
        }

        if (IsUnavailable)
        {
            return NoDataMessage;
        }

        if (_result.Entries.Count == 0)
        {
            return Text("Map.Loot.NoMatches");
        }

        if (_result.Status.Completeness == ResultCompleteness.Partial)
        {
            return Text("Map.Loot.Partial");
        }

        return Text("Map.Loot.Ready");
    }

    private string DescribeFreshness()
    {
        if (!_isLayerVisible ||
            _result.Status.Completeness is ResultCompleteness.Unknown or ResultCompleteness.Unavailable)
        {
            return string.Empty;
        }

        return _result.Status.Freshness switch
        {
            FreshnessState.Stale => Text("Map.Loot.Stale"),
            FreshnessState.Unknown => Text("Map.Loot.FreshnessUnknown"),
            _ => string.Empty,
        };
    }

    private string DescribeBasis(LootSpawnValueBasis basis) => Text($"Map.Loot.Basis.{basis}");

    private string DescribeThreshold(long threshold) => threshold == 0
        ? Text("Map.Loot.Threshold.Any")
        : Format("Map.Loot.Threshold.Amount", (threshold / 1000).ToString("N0", _presentation.Culture));

    private static string MapName(string mapId) => string.Join(' ', mapId
        .Split('-', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private string DescribeTier(LootSpawnValueTier tier) => Text($"Map.Loot.Tier.{tier}");

    private static int TierRank(LootSpawnValueTier tier) => tier switch
    {
        LootSpawnValueTier.Exceptional => 5,
        LootSpawnValueTier.High => 4,
        LootSpawnValueTier.Moderate => 3,
        LootSpawnValueTier.Qualifying or LootSpawnValueTier.ProfileRelevant => 2,
        LootSpawnValueTier.Unknown => 1,
        _ => 0,
    };

    private static int TierSortRank(LootSpawnValueTier tier) => tier == LootSpawnValueTier.ProfileRelevant
        ? 6
        : TierRank(tier);

    private static long? RankingValue(HighValueLootEntry entry, LootSpawnValueBasis basis) =>
        basis == LootSpawnValueBasis.ValuePerSquare ? entry.MaximumValuePerSquare : entry.MaximumValue;

    private static IReadOnlyList<string> CategoriesFrom(HighValueLootLayerResult result) => result.Entries
        .SelectMany(entry => entry.Spawn.Candidates)
        .Select(candidate => candidate.Category)
        .ToArray();

    private static IReadOnlyList<string> ActiveSingleCategory(HighValueLootLayerFilterState state) =>
        state.Filter.Categories.Count == 1 ? [state.Filter.Categories[0]] : [];

    private static IReadOnlyList<string> ActiveFloor(HighValueLootLayerFilterState state) =>
        state.Filter.FloorId is { } floorId ? [floorId] : [];

    private static NormalizedOptions NormalizeOptions(
        IReadOnlyList<string> values,
        IReadOnlyList<string> activeValues)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(activeValues);
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputWasTruncated = false;
        using var enumerator = values.GetEnumerator();
        var read = 0;
        while (read < MaximumFilterOptionsRead && enumerator.MoveNext())
        {
            read++;
            var value = enumerator.Current;
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= MaximumFilterOptionLength)
            {
                distinct.Add(value.Trim());
            }
            else if (value?.Length > MaximumFilterOptionLength)
            {
                inputWasTruncated = true;
            }
        }

        // Do not trust Count: an external IReadOnlyList can under-report its size or enumerate
        // forever. Probe once beyond the read ceiling, then stop without materializing the tail.
        if (read == MaximumFilterOptionsRead && enumerator.MoveNext())
        {
            inputWasTruncated = true;
        }

        // A shared filter can name a valid category or floor beyond the rendered cap. Keep that
        // active choice visible so the controls never imply that no filter is selected. It is
        // prepended to the ordinary bounded choices and does not cause the host stream's tail to
        // be enumerated.
        var active = activeValues
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumFilterOptionLength)
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRenderedFilterOptions)
            .ToArray();
        var ordered = distinct
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var rendered = active
            .Concat(ordered.Where(value => !active.Contains(value, StringComparer.OrdinalIgnoreCase)))
            .Take(MaximumRenderedFilterOptions)
            .ToArray();
        var availableCount = distinct
            .Concat(active)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return new(rendered, inputWasTruncated || rendered.Length < availableCount);
    }

    private string Number(int value) => value.ToString("N0", _presentation.Culture);

    private string Text(string key) => _presentation.Get(key);

    private string Format(string key, params object?[] arguments) => _presentation.Format(key, arguments);

    private void RaiseAllChanged()
    {
        foreach (var property in new[]
                 {
                     nameof(Legend), nameof(LayerMenuStatus), nameof(StateMessage), nameof(FreshnessMessage), nameof(HasFreshnessMessage),
                     nameof(CoverageLabel), nameof(HasCoverage),
                     nameof(ValueBasisChoices), nameof(CompactValueBasisChoices), nameof(ValueThresholdChoices),
                     nameof(HasPerSlotValues),
                     nameof(CompactFilterLabel), nameof(MinimumTierChoices), nameof(ProfileRelevanceChoices),
                     nameof(CategoryChoices), nameof(FloorChoices), nameof(HasCategoryOptionsNotice),
                     nameof(HasFloorOptionsNotice), nameof(CategoryOptionsNotice), nameof(FloorOptionsNotice),
                     nameof(HasMultipleCategoryFilter), nameof(MultipleCategoryFilterMessage),
                     nameof(Rows), nameof(HasEntries), nameof(IsUnavailable), nameof(NoDataMessage),
                     nameof(ShowsEmpty), nameof(FilteredCount), nameof(PageCount), nameof(PageNumber),
                     nameof(PageLabel), nameof(CanGoToPreviousPage), nameof(CanGoToNextPage),
                 })
        {
            OnPropertyChanged(property);
        }
    }

    private void RaisePageChanged()
    {
        foreach (var property in new[]
                 {
                     nameof(Rows), nameof(PageNumber), nameof(PageLabel),
                     nameof(CanGoToPreviousPage), nameof(CanGoToNextPage),
                 })
        {
            OnPropertyChanged(property);
        }
    }

    private sealed record NormalizedOptions(IReadOnlyList<string> Values, bool Truncated);
}

public sealed record HighValueLootLayerFilterState
{
    public HighValueLootLayerFilterState(HighValueLootFilter filter, LootSpawnValueTier minimumTier)
    {
        Filter = filter ?? throw new ArgumentNullException(nameof(filter));
        if (minimumTier is not (LootSpawnValueTier.Qualifying or LootSpawnValueTier.Moderate or
            LootSpawnValueTier.High or LootSpawnValueTier.Exceptional))
        {
            throw new ArgumentOutOfRangeException(nameof(minimumTier));
        }

        MinimumTier = minimumTier;
    }

    public HighValueLootFilter Filter { get; init; }

    public LootSpawnValueTier MinimumTier { get; init; }

    public HighValueLootLayerFilterState WithFilter(HighValueLootFilter filter) => this with { Filter = filter };

    public static HighValueLootLayerFilterState Default { get; } = new(
        HighValueLootFilter.Default,
        LootSpawnValueTier.Qualifying);
}

public sealed class HighValueLootFilterChoiceViewModel
{
    public HighValueLootFilterChoiceViewModel(string id, string label, bool isSelected, Action select)
    {
        Id = id;
        Label = label;
        IsSelected = isSelected;
        SelectCommand = new DelegateCommand(select ?? throw new ArgumentNullException(nameof(select)));
    }

    public string Id { get; }
    public string Label { get; }
    public bool IsSelected { get; }
    public string AutomationId => $"v2-map-loot-filter-{MapRendererToken.From(Id)}";
    public ICommand SelectCommand { get; }
}

public sealed class HighValueLootEntryViewModel
{
    public HighValueLootEntryViewModel(
        HighValueLootEntry entry,
        HighValueLootFilter filter,
        MapSceneRendererPresentation presentation,
        Action select,
        Action? makeWaypoint = null)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        ArgumentNullException.ThrowIfNull(filter);
        var matchedCandidates = entry.Spawn.Candidates
            .Where(candidate =>
                (filter.ItemIds.Count == 0 || filter.ItemIds.Contains(candidate.ItemId, StringComparer.OrdinalIgnoreCase)) &&
                (filter.Categories.Count == 0 || filter.Categories.Contains(candidate.Category, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
        var basisLabel = presentation.Get($"Map.Loot.Basis.{filter.ValueBasis}");
        Label = entry.Spawn.Label;
        TierLabel = presentation.Get($"Map.Loot.Tier.{entry.Tier}");
        ValueLabel = DescribeValue(entry, filter.ValueBasis, presentation);
        CandidateLabel = filter.ValueBasis == LootSpawnValueBasis.ProfileUtility
            ? presentation.Format(
                "Map.Loot.Candidates.ProfileUtility",
                entry.MatchedCandidateCount.ToString("N0", presentation.Culture))
            : presentation.Format(
                "Map.Loot.Candidates",
                entry.HighValueCandidateCount.ToString("N0", presentation.Culture),
                entry.MatchedCandidateCount.ToString("N0", presentation.Culture),
                basisLabel);
        LocationLabel = DescribeLocation(entry, presentation);
        FloorLabel = entry.Spawn.Location.FloorIds.Count == 0
            ? presentation.Get("Map.Loot.FloorUnknown")
            : string.Join(", ", entry.Spawn.Location.FloorIds);
        IsListOnly = entry.SceneObjectId is null;
        ListOnlyLabel = IsListOnly ? presentation.Get("Map.Loot.ListOnly") : string.Empty;
        HasProfileRelevance = entry.ProfileNeeds.Count > 0;
        ProfileRelevanceLabel = HasProfileRelevance
            ? JoinBounded(
                entry.ProfileNeeds.Select(need => need.Explanation),
                3,
                presentation,
                "Map.Loot.MoreProfileReasons")
            : string.Empty;
        HasProfileConflicts = entry.ProfileNeedConflictCodes.Count > 0;
        ProfileConflictLabel = HasProfileConflicts
            ? presentation.Format(
                "Map.Loot.ProfileConflicts",
                JoinBounded(
                    entry.ProfileNeedConflictCodes,
                    5,
                    presentation,
                    "Map.Loot.MoreProfileConflicts"))
            : string.Empty;
        PossibleItemsLabel = JoinBounded(
            matchedCandidates.Select(candidate => candidate.DisplayName),
            8,
            presentation,
            "Map.Loot.MoreItems");
        CategoriesLabel = string.Join(", ", matchedCandidates
            .Select(candidate => candidate.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));
        ProbabilityLabel = entry.ProjectedSpawnProbability is { } probability
            ? presentation.Format("Map.Loot.ProbabilityKnown", presentation.Percent(probability))
            : presentation.Get("Map.Loot.ProbabilityUnknown");
        RespawnLabel = string.IsNullOrWhiteSpace(entry.ProjectedRespawnBehavior)
            ? presentation.Get("Map.Loot.RespawnUnknown")
            : entry.ProjectedRespawnBehavior;
        AccessLabel = entry.Spawn.AccessNote ?? presentation.Get("Map.Loot.AccessUnknown");
        MissingFactsLabel = entry.MissingFacts.Count == 0
            ? string.Empty
            : string.Join(" · ", entry.MissingFacts);
        HasMissingFacts = entry.MissingFacts.Count > 0;
        DatasetLabel = presentation.Format(
            "Map.Loot.Dataset",
            entry.Spawn.DatasetVersion,
            entry.Spawn.TransformVersion);
        SourceLabel = presentation.Format(
            "Map.Loot.Source",
            presentation.Get($"Map.Loot.SourceClass.{entry.Spawn.Provenance.SourceClass}"),
            entry.Spawn.Provenance.SourceIdentifier,
            presentation.Instant(entry.Spawn.Provenance.EvidenceThroughUtc));
        ProducerLabel = string.IsNullOrWhiteSpace(entry.Spawn.Provenance.Producer.ModelVersion)
            ? presentation.Format(
                "Map.Loot.Producer",
                entry.Spawn.Provenance.Producer.Name,
                entry.Spawn.Provenance.Producer.Version)
            : presentation.Format(
                "Map.Loot.ProducerWithModel",
                entry.Spawn.Provenance.Producer.Name,
                entry.Spawn.Provenance.Producer.Version,
                entry.Spawn.Provenance.Producer.ModelVersion);
        EvidenceTimeLabel = entry.Spawn.Provenance.GeneratedUtc is { } generatedUtc
            ? presentation.Format(
                "Map.Loot.EvidenceTimes.Generated",
                presentation.Instant(entry.Spawn.Provenance.ObservedUtc),
                presentation.Instant(generatedUtc))
            : presentation.Format(
                "Map.Loot.EvidenceTimes.NotGenerated",
                presentation.Instant(entry.Spawn.Provenance.ObservedUtc));
        ConfidenceLabel = DescribeConfidence(entry.Spawn.Provenance.Confidence, presentation);
        SourceCoverageLabel = DescribeCoverage(entry.Spawn.Provenance.Coverage, presentation);
        AutomationName = presentation.Format(
            "Map.Loot.RowAutomation",
            Label,
            TierLabel,
            ValueLabel,
            LocationLabel,
            ListOnlyLabel);
        SelectCommand = new DelegateCommand(select ?? throw new ArgumentNullException(nameof(select)));
        // A spawn with no drawn marker (map-only or unresolved precision) has no point to put a
        // waypoint on, so it is not offered one rather than given a made-up position.
        CanMakeWaypoint = makeWaypoint is not null && !IsListOnly;
        MakeWaypointLabel = presentation.Get("Map.Loot.MakeWaypoint");
        MakeWaypointCommand = new DelegateCommand(() => makeWaypoint?.Invoke());
    }

    public HighValueLootEntry Entry { get; }
    public bool CanMakeWaypoint { get; }
    public string MakeWaypointLabel { get; }
    public ICommand MakeWaypointCommand { get; }
    public string Label { get; }
    public string TierLabel { get; }
    public string ValueLabel { get; }
    public string CandidateLabel { get; }
    public string LocationLabel { get; }
    public string FloorLabel { get; }
    public bool IsListOnly { get; }
    public string ListOnlyLabel { get; }
    public bool HasProfileRelevance { get; }
    public string ProfileRelevanceLabel { get; }
    public bool HasProfileConflicts { get; }
    public string ProfileConflictLabel { get; }
    public string PossibleItemsLabel { get; }
    public string CategoriesLabel { get; }
    public string ProbabilityLabel { get; }
    public string RespawnLabel { get; }
    public string AccessLabel { get; }
    public bool HasMissingFacts { get; }
    public string MissingFactsLabel { get; }
    public string DatasetLabel { get; }
    public string SourceLabel { get; }
    public string ProducerLabel { get; }
    public string EvidenceTimeLabel { get; }
    public string ConfidenceLabel { get; }
    public string SourceCoverageLabel { get; }
    public string AutomationName { get; }
    public string AutomationId => $"v2-map-loot-row-{MapRendererToken.From(Entry.Spawn.SpawnId)}";
    public ICommand SelectCommand { get; }

    private static string DescribeValue(
        HighValueLootEntry entry,
        LootSpawnValueBasis basis,
        MapSceneRendererPresentation presentation)
    {
        if (basis == LootSpawnValueBasis.ProfileUtility)
        {
            return presentation.Get("Map.Loot.ValueProfileUtility");
        }

        var minimum = basis == LootSpawnValueBasis.ValuePerSquare
            ? entry.MinimumValuePerSquare
            : entry.MinimumValue;
        var maximumValue = basis == LootSpawnValueBasis.ValuePerSquare
            ? entry.MaximumValuePerSquare
            : entry.MaximumValue;
        if (maximumValue is null)
        {
            return presentation.Get("Map.Loot.ValueUnknown");
        }

        var maximum = maximumValue.Value.ToString("N0", presentation.Culture);
        if (!entry.IsValueRangeComplete || minimum is null)
        {
            return basis == LootSpawnValueBasis.ValuePerSquare
                ? presentation.Format("Map.Loot.ValuePerSquarePartial", maximum)
                : presentation.Format(
                    "Map.Loot.ValuePartial",
                    maximum,
                    presentation.Get($"Map.Loot.Basis.{basis}"));
        }

        if (minimum == maximumValue)
        {
            return basis == LootSpawnValueBasis.ValuePerSquare
                ? presentation.Format("Map.Loot.ValuePerSquareSingle", maximum)
                : presentation.Format(
                    "Map.Loot.ValueSingle",
                    maximum,
                    presentation.Get($"Map.Loot.Basis.{basis}"));
        }

        return basis == LootSpawnValueBasis.ValuePerSquare
            ? presentation.Format(
                "Map.Loot.ValuePerSquareRange",
                minimum!.Value.ToString("N0", presentation.Culture),
                maximum)
            : presentation.Format(
                "Map.Loot.ValueRange",
                minimum!.Value.ToString("N0", presentation.Culture),
                maximum,
                presentation.Get($"Map.Loot.Basis.{basis}"));
    }

    private static string DescribeLocation(HighValueLootEntry entry, MapSceneRendererPresentation presentation) =>
        entry.Spawn.Location.Precision switch
        {
            LootSpawnPrecision.ExactPoint => presentation.Get("Map.Loot.Precision.ExactPoint"),
            LootSpawnPrecision.BoundedArea => presentation.Get("Map.Loot.Precision.BoundedArea"),
            LootSpawnPrecision.RoomOrRegion => presentation.Get("Map.Loot.Precision.RoomOrRegion"),
            LootSpawnPrecision.MapOnly => presentation.Get("Map.Loot.Precision.MapOnly"),
            _ => throw new ArgumentOutOfRangeException(nameof(entry)),
        };

    private static string DescribeConfidence(
        EvidenceConfidence confidence,
        MapSceneRendererPresentation presentation)
    {
        var kind = presentation.Get($"Map.Loot.ConfidenceKind.{confidence.Kind}");
        var score = confidence.Score is { } value
            ? presentation.Percent(value)
            : presentation.Get("Map.Loot.ConfidenceUnknown");
        return confidence.CalibrationReference is { } calibration
            ? presentation.Format("Map.Loot.ConfidenceCalibrated", kind, score, calibration)
            : presentation.Format("Map.Loot.Confidence", kind, score);
    }

    private static string DescribeCoverage(
        EvidenceCoverage? coverage,
        MapSceneRendererPresentation presentation)
    {
        if (coverage is null)
        {
            return presentation.Get("Map.Loot.SourceCoverageUnknown");
        }

        var facts = new List<string>(3);
        if (coverage.Fraction is { } fraction)
        {
            facts.Add(presentation.Format("Map.Loot.SourceCoverageFraction", presentation.Percent(fraction)));
        }

        if (coverage.SampleSize is { } sampleSize)
        {
            facts.Add(presentation.Format(
                "Map.Loot.SourceCoverageSample",
                sampleSize.ToString("N0", presentation.Culture)));
        }

        if (coverage.Description is { } description)
        {
            facts.Add(description);
        }

        return presentation.Format("Map.Loot.SourceCoverage", string.Join(" · ", facts));
    }

    private static string JoinBounded(
        IEnumerable<string> values,
        int maximum,
        MapSceneRendererPresentation presentation,
        string moreKey)
    {
        var materialized = values.ToArray();
        var shown = string.Join(", ", materialized.Take(maximum));
        return materialized.Length <= maximum
            ? shown
            : $"{shown} · {presentation.Format(moreKey, materialized.Length - maximum)}";
    }
}
