using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Loot;

namespace TarkovCompanion.App.ViewModels.V2.LootScan;

/// <summary>Dense, review-first presentation of a single immutable Loot Scan result.</summary>
public sealed class LootScanViewModel
{
    public LootScanViewModel(LootScanResult result, Action<LootScanDecision>? openEvidence = null)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        Decisions = result.Decisions
            .Select(decision => new LootScanDecisionViewModel(decision, result.EvaluatedUtc, openEvidence))
            .ToArray();
        Issues = result.Issues.Select(issue => issue.Explanation).Distinct(StringComparer.Ordinal).ToArray();
    }

    public LootScanResult Result { get; }

    public IReadOnlyList<LootScanDecisionViewModel> Decisions { get; }

    public IReadOnlyList<string> Issues { get; }

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

    public string CaptureLabel => $"Capture {Result.CorrelationId} • decode {Result.DecodeRevision.ToString(CultureInfo.InvariantCulture)}";

    public string TimingLabel
    {
        get
        {
            var elapsed = Result.Timings.Sum(stage => stage.ElapsedMilliseconds);
            return elapsed < 1000
                ? $"{elapsed.ToString(CultureInfo.InvariantCulture)} ms"
                : $"{(elapsed / 1000d).ToString("0.0", CultureInfo.InvariantCulture)} s";
        }
    }

    public string FocusLabel => $"Result returns to {Result.FocusDeviceId}";

    public int TakeCount => Decisions.Count(item => item.IsTake);

    public int SwapCount => Decisions.Count(item => item.IsSwap);

    public int LeaveCount => Decisions.Count(item => item.IsLeave);

    public int ReviewCount => Decisions.Count(item => item.IsReview);

    public string TakeSummary => $"{TakeCount.ToString(CultureInfo.InvariantCulture)} take";

    public string SwapSummary => $"{SwapCount.ToString(CultureInfo.InvariantCulture)} swap";

    public string LeaveSummary => $"{LeaveCount.ToString(CultureInfo.InvariantCulture)} leave";

    public string ReviewSummary => $"{ReviewCount.ToString(CultureInfo.InvariantCulture)} review";

    public bool HasDecisions => Decisions.Count > 0;

    public bool HasIssues => Issues.Count > 0;

    public bool IsComplete => Result.Status.Completeness == ResultCompleteness.Complete;

    public bool IsPartial => Result.Status.Completeness == ResultCompleteness.Partial;
}

public sealed class LootScanDecisionViewModel
{
    private readonly LootScanDecision _decision;

    public LootScanDecisionViewModel(
        LootScanDecision decision,
        DateTimeOffset evaluatedUtc,
        Action<LootScanDecision>? openEvidence)
    {
        _decision = decision ?? throw new ArgumentNullException(nameof(decision));
        EvaluatedUtc = evaluatedUtc;
        OpenEvidenceCommand = new DelegateCommand(() => openEvidence?.Invoke(_decision));
    }

    public DateTimeOffset EvaluatedUtc { get; }

    public ICommand OpenEvidenceCommand { get; }

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
                ? $"{width.ToString(CultureInfo.InvariantCulture)}×{height.ToString(CultureInfo.InvariantCulture)} • {(width * height).ToString(CultureInfo.InvariantCulture)} squares"
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
                parts.Add($"stack {quantity.ToString(CultureInfo.InvariantCulture)}");
            }

            if (item.FoundInRaid.Value is { } foundInRaid)
            {
                parts.Add(foundInRaid ? "found in raid" : "not found in raid");
            }

            if (item.Condition.Value is { } condition && condition.Kind != ItemConditionKind.NotApplicable)
            {
                parts.Add(condition.Current is { } current && condition.Maximum is { } maximum
                    ? $"condition {current.ToString("0.#", CultureInfo.InvariantCulture)}/{maximum.ToString("0.#", CultureInfo.InvariantCulture)}"
                    : $"condition {condition.Kind.ToString().ToLowerInvariant()}");
            }

            return parts.Count == 0 ? "No stack or condition detail" : string.Join(" • ", parts);
        }
    }

    public string ValueLabel => _decision.Economics?.BestNetValueRoubles is { } value
        ? $"{value.ToString("N0", CultureInfo.InvariantCulture)} ₽ net"
        : "Value needs review";

    public string ValuePerSquareLabel => _decision.Economics?.ValuePerSquareRoubles is { } value
        ? $"{value.ToString("N0", CultureInfo.InvariantCulture)} ₽ / square • {_decision.Economics.ValueBand?.ToString().ToLowerInvariant()}"
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
            return $"Place at row {placement.Anchor.Row + 1}, column {placement.Anchor.Column + 1}{rotation}";
        }
    }

    public bool HasPlacement => _decision.Placement is not null;

    public string SwapLabel => _decision.Verdict == LootScanVerdict.Swap
        ? $"Replace {_decision.Drops.Count.ToString(CultureInfo.InvariantCulture)} item(s) • {_decision.ReplacementCostRoubles.GetValueOrDefault().ToString("N0", CultureInfo.InvariantCulture)} ₽ given up"
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
                ? score.ToString("P0", CultureInfo.InvariantCulture)
                : "unscored";
            return $"{provenance.SourceClass} • {age} • {confidence} confidence";
        }
    }

    public string CandidateLabel => _decision.Item.Candidates.Count switch
    {
        0 => "Identity matched",
        1 => "1 alternate identity",
        var count => $"{count.ToString(CultureInfo.InvariantCulture)} alternate identities",
    };

    public string EvidenceActionLabel => IsReview ? "Review / correct" : "Open evidence";

    private static string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{Math.Floor(age.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min old";
        }

        return age < TimeSpan.FromDays(1)
            ? $"{Math.Floor(age.TotalHours).ToString(CultureInfo.InvariantCulture)} h old"
            : $"{Math.Floor(age.TotalDays).ToString(CultureInfo.InvariantCulture)} d old";
    }
}
