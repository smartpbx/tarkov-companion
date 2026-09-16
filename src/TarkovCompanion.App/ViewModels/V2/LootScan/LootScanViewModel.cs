using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels;
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
    private int _pageIndex;

    public LootScanViewModel(
        LootScanResult result,
        Action<LootScanDecision>? openEvidence = null,
        CultureInfo? culture = null)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        _culture = culture ?? CultureInfo.CurrentCulture;
        Decisions = result.Decisions
            .Select(decision => new LootScanDecisionViewModel(decision, result.EvaluatedUtc, openEvidence, _culture))
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

    public ICommand PreviousPageCommand { get; }

    public ICommand NextPageCommand { get; }

    public string Heading => "Loot Scan";

    public string StatusLabel => Result.Status.Completeness switch
    {
        ResultCompleteness.Complete => "Ready to review",
        ResultCompleteness.Partial => "Review needed",
        _ => "No usable result",
    };

    public string StatusDetail => Result.Status.Completeness switch
    {
        ResultCompleteness.Complete => "Every recommendation is tied to the reviewed screenshot and visible carried space.",
        ResultCompleteness.Partial => "Uncertain cells stay visible and are never promoted into a take or swap.",
        _ => "Retake the screenshot with both the loot and carried grid visible.",
    };

    public string ContextLabel
    {
        get
        {
            var parts = new[] { Result.Context.ActiveMap, Result.Context.ActiveProfile }
                .Where(value => !string.IsNullOrWhiteSpace(value));
            var context = string.Join(" • ", parts);
            return string.IsNullOrEmpty(context) ? "Current capture context" : context;
        }
    }

    public string CaptureLabel => $"Capture {Result.CorrelationId} • decode {Result.DecodeRevision.ToString(_culture)}";

    public string TimingLabel
    {
        get
        {
            var elapsed = Result.Timings.Sum(stage => stage.ElapsedMilliseconds);
            return elapsed < 1000
                ? $"{elapsed.ToString(_culture)} ms"
                : $"{(elapsed / 1000d).ToString("0.0", _culture)} s";
        }
    }

    public string FocusLabel => $"Result returns to {Result.FocusDeviceId}";

    public int TakeCount => Decisions.Count(item => item.IsTake);

    public int SwapCount => Decisions.Count(item => item.IsSwap);

    public int LeaveCount => Decisions.Count(item => item.IsLeave);

    public int ReviewCount => Decisions.Count(item => item.IsReview);

    public string TakeSummary => $"{TakeCount.ToString(_culture)} take";

    public string SwapSummary => $"{SwapCount.ToString(_culture)} swap";

    public string LeaveSummary => $"{LeaveCount.ToString(_culture)} leave";

    public string ReviewSummary => $"{ReviewCount.ToString(_culture)} review";

    public bool HasDecisions => Decisions.Count > 0;

    public bool HasIssues => Issues.Count > 0;

    public bool HasHiddenIssues => Issues.Count > VisibleIssueLimit;

    public string HiddenIssuesLabel =>
        $"+{(Issues.Count - VisibleIssueLimit).ToString(_culture)} more limitations. Open evidence for item-level detail.";

    public int PageCount => Math.Max(1, (Decisions.Count + DecisionsPerPage - 1) / DecisionsPerPage);

    public bool HasMultiplePages => PageCount > 1;

    public bool HasPreviousPage => _pageIndex > 0;

    public bool HasNextPage => _pageIndex + 1 < PageCount;

    public string PageSummary =>
        $"Page {(_pageIndex + 1).ToString(_culture)} of {PageCount.ToString(_culture)} • {Decisions.Count.ToString(_culture)} items";

    public bool IsComplete => Result.Status.Completeness == ResultCompleteness.Complete;

    public bool IsPartial => Result.Status.Completeness == ResultCompleteness.Partial;

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
    }
}

public sealed class LootScanDecisionViewModel
{
    private readonly LootScanDecision _decision;
    private readonly CultureInfo _culture;

    public LootScanDecisionViewModel(
        LootScanDecision decision,
        DateTimeOffset evaluatedUtc,
        Action<LootScanDecision>? openEvidence,
        CultureInfo? culture = null)
    {
        _decision = decision ?? throw new ArgumentNullException(nameof(decision));
        _culture = culture ?? CultureInfo.CurrentCulture;
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
                $"Item at {_decision.SourceAnchor.Row + 1},{_decision.SourceAnchor.Column + 1}";
        }
    }

    public string VerdictLabel => _decision.Verdict.ToString().ToUpperInvariant();

    public string AutomationSummary => $"{VerdictLabel}: {Name}. {WhyLabel}";

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
                ? $"{width.ToString(_culture)}×{height.ToString(_culture)} • {(width * height).ToString(_culture)} squares"
                : "Footprint needs review";
        }
    }

    public string AttributesLabel
    {
        get
        {
            var item = _decision.Item.Value;
            if (item is null)
            {
                return "Identity unresolved";
            }

            var parts = new List<string>();
            if (item.Quantity.Value is { } quantity)
            {
                parts.Add($"stack {quantity.ToString(_culture)}");
            }

            if (item.FoundInRaid.Value is { } foundInRaid)
            {
                parts.Add(foundInRaid ? "found in raid" : "not found in raid");
            }

            if (item.Condition.Value is { } condition && condition.Kind != ItemConditionKind.NotApplicable)
            {
                parts.Add(condition.Current is { } current && condition.Maximum is { } maximum
                    ? $"condition {current.ToString("0.#", _culture)}/{maximum.ToString("0.#", _culture)}"
                    : $"condition {condition.Kind.ToString().ToLowerInvariant()}");
            }

            return parts.Count == 0 ? "No stack or condition detail" : string.Join(" • ", parts);
        }
    }

    public string ValueLabel => _decision.Economics?.BestNetValueRoubles is { } value
        ? $"{value.ToString("N0", _culture)} ₽ net"
        : "Value needs review";

    public string ValuePerSquareLabel => _decision.Economics?.ValuePerSquareRoubles is { } value
        ? $"{value.ToString("N0", _culture)} ₽ / square • {_decision.Economics.ValueBand?.ToString().ToLowerInvariant()}"
        : "Value per square unavailable";

    public string PriceBasisLabel => _decision.Economics?.SelectedPriceBasis switch
    {
        "flea-net" => "Flea net after fee",
        "trader" => "Best trader value",
        _ => "Price source needs review",
    };

    public string PlacementLabel
    {
        get
        {
            if (_decision.Placement is not { } placement)
            {
                return string.Empty;
            }

            var rotation = placement.RotateFromObserved ? " • rotate" : string.Empty;
            return $"Place at row {(placement.Anchor.Row + 1).ToString(_culture)}, column {(placement.Anchor.Column + 1).ToString(_culture)}{rotation}";
        }
    }

    public bool HasPlacement => _decision.Placement is not null;

    public string SwapLabel => _decision.Verdict == LootScanVerdict.Swap
        ? $"Replace {_decision.Drops.Count.ToString(_culture)} item(s) • {_decision.ReplacementCostRoubles.GetValueOrDefault().ToString("N0", _culture)} ₽ given up"
        : string.Empty;

    public bool HasSwap => _decision.Verdict == LootScanVerdict.Swap;

    public string EvidenceLabel
    {
        get
        {
            var provenance = _decision.Item.Provenance;
            var age = provenance.EvidenceThroughUtc > EvaluatedUtc
                ? "timestamp ahead"
                : DescribeAge(EvaluatedUtc - provenance.EvidenceThroughUtc);
            var confidence = provenance.Confidence.Score is { } score
                ? score.ToString("P0", _culture)
                : "unscored";
            return $"{provenance.SourceClass} • {age} • {confidence} confidence";
        }
    }

    public string CandidateLabel => _decision.Item.Candidates.Count switch
    {
        0 => "Identity matched",
        1 => "1 alternate identity",
        var count => $"{count.ToString(_culture)} alternate identities",
    };

    public string EvidenceActionLabel => !CanOpenEvidence
        ? "Evidence unavailable"
        : IsReview
            ? "Review / correct"
            : "Open evidence";

    private string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{Math.Floor(age.TotalMinutes).ToString(_culture)} min old";
        }

        return age < TimeSpan.FromDays(1)
            ? $"{Math.Floor(age.TotalHours).ToString(_culture)} h old"
            : $"{Math.Floor(age.TotalDays).ToString(_culture)} d old";
    }
}
