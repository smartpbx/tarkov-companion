using System.Globalization;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels;

public sealed record FleaPriceViewModel(
    string ItemId,
    string Name,
    string ShortName,
    string Dimensions,
    string FleaPrice,
    string DailyBand,
    string BestSale,
    string ValuePerSlot,
    string Provenance);

public sealed record FleaHistoryPointViewModel(string Observed, string FleaPrice, string TraderValue, string Source);

/// <summary>One offer the game reported as sold while the player was busy.</summary>
/// <param name="Item">What sold, by name where the id resolves against synced data.</param>
/// <param name="Quantity">How many.</param>
/// <param name="Observed">When the companion read the notification.</param>
public sealed record FleaSaleViewModel(string Item, string Quantity, string Observed);

/// <summary>
/// Looks up what an item is worth and where it is best sold.
/// </summary>
/// <remarks>
/// The page is deliberately framed around the current cached price and the 24-hour band
/// rather than a price chart. json.tarkov.dev has no historical-price endpoint, and the sync
/// records one observation per item per run, so local history only accrues across days of
/// use. Promising a chart on day one would promise something the data cannot support.
/// </remarks>
public sealed class FleaPageViewModel : PageViewModel
{
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// What the sale list says before anything has sold.
    /// </summary>
    /// <remarks>
    /// The game posts a sale notification while the player is in a raid or a menu, which is
    /// exactly when they cannot read it, so this list is only ever filled while the companion
    /// is running alongside the game. Saying so is better than an empty panel that looks
    /// broken.
    /// </remarks>
    private const string SalesNotObserved =
        "Nothing has sold since the companion started. Sales appear here as the game reports them, "
        + "including the ones that land while you are in a raid.";

    private readonly IItemSearchService _searchService;
    private readonly IItemRepository _itemRepository;
    private readonly IPriceHistoryService _priceHistoryService;
    private readonly Dictionary<string, string> _soldItemNames = new(StringComparer.Ordinal);
    private IReadOnlyList<FleaSaleViewModel> _sales = [];
    private string _salesStatus = SalesNotObserved;
    private DateTimeOffset _renderedSales = DateTimeOffset.MinValue;
    private string _searchQuery = string.Empty;
    /// <summary>What the status line says when there is nothing wrong and nothing searched.</summary>
    private const string ReadyToSearch = "Search an item to see what it is worth and where to sell it.";

    private bool _showingNoData;
    private string _searchStatus = ReadyToSearch;
    private string _historyStatus = "Select an item to see the observations stored locally.";
    private IReadOnlyList<FleaPriceViewModel> _results = [];
    private IReadOnlyList<FleaHistoryPointViewModel> _history = [];
    private FleaPriceViewModel? _selected;
    private ApplicationRuntimeSnapshot? _snapshot;

    public FleaPageViewModel(
        IItemSearchService searchService,
        IItemRepository itemRepository,
        IPriceHistoryService priceHistoryService)
        : base("Flea", "Current value, daily band, and the best way to sell it", "Runtime state not loaded")
    {
        _searchService = searchService;
        _itemRepository = itemRepository;
        _priceHistoryService = priceHistoryService;
        SearchCommand = new AsyncDelegateCommand(SearchAsync);
    }

    public AsyncDelegateCommand SearchCommand { get; }

    /// <summary>Offers the game has reported as sold, newest first.</summary>
    public IReadOnlyList<FleaSaleViewModel> Sales
    {
        get => _sales;
        private set => SetProperty(ref _sales, value);
    }

    public string SalesStatus
    {
        get => _salesStatus;
        private set => SetProperty(ref _salesStatus, value);
    }

    public bool HasSales => Sales.Count > 0;

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

    public string HistoryStatus
    {
        get => _historyStatus;
        private set => SetProperty(ref _historyStatus, value);
    }

    public IReadOnlyList<FleaPriceViewModel> Results
    {
        get => _results;
        private set => SetProperty(ref _results, value);
    }

    public IReadOnlyList<FleaHistoryPointViewModel> History
    {
        get => _history;
        private set => SetProperty(ref _history, value);
    }

    public FleaPriceViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value) && value is not null)
            {
                _ = LoadHistoryAsync(value, CancellationToken.None);
            }
        }
    }

    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        Evidence = $"{snapshot.Data.Availability} · {snapshot.Data.ItemCount:N0} cached items";
        if (snapshot.Data.ItemCount == 0)
        {
            Results = [];
            History = [];
            _showingNoData = true;
            SearchStatus = snapshot.Data.Detail;
        }
        else if (_showingNoData)
        {
            // The page is built before the item cache has loaded, so it says there is no data
            // and then never takes it back. That left it insisting no data was available while
            // the header counted five thousand cached items, which reads as a broken page.
            _showingNoData = false;
            SearchStatus = ReadyToSearch;
        }

        ApplySales(snapshot.FleaSales);
    }

    /// <summary>
    /// Renders the sales observed so far.
    /// </summary>
    /// <remarks>
    /// Runs on every snapshot, so it returns immediately when nothing has sold since the last
    /// one. The snapshot's own timestamp is the comparison rather than the list, which is
    /// rebuilt on every sale and would never compare equal.
    /// </remarks>
    private void ApplySales(FleaSalesSnapshot sales)
    {
        if (sales.UpdatedUtc == _renderedSales)
        {
            return;
        }

        _renderedSales = sales.UpdatedUtc;
        Sales = sales.Sales.Select(Describe).ToArray();
        OnPropertyChanged(nameof(HasSales));
        SalesStatus = sales.Sales.Count switch
        {
            0 => SalesNotObserved,
            1 => "1 offer has sold since the companion started. The game states no price, so none is shown.",
            var count => string.Create(
                CultureInfo.CurrentCulture,
                $"{count} offers have sold since the companion started. The game states no price, so none is shown."),
        };
        _ = ResolveSoldItemNamesAsync(sales);
    }

    private FleaSaleViewModel Describe(FleaSaleObservation sale) => new(
        sale.HandbookItemId is { } itemId && _soldItemNames.TryGetValue(itemId, out var name)
            ? name
            : "Item not in the synced catalog",
        sale.Count == 1
            ? "1 sold"
            : string.Create(CultureInfo.CurrentCulture, $"{sale.Count} sold"),
        sale.ObservedUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));

    /// <summary>
    /// Names the items that sold, once each.
    /// </summary>
    /// <remarks>
    /// The notification states an id and never a name. Whether that id resolves against synced
    /// item data is not assumed: an unresolved one is shown as unresolved rather than as a raw
    /// id, which would read like a bug, and it is left out of the cache so a later sync can
    /// still fill it in.
    /// </remarks>
    private async Task ResolveSoldItemNamesAsync(FleaSalesSnapshot sales)
    {
        var unknown = sales.Sales
            .Select(sale => sale.HandbookItemId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(itemId => !_soldItemNames.ContainsKey(itemId))
            .ToArray();
        if (unknown.Length == 0)
        {
            return;
        }

        var resolved = false;
        foreach (var itemId in unknown)
        {
            try
            {
                if (await _itemRepository.GetAsync(itemId, CancellationToken.None).ConfigureAwait(true) is { } item)
                {
                    _soldItemNames[itemId] = item.Name;
                    resolved = true;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
            {
                return;
            }
        }

        if (resolved)
        {
            Sales = sales.Sales.Select(Describe).ToArray();
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
            var hits = await _searchService.SearchAsync(SearchQuery, 20, cancellationToken).ConfigureAwait(true);
            var results = new List<FleaPriceViewModel>(hits.Count);
            foreach (var hit in hits)
            {
                var price = await _itemRepository.GetPriceAsync(hit.Item.Id, cancellationToken).ConfigureAwait(true);
                results.Add(new(
                    hit.Item.Id,
                    hit.Item.Name,
                    hit.Item.ShortName,
                    $"{hit.Item.Dimensions.Width} × {hit.Item.Dimensions.Height} · {hit.Item.Dimensions.Slots} slot(s)",
                    price?.FleaPriceRoubles is { } flea ? Roubles(flea) : "Not sold on the flea",
                    DescribeBand(price),
                    DescribeBestSale(price),
                    DescribeValuePerSlot(hit.Item, price),
                    $"json.tarkov.dev · {Describe(hit.Item.Provenance.SourceUpdatedUtc)}"));
            }

            Results = results;
            Selected = results.Count == 1 ? results[0] : null;
            SearchStatus = results.Count == 0
                ? "No local item matched that query."
                : $"{results.Count} result(s) from the local cache; nothing was fetched.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Results = [];
            SearchStatus = $"Item search failed: {exception.Message}";
        }
    }

    private async Task LoadHistoryAsync(FleaPriceViewModel item, CancellationToken cancellationToken)
    {
        try
        {
            HistoryStatus = "Reading locally stored observations…";
            var points = await _priceHistoryService
                .GetAsync(item.ItemId, HistoryWindow, cancellationToken)
                .ConfigureAwait(true);
            History = points
                .OrderByDescending(point => point.TimestampUtc)
                .Select(point => new FleaHistoryPointViewModel(
                    point.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                    point.FleaPriceRoubles is { } flea ? Roubles(flea) : "—",
                    point.TraderValueRoubles is { } trader ? Roubles(trader) : "—",
                    point.Source))
                .ToArray();

            // One observation is the expected result on a fresh install: the sync records a
            // single point per item per run, so this fills in over days rather than at once.
            HistoryStatus = History.Count switch
            {
                0 => $"No stored observations for {item.Name} yet.",
                1 => $"1 observation of {item.Name} so far. More accrue each time data refreshes.",
                _ => $"{History.Count} observations of {item.Name} in the last {HistoryWindow.TotalDays:N0} days.",
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            History = [];
            HistoryStatus = $"Could not read stored prices: {exception.Message}";
        }
    }

    private static string DescribeValuePerSlot(ItemDefinition item, ItemPriceSnapshot? price)
    {
        if (price is null)
        {
            return "Value per slot unavailable";
        }

        var perSlot = item.ValuePerSlot(price);
        return perSlot > 0 ? $"{Roubles(perSlot)} per slot" : "Value per slot unavailable";
    }

    private static string DescribeBand(ItemPriceSnapshot? price)
    {
        if (price?.Low24HourRoubles is not { } low || price.High24HourRoubles is not { } high)
        {
            return "No 24-hour range recorded upstream.";
        }

        var average = price.Average24HourRoubles is { } value ? $" · average {Roubles(value)}" : string.Empty;
        return $"24h {Roubles(low)} to {Roubles(high)}{average}";
    }

    private static string DescribeBestSale(ItemPriceSnapshot? price)
    {
        if (price is null)
        {
            return "No price is cached for this item.";
        }

        var best = price.BestEconomicValue;
        if (best <= 0)
        {
            return "No sale value is cached for this item.";
        }

        var trader = price.BestTrader;
        return trader is null
            ? $"Best {Roubles(best)} on the flea"
            : $"Best {Roubles(best)} · {price.BestSaleChannel} ({trader.TraderName})";
    }

    private static string Roubles(long value) => value.ToString("N0", CultureInfo.CurrentCulture) + " ₽";

    private static string Describe(DateTimeOffset? timestamp) =>
        timestamp is { } value ? value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "no timestamp";
}
