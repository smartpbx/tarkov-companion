using System.ComponentModel;
using System.Windows.Input;

namespace TarkovCompanion.App.ViewModels.V2.Intel;

/// <summary>One flea lookup row: the price first, then where it sells best and what a slot is worth.</summary>
public sealed record FleaListRowViewModel(FleaPriceViewModel Price, bool IsSelected, ICommand SelectCommand)
{
    public string Name => Price.Name;

    /// <summary>The name, with the short name after it when that says something the name does not.</summary>
    public string Title => Price.ShortName.Length == 0 || string.Equals(Price.ShortName, Price.Name, StringComparison.OrdinalIgnoreCase)
        ? Price.Name
        : $"{Price.Name} · {Price.ShortName}";

    public string FleaPrice => Price.FleaPrice;

    public string BestSale => Price.BestSale;

    public string DailyBand => Price.DailyBand;

    public string ValuePerSlot => Price.ValuePerSlot;

    public string Dimensions => Price.Dimensions;

    public string AutomationId => $"v2-flea-row-{Price.ItemId}";
}

/// <summary>
/// V2 Flea workspace: what an item is worth and where it sells best, the locally stored
/// observations of its price, and the offers the game reported sold while the player was busy.
/// </summary>
/// <remarks>
/// An adapter over <see cref="FleaPageViewModel"/>, which owns the search, the price rows, the
/// history read and the sale notifications. Nothing is re-derived here: the price history is the
/// page's own local observations and the sales are the game's own notifications, so both keep the
/// page's honesty about how little a fresh install has of either.
/// </remarks>
public sealed class FleaWorkspaceViewModel : BindableViewModel
{
    private readonly FleaPageViewModel _page;
    private readonly Action<string>? _openItem;

    public FleaWorkspaceViewModel(FleaPageViewModel page, Action<string>? openItem = null)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _openItem = openItem;
        OpenInIntelCommand = new DelegateCommand(() =>
        {
            if (_page.Selected is { } selected)
            {
                _openItem?.Invoke(selected.ItemId);
            }
        });
        _page.PropertyChanged += PageChanged;
    }

    public ICommand SearchCommand => _page.SearchCommand;

    public ICommand OpenInIntelCommand { get; }

    public string SearchQuery
    {
        get => _page.SearchQuery;
        set => _page.SearchQuery = value;
    }

    public string SearchStatus => _page.SearchStatus;

    public IReadOnlyList<FleaListRowViewModel> Results
    {
        get
        {
            var selected = _page.Selected;
            return
            [
                .. _page.Results.Select(price => new FleaListRowViewModel(
                    price,
                    ReferenceEquals(price, selected),
                    new DelegateCommand(() => _page.Selected = price))),
            ];
        }
    }

    public bool HasResults => _page.Results.Count > 0;

    public bool ShowsNoResults => !HasResults;

    public bool HasSelection => _page.Selected is not null;

    public string SelectedName => _page.Selected?.Name ?? string.Empty;

    public string SelectedPrice => _page.Selected?.FleaPrice ?? string.Empty;

    public string SelectedBestSale => _page.Selected?.BestSale ?? string.Empty;

    public string SelectedBand => _page.Selected?.DailyBand ?? string.Empty;

    public string SelectedValuePerSlot => _page.Selected?.ValuePerSlot ?? string.Empty;

    public string SelectedProvenance => _page.Selected?.Provenance ?? string.Empty;

    public string HistoryStatus => _page.HistoryStatus;

    public IReadOnlyList<FleaHistoryPointViewModel> History => _page.History;

    public bool HasHistory => _page.History.Count > 0;

    public string SalesStatus => _page.SalesStatus;

    public IReadOnlyList<FleaSaleViewModel> Sales => _page.Sales;

    public bool HasSales => _page.HasSales;

    private void PageChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        switch (eventArgs.PropertyName)
        {
            case nameof(FleaPageViewModel.Results):
                OnPropertyChanged(nameof(Results));
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ShowsNoResults));
                break;
            case nameof(FleaPageViewModel.Selected):
                OnPropertyChanged(nameof(Results));
                foreach (var name in new[]
                {
                    nameof(HasSelection), nameof(SelectedName), nameof(SelectedPrice), nameof(SelectedBestSale),
                    nameof(SelectedBand), nameof(SelectedValuePerSlot), nameof(SelectedProvenance),
                })
                {
                    OnPropertyChanged(name);
                }

                break;
            case nameof(FleaPageViewModel.SearchQuery):
                OnPropertyChanged(nameof(SearchQuery));
                break;
            case nameof(FleaPageViewModel.SearchStatus):
                OnPropertyChanged(nameof(SearchStatus));
                break;
            case nameof(FleaPageViewModel.HistoryStatus):
                OnPropertyChanged(nameof(HistoryStatus));
                break;
            case nameof(FleaPageViewModel.History):
                OnPropertyChanged(nameof(History));
                OnPropertyChanged(nameof(HasHistory));
                break;
            case nameof(FleaPageViewModel.SalesStatus):
                OnPropertyChanged(nameof(SalesStatus));
                break;
            case nameof(FleaPageViewModel.Sales):
                OnPropertyChanged(nameof(Sales));
                OnPropertyChanged(nameof(HasSales));
                break;
        }
    }
}
