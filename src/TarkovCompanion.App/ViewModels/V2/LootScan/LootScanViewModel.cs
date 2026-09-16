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
            .Select(decision => new LootScanDecisionViewModel(decision, result.EvaluatedUtc, openEvidence, _culture, _text))
            .ToArray();
        Issues = result.Issues.Select(issue => issue.Explanation).Distinct(StringComparer.Ordinal).ToArray();
        PreviousPageCommand = new DelegateCommand(PreviousPage);
        NextPageCommand = new DelegateCommand(NextPage);
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
    public IReadOnlyList<LootScanDecisionViewModel> VisibleDecisions => Decisions
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

    public int PageCount => Math.Max(1, (Decisions.Count + DecisionsPerPage - 1) / DecisionsPerPage);

    public bool HasMultiplePages => PageCount > 1;

    public bool HasPreviousPage => _pageIndex > 0;

    public bool HasNextPage => _pageIndex + 1 < PageCount;

    public string PageSummary => Format(
        _text.PageTemplate,
        ("page", (_pageIndex + 1).ToString(_culture)),
        ("pages", PageCount.ToString(_culture)),
        ("items", Decisions.Count.ToString(_culture)));

    public bool IsComplete =>
        Result.Status.Completeness == ResultCompleteness.Complete &&
        Result.Status.Freshness == FreshnessState.Current;

    public bool IsPartial =>
        Result.Status.Completeness == ResultCompleteness.Partial ||
        Result.Status.Freshness != FreshnessState.Current;

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

public sealed class LootScanDecisionViewModel
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
    }

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

            var field = recommendation.OpportunityCostRoubles;
            return recommendation.OpportunityCostLineage is { } lineage
                ? Message(
                    _text.OpportunityCostLineageTemplate,
                    ("value", Roubles(value)),
                    ("evidence", DescribeProvenance(field.Provenance)),
                    ("priceEvidence", DescribeProvenance(lineage.Price)),
                    ("footprintEvidence", DescribeProvenance(lineage.Footprint)))
                : Message(
                    _text.OpportunityCostTemplate,
                    ("value", Roubles(value)),
                    ("evidence", DescribeProvenance(field.Provenance)));
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

    public static LootScanPresentationText Default { get; } = new();
}
