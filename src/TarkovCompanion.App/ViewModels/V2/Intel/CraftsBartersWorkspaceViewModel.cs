using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Intel;

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
    public string Label => V2ShellText.Get($"V2.Shell.Intel.Trade.Sort.{Sort}");
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
        ? string.Create(CultureInfo.CurrentCulture, $"{Count}× {Name}")
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
    string RecordedLevelLabel = "")
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
    public string ReadinessLabel => Readiness switch
    {
        IntelTradeReadiness.Ready => V2ShellText.Get("V2.Shell.Intel.Trade.Ready"),
        IntelTradeReadiness.Locked => V2ShellText.Get("V2.Shell.Intel.Trade.Locked"),
        IntelTradeReadiness.Unknown => V2ShellText.Get("V2.Shell.Intel.Trade.LevelUnknown"),
        _ => string.Empty,
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
public sealed class CraftsBartersWorkspaceViewModel : BindableViewModel
{
    private readonly IIntelTradeCatalogService _catalog;
    private readonly Action<string> _openItem;
    private IReadOnlyList<IntelTradeRow> _all = [];
    private string _searchText = string.Empty;
    private bool _readyNowOnly;
    private IntelTradeSort _sort = IntelTradeSort.Profit;
    private bool _loading;
    private bool _loaded;

    public CraftsBartersWorkspaceViewModel(IIntelTradeCatalogService catalog, Action<string> openItem)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(openItem);
        _catalog = catalog;
        _openItem = openItem;
        Sorts = Enum.GetValues<IntelTradeSort>()
            .Select(sort => new IntelTradeSortViewModel(sort, SelectSort))
            .ToArray();
        Sorts.Single(sort => sort.Sort == _sort).IsSelected = true;
        LoadTask = LoadAsync();
    }

    /// <summary>
    /// The in-flight (or, once it resolves, completed) initial load. Nothing in the running app
    /// awaits this — the view binds <see cref="IsLoading"/> instead — but headless render tooling
    /// has no tick to wait a fixed number of times against and needs a real task to await.
    /// </summary>
    internal Task LoadTask { get; private set; }

    public string SearchPlaceholder => V2ShellText.Get("V2.Shell.Intel.Trade.SearchPlaceholder");
    public string ReadyNowLabel => V2ShellText.Get("V2.Shell.Intel.Trade.ReadyNow");
    public string SortHeading => V2ShellText.Get("V2.Shell.Intel.Trade.SortHeading");
    public string EmptyLabel => V2ShellText.Get("V2.Shell.Intel.Trade.Empty");
    public string LoadingLabel => V2ShellText.Get("V2.Shell.Intel.Trade.Loading");
    public IReadOnlyList<IntelTradeSortViewModel> Sorts { get; }

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
                RaiseRowsChanged();
            }
        }
    }

    public bool IsLoading => _loading;
    public bool ShowsEmpty => _loaded && !_loading && Rows.Count == 0;

    public IReadOnlyList<IntelTradeRowViewModel> Rows => Filtered()
        .Select(Describe)
        .ToArray();

    public string ResultCountLabel => Rows.Count == 1
        ? V2ShellText.Get("V2.Shell.Intel.OneResult")
        : V2ShellText.Format("V2.Shell.Intel.Results", CultureInfo.CurrentCulture, Rows.Count);

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
        V2ShellText.Format("V2.Shell.Intel.Trade.UnknownCount", CultureInfo.CurrentCulture, ReadyNowUnknownCount);

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
        OnPropertyChanged(nameof(ReadyNowUnknownCount));
        OnPropertyChanged(nameof(HasReadyNowUnknownCount));
        OnPropertyChanged(nameof(ReadyNowUnknownLabel));
    }

    private IntelTradeRowViewModel Describe(IntelTradeRow row) => new(
        row.TradeId,
        row.Kind == IntelTradeKind.Craft
            ? V2ShellText.Get("V2.Shell.Intel.Trade.Craft")
            : V2ShellText.Get("V2.Shell.Intel.Trade.Barter"),
        row.Inputs.Select(input => new IntelTradeIngredientRowViewModel(input.ItemId, input.Name, input.Count)).ToArray(),
        new IntelTradeIngredientRowViewModel(row.Output.ItemId, row.Output.Name, row.Output.Count),
        row.SourceName,
        row.LevelLabel,
        row.Duration is { } duration ? DurationLabel(duration) : string.Empty,
        row.ProfitRoubles is { } profit
            ? V2ShellText.Format("V2.Shell.Intel.Trade.Profit", CultureInfo.CurrentCulture, Roubles(profit))
            : V2ShellText.Get("V2.Shell.Intel.Trade.ProfitUnknown"),
        row.ProfitRoubles is null,
        row.ProfitRoubles is < 0,
        row.Readiness,
        new DelegateCommand(() => _openItem(row.Output.ItemId)),
        RecordedLevelLabel(row));

    /// <summary>"you: Loyalty N"/"you: Level N" — only for a genuine Locked, never for an Unknown, which has nothing to report.</summary>
    private static string RecordedLevelLabel(IntelTradeRow row) =>
        row.Readiness == IntelTradeReadiness.Locked && row.RecordedLevel is { } level
            ? V2ShellText.Format(
                row.Kind == IntelTradeKind.Craft ? "V2.Shell.Intel.Trade.YourLevel" : "V2.Shell.Intel.Trade.YourLoyalty",
                CultureInfo.CurrentCulture,
                level)
            : string.Empty;

    private static string DurationLabel(TimeSpan duration) => duration.TotalHours >= 1
        ? string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalHours}h {duration.Minutes}m")
        : string.Create(CultureInfo.CurrentCulture, $"{(int)duration.TotalMinutes}m");

    private static string Roubles(long value) => V2ShellText.Format("V2.Shell.Intel.Roubles", CultureInfo.CurrentCulture, value);
}
