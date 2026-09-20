using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Items;

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

/// <summary>How the result list is ordered; relevance is the search's own ranking.</summary>
public enum V2IntelSort
{
    Relevance,
    Price,
    PerSlot,
    Name,
}

/// <summary>One sort chip under the Intel kind chips.</summary>
public sealed class V2IntelSortViewModel(V2IntelSort sort, Action<V2IntelSort> select) : BindableViewModel
{
    private bool _isSelected;

    public V2IntelSort Sort { get; } = sort;
    public string Label => V2ShellText.Get($"V2.Shell.Intel.Sort.{Sort}");
    public string AutomationId => $"v2-intel-sort-{Sort.ToString().ToLowerInvariant()}";
    public ICommand SelectCommand { get; } = new DelegateCommand(() => select(sort));

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>One search result row: name, category, size and best price, selectable into the detail pane.</summary>
/// <param name="PerSlotLabel">The value of one stash slot of it, where a price is known.</param>
/// <param name="MatchLabel">Which text matched, only when that was not the item's own name.</param>
public sealed record V2IntelResultRowViewModel(
    string ItemId,
    string Name,
    string ShortName,
    string Category,
    string PriceLabel,
    bool IsSelected,
    ICommand OpenCommand,
    string Size = "",
    string PerSlotLabel = "",
    string MatchLabel = "")
{
    public string AutomationId => $"v2-intel-result-{ItemId}";
    public string Subtitle => string.IsNullOrEmpty(Size) ? Category : $"{Category} · {Size}";
    public bool HasPerSlot => PerSlotLabel.Length > 0;
    public bool HasMatchLabel => MatchLabel.Length > 0;
}

/// <summary>One armor class's verdict for an ammo round, for the strip in the ballistics card.</summary>
public sealed record V2IntelArmorClassViewModel(string ClassLabel, string Rating, bool IsStrong, bool IsMarginal, bool IsWeak);

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
    private V2IntelSort _intelSort = V2IntelSort.Relevance;
    private IReadOnlyList<V2IntelSortViewModel>? _intelSorts;

    // Package 33 (#287): the Intel landing page's four real sections (needed now, pinned,
    // recently opened, highest value), loaded once and kept fresh on a timer rather than on
    // every property read — each row is resolved through the same Intel service the detail pane
    // uses, which is not free.
    private readonly IIntelLandingService _intelLanding;
    private static readonly TimeSpan IntelLandingRefreshInterval = TimeSpan.FromSeconds(30);
    private IntelLandingSnapshot? _intelLandingSnapshot;
    private string? _intelLandingKey;
    private DateTimeOffset _intelLandingLoadedUtc = DateTimeOffset.MinValue;
    private bool _intelLandingLoading;
    private bool _intelLandingAutoSelected;
    private CancellationTokenSource? _intelLandingCts;

    /// <summary>
    /// How many hits an Intel search keeps. V1's cards fit a dozen; this list scrolls, and a
    /// kind chip filters the hits it has, so a dozen left "Keys" empty for a search that had
    /// keys past the twelfth.
    /// </summary>
    internal const int IntelResultLimit = 40;

    /// <summary>The search route, and the item route drawn as a selection inside it.</summary>
    public bool ShowsIntelWorkspace =>
        Registry[Router.Current.Location.Route].Content == V2RouteContent.IntelWorkspace ||
        Router.Current.Location.Route == V2Routes.Item;

    /// <summary>The compact Intel card, for an item opened beside some other page.</summary>
    public bool ShowsIntelCard => ShowsIntel && !ShowsIntelWorkspace;

    public string IntelSearchPlaceholder => V2ShellText.Get("V2.Shell.Intel.SearchPlaceholder");
    public string IntelClearSearchLabel => V2ShellText.Get("V2.Shell.Intel.ClearSearch");
    public ICommand ClearIntelSearchCommand => _clearIntelSearch ??= new DelegateCommand(ClearIntelSearch);
    private ICommand? _clearIntelSearch;

    /// <summary>
    /// Clears the box and the results it produced, back to the landing page's own sections.
    /// </summary>
    /// <remarks>
    /// Before this, the button only emptied <see cref="SearchText"/> — the box the player types
    /// into — and left <c>Legacy.Items.Results</c> exactly as the last search left it, so the list
    /// still showed the old hits under an empty search box with no way back to "Needed now" short
    /// of typing something else.
    /// </remarks>
    private void ClearIntelSearch()
    {
        SearchText = string.Empty;
        if (Legacy is null || Legacy.Items.SearchQuery.Length == 0)
        {
            return;
        }

        Legacy.Items.SearchQuery = string.Empty;
        Legacy.Items.SearchCommand.Execute(null);
        RaiseIntelWorkspaceChanged();
    }
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    public IReadOnlyList<V2IntelKindFilterViewModel> IntelKindFilters => _intelKindFilters ??= CreateIntelKindFilters();

    public IReadOnlyList<V2IntelSortViewModel> IntelSorts => _intelSorts ??= CreateIntelSorts();
    public string IntelSortHeading => V2ShellText.Get("V2.Shell.Intel.SortHeading");

    public IReadOnlyList<V2IntelResultRowViewModel> IntelResults
    {
        get
        {
            if (Legacy is null)
            {
                return [];
            }

            var selected = IntelItem;
            return SortResults(
                    Legacy.Items.Results.Where(result => MatchesKind(result.Category, _intelKindFilter)),
                    _intelSort)
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
                        new DelegateCommand(() => OpenSuggestedItem(result.Id, automationId)),
                        result.Size,
                        result.ValuePerSlotRoubles is { } perSlot
                            ? V2ShellText.Format("V2.Shell.Intel.PerSlot", CultureInfo.CurrentCulture, perSlot)
                            : string.Empty,
                        MatchNote(result));
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
    //
    // Package 33 (#287): the quest half of this used to read result.Value — QuestsNeedingIt,
    // TrackedQuestsNeedingIt, OutstandingFoundInRaidItems — which comes from
    // ProfileNeedAggregationService's cached quest-requirement snapshot. Every item tried against
    // the seed database read zero quest need from it, including one (Salewa first aid kit, seeded
    // active on "Shortage") verified by direct SQL to have a real requirement; the hideout half of
    // that exact snapshot was correct for the same items (Bolts: 63, matches the named list
    // below). Rather than ship a headline that visibly disagrees with the named "Should I keep it"
    // list under it, the quest figures here read Keep, which asks the quest board directly and is
    // the same source that list is built from. Hideout stays on Value, which this render evidence
    // shows is not the broken half. Reported to Clayton; not fixed here (outside Intel's owned
    // paths).
    public int IntelKeepCount =>
        (_intelResult?.Keep?.Quests.Sum(row => row.Remaining ?? 0) ?? 0) + (_intelResult?.Value?.HideoutCount ?? 0);
    public bool IntelIsNeeded => IntelKeepCount > 0 || _intelResult?.Keep?.Quests.Count > 0;
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

            var tracked = result.Keep?.Quests.Count ?? 0;
            var hideout = result.Value?.HideoutCount ?? 0;
            var lines = new List<V2IntelNeedLineViewModel>
            {
                new(V2ShellText.Format("V2.Shell.Intel.Need.Tracked", CultureInfo.CurrentCulture, tracked), tracked > 0),
                new(V2ShellText.Format("V2.Shell.Intel.Need.Hideout", CultureInfo.CurrentCulture, hideout), hideout > 0),
            };
            if (result.Keep?.Quests.Where(row => row.FoundInRaidRequired).Sum(row => row.Remaining ?? 0) is > 0 and var foundInRaid)
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

    // Package 33 (#287): "what is it worth" also asks for a per-slot value and the flea fee.
    public string IntelPerSlotHeading => V2ShellText.Get("V2.Shell.Intel.PerSlotHeading");
    public bool HasIntelPerSlotValue => _intelResult is { Kind: not V2IntelKind.Unknown, Width: > 0, Height: > 0 } result &&
        result.Value?.ValueRoubles is > 0;
    public string IntelPerSlotValueLabel => _intelResult is { Kind: not V2IntelKind.Unknown, Width: > 0, Height: > 0 } result &&
        result.Value?.ValueRoubles is { } value
        ? Roubles(value / (result.Width * result.Height))
        : string.Empty;

    public string IntelFeeHeading => V2ShellText.Get("V2.Shell.Intel.Fee");
    public string IntelFeeLabel => _intelResult?.Prices?.FeeRoubles is { } fee
        ? Roubles(fee)
        : V2ShellText.Get("V2.Shell.Intel.FeeUnknown");

    // Package 33 (#287): "should I keep it," named rather than counted — which quests and which
    // hideout levels, not just how many.
    public string IntelKeepHeading => V2ShellText.Get("V2.Shell.Intel.Keep.Heading");
    public string IntelKeepEmptyLabel => V2ShellText.Get("V2.Shell.Intel.Keep.None");

    public IReadOnlyList<string> IntelKeepQuestLines => _intelResult?.Keep?.Quests
        .Select(row => row.Remaining is { } remaining
            ? V2ShellText.Format(
                row.FoundInRaidRequired ? "V2.Shell.Intel.Keep.QuestFoundInRaid" : "V2.Shell.Intel.Keep.Quest",
                CultureInfo.CurrentCulture,
                row.TaskName,
                remaining)
            : row.TaskName)
        .ToArray() ?? [];

    public IReadOnlyList<string> IntelKeepHideoutLines => _intelResult?.Keep?.Hideout
        .Select(row => V2ShellText.Format(
            "V2.Shell.Intel.Keep.Hideout",
            CultureInfo.CurrentCulture,
            row.StationName,
            row.TargetLevel,
            row.Remaining))
        .ToArray() ?? [];

    public bool HasIntelKeepDetail => IntelKeepQuestLines.Count > 0 || IntelKeepHideoutLines.Count > 0;
    public bool ShowsIntelKeepEmpty => IntelHasResult && _intelResult?.Keep is not null && !HasIntelKeepDetail;

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
    public string IntelArmorClassesHeading => V2ShellText.Get("V2.Shell.Intel.ArmorClasses");

    /// <summary>Classes one to six, each rated by the same penetration comparison the Ammo page uses.</summary>
    public IReadOnlyList<V2IntelArmorClassViewModel> IntelArmorClasses
    {
        get
        {
            if (_intelResult?.Ammo is not { } ammo)
            {
                return [];
            }

            return Enumerable.Range(1, 6)
                .Select(armorClass => ammo.ArmorClassRatings.TryGetValue(armorClass, out var rating)
                    ? new V2IntelArmorClassViewModel(
                        armorClass.ToString(CultureInfo.CurrentCulture),
                        rating.ToString(),
                        rating is ArmorEffectiveness.Excellent or ArmorEffectiveness.Good,
                        rating is ArmorEffectiveness.Fair or ArmorEffectiveness.Limited,
                        rating is ArmorEffectiveness.Poor)
                    : new V2IntelArmorClassViewModel(armorClass.ToString(CultureInfo.CurrentCulture), "–", false, false, false))
                .ToArray();
        }
    }
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

    private IReadOnlyList<V2IntelSortViewModel> CreateIntelSorts()
    {
        var sorts = Enum.GetValues<V2IntelSort>()
            .Select(sort => new V2IntelSortViewModel(sort, SelectIntelSort))
            .ToArray();
        foreach (var sort in sorts)
        {
            sort.IsSelected = sort.Sort == _intelSort;
        }

        return sorts;
    }

    private void SelectIntelSort(V2IntelSort sort)
    {
        _intelSort = sort;
        foreach (var chip in IntelSorts)
        {
            chip.IsSelected = chip.Sort == sort;
        }

        RaiseIntelWorkspaceChanged();
    }

    /// <summary>Orders hits without disturbing relevance: LINQ's ordering is stable, so ties keep the search's own order.</summary>
    internal static IEnumerable<ItemSearchResultViewModel> SortResults(IEnumerable<ItemSearchResultViewModel> results, V2IntelSort sort) => sort switch
    {
        V2IntelSort.Price => results.OrderByDescending(result => result.BestValueRoubles ?? -1),
        V2IntelSort.PerSlot => results.OrderByDescending(result => result.ValuePerSlotRoubles ?? -1),
        V2IntelSort.Name => results.OrderBy(result => result.Name, StringComparer.CurrentCultureIgnoreCase),
        _ => results,
    };

    private static string MatchNote(ItemSearchResultViewModel result) =>
        result.MatchedText.Length == 0 ||
        string.Equals(result.MatchedText, result.Name, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : V2ShellText.Format("V2.Shell.Intel.MatchedAs", CultureInfo.CurrentCulture, result.MatchedText);

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
            nameof(HasIntelPerSlotValue), nameof(IntelPerSlotValueLabel), nameof(IntelFeeLabel),
            nameof(IntelKeepQuestLines), nameof(IntelKeepHideoutLines), nameof(HasIntelKeepDetail), nameof(ShowsIntelKeepEmpty),
            nameof(IntelIsKey), nameof(IntelIsAmmo), nameof(IntelKeyMapLabel), nameof(IntelKeyLocks),
            nameof(IntelHasAmmoFacts), nameof(IntelHasNoAmmoFacts), nameof(IntelAmmoDamage), nameof(IntelAmmoPenetration),
            nameof(IntelAmmoTier), nameof(IntelAmmoAdvice), nameof(IntelArmorClasses),
            nameof(IntelHomeNeededNow), nameof(IntelHomePinned), nameof(IntelHomeRecent), nameof(IntelHomeHighestValue),
            nameof(HasIntelHomeNeededNow), nameof(HasIntelHomePinned), nameof(HasIntelHomeRecent), nameof(HasIntelHomeHighestValue),
            nameof(ShowsIntelHomeEmpty),
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

    // Package 33 (#287): the Intel landing page. Four real sections instead of the address-book
    // "Suggested" list, which had nothing to show for a fresh profile and one junk row ("Items ·
    // Opened recently · Items") for a used one — every navigation to the Items route itself, with
    // no item selected, was recorded as a "recently opened" address and printed back with the
    // route's own heading standing in for a name.
    public string IntelHomeNeededNowHeading => V2ShellText.Get("V2.Shell.Intel.Home.NeededNow");
    public string IntelHomePinnedHeading => V2ShellText.Get("V2.Shell.Intel.Home.Pinned");
    public string IntelHomeRecentHeading => V2ShellText.Get("V2.Shell.Intel.Home.Recent");
    public string IntelHomeHighestValueHeading => V2ShellText.Get("V2.Shell.Intel.Home.HighestValue");

    public IReadOnlyList<V2IntelResultRowViewModel> IntelHomeNeededNow => BuildHomeRows(_intelLandingSnapshot?.NeededNow);
    public IReadOnlyList<V2IntelResultRowViewModel> IntelHomePinned => BuildHomeRows(_intelLandingSnapshot?.Pinned);
    public IReadOnlyList<V2IntelResultRowViewModel> IntelHomeRecent => BuildHomeRows(_intelLandingSnapshot?.Recent);
    public IReadOnlyList<V2IntelResultRowViewModel> IntelHomeHighestValue => BuildHomeRows(_intelLandingSnapshot?.HighestValue);

    public bool HasIntelHomeNeededNow => IntelHomeNeededNow.Count > 0;
    public bool HasIntelHomePinned => IntelHomePinned.Count > 0;
    public bool HasIntelHomeRecent => IntelHomeRecent.Count > 0;
    public bool HasIntelHomeHighestValue => IntelHomeHighestValue.Count > 0;

    /// <summary>Nothing to show yet: a brand-new profile with an empty catalog still loading.</summary>
    public bool ShowsIntelHomeEmpty => _intelLandingSnapshot is not null &&
        !HasIntelHomeNeededNow && !HasIntelHomePinned && !HasIntelHomeRecent && !HasIntelHomeHighestValue;
    public string IntelHomeEmptyLabel => V2ShellText.Get("V2.Shell.Intel.NoSelection");

    private IReadOnlyList<V2IntelResultRowViewModel> BuildHomeRows(IReadOnlyList<IntelLandingRow>? rows)
    {
        if (rows is null || rows.Count == 0)
        {
            return [];
        }

        var selected = IntelItem;
        return rows.Select(row => BuildHomeRow(row, selected)).ToArray();
    }

    private V2IntelResultRowViewModel BuildHomeRow(IntelLandingRow row, string selected)
    {
        var automationId = $"v2-intel-home-{row.ItemId}";
        var matchLabel = row.Count > 0
            ? V2ShellText.Format("V2.Shell.Intel.Home.NeedCount", CultureInfo.CurrentCulture, row.Count)
            : row.SaleChannelLabel is { Length: > 0 } channel
                ? V2ShellText.Format("V2.Shell.Intel.Home.Via", CultureInfo.CurrentCulture, ChannelName(channel))
                : string.Empty;
        return new V2IntelResultRowViewModel(
            row.ItemId,
            row.Name,
            row.ShortName,
            CategoryLabel(row.Category),
            row.ValueRoubles is { } roubles ? Roubles(roubles) : V2ShellText.Get("V2.Shell.Intel.NoPrice"),
            string.Equals(row.ItemId, selected, StringComparison.Ordinal),
            new DelegateCommand(() => OpenSuggestedItem(row.ItemId, automationId)),
            MatchLabel: matchLabel);
    }

    /// <summary>
    /// Loads the landing snapshot once per distinct pins/recents fingerprint, and re-reads it
    /// every <see cref="IntelLandingRefreshInterval"/> regardless, so a quest pinned or an item
    /// obtained mid-session eventually shows up without a restart.
    /// </summary>
    private void RefreshIntelLandingIfNeeded()
    {
        if (!ShowsIntelWorkspace)
        {
            // Leaving Intel resets the one-shot auto-select below, so the next visit picks a
            // first suggestion again; staying and explicitly closing the detail pane does not.
            _intelLandingAutoSelected = false;
            return;
        }

        var key = string.Join('|', Pins.Take(6)) + "||" + string.Join('|', Recents.Take(6));
        var stale = _clock.GetUtcNow() - _intelLandingLoadedUtc > IntelLandingRefreshInterval;
        if (_intelLandingLoading || (_intelLandingSnapshot is not null && key == _intelLandingKey && !stale))
        {
            return;
        }

        _intelLandingCts?.Cancel();
        var cts = new CancellationTokenSource();
        _intelLandingCts = cts;
        _intelLandingKey = key;
        _intelLandingLoading = true;
        _ = LoadIntelLandingAsync(cts.Token);
    }

    private async Task LoadIntelLandingAsync(CancellationToken cancellationToken)
    {
        IntelLandingSnapshot snapshot;
        try
        {
            snapshot = await _intelLanding.GetAsync(
                ItemIdsFromAddresses(Pins, 8),
                ItemIdsFromAddresses(Recents, 8),
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            _intelLandingLoading = false;
            return;
        }

        if (cancellationToken.IsCancellationRequested || _disposed)
        {
            return;
        }

        _intelLandingSnapshot = snapshot;
        _intelLandingLoadedUtc = _clock.GetUtcNow();
        _intelLandingLoading = false;
        RaiseIntelWorkspaceChanged();
        AutoSelectFirstIntelHomeItem();
    }

    /// <summary>
    /// With nothing selected, the detail pane opens on the first real suggestion rather than a
    /// sentence — once per visit to Intel, so a deliberate Close afterward stays closed.
    /// </summary>
    private void AutoSelectFirstIntelHomeItem()
    {
        if (_intelLandingAutoSelected || HasIntelSelection || _intelLandingSnapshot is not { } snapshot)
        {
            return;
        }

        // The landing loads in the background, so it can finish after the player has started
        // typing. Opening a suggestion then would put an item nobody asked for beside a search that
        // found nothing — which is how this surfaced: "a search that finds nothing selects nothing"
        // failed in CI whenever the load lost the race. A search is a decision; the suggestion yields.
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            _intelLandingAutoSelected = true;
            return;
        }

        var first = snapshot.NeededNow.Concat(snapshot.Pinned).Concat(snapshot.Recent).Concat(snapshot.HighestValue)
            .FirstOrDefault();
        if (first is null)
        {
            return;
        }

        _intelLandingAutoSelected = true;
        Act(Router.OpenIntel(first.ItemId, invoker: null));
    }

    /// <summary>Saved addresses resolved to the item each one is actually open on, bare page visits dropped.</summary>
    private IReadOnlyList<string> ItemIdsFromAddresses(IReadOnlyList<string> addresses, int limit)
    {
        var ids = new List<string>(Math.Min(addresses.Count, limit));
        foreach (var address in addresses)
        {
            if (ids.Count >= limit)
            {
                break;
            }

            if (Router.Addresses.Parse(address).Location is not { } location)
            {
                continue;
            }

            var itemId = location.Item ?? location.IntelItem;
            if (itemId is not null)
            {
                ids.Add(itemId);
            }
        }

        return ids;
    }
}

/// <summary>The fallback used in tests that build the shell without composing the landing service.</summary>
internal sealed class NullIntelLandingService : IIntelLandingService
{
    public static readonly NullIntelLandingService Instance = new();

    public Task<IntelLandingSnapshot> GetAsync(
        IReadOnlyList<string> pinnedItemIds,
        IReadOnlyList<string> recentItemIds,
        CancellationToken cancellationToken) =>
        Task.FromResult(new IntelLandingSnapshot([], [], [], []));
}
