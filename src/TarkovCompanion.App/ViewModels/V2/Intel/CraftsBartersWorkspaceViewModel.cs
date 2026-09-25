using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.App.ViewModels.V2.Intel;

/// <summary>How the Crafts &amp; barters list is ordered.</summary>
public enum IntelTradeSort
{
    Profit,
    Duration,
    Name,
}

/// <summary>One sort chip above the Crafts &amp; barters list.</summary>
public sealed class IntelTradeSortViewModel(IntelTradeSort sort, Action<IntelTradeSort> select) : BindableViewModel
{
    private bool _isSelected;

    public IntelTradeSort Sort { get; } = sort;
    public string Label => Sort switch
    {
        IntelTradeSort.Duration => IntelText.CraftsSortDuration,
        IntelTradeSort.Name => IntelText.CraftsSortName,
        _ => IntelText.CraftsSortProfit,
    };
    public string AutomationId => $"v2-intel-trade-sort-{Sort.ToString().ToLowerInvariant()}";
    public ICommand SelectCommand { get; } = new DelegateCommand(() => select(sort));

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>One input or output line inside a trade row: a count and a name.</summary>
public sealed record IntelTradeIngredientRowViewModel(string ItemId, string Name, int Count)
{
    public string Label => Count > 1
        ? IntelText.CraftsCount(Count, Name)
        : Name;
}

/// <summary>One craft or barter, priced and ready to read at a glance.</summary>
public sealed record IntelTradeRowViewModel(
    string TradeId,
    string KindLabel,
    IReadOnlyList<IntelTradeIngredientRowViewModel> Inputs,
    IntelTradeIngredientRowViewModel Output,
    string SourceName,
    string LevelLabel,
    string DurationLabel,
    string ProfitLabel,
    bool ProfitIsUnknown,
    bool ProfitIsNegative,
    IntelTradeReadiness Readiness,
    ICommand OpenOutputCommand,
    // #287 review: a station/trader level this profile has never recorded reads as Unknown, not
    // Locked. RecordedLevelLabel is only ever non-empty for a genuine Locked — a real, known
    // level short of what the trade asks for — never for an Unknown one, which has nothing to
    // report.
    string RecordedLevelLabel = "",
    ICommand? OpenChainCommand = null)
{
    public string AutomationId => $"v2-intel-trade-{TradeId}";
    public string InputsLabel => string.Join(" + ", Inputs.Select(input => input.Label));
    public bool HasSourceName => SourceName.Length > 0;
    public bool HasLevelLabel => LevelLabel.Length > 0;
    public bool HasDuration => DurationLabel.Length > 0;
    public bool IsLocked => Readiness == IntelTradeReadiness.Locked;
    public bool IsReady => Readiness == IntelTradeReadiness.Ready;
    public bool IsUnknownReadiness => Readiness == IntelTradeReadiness.Unknown;
    public bool HasRecordedLevel => RecordedLevelLabel.Length > 0;
    public string ChainLabel => IntelText.CraftsChainHeading;
    public string ReadinessLabel => Readiness switch
    {
        IntelTradeReadiness.Ready => IntelText.CraftsReady,
        IntelTradeReadiness.Locked => IntelText.CraftsLocked,
        IntelTradeReadiness.Unknown => IntelText.CraftsLevelUnknown,
        _ => string.Empty,
    };

    public string LearnReason => Readiness switch
    {
        IntelTradeReadiness.Ready => IntelText.CraftsLearnReady(ProfitLabel),
        IntelTradeReadiness.Locked => IntelText.CraftsLearnLocked(SourceName, LevelLabel).TrimEnd(),
        _ => IntelText.CraftsLearnCheck(SourceName, LevelLabel).TrimEnd(),
    };
}

/// <summary>
/// V2 (#287): every craft and barter the catalog holds, searchable by an input or output item
/// name and ranked by profit at the catalog's current prices.
/// </summary>
/// <remarks>
/// A thin presentation over <see cref="IIntelTradeCatalogService"/>, the same separation
/// <c>AmmoWorkspaceViewModel</c> keeps from its page: this class only filters, sorts and labels
/// what the service already priced. The heavy read (~900 items, each priced once) happens once,
/// off the interface thread, and is kept until the service's own cache goes stale; typing in the
/// search box or flipping a sort chip only re-filters the rows already in memory.
/// </remarks>
public sealed partial class CraftsBartersWorkspaceViewModel : BindableViewModel
{
    private readonly IIntelTradeCatalogService _catalog;
    private readonly Action<string> _openItem;
    private IReadOnlyList<IntelTradeRow> _all = [];
    private string _searchText = string.Empty;
    private bool _readyNowOnly;
    private IntelTradeSort _sort = IntelTradeSort.Profit;
    private bool _loading;
    private bool _loaded;
    private readonly TarkovCompanion.Application.Services.Workspaces.PageState _state;

    public CraftsBartersWorkspaceViewModel(
        IIntelTradeCatalogService catalog,
        Action<string> openItem,
        TarkovCompanion.App.ViewModels.V2.Plan.LearnModeSetting? learnMode = null)
        : this(catalog, NullAcquisitionChainPlanningService.Instance, openItem, learnMode)
    {
    }

    public CraftsBartersWorkspaceViewModel(
        IIntelTradeCatalogService catalog,
        IAcquisitionChainPlanningService chains,
        Action<string> openItem,
        TarkovCompanion.App.ViewModels.V2.Plan.LearnModeSetting? learnMode = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(chains);
        ArgumentNullException.ThrowIfNull(openItem);
        _catalog = catalog;
        _openItem = openItem;
        Chain = new(chains);
        CloseChainCommand = new DelegateCommand(Chain.Clear);
        LearnMode = learnMode ?? new();
        // [#902 P8] "I can do this now" and the sort come back after a visit elsewhere and a restart.
        _state = LearnMode.Page(TarkovCompanion.Application.Services.Workspaces.WorkspaceLayoutKeys.PageCrafts);
        _readyNowOnly = _state.Bool("ready-now", false);
        _sort = _state.Enum("sort", IntelTradeSort.Profit);
        Sorts = Enum.GetValues<IntelTradeSort>()
            .Select(sort => new IntelTradeSortViewModel(sort, SelectSort))
            .ToArray();
        Sorts.Single(sort => sort.Sort == _sort).IsSelected = true;
        LoadTask = LoadAsync();
    }

    public TarkovCompanion.App.ViewModels.V2.Plan.LearnModeSetting LearnMode { get; }

    /// <summary>
    /// The in-flight (or, once it resolves, completed) initial load. Nothing in the running app
    /// awaits this — the view binds <see cref="IsLoading"/> instead — but headless render tooling
    /// has no tick to wait a fixed number of times against and needs a real task to await.
    /// </summary>
    internal Task LoadTask { get; private set; }

    public string SearchPlaceholder => IntelText.CraftsSearchPlaceholder;
    public string ReadyNowLabel => IntelText.CraftsReadyNow;
    public string SortHeading => IntelText.CraftsSortHeading;
    public string EmptyLabel => _readyNowOnly && ReadyNowUnknownCount > 0
        ? IntelText.CraftsNeedLevels(ReadyNowUnknownCount)
        : _readyNowOnly && SearchMatches().Any() ? IntelText.CraftsNoneReadyNow : IntelText.CraftsEmpty;
    public string LoadingLabel => IntelText.CraftsLoading;
    public IReadOnlyList<IntelTradeSortViewModel> Sorts { get; }
    public AcquisitionChainViewModel Chain { get; }
    public ICommand CloseChainCommand { get; }
    public string CloseChainLabel => IntelText.CraftsCloseChain;

    public Task ShowChainAsync(string itemId) => Chain.ShowAsync(itemId);

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RaiseRowsChanged();
            }
        }
    }

    public bool ReadyNowOnly
    {
        get => _readyNowOnly;
        set
        {
            if (SetProperty(ref _readyNowOnly, value))
            {
                _state.SetBool("ready-now", value, false);
                RaiseRowsChanged();
            }
        }
    }

    public bool IsLoading => _loading;

    /// <summary>[#279] The first read has finished and none is running; the gallery waits for it.</summary>
    public bool HasLoaded => LoadTask.IsCompleted && !_loading;
    public bool ShowsEmpty => _loaded && !_loading && Rows.Count == 0;

    /// <summary>[#902 P8] The remembered "I can do this now" emptied the list: one click turns it off.</summary>
    public bool ShowsFilterReset => ShowsEmpty && _readyNowOnly && SearchMatches().Any();

    public ICommand ClearFilterCommand => _clearFilter ??= new DelegateCommand(() => ReadyNowOnly = false);

    private ICommand? _clearFilter;

    public IReadOnlyList<IntelTradeRowViewModel> Rows => Filtered()
        .Select(Describe)
        .ToArray();

    public string ResultCountLabel => IntelText.CraftsResults(Rows.Count);

    /// <summary>
    /// How many of the current search's matches "I can do this now" left out because their
    /// readiness is unknown, not because they were locked. Shown so the filter never reads as
    /// "everything else is locked" when some of it simply was not checkable.
    /// </summary>
    public int ReadyNowUnknownCount => _readyNowOnly
        ? SearchMatches().Count(row => row.Readiness == IntelTradeReadiness.Unknown)
        : 0;
    public bool HasReadyNowUnknownCount => ReadyNowUnknownCount > 0;
    public string ReadyNowUnknownLabel =>
        IntelText.CraftsUnknownCount(ReadyNowUnknownCount);

    /// <summary>Every row whose output is this item — the recipe(s) that make it.</summary>
    public IReadOnlyList<IntelTradeRowViewModel> MadeBy(string itemId) => _all
        .Where(row => string.Equals(row.Output.ItemId, itemId, StringComparison.Ordinal))
        .OrderByDescending(row => row.ProfitRoubles ?? long.MinValue)
        .Select(Describe)
        .ToArray();

    /// <summary>Every row that consumes this item as an input.</summary>
    public IReadOnlyList<IntelTradeRowViewModel> UsedIn(string itemId) => _all
        .Where(row => row.Inputs.Any(input => string.Equals(input.ItemId, itemId, StringComparison.Ordinal)))
        .OrderByDescending(row => row.ProfitRoubles ?? long.MinValue)
        .Select(Describe)
        .ToArray();

    /// <summary>
    /// Re-reads the catalog if its own cache has gone stale. Cheap when it has not — and a load
    /// already running is left alone rather than replaced with a second one, so
    /// <see cref="LoadTask"/> always resolves once the read it named is actually finished.
    /// </summary>
    public void RefreshIfStale()
    {
        if (!_loading)
        {
            LoadTask = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        OnPropertyChanged(nameof(IsLoading));
        try
        {
            _all = await OffInterfaceThread.Run(() => _catalog.GetAllAsync(CancellationToken.None)).ConfigureAwait(true);
            _loaded = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _all = [];
        }
        finally
        {
            _loading = false;
            OnPropertyChanged(nameof(IsLoading));
            RaiseRowsChanged();
        }
    }

    /// <summary>Every row the current search text matches, before the readiness filter narrows it further.</summary>
    private IEnumerable<IntelTradeRow> SearchMatches()
    {
        if (string.IsNullOrWhiteSpace(_searchText))
        {
            return _all;
        }

        var needle = _searchText.Trim();
        return _all.Where(row =>
            row.Output.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            row.Inputs.Any(input => input.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// "I can do this now" keeps only what is actually known to be ready — an unknown
    /// station/trader level is left out of the list exactly like a locked one, but
    /// <see cref="ReadyNowUnknownCount"/> says so, so leaving a row out is never silently the
    /// same thing as calling it locked.
    /// </summary>
    private IEnumerable<IntelTradeRow> Filtered()
    {
        var query = SearchMatches();
        if (_readyNowOnly)
        {
            query = query.Where(row => row.Readiness == IntelTradeReadiness.Ready);
        }

        return _sort switch
        {
            IntelTradeSort.Duration => query.OrderByDescending(row => row.Duration ?? TimeSpan.MinValue),
            IntelTradeSort.Name => query.OrderBy(row => row.Output.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderByDescending(row => row.ProfitRoubles ?? long.MinValue),
        };
    }

    private void SelectSort(IntelTradeSort sort)
    {
        _sort = sort;
        _state.SetEnum("sort", sort, IntelTradeSort.Profit);
        foreach (var chip in Sorts)
        {
            chip.IsSelected = chip.Sort == sort;
        }

        RaiseRowsChanged();
    }

    private void RaiseRowsChanged()
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(ResultCountLabel));
        OnPropertyChanged(nameof(ShowsEmpty));
        OnPropertyChanged(nameof(ShowsFilterReset));
        OnPropertyChanged(nameof(EmptyLabel));
        OnPropertyChanged(nameof(ReadyNowUnknownCount));
        OnPropertyChanged(nameof(HasReadyNowUnknownCount));
        OnPropertyChanged(nameof(ReadyNowUnknownLabel));
        OnPropertyChanged(nameof(ShowsSetTraderLevels));
        OnPropertyChanged(nameof(ShowsSetHideoutLevels));
    }

    private IntelTradeRowViewModel Describe(IntelTradeRow row) => new(
        row.TradeId,
        row.Kind == IntelTradeKind.Craft
            ? IntelText.CraftsCraft
            : IntelText.CraftsBarter,
        row.Inputs.Select(input => new IntelTradeIngredientRowViewModel(input.ItemId, input.Name, input.Count)).ToArray(),
        new IntelTradeIngredientRowViewModel(row.Output.ItemId, row.Output.Name, row.Output.Count),
        row.SourceName,
        row.LevelLabel,
        row.Duration is { } duration ? DurationLabel(duration) : string.Empty,
        row.ProfitRoubles is { } profit
            ? IntelText.CraftsProfit(Roubles(profit))
            : IntelText.CraftsProfitUnknown,
        row.ProfitRoubles is null,
        row.ProfitRoubles is < 0,
        row.Readiness,
        new DelegateCommand(() => _openItem(row.Output.ItemId)),
        RecordedLevelLabel(row),
        new DelegateCommand(() => ShowChainAsync(row.Output.ItemId).Observe("intel", "plan an acquisition chain")));

    /// <summary>"you: Loyalty N"/"you: Level N" — only for a genuine Locked, never for an Unknown, which has nothing to report.</summary>
    private static string RecordedLevelLabel(IntelTradeRow row) =>
        row.Readiness == IntelTradeReadiness.Locked && row.RecordedLevel is { } level
            ? row.Kind == IntelTradeKind.Craft ? IntelText.CraftsYourLevel(level) : IntelText.CraftsYourLoyalty(level)
            : string.Empty;

    private static string DurationLabel(TimeSpan duration) => duration.TotalHours >= 1
        ? IntelText.CraftsHoursMinutes((int)duration.TotalHours, duration.Minutes)
        : IntelText.CraftsMinutes((int)duration.TotalMinutes);

    private static string Roubles(long value) => IntelText.CraftsRoubles(value);
}

internal sealed class NullAcquisitionChainPlanningService : IAcquisitionChainPlanningService
{
    public static readonly NullAcquisitionChainPlanningService Instance = new();

    public Task<AcquisitionChainPlan> PlanAsync(string itemId, int quantity, CancellationToken cancellationToken) =>
        Task.FromResult(new AcquisitionChainPlan(itemId, itemId, quantity, null, false, false, false, null));
}
