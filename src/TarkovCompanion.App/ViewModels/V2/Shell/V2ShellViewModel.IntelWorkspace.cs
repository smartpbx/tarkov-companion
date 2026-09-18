using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Intel;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>Which kind of catalog item a result-list chip keeps.</summary>
public enum V2IntelKindFilter
{
    All,
    Items,
    Ammo,
    Keys,
}

/// <summary>One kind chip above the Intel results.</summary>
public sealed class V2IntelKindFilterViewModel(V2IntelKindFilter kind, Action<V2IntelKindFilter> select) : BindableViewModel
{
    private bool _isSelected;

    public V2IntelKindFilter Kind { get; } = kind;
    public string Label => V2ShellText.Get($"V2.Shell.Intel.Filter.{Kind}");
    public string AutomationId => $"v2-intel-filter-{Kind.ToString().ToLowerInvariant()}";
    public ICommand SelectCommand { get; } = new DelegateCommand(() => select(kind));

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>One search result row: name, category and best price, selectable into the detail pane.</summary>
public sealed record V2IntelResultRowViewModel(
    string ItemId,
    string Name,
    string ShortName,
    string Category,
    string PriceLabel,
    bool IsSelected,
    ICommand OpenCommand)
{
    public string AutomationId => $"v2-intel-result-{ItemId}";
}

/// <summary>One line of the need summary in the context panel.</summary>
public sealed record V2IntelNeedLineViewModel(string Text, bool IsActive);

/// <summary>One sale channel beside the others, with its share of the best price for the bar.</summary>
public sealed record V2IntelPriceSourceViewModel(string Name, string ValueLabel, double Share, bool IsAvailable, bool IsBest);

/// <summary>
/// V2 rough package 17: presentation for the Intel workspace (docs/design/v2/v2-intel-workspace-concept.png).
/// </summary>
/// <remarks>
/// Every value here comes from state this shell already owned before the workspace existed: the
/// search runs through the same V1 item search (<c>Legacy.Items</c>), selection is the router's
/// item address, and the detail is the <see cref="IItemIntelService"/> result the Intel card
/// already loaded. Nothing is estimated here; a fact the catalog lacks reads as missing.
/// </remarks>
public sealed partial class V2ShellViewModel
{
    private V2IntelKindFilter _intelKindFilter = V2IntelKindFilter.All;
    private IReadOnlyList<V2IntelKindFilterViewModel>? _intelKindFilters;

    /// <summary>The search route, and the item route drawn as a selection inside it.</summary>
    public bool ShowsIntelWorkspace =>
        Registry[Router.Current.Location.Route].Content == V2RouteContent.IntelWorkspace ||
        Router.Current.Location.Route == V2Routes.Item;

    /// <summary>The compact Intel card, for an item opened beside some other page.</summary>
    public bool ShowsIntelCard => ShowsIntel && !ShowsIntelWorkspace;

    public string IntelSearchPlaceholder => V2ShellText.Get("V2.Shell.Intel.SearchPlaceholder");
    public string IntelClearSearchLabel => V2ShellText.Get("V2.Shell.Intel.ClearSearch");
    public ICommand ClearIntelSearchCommand => _clearIntelSearch ??= new DelegateCommand(() => SearchText = string.Empty);
    private ICommand? _clearIntelSearch;
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    public IReadOnlyList<V2IntelKindFilterViewModel> IntelKindFilters => _intelKindFilters ??= CreateIntelKindFilters();

    public IReadOnlyList<V2IntelResultRowViewModel> IntelResults
    {
        get
        {
            if (Legacy is null)
            {
                return [];
            }

            var selected = IntelItem;
            return Legacy.Items.Results
                .Where(result => MatchesKind(result.Category, _intelKindFilter))
                .Select(result =>
                {
                    var automationId = $"v2-intel-result-{result.Id}";
                    return new V2IntelResultRowViewModel(
                        result.Id,
                        result.Name,
                        result.ShortName,
                        CategoryLabel(result.Category),
                        result.BestValueRoubles is { } roubles
                            ? Roubles(roubles)
                            : V2ShellText.Get("V2.Shell.Intel.NoPrice"),
                        string.Equals(result.Id, selected, StringComparison.Ordinal),
                        new DelegateCommand(() => OpenSuggestedItem(result.Id, automationId)));
                })
                .ToArray();
        }
    }

    public bool HasIntelResults => IntelResults.Count > 0;

    /// <summary>Whether anything has been searched; until then the list offers suggestions instead.</summary>
    public bool HasIntelSearchResults => Legacy?.Items.Results.Count > 0;
    public bool ShowsIntelSuggestionList => !HasIntelSearchResults;
    public bool ShowsIntelNoKindMatch => HasIntelSearchResults && !HasIntelResults;
    public string IntelNoKindMatchLabel => V2ShellText.Get("V2.Shell.Intel.NoKindMatch");

    public string IntelResultCountLabel => IntelResults.Count == 1
        ? V2ShellText.Get("V2.Shell.Intel.OneResult")
        : V2ShellText.Format("V2.Shell.Intel.Results", CultureInfo.CurrentCulture, IntelResults.Count);

    /// <summary>The search's own status line (no data yet, nothing matched, failed), when it has one worth showing.</summary>
    public string IntelSearchStatus => Legacy is { } legacy && legacy.Items.Results.Count == 0 &&
        !string.IsNullOrWhiteSpace(legacy.Items.SearchQuery)
            ? legacy.Items.SearchStatus
            : string.Empty;
    public bool HasIntelSearchStatus => IntelSearchStatus.Length > 0;

    public bool HasIntelSelection => IntelItem.Length > 0;
    public bool ShowsIntelNoSelection => !HasIntelSelection;
    public string IntelNoSelectionLabel => V2ShellText.Get("V2.Shell.Intel.NoSelection");
    public bool IntelHasResult => _intelResult is { Kind: not V2IntelKind.Unknown };
    public bool ShowsIntelDetailStatus => HasIntelSelection && !IntelHasResult;

    /// <summary>
    /// Whether the context column has anything to put in it: a resolved item, or one on its way.
    /// </summary>
    /// <remarks>
    /// V2 rough package 30 (acceptance sweep): every card in that column is bound to a resolved
    /// item, so before one arrives the column is 460px of nothing down the right of the window.
    /// Selection alone is not enough — a deep link to an item this install's catalog has never
    /// heard of stays selected and unresolved, and that is the state the sweep photographed.
    /// </remarks>
    public bool ShowsIntelContextPanel => IntelHasResult || (HasIntelSelection && IntelIsLoading);

    public string IntelName => _intelResult is { Kind: not V2IntelKind.Unknown } result ? result.Name : string.Empty;

    public string IntelSubtitle => _intelResult is { Kind: not V2IntelKind.Unknown } result
        ? string.Join(
            "  |  ",
            new[]
            {
                CategoryLabel(result.Category.ToString()),
                V2ShellText.Format("V2.Shell.Intel.Slots", CultureInfo.CurrentCulture, result.Width, result.Height),
                result.ShortName,
            }.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct(StringComparer.Ordinal))
        : string.Empty;

    public string IntelItemDescription => _intelResult?.Description ?? string.Empty;
    public bool HasIntelItemDescription => IntelHasResult && !string.IsNullOrWhiteSpace(IntelItemDescription);
    public string IntelKeyInfoHeading => V2ShellText.Get("V2.Shell.Intel.KeyInfo");
    public string IntelCloseLabel => V2ShellText.Get("V2.Shell.Intel.Close");

    /// <summary>The item's footprint as grid cells, the one picture of it the catalog can draw honestly.</summary>
    public IReadOnlyList<int> IntelSlotCells => _intelResult is { Kind: not V2IntelKind.Unknown } result
        ? Enumerable.Range(0, Math.Clamp(result.Width, 1, 10) * Math.Clamp(result.Height, 1, 10)).ToArray()
        : [];
    public int IntelSlotColumns => _intelResult is { Kind: not V2IntelKind.Unknown } result ? Math.Clamp(result.Width, 1, 10) : 1;
    public string IntelSlotsLabel => _intelResult is { Kind: not V2IntelKind.Unknown } result
        ? V2ShellText.Format("V2.Shell.Intel.Slots", CultureInfo.CurrentCulture, result.Width, result.Height)
        : string.Empty;

    // Need summary: the context panel's headline card.
    public int IntelKeepCount => _intelResult?.Value is { } value ? value.OutstandingItems + value.HideoutCount : 0;
    public bool IntelIsNeeded => _intelResult?.Value is { } value &&
        (value.OutstandingItems > 0 || value.HideoutCount > 0 || value.QuestsNeedingIt > 0);
    public string IntelVerdictHeadline => !IntelHasResult
        ? string.Empty
        : IntelKeepCount > 0
            ? V2ShellText.Format("V2.Shell.Intel.Verdict.Keep", CultureInfo.CurrentCulture, IntelKeepCount)
            : IntelIsNeeded
                ? V2ShellText.Get("V2.Shell.Intel.Verdict.KeepSome")
                : V2ShellText.Get("V2.Shell.Intel.Verdict.NoNeed");

    public IReadOnlyList<V2IntelNeedLineViewModel> IntelNeedLines
    {
        get
        {
            if (_intelResult is not { Kind: not V2IntelKind.Unknown } result)
            {
                return [];
            }

            var value = result.Value;
            var tracked = value?.TrackedQuestsNeedingIt ?? 0;
            var later = Math.Max(0, (value?.QuestsNeedingIt ?? 0) - tracked);
            var hideout = value?.HideoutCount ?? 0;
            var lines = new List<V2IntelNeedLineViewModel>
            {
                new(V2ShellText.Format("V2.Shell.Intel.Need.Tracked", CultureInfo.CurrentCulture, tracked), tracked > 0),
                new(V2ShellText.Format("V2.Shell.Intel.Need.Later", CultureInfo.CurrentCulture, later), later > 0),
                new(V2ShellText.Format("V2.Shell.Intel.Need.Hideout", CultureInfo.CurrentCulture, hideout), hideout > 0),
            };
            if (value?.OutstandingFoundInRaidItems is > 0 and var foundInRaid)
            {
                lines.Add(new(V2ShellText.Format("V2.Shell.Intel.Need.FoundInRaid", CultureInfo.CurrentCulture, foundInRaid), true));
            }

            return lines;
        }
    }

    public string IntelBestSaleLabel => _intelResult?.Value is { SaleChannelLabel: { } channel, ValueRoubles: not null }
        ? V2ShellText.Format("V2.Shell.Intel.BestSale", CultureInfo.CurrentCulture, ChannelName(channel))
        : V2ShellText.Get("V2.Shell.Intel.NoPrice");

    // Prices card.
    public string IntelPricesHeading => V2ShellText.Get("V2.Shell.Intel.Prices");
    public bool IntelHasPrices => _intelResult?.Prices is not null;
    public bool IntelHasNoPrices => IntelHasResult && !IntelHasPrices;
    public string IntelNoPricesLabel => V2ShellText.Get("V2.Shell.Intel.NoPrices");
    public string IntelFleaPriceLabel => _intelResult?.Prices?.FleaRoubles is { } flea
        ? Roubles(flea)
        : V2ShellText.Get("V2.Shell.Intel.NotOnFlea");
    public string IntelFleaCaption => V2ShellText.Get("V2.Shell.Intel.FleaMarket");
    public string IntelPriceUpdatedLabel => _intelResult?.Prices is { } prices
        ? V2ShellText.Format(
            "V2.Shell.Intel.PriceUpdated",
            CultureInfo.CurrentCulture,
            ApproximateAgeOrJustNow(_clock.GetUtcNow() - prices.UpdatedUtc))
        : string.Empty;
    public bool IntelHasTraderPrice => _intelResult?.Prices?.Traders.Count > 0;
    public string IntelTraderPriceLabel => _intelResult?.Prices?.Traders.FirstOrDefault() is { } best
        ? Roubles(best.ValueRoubles)
        : string.Empty;
    public string IntelTraderCaption => _intelResult?.Prices?.Traders.FirstOrDefault()?.TraderName ?? string.Empty;
    public string IntelTraderDetail => V2ShellText.Get("V2.Shell.Intel.BestTrader");

    public bool IntelHas24HourRange => _intelResult?.Prices is { Low24HourRoubles: not null, High24HourRoubles: not null };
    public string Intel24HourLabel => V2ShellText.Get("V2.Shell.Intel.Last24Hours");
    public string Intel24HourRange => _intelResult?.Prices is { Low24HourRoubles: { } low, High24HourRoubles: { } high } prices
        ? prices.Average24HourRoubles is { } average
            ? V2ShellText.Format("V2.Shell.Intel.RangeWithAverage", CultureInfo.CurrentCulture, Roubles(low), Roubles(high), Roubles(average))
            : V2ShellText.Format("V2.Shell.Intel.Range", CultureInfo.CurrentCulture, Roubles(low), Roubles(high))
        : string.Empty;

    public string IntelSourcesHeading => V2ShellText.Get("V2.Shell.Intel.Sources");

    /// <summary>Flea and every trader side by side, each bar a share of the best of them.</summary>
    public IReadOnlyList<V2IntelPriceSourceViewModel> IntelPriceSources
    {
        get
        {
            if (_intelResult is not { Kind: not V2IntelKind.Unknown, Prices: { } prices } result)
            {
                return [];
            }

            var best = Math.Max(prices.FleaRoubles ?? 0, prices.Traders.Count > 0 ? prices.Traders.Max(trader => trader.ValueRoubles) : 0);
            var rows = new List<V2IntelPriceSourceViewModel>();
            if (prices.FleaRoubles is { } flea)
            {
                rows.Add(new(V2ShellText.Get("V2.Shell.Intel.FleaMarket"), Roubles(flea), Share(flea, best), true, flea == best));
            }
            else
            {
                rows.Add(new(
                    V2ShellText.Get("V2.Shell.Intel.FleaMarket"),
                    V2ShellText.Get(result.FleaEligible ? "V2.Shell.Intel.Unavailable" : "V2.Shell.Intel.NotOnFlea"),
                    0,
                    false,
                    false));
            }

            rows.AddRange(prices.Traders.Select(trader => new V2IntelPriceSourceViewModel(
                trader.TraderName,
                Roubles(trader.ValueRoubles),
                Share(trader.ValueRoubles, best),
                true,
                trader.ValueRoubles == best && prices.FleaRoubles != best)));
            return rows;
        }
    }

    // Kind-specific card: what a key opens, or an ammo's ballistics.
    public bool IntelIsKey => _intelResult?.Kind == V2IntelKind.Key;
    public bool IntelIsAmmo => _intelResult?.Kind == V2IntelKind.Ammo;
    public string IntelOpensHeading => V2ShellText.Get("V2.Shell.Intel.Opens");
    public string IntelKeyMapLabel => _intelResult?.Key?.MapId ?? V2ShellText.Get("V2.Shell.Intel.OpensUnknown");
    public IReadOnlyList<string> IntelKeyLocks => _intelResult?.Key?.Locks ?? [];
    public string IntelBallisticsHeading => V2ShellText.Get("V2.Shell.Intel.Ballistics");
    public bool IntelHasAmmoFacts => _intelResult?.Ammo is not null;
    public bool IntelHasNoAmmoFacts => IntelIsAmmo && !IntelHasAmmoFacts;
    public string IntelAmmoUnknownLabel => V2ShellText.Get("V2.Shell.Intel.AmmoUnknown");
    public string IntelAmmoDamage => _intelResult?.Ammo?.Damage.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
    public string IntelAmmoPenetration => _intelResult?.Ammo?.Penetration.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
    public string IntelAmmoTier => _intelResult?.Ammo?.Tier ?? string.Empty;
    public string IntelAmmoAdvice => _intelResult?.Ammo?.PracticalAdvice ?? string.Empty;
    public string IntelDamageLabel => V2ShellText.Get("V2.Shell.Intel.Damage");
    public string IntelPenetrationLabel => V2ShellText.Get("V2.Shell.Intel.Penetration");
    public string IntelTierLabel => V2ShellText.Get("V2.Shell.Intel.Tier");

    private IReadOnlyList<V2IntelKindFilterViewModel> CreateIntelKindFilters()
    {
        var filters = Enum.GetValues<V2IntelKindFilter>()
            .Select(kind => new V2IntelKindFilterViewModel(kind, SelectIntelKindFilter))
            .ToArray();
        foreach (var filter in filters)
        {
            filter.IsSelected = filter.Kind == _intelKindFilter;
        }

        return filters;
    }

    private void SelectIntelKindFilter(V2IntelKindFilter kind)
    {
        _intelKindFilter = kind;
        foreach (var filter in IntelKindFilters)
        {
            filter.IsSelected = filter.Kind == kind;
        }

        RaiseIntelWorkspaceChanged();
    }

    private void RaiseIntelWorkspaceChanged()
    {
        foreach (var property in new[]
        {
            nameof(ShowsIntelWorkspace), nameof(ShowsIntelCard), nameof(HasSearchText),
            nameof(IntelResults), nameof(HasIntelResults), nameof(HasIntelSearchResults), nameof(ShowsIntelSuggestionList),
            nameof(ShowsIntelNoKindMatch), nameof(IntelResultCountLabel), nameof(IntelSearchStatus), nameof(HasIntelSearchStatus),
            nameof(HasIntelSelection), nameof(ShowsIntelNoSelection), nameof(IntelHasResult), nameof(ShowsIntelDetailStatus),
            nameof(ShowsIntelContextPanel),
            nameof(IntelName), nameof(IntelSubtitle), nameof(IntelItemDescription), nameof(HasIntelItemDescription),
            nameof(IntelSlotCells), nameof(IntelSlotColumns), nameof(IntelSlotsLabel),
            nameof(IntelKeepCount), nameof(IntelIsNeeded), nameof(IntelVerdictHeadline), nameof(IntelNeedLines),
            nameof(IntelBestSaleLabel), nameof(IntelHasPrices), nameof(IntelHasNoPrices), nameof(IntelFleaPriceLabel),
            nameof(IntelPriceUpdatedLabel), nameof(IntelHasTraderPrice), nameof(IntelTraderPriceLabel),
            nameof(IntelTraderCaption), nameof(IntelHas24HourRange), nameof(Intel24HourRange), nameof(IntelPriceSources),
            nameof(IntelIsKey), nameof(IntelIsAmmo), nameof(IntelKeyMapLabel), nameof(IntelKeyLocks),
            nameof(IntelHasAmmoFacts), nameof(IntelHasNoAmmoFacts), nameof(IntelAmmoDamage), nameof(IntelAmmoPenetration),
            nameof(IntelAmmoTier), nameof(IntelAmmoAdvice),
        })
        {
            OnPropertyChanged(property);
        }
    }

    private static bool MatchesKind(string category, V2IntelKindFilter kind) => kind switch
    {
        V2IntelKindFilter.Ammo => category is "Ammunition" or "AmmunitionPack",
        V2IntelKindFilter.Keys => category is "Key",
        V2IntelKindFilter.Items => category is not ("Ammunition" or "AmmunitionPack" or "Key"),
        _ => true,
    };

    private static string CategoryLabel(string category) => category switch
    {
        "AmmunitionPack" => V2ShellText.Get("V2.Shell.Intel.Category.AmmunitionPack"),
        "Unknown" => V2ShellText.Get("V2.Shell.Intel.Category.Unknown"),
        _ => category,
    };

    private static string ChannelName(string channel) => channel == "Flea"
        ? V2ShellText.Get("V2.Shell.Intel.FleaMarket")
        : channel;

    private static string Roubles(long roubles) =>
        V2ShellText.Format("V2.Shell.Intel.Roubles", CultureInfo.CurrentCulture, roubles);

    private static double Share(long value, long best) => best <= 0 ? 0 : Math.Clamp((double)value / best, 0, 1);

    private static string ApproximateAgeOrJustNow(TimeSpan age) => age < TimeSpan.FromMinutes(1)
        ? V2ShellText.Get("V2.Shell.Intel.JustNow")
        : age < TimeSpan.FromDays(2)
            ? V2ShellText.Format("V2.Shell.Intel.Ago", CultureInfo.CurrentCulture, FormatApproximateAge(age))
            : V2ShellText.Format("V2.Shell.Intel.Days", CultureInfo.CurrentCulture, (int)age.TotalDays);
}
