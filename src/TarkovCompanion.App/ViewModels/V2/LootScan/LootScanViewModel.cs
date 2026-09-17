using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.Views.V2.Primitives;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Loot;

namespace TarkovCompanion.App.ViewModels.V2.LootScan;

/// <summary>Dense, review-first presentation of a single immutable Loot Scan result.</summary>
public sealed class LootScanViewModel : BindableViewModel
{
    private const int DecisionsPerPage = 24;
    private const int VisibleIssueLimit = 8;

    private readonly CultureInfo _culture;
    private readonly LootScanPresentationText _text;
    private int _pageIndex;
    private LootScanVerdict? _filter;
    private LootScanDecisionViewModel? _selected;

    public LootScanViewModel(
        LootScanResult result,
        Action<LootScanDecision>? openEvidence = null,
        CultureInfo? culture = null,
        LootScanPresentationText? text = null)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        _culture = culture ?? CultureInfo.CurrentCulture;
        _text = text ?? LootScanPresentationText.Default;
        Decisions = result.Decisions
            .Select(decision => new LootScanDecisionViewModel(decision, result.EvaluatedUtc, openEvidence, _culture, _text)
            {
                SelectAction = Select,
            })
            .ToArray();
        Issues = result.Issues.Select(issue => issue.Explanation).Distinct(StringComparer.Ordinal).ToArray();
        PreviousPageCommand = new DelegateCommand(PreviousPage);
        NextPageCommand = new DelegateCommand(NextPage);
        ScanAgainCommand = new DelegateCommand(() => ScanAgainRequested?.Invoke(this, EventArgs.Empty));
        Filters =
        [
            new LootScanFilterViewModel(null, _text.FilterAll, Decisions.Count, SetFilter) { IsSelected = true },
            new LootScanFilterViewModel(LootScanVerdict.Take, _text.FilterTake, TakeCount, SetFilter),
            new LootScanFilterViewModel(LootScanVerdict.Swap, _text.FilterSwap, SwapCount, SetFilter),
            new LootScanFilterViewModel(LootScanVerdict.Leave, _text.FilterLeave, LeaveCount, SetFilter),
            new LootScanFilterViewModel(LootScanVerdict.Review, _text.FilterReview, ReviewCount, SetFilter),
        ];
        LootGrid = BuildLootGrid();
        CarriedGrid = BuildCarriedGrid();

        // The concept opens on its most consequential call: a swap first, then a take.
        Select(Decisions.FirstOrDefault(item => item.IsSwap) ??
               Decisions.FirstOrDefault(item => item.IsTake) ??
               Decisions.FirstOrDefault());
    }

    public LootScanResult Result { get; }

    public IReadOnlyList<LootScanDecisionViewModel> Decisions { get; }

    public IReadOnlyList<string> Issues { get; }

    /// <summary>
    /// The cards drawn for the current page. Rendering every recognized grid item at once made a
    /// maximum-size scan allocate hundreds of expanders and buttons before the first result could
    /// be read. Paging is deliberately fixed-size so the review cost stays bounded on desktop and
    /// on the paired tablet surface.
    /// </summary>
    public IReadOnlyList<LootScanDecisionViewModel> VisibleDecisions => FilteredDecisions
        .Skip(_pageIndex * DecisionsPerPage)
        .Take(DecisionsPerPage)
        .ToArray();

    public IReadOnlyList<string> VisibleIssues => Issues.Take(VisibleIssueLimit).ToArray();

    /// <summary>Requests the view to announce and reveal the new page after explicit navigation.</summary>
    public event EventHandler? PageNavigated;

    public ICommand PreviousPageCommand { get; }

    public ICommand NextPageCommand { get; }

    public string Heading => _text.Heading;

    public string StatusLabel => Result.Status.Freshness switch
    {
        FreshnessState.Stale => _text.StatusStale,
        FreshnessState.Unknown => _text.StatusUnknown,
        _ => Result.Status.Completeness switch
        {
            ResultCompleteness.Complete => _text.StatusComplete,
            ResultCompleteness.Partial => _text.StatusPartial,
            _ => _text.StatusUnavailable,
        },
    };

    public string StatusDetail => Result.Status.Freshness switch
    {
        FreshnessState.Stale => _text.StatusDetailStale,
        FreshnessState.Unknown => _text.StatusDetailUnknown,
        _ => Result.Status.Completeness switch
        {
            ResultCompleteness.Complete => _text.StatusDetailComplete,
            ResultCompleteness.Partial => _text.StatusDetailPartial,
            _ => _text.StatusDetailUnavailable,
        },
    };

    public string ContextLabel
    {
        get
        {
            var parts = new[] { Result.Context.ActiveMap, Result.Context.ActiveProfile }
                .Where(value => !string.IsNullOrWhiteSpace(value));
            var context = string.Join(" • ", parts);
            return string.IsNullOrEmpty(context) ? _text.CurrentCaptureContext : context;
        }
    }

    public string CaptureLabel => Format(
        _text.CaptureTemplate,
        ("correlation", Result.CorrelationId.ToString()),
        ("revision", Result.DecodeRevision.ToString(_culture)));

    public string TimingLabel
    {
        get
        {
            var elapsed = Result.Timings.Sum(stage => stage.ElapsedMilliseconds);
            return elapsed < 1000
                ? Format(_text.MillisecondsTemplate, ("value", elapsed.ToString(_culture)))
                : Format(_text.SecondsTemplate, ("value", (elapsed / 1000d).ToString("0.0", _culture)));
        }
    }

    public string FocusLabel => Format(_text.FocusTemplate, ("device", Result.FocusDeviceId));

    public int TakeCount => Decisions.Count(item => item.IsTake);

    public int SwapCount => Decisions.Count(item => item.IsSwap);

    public int LeaveCount => Decisions.Count(item => item.IsLeave);

    public int ReviewCount => Decisions.Count(item => item.IsReview);

    public string TakeSummary => Format(_text.TakeCountTemplate, ("count", TakeCount.ToString(_culture)));

    public string SwapSummary => Format(_text.SwapCountTemplate, ("count", SwapCount.ToString(_culture)));

    public string LeaveSummary => Format(_text.LeaveCountTemplate, ("count", LeaveCount.ToString(_culture)));

    public string ReviewSummary => Format(_text.ReviewCountTemplate, ("count", ReviewCount.ToString(_culture)));

    public bool HasDecisions => Decisions.Count > 0;

    public bool HasNoVisibleLoot =>
        Decisions.Count == 0 &&
        Result.Status.Completeness != ResultCompleteness.Unavailable &&
        Result.Issues.All(issue => issue.Kind is not (
            LootScanIssueKind.CaptureChanged or
            LootScanIssueKind.LootCoveragePartial));

    public bool HasUnavailableResult => !HasDecisions && !HasNoVisibleLoot;

    public bool HasIssues => Issues.Count > 0;

    public bool HasHiddenIssues => Issues.Count > VisibleIssueLimit;

    public string HiddenIssuesLabel => Format(
        _text.HiddenIssuesTemplate,
        ("count", (Issues.Count - VisibleIssueLimit).ToString(_culture)));

    public int PageCount => Math.Max(1, (FilteredDecisions.Count + DecisionsPerPage - 1) / DecisionsPerPage);

    public bool HasMultiplePages => PageCount > 1;

    public bool HasPreviousPage => _pageIndex > 0;

    public bool HasNextPage => _pageIndex + 1 < PageCount;

    public string PageSummary => Format(
        _text.PageTemplate,
        ("page", (_pageIndex + 1).ToString(_culture)),
        ("pages", PageCount.ToString(_culture)),
        ("items", FilteredDecisions.Count.ToString(_culture)));

    public bool IsComplete =>
        Result.Status.Completeness == ResultCompleteness.Complete &&
        Result.Status.Freshness == FreshnessState.Current;

    public bool IsPartial =>
        Result.Status.Completeness == ResultCompleteness.Partial ||
        Result.Status.Freshness != FreshnessState.Current;

    /// <summary>Asks whoever hosts this result to arm another loot capture (the shell's capture dialog).</summary>
    public event EventHandler? ScanAgainRequested;

    public ICommand ScanAgainCommand { get; }

    /// <summary>Verdict chips above the decision list; "All" is first and selected by default.</summary>
    public IReadOnlyList<LootScanFilterViewModel> Filters { get; }

    public LootScanVerdict? Filter => _filter;

    public LootScanGridViewModel? LootGrid { get; }

    public LootScanGridViewModel? CarriedGrid { get; }

    public bool HasLootGrid => LootGrid is not null;

    public bool HasNoLootGrid => LootGrid is null;

    public bool HasCarriedGrid => CarriedGrid is not null;

    public bool HasNoCarriedGrid => CarriedGrid is null;

    public string LootItemsLabel => Format(
        Decisions.Count == 1 ? _text.OneItemTemplate : _text.ItemsTemplate,
        ("count", Decisions.Count.ToString(_culture)));

    /// <summary>"Take 3 · Swap 1 · Leave 1", naming only the verdicts that occur.</summary>
    public string DecisionSummary
    {
        get
        {
            var parts = new[]
                {
                    (_text.FilterTake, TakeCount),
                    (_text.FilterSwap, SwapCount),
                    (_text.FilterLeave, LeaveCount),
                    (_text.FilterReview, ReviewCount),
                }
                .Where(part => part.Item2 > 0)
                .Select(part => $"{part.Item1} {part.Item2.ToString(_culture)}");
            var summary = string.Join(" · ", parts);
            return summary.Length == 0 ? _text.NothingToDecide : summary;
        }
    }

    public LootScanDecisionViewModel? SelectedDecision
    {
        get => _selected;
        private set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelectedDecision));
            }
        }
    }

    public bool HasSelectedDecision => SelectedDecision is not null;

    public string TimingSummary => Format(_text.AnalysedInTemplate, ("duration", TimingLabel));

    private IReadOnlyList<LootScanDecisionViewModel> FilteredDecisions => _filter is { } verdict
        ? Decisions.Where(item => item.Verdict == verdict).ToArray()
        : Decisions;

    public void Select(LootScanDecisionViewModel? decision)
    {
        if (decision is not null && !Decisions.Contains(decision))
        {
            return;
        }

        foreach (var item in Decisions)
        {
            item.IsSelected = ReferenceEquals(item, decision);
        }

        foreach (var tile in (LootGrid?.Tiles ?? []).Concat(CarriedGrid?.Tiles ?? []))
        {
            tile.IsSelected = tile.Decision is not null && ReferenceEquals(tile.Decision, decision);
        }

        SelectedDecision = decision;
    }

    private void SetFilter(LootScanVerdict? verdict)
    {
        _filter = verdict;
        foreach (var chip in Filters)
        {
            chip.IsSelected = chip.Verdict == verdict;
        }

        _pageIndex = 0;
        OnPropertyChanged(nameof(Filter));
        OnPropertyChanged(nameof(VisibleDecisions));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(HasMultiplePages));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(PageSummary));
    }

    private LootScanGridViewModel? BuildLootGrid()
    {
        var byAnchor = Decisions.ToDictionary(item => item.SourceAnchor);
        var tiles = new List<LootScanGridTileViewModel>();
        var grid = Result.VisibleLootGrid;
        if (grid is not null)
        {
            foreach (var cell in grid.Cells)
            {
                byAnchor.TryGetValue(cell.Anchor, out var decision);
                tiles.Add(LootTile(cell.Anchor, cell.Item.Value, decision));
            }
        }
        else
        {
            tiles.AddRange(Decisions.Select(decision => LootTile(decision.SourceAnchor, decision.Item, decision)));
        }

        return tiles.Count == 0 && grid is null
            ? null
            : new LootScanGridViewModel(grid?.Geometry.Rows.Value, grid?.Geometry.Columns.Value, tiles, occupied: null, _text, _culture);
    }

    private LootScanGridTileViewModel LootTile(GridCellAddress anchor, RecognizedItem? item, LootScanDecisionViewModel? decision) =>
        new(anchor, item?.WidthCells.Value ?? 1, item?.HeightCells.Value ?? 1,
            decision?.Name ?? item?.DisplayName.Value ?? _text.UnknownItem,
            decision?.ShortValueLabel ?? string.Empty,
            LootScanTileKind.Loot, decision);

    private LootScanGridViewModel? BuildCarriedGrid()
    {
        var grid = Result.CarriedGrid;
        var dropOwners = new Dictionary<GridCellAddress, LootScanDecisionViewModel>();
        foreach (var decision in Decisions)
        {
            foreach (var drop in decision.Drops)
            {
                dropOwners.TryAdd(drop.Anchor, decision);
            }
        }

        var tiles = new List<LootScanGridTileViewModel>();
        var occupied = 0;
        if (grid is not null)
        {
            foreach (var cell in grid.Cells)
            {
                var item = cell.Item.Value;
                var width = item?.WidthCells.Value ?? 1;
                var height = item?.HeightCells.Value ?? 1;
                occupied += width * height;
                var isDrop = dropOwners.TryGetValue(cell.Anchor, out var owner);
                tiles.Add(new(cell.Anchor, width, height,
                    item?.DisplayName.Value ?? _text.UnknownItem,
                    item?.Quantity.Value is > 1 and var quantity ? quantity.ToString(_culture) : string.Empty,
                    isDrop ? LootScanTileKind.Drop : LootScanTileKind.Carried,
                    owner));
            }
        }
        else
        {
            foreach (var decision in Decisions)
            {
                foreach (var drop in decision.Drops)
                {
                    tiles.Add(new(drop.Anchor, drop.Item.Value?.WidthCells.Value ?? 1, drop.Item.Value?.HeightCells.Value ?? 1,
                        drop.Item.Value?.DisplayName.Value ?? _text.UnknownItem, string.Empty, LootScanTileKind.Drop, decision));
                }
            }
        }

        // Where each take or swap would land, drawn over what it lands on.
        foreach (var decision in Decisions.Where(item => item.Placement is not null && (item.IsTake || item.IsSwap)))
        {
            var placement = decision.Placement!;
            tiles.Add(new(placement.Anchor, placement.WidthCells, placement.HeightCells,
                decision.Name, string.Empty, LootScanTileKind.Incoming, decision));
        }

        return tiles.Count == 0 && grid is null
            ? null
            : new LootScanGridViewModel(grid?.Geometry.Rows.Value, grid?.Geometry.Columns.Value, tiles,
                grid is null ? null : occupied, _text, _culture);
    }

    private void PreviousPage()
    {
        if (!HasPreviousPage)
        {
            return;
        }

        _pageIndex--;
        PageChanged();
    }

    private void NextPage()
    {
        if (!HasNextPage)
        {
            return;
        }

        _pageIndex++;
        PageChanged();
    }

    private void PageChanged()
    {
        OnPropertyChanged(nameof(VisibleDecisions));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(PageSummary));
        PageNavigated?.Invoke(this, EventArgs.Empty);
    }

    private static string Format(string template, params (string Key, string Value)[] values) =>
        V2PresentationFormatting.Message(
            template,
            values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
}

public sealed class LootScanDecisionViewModel : BindableViewModel
{
    private readonly LootScanDecision _decision;
    private readonly CultureInfo _culture;
    private readonly LootScanPresentationText _text;

    public LootScanDecisionViewModel(
        LootScanDecision decision,
        DateTimeOffset evaluatedUtc,
        Action<LootScanDecision>? openEvidence,
        CultureInfo? culture = null,
        LootScanPresentationText? text = null)
    {
        _decision = decision ?? throw new ArgumentNullException(nameof(decision));
        _culture = culture ?? CultureInfo.CurrentCulture;
        _text = text ?? LootScanPresentationText.Default;
        EvaluatedUtc = evaluatedUtc;
        OpenEvidenceCommand = new DelegateCommand(() => openEvidence?.Invoke(_decision));
        CanOpenEvidence = openEvidence is not null;
        SelectCommand = new DelegateCommand(() => SelectAction?.Invoke(this));
    }

    /// <summary>Set by the owning result so a row or grid tile can make this the selected decision.</summary>
    internal Action<LootScanDecisionViewModel>? SelectAction { get; init; }

    public ICommand SelectCommand { get; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public LootScanVerdict Verdict => _decision.Verdict;

    public GridCellAddress SourceAnchor => _decision.SourceAnchor;

    public RecognizedItem? Item => _decision.Item.Value;

    public LootScanPlacement? Placement => _decision.Placement;

    public IReadOnlyList<LootScanDropItem> Drops => _decision.Drops;

    /// <summary>
    /// One plain line under the item name: the strongest profile reason ("Current quest",
    /// "Hideout") when the recommendation names one, otherwise the planner's own first reason.
    /// </summary>
    public string HeadlineReason
    {
        get
        {
            // The planner's own reasons are full sentences meant for the evidence disclosure; a
            // row gets the short form of what actually decided it.
            var code = _decision.Reasons.FirstOrDefault()?.Code;
            var planner = code switch
            {
                "capacity.visible-fit" => _text.ReasonFits,
                "capacity.bounded-swap" => _text.ReasonSwapFits,
                "capacity.no-supported-fit" => _text.ReasonNoRoom,
                "swap.cost-exceeds-value" => _text.ReasonSwapCosts,
                _ => null,
            };
            var category = _decision.Recommendation?.Decision.Value?.Reasons
                .OrderBy(reason => reason.Priority)
                .Select(reason => (RecommendationReasonCategory?)reason.Category)
                .FirstOrDefault(value => value is not (RecommendationReasonCategory.Economics or RecommendationReasonCategory.EvidenceQuality));
            if (category is null && planner is not null)
            {
                return planner;
            }

            return category switch
            {
                RecommendationReasonCategory.ExplicitOverride => _text.ReasonExplicit,
                RecommendationReasonCategory.Safety => _text.ReasonProtected,
                RecommendationReasonCategory.CurrentFoundInRaidQuest => _text.ReasonCurrentQuestFir,
                RecommendationReasonCategory.CurrentQuest => _text.ReasonCurrentQuest,
                RecommendationReasonCategory.FutureQuest => _text.ReasonFutureQuest,
                RecommendationReasonCategory.Hideout => _text.ReasonHideout,
                RecommendationReasonCategory.CraftOrBarter => _text.ReasonCraft,
                RecommendationReasonCategory.SpecialistUtility => _text.ReasonUtility,
                RecommendationReasonCategory.PinOrWishlist => _text.ReasonPinned,
                RecommendationReasonCategory.ScarcityOrObtainability => _text.ReasonScarce,
                _ => planner ?? _text.ReasonValueOnly,
            };
        }
    }

    /// <summary>"₽68k": the whole item's value, short enough for a grid tile.</summary>
    public string ShortValueLabel => _decision.Economics?.BestNetValueRoubles is { } value
        ? CompactRoubles(value, _culture)
        : string.Empty;

    /// <summary>"₽68k / sq", or empty when the value per square is unknown.</summary>
    public string ShortValuePerSquareLabel => _decision.Economics?.ValuePerSquareRoubles is { } value
        ? Message(_text.ShortPerSquareTemplate, ("value", CompactRoubles(value, _culture)))
        : string.Empty;

    public bool HasShortValuePerSquare => ShortValuePerSquareLabel.Length > 0;

    /// <summary>What the selected swap gives up, as one line per carried item.</summary>
    public string DropSummary => _decision.Drops.Count == 0
        ? string.Empty
        : Message(
            _text.DropSummaryTemplate,
            ("items", string.Join(", ", _decision.Drops.Select(drop => ItemName(drop.Item, drop.Anchor)))),
            ("cost", CompactRoubles(_decision.ReplacementCostRoubles.GetValueOrDefault(), _culture)));

    public string ReplacementCostLabel => _decision.ReplacementCostRoubles is { } cost
        ? Message(_text.GivesUpTemplate, ("value", CompactRoubles(cost, _culture)))
        : string.Empty;

    internal static string CompactRoubles(long value, CultureInfo culture) => Math.Abs(value) switch
    {
        >= 1_000_000 => "₽" + (value / 1_000_000d).ToString("0.#", culture) + "M",
        >= 10_000 => "₽" + (value / 1_000d).ToString("0", culture) + "k",
        >= 1_000 => "₽" + (value / 1_000d).ToString("0.#", culture) + "k",
        _ => "₽" + value.ToString(culture),
    };

    public DateTimeOffset EvaluatedUtc { get; }

    public ICommand OpenEvidenceCommand { get; }

    public bool CanOpenEvidence { get; }

    public string AutomationId => $"v2-loot-scan-{_decision.SourceAnchor.Row}-{_decision.SourceAnchor.Column}";

    public string Name
    {
        get
        {
            var item = _decision.Item.Value;
            return item?.DisplayName.Value ?? item?.CanonicalId.Value ??
                Message(
                    _text.ItemAtTemplate,
                    ("row", (_decision.SourceAnchor.Row + 1).ToString(_culture)),
                    ("column", (_decision.SourceAnchor.Column + 1).ToString(_culture)));
        }
    }

    public string VerdictLabel => _decision.Verdict.ToString().ToUpperInvariant();

    public string AutomationSummary => Message(
        _text.AutomationSummaryTemplate,
        ("verdict", VerdictLabel),
        ("item", Name),
        ("reason", WhyLabel));

    public bool IsTake => _decision.Verdict == LootScanVerdict.Take;

    public bool IsSwap => _decision.Verdict == LootScanVerdict.Swap;

    public bool IsLeave => _decision.Verdict == LootScanVerdict.Leave;

    public bool IsReview => _decision.Verdict == LootScanVerdict.Review;

    public string WhyLabel => string.Join(" ", _decision.Reasons.Select(reason => reason.Explanation));

    public string SizeLabel
    {
        get
        {
            var item = _decision.Item.Value;
            return item?.WidthCells.Value is { } width && item.HeightCells.Value is { } height
                ? Message(
                    _text.SizeTemplate,
                    ("width", width.ToString(_culture)),
                    ("height", height.ToString(_culture)),
                    ("squares", (width * height).ToString(_culture)))
                : _text.FootprintNeedsReview;
        }
    }

    public string AttributesLabel
    {
        get
        {
            var item = _decision.Item.Value;
            if (item is null)
            {
                return _text.IdentityUnresolved;
            }

            var parts = new List<string>();
            if (item.Quantity.Value is { } quantity)
            {
                parts.Add(Message(_text.StackTemplate, ("quantity", quantity.ToString(_culture))));
            }

            if (item.FoundInRaid.Value is { } foundInRaid)
            {
                parts.Add(foundInRaid ? _text.FoundInRaid : _text.NotFoundInRaid);
            }

            if (item.Condition.Value is { } condition && condition.Kind != ItemConditionKind.NotApplicable)
            {
                parts.Add(condition.Current is { } current && condition.Maximum is { } maximum
                    ? Message(
                        _text.ConditionValuesTemplate,
                        ("current", current.ToString("0.#", _culture)),
                        ("maximum", maximum.ToString("0.#", _culture)))
                    : Message(
                        _text.ConditionKindTemplate,
                        ("kind", condition.Kind.ToString().ToLowerInvariant())));
            }

            return parts.Count == 0 ? _text.NoAttributeDetail : string.Join(" • ", parts);
        }
    }

    public string ValueLabel => _decision.Economics?.BestNetValueRoubles is { } value
        ? Message(_text.NetValueTemplate, ("value", Roubles(value)))
        : _text.ValueNeedsReview;

    public string ValuePerSquareLabel => _decision.Economics?.ValuePerSquareRoubles is { } value
        ? Message(
            _text.ValuePerSquareTemplate,
            ("value", Roubles(value)),
            ("band", _decision.Economics.ValueBand?.ToString().ToLowerInvariant() ?? _text.UnknownValueBand))
        : _text.ValuePerSquareUnavailable;

    public string PriceBasisLabel => _decision.Economics?.SelectedPriceBasis switch
    {
        "flea-net" => _text.FleaNetBasis,
        "trader" => _text.TraderBasis,
        _ => _text.PriceSourceNeedsReview,
    };

    public string PlacementLabel
    {
        get
        {
            if (_decision.Placement is not { } placement)
            {
                return string.Empty;
            }

            var template = placement.RotateFromObserved
                ? _text.PlacementRotatedTemplate
                : _text.PlacementTemplate;
            return Message(
                template,
                ("row", (placement.Anchor.Row + 1).ToString(_culture)),
                ("column", (placement.Anchor.Column + 1).ToString(_culture)));
        }
    }

    public bool HasPlacement => _decision.Placement is not null;

    public string SwapLabel => _decision.Verdict == LootScanVerdict.Swap
        ? Message(
            _text.SwapSummaryTemplate,
            ("count", _decision.Drops.Count.ToString(_culture)),
            ("cost", Roubles(_decision.ReplacementCostRoubles.GetValueOrDefault())))
        : string.Empty;

    public bool HasSwap => _decision.Verdict == LootScanVerdict.Swap;

    public IReadOnlyList<string> DropLabels => _decision.Drops
        .Select(drop => Message(
            _text.DropTemplate,
            ("item", ItemName(drop.Item, drop.Anchor)),
            ("row", (drop.Anchor.Row + 1).ToString(_culture)),
            ("column", (drop.Anchor.Column + 1).ToString(_culture)),
            ("cost", Roubles(drop.ReplacementValueRoubles)),
            ("evidence", DescribeProvenance(drop.ValueProvenance))))
        .ToArray();

    public IReadOnlyList<string> RecommendationReasonLabels =>
        _decision.Recommendation?.Decision.Value?.Reasons
            .Take(LootScanPlannerLimits.MaximumRecommendationReasons)
            .Select(reason => Message(
                _text.RecommendationReasonTemplate,
                ("category", reason.Category.ToString()),
                ("reason", reason.Explanation),
                ("code", reason.Code),
                ("evidence", DescribeProvenance(reason.Provenance))))
            .ToArray() ?? [];

    public bool HasRecommendationReasons => RecommendationReasonLabels.Count > 0;

    public string OpportunityCostLabel
    {
        get
        {
            var recommendation = _decision.Recommendation?.Decision.Value;
            if (recommendation is null || recommendation.OpportunityCostRoubles.Value is not { } value)
            {
                return _text.OpportunityCostUnavailable;
            }

            var opportunityCost = recommendation.OpportunityCostRoubles;
            return recommendation.OpportunityCostLineage is { } lineage
                ? Message(
                    _text.OpportunityCostLineageTemplate,
                    ("value", Roubles(value)),
                    ("evidence", DescribeProvenance(opportunityCost.Provenance)),
                    ("priceEvidence", DescribeProvenance(lineage.Price)),
                    ("footprintEvidence", DescribeProvenance(lineage.Footprint)))
                : Message(
                    _text.OpportunityCostTemplate,
                    ("value", Roubles(value)),
                    ("evidence", DescribeProvenance(opportunityCost.Provenance)));
        }
    }

    public string EconomicEvidenceLabel
    {
        get
        {
            if (_decision.Economics is not { } economics)
            {
                return _text.EconomicEvidenceUnavailable;
            }

            var price = economics.SelectedPriceBasis == "trader"
                ? economics.Inputs.TraderRoubles
                : economics.Inputs.FleaNetRoubles.Value is not null
                    ? economics.Inputs.FleaNetRoubles
                    : economics.Inputs.TraderRoubles;
            return economics.CalculationProvenance is { } calculation
                ? Message(
                    _text.EconomicEvidenceWithInputsTemplate,
                    ("basis", PriceBasisLabel),
                    ("evidence", DescribeProvenance(calculation)),
                    ("priceEvidence", DescribeProvenance(price.Provenance)),
                    ("footprintEvidence", DescribeProvenance(economics.Inputs.OccupiedSquares.Provenance)))
                : Message(
                    _text.EconomicInputsEvidenceTemplate,
                    ("status", economics.Status.Code ?? _text.UnknownEconomicStatus),
                    ("priceEvidence", DescribeProvenance(price.Provenance)),
                    ("footprintEvidence", DescribeProvenance(economics.Inputs.OccupiedSquares.Provenance)));
        }
    }

    public string EvidenceLabel
    {
        get
        {
            return DescribeProvenance(_decision.Item.Provenance);
        }
    }

    public string CandidateLabel => AlternateIdentityCount() switch
    {
        0 => _text.IdentityMatched,
        1 => _text.OneAlternateIdentity,
        var count => Message(_text.AlternateIdentitiesTemplate, ("count", count.ToString(_culture))),
    };

    public string EvidenceDisclosureName => Message(
        _text.EvidenceDisclosureTemplate,
        ("item", Name));

    public string EvidenceActionLabel => !CanOpenEvidence
        ? _text.EvidenceUnavailable
        : IsReview
            ? _text.ReviewOrCorrect
            : _text.OpenEvidence;

    private string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return _text.JustNow;
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Message(
                _text.MinutesOldTemplate,
                ("value", Math.Floor(age.TotalMinutes).ToString(_culture)));
        }

        return age < TimeSpan.FromDays(1)
            ? Message(
                _text.HoursOldTemplate,
                ("value", Math.Floor(age.TotalHours).ToString(_culture)))
            : Message(
                _text.DaysOldTemplate,
                ("value", Math.Floor(age.TotalDays).ToString(_culture)));
    }

    private string DescribeProvenance(EvidenceProvenance provenance)
    {
        var age = provenance.EvidenceThroughUtc > EvaluatedUtc
            ? _text.TimestampAhead
            : DescribeAge(EvaluatedUtc - provenance.EvidenceThroughUtc);
        var confidence = provenance.Confidence.Score is { } score
            ? score.ToString("P0", _culture)
            : _text.Unscored;
        return Message(
            _text.ProvenanceTemplate,
            ("source", provenance.SourceClass.ToString()),
            ("age", age),
            ("confidence", confidence));
    }

    private int AlternateIdentityCount()
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var current = _decision.Item.Value?.CanonicalId.Value;
        foreach (var candidate in _decision.Item.Candidates)
        {
            AddIdentity(candidate.Value.CanonicalId.Value);
            foreach (var nested in candidate.Value.CanonicalId.Candidates)
            {
                AddIdentity(nested.Value);
            }
        }

        if (_decision.Item.Value is { } item)
        {
            foreach (var candidate in item.CanonicalId.Candidates)
            {
                AddIdentity(candidate.Value);
            }
        }

        return identities.Count;

        void AddIdentity(string? identity)
        {
            if (!string.IsNullOrWhiteSpace(identity) && !string.Equals(identity, current, StringComparison.Ordinal))
            {
                identities.Add(identity);
            }
        }
    }

    private string ItemName(EvidencedValue<RecognizedItem> item, GridCellAddress anchor) =>
        item.Value?.DisplayName.Value ?? item.Value?.CanonicalId.Value ??
        Message(
            _text.ItemAtTemplate,
            ("row", (anchor.Row + 1).ToString(_culture)),
            ("column", (anchor.Column + 1).ToString(_culture)));

    private string Roubles(long value) =>
        V2PresentationFormatting.Currency(value, "RUB", 0, _culture);

    private static string Message(string template, params (string Key, string Value)[] values) =>
        V2PresentationFormatting.Message(
            template,
            values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
}

/// <summary>Whole-message resources used by the Loot Scan presentation adapter.</summary>
public sealed record LootScanPresentationText
{
    public string Heading { get; init; } = "Loot Scan";
    public string StatusStale { get; init; } = "Evidence expired — review needed";
    public string StatusUnknown { get; init; } = "Evidence freshness unknown";
    public string StatusComplete { get; init; } = "Ready to review";
    public string StatusPartial { get; init; } = "Review needed";
    public string StatusUnavailable { get; init; } = "No usable result";
    public string StatusDetailStale { get; init; } = "Retake or refresh the scan before relying on its recommendations.";
    public string StatusDetailUnknown { get; init; } = "The scan cannot prove when all supporting evidence was current.";
    public string StatusDetailComplete { get; init; } = "Every recommendation is tied to the reviewed screenshot and visible carried space.";
    public string StatusDetailPartial { get; init; } = "Uncertain cells stay visible and are never promoted into a take or swap.";
    public string StatusDetailUnavailable { get; init; } = "Retake the screenshot with both the loot and carried grid visible.";
    public string CurrentCaptureContext { get; init; } = "Current capture context";
    public string CaptureTemplate { get; init; } = "Capture {correlation} • decode {revision}";
    public string MillisecondsTemplate { get; init; } = "{value} ms";
    public string SecondsTemplate { get; init; } = "{value} s";
    public string FocusTemplate { get; init; } = "Result returns to {device}";
    public string TakeCountTemplate { get; init; } = "{count} take";
    public string SwapCountTemplate { get; init; } = "{count} swap";
    public string LeaveCountTemplate { get; init; } = "{count} leave";
    public string ReviewCountTemplate { get; init; } = "{count} review";
    public string HiddenIssuesTemplate { get; init; } = "+{count} more limitations. Open evidence for item-level detail.";
    public string PageTemplate { get; init; } = "Page {page} of {pages} • {items} items";
    public string AutomationSummaryTemplate { get; init; } = "{verdict}: {item}. {reason}";
    public string SizeTemplate { get; init; } = "{width}×{height} • {squares} squares";
    public string FootprintNeedsReview { get; init; } = "Footprint needs review";
    public string IdentityUnresolved { get; init; } = "Identity unresolved";
    public string StackTemplate { get; init; } = "stack {quantity}";
    public string FoundInRaid { get; init; } = "found in raid";
    public string NotFoundInRaid { get; init; } = "not found in raid";
    public string ConditionValuesTemplate { get; init; } = "condition {current}/{maximum}";
    public string ConditionKindTemplate { get; init; } = "condition {kind}";
    public string NoAttributeDetail { get; init; } = "No stack or condition detail";
    public string NetValueTemplate { get; init; } = "{value} net";
    public string ValueNeedsReview { get; init; } = "Value needs review";
    public string ValuePerSquareTemplate { get; init; } = "{value} per square • {band}";
    public string ValuePerSquareUnavailable { get; init; } = "Value per square unavailable";
    public string UnknownValueBand { get; init; } = "unknown band";
    public string FleaNetBasis { get; init; } = "Flea net after fee";
    public string TraderBasis { get; init; } = "Best trader value";
    public string PriceSourceNeedsReview { get; init; } = "Price source needs review";
    public string PlacementTemplate { get; init; } = "Place at row {row}, column {column}";
    public string PlacementRotatedTemplate { get; init; } = "Place at row {row}, column {column} • rotate";
    public string SwapSummaryTemplate { get; init; } = "Replace {count} item(s) • {cost} given up";
    public string DropTemplate { get; init; } = "Drop {item} at row {row}, column {column} ({cost}) • {evidence}";
    public string RecommendationReasonTemplate { get; init; } = "{category}: {reason} [{code}] • {evidence}";
    public string OpportunityCostTemplate { get; init; } = "Opportunity cost {value} • {evidence}";
    public string OpportunityCostLineageTemplate { get; init; } = "Opportunity cost {value} • calculation {evidence} • price {priceEvidence} • footprint {footprintEvidence}";
    public string OpportunityCostUnavailable { get; init; } = "Opportunity cost unavailable";
    public string EconomicEvidenceWithInputsTemplate { get; init; } = "{basis} • calculation {evidence} • price {priceEvidence} • footprint {footprintEvidence}";
    public string EconomicInputsEvidenceTemplate { get; init; } = "Economic evidence needs review [{status}] • price {priceEvidence} • footprint {footprintEvidence}";
    public string UnknownEconomicStatus { get; init; } = "unspecified";
    public string EconomicEvidenceUnavailable { get; init; } = "Economic calculation evidence unavailable";
    public string EvidenceDisclosureTemplate { get; init; } = "Evidence and alternatives for {item}";
    public string IdentityMatched { get; init; } = "Identity matched";
    public string OneAlternateIdentity { get; init; } = "1 alternate identity";
    public string AlternateIdentitiesTemplate { get; init; } = "{count} alternate identities";
    public string EvidenceUnavailable { get; init; } = "Evidence unavailable";
    public string ReviewOrCorrect { get; init; } = "Review / correct";
    public string OpenEvidence { get; init; } = "Open evidence";
    public string ProvenanceTemplate { get; init; } = "{source} • {age} • {confidence} confidence";
    public string TimestampAhead { get; init; } = "timestamp ahead";
    public string Unscored { get; init; } = "unscored";
    public string JustNow { get; init; } = "just now";
    public string MinutesOldTemplate { get; init; } = "{value} min old";
    public string HoursOldTemplate { get; init; } = "{value} h old";
    public string DaysOldTemplate { get; init; } = "{value} d old";
    public string ItemAtTemplate { get; init; } = "item at row {row}, column {column}";
    public string FilterAll { get; init; } = "All";
    public string FilterTake { get; init; } = "Take";
    public string FilterSwap { get; init; } = "Swap";
    public string FilterLeave { get; init; } = "Leave";
    public string FilterReview { get; init; } = "Review";
    public string NothingToDecide { get; init; } = "Nothing to decide";
    public string OneItemTemplate { get; init; } = "{count} item";
    public string ItemsTemplate { get; init; } = "{count} items";
    public string AnalysedInTemplate { get; init; } = "Analysed in {duration}";
    public string ShortPerSquareTemplate { get; init; } = "{value} / sq";
    public string GivesUpTemplate { get; init; } = "gives up {value}";
    public string DropSummaryTemplate { get; init; } = "Drop {items} · {cost} given up";
    public string GridSizeTemplate { get; init; } = "{columns} × {rows} squares";
    public string FreeSquaresTemplate { get; init; } = "{count} free squares";
    public string OneFreeSquare { get; init; } = "1 free square";
    public string UnknownItem { get; init; } = "Unknown item";
    public string ReasonExplicit { get; init; } = "Your own rule";
    public string ReasonProtected { get; init; } = "Protected item";
    public string ReasonCurrentQuestFir { get; init; } = "Current quest · found in raid";
    public string ReasonCurrentQuest { get; init; } = "Current quest";
    public string ReasonFutureQuest { get; init; } = "Future quest";
    public string ReasonHideout { get; init; } = "Hideout";
    public string ReasonCraft { get; init; } = "Craft or barter";
    public string ReasonUtility { get; init; } = "Useful gear";
    public string ReasonPinned { get; init; } = "Pinned";
    public string ReasonScarce { get; init; } = "Hard to find";
    public string ReasonValueOnly { get; init; } = "Value only";
    public string ReasonFits { get; init; } = "Fits the free space";
    public string ReasonSwapFits { get; init; } = "Fits after a swap";
    public string ReasonNoRoom { get; init; } = "No room for it";
    public string ReasonSwapCosts { get; init; } = "A swap would cost more than it gains";

    public static LootScanPresentationText Default { get; } = new();
}

/// <summary>A verdict chip over the decision list.</summary>
public sealed class LootScanFilterViewModel : BindableViewModel
{
    private bool _isSelected;

    public LootScanFilterViewModel(LootScanVerdict? verdict, string label, int count, Action<LootScanVerdict?> select)
    {
        Verdict = verdict;
        Label = label;
        Count = count;
        SelectCommand = new DelegateCommand(() => select(verdict));
    }

    public LootScanVerdict? Verdict { get; }

    public string Label { get; }

    public int Count { get; }

    public string CountLabel => Count.ToString(CultureInfo.CurrentCulture);

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public enum LootScanTileKind
{
    /// <summary>An item in the container, coloured by its verdict.</summary>
    Loot = 1,

    /// <summary>Something already carried that no decision touches.</summary>
    Carried,

    /// <summary>A carried item a swap would drop.</summary>
    Drop,

    /// <summary>Where a take or swap would put the new item.</summary>
    Incoming,
}

/// <summary>
/// One reviewed grid (the container, or the carried backpack) laid out on a fixed square size.
/// The view scales the whole grid down to fit; positions stay in grid units times
/// <see cref="CellSize"/> so tiles and the cell pattern line up at any scale.
/// </summary>
public sealed class LootScanGridViewModel
{
    public const double CellSize = 80;

    public LootScanGridViewModel(
        int? rows,
        int? columns,
        IReadOnlyList<LootScanGridTileViewModel> tiles,
        int? occupied,
        LootScanPresentationText text,
        CultureInfo culture)
    {
        Tiles = tiles;
        var extentRows = tiles.Select(tile => tile.Row + tile.HeightCells).DefaultIfEmpty(1).Max();
        var extentColumns = tiles.Select(tile => tile.Column + tile.WidthCells).DefaultIfEmpty(1).Max();
        Rows = Math.Max(rows ?? extentRows, extentRows);
        Columns = Math.Max(columns ?? extentColumns, extentColumns);
        SizeKnown = rows is not null && columns is not null;
        SizeLabel = SizeKnown
            ? V2PresentationFormatting.Message(text.GridSizeTemplate, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["columns"] = Columns.ToString(culture),
                ["rows"] = Rows.ToString(culture),
            })
            : string.Empty;
        if (SizeKnown && occupied is { } used)
        {
            FreeSquares = Math.Max(0, (Rows * Columns) - used);
            FreeSquaresLabel = FreeSquares == 1
                ? text.OneFreeSquare
                : V2PresentationFormatting.Message(text.FreeSquaresTemplate, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["count"] = FreeSquares.Value.ToString(culture),
                });
        }
    }

    public int Rows { get; }

    public int Columns { get; }

    public bool SizeKnown { get; }

    public string SizeLabel { get; }

    public int? FreeSquares { get; }

    public string FreeSquaresLabel { get; } = string.Empty;

    public bool HasFreeSquares => FreeSquaresLabel.Length > 0;

    public double PixelWidth => Columns * CellSize;

    public double PixelHeight => Rows * CellSize;

    /// <summary>How far the view may scale a small grid up before its squares stop reading as squares.</summary>
    public double MaxPixelWidth => PixelWidth * 1.4;

    public IReadOnlyList<LootScanGridTileViewModel> Tiles { get; }
}

public sealed class LootScanGridTileViewModel : BindableViewModel
{
    private const double Gap = 2;
    private bool _isSelected;

    public LootScanGridTileViewModel(
        GridCellAddress anchor,
        int widthCells,
        int heightCells,
        string name,
        string detail,
        LootScanTileKind kind,
        LootScanDecisionViewModel? decision)
    {
        Row = anchor.Row;
        Column = anchor.Column;
        WidthCells = Math.Max(1, widthCells);
        HeightCells = Math.Max(1, heightCells);
        Name = name;
        Detail = detail;
        Kind = kind;
        Decision = decision;
        SelectCommand = new DelegateCommand(() => decision?.SelectCommand.Execute(null));
    }

    public int Row { get; }

    public int Column { get; }

    public int WidthCells { get; }

    public int HeightCells { get; }

    public double Left => (Column * LootScanGridViewModel.CellSize) + Gap;

    public double Top => (Row * LootScanGridViewModel.CellSize) + Gap;

    public double Width => (WidthCells * LootScanGridViewModel.CellSize) - (2 * Gap);

    public double Height => (HeightCells * LootScanGridViewModel.CellSize) - (2 * Gap);

    public string Name { get; }

    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    public LootScanTileKind Kind { get; }

    public LootScanDecisionViewModel? Decision { get; }

    public bool CanSelect => Decision is not null;

    public ICommand SelectCommand { get; }

    public bool IsTake => Kind == LootScanTileKind.Loot && Decision?.IsTake == true;

    public bool IsSwap => Kind == LootScanTileKind.Loot && Decision?.IsSwap == true;

    public bool IsLeave => Kind == LootScanTileKind.Loot && Decision?.IsLeave == true;

    public bool IsReview => Kind == LootScanTileKind.Loot && (Decision is null || Decision.IsReview);

    public bool IsCarried => Kind == LootScanTileKind.Carried;

    public bool IsDrop => Kind == LootScanTileKind.Drop;

    public bool IsIncoming => Kind == LootScanTileKind.Incoming;

    /// <summary>
    /// Where a take or swap would land is drawn over what is already carried, so only the
    /// selected call's placement, and the carried items it gives up, are marked; drawing every
    /// call's placement at once stacked four labels on the same squares.
    /// </summary>
    public bool ShowsTile => Kind != LootScanTileKind.Incoming || IsSelected;

    public bool IsGivenUp => IsDrop && IsSelected;

    public string AutomationName => HasDetail ? $"{Name}, {Detail}" : Name;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(ShowsTile));
                OnPropertyChanged(nameof(IsGivenUp));
            }
        }
    }
}
