using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using RecommendationResult = TarkovCompanion.Core.Abstractions.V2.RecommendationResult;
using RecommendationAction = TarkovCompanion.Core.Abstractions.V2.RecommendationAction;

namespace TarkovCompanion.Application.Services.LootScan;

/// <summary>
/// Produces reviewable loot advice from already reconstructed grids and evaluated item advice.
/// It never turns the result into game input.
/// </summary>
public sealed class LootScanDecisionService
{
    private static readonly TimeSpan MaximumVolatileRaidRecommendationAge = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _timeProvider;
    private readonly ExplainableRecommendationPolicy _policy;
    private readonly int _maximumPlacementCellVisits;

    public LootScanDecisionService(
        TimeProvider? timeProvider = null,
        ExplainableRecommendationPolicy? policy = null,
        int maximumPlacementCellVisits = LootScanPlannerLimits.MaximumPlacementCellVisits)
    {
        if (maximumPlacementCellVisits is < 1 or > LootScanPlannerLimits.MaximumPlacementCellVisits)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPlacementCellVisits));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _policy = policy ?? ExplainableRecommendationPolicy.Default;
        _maximumPlacementCellVisits = maximumPlacementCellVisits;
    }

    public LootScanResult Evaluate(LootScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var started = _timeProvider.GetTimestamp();
        var issues = new List<LootScanIssue>();
        var decisions = new List<LootScanDecision>();
        var placementBudget = new PlacementWorkBudget(_maximumPlacementCellVisits, cancellationToken);

        if (!request.IsReviewedFrameCurrent)
        {
            issues.Add(new(
                LootScanIssueKind.CaptureChanged,
                "capture.changed",
                "The reviewed screenshot no longer matches this result; review the items again."));
        }

        if (request.VisibleLoot.Outcome == GridReconstructionOutcome.Partial)
        {
            issues.Add(new(
                LootScanIssueKind.LootCoveragePartial,
                "loot.coverage-partial",
                "Some visible loot could not be resolved; those cells remain review-only."));
        }

        if (request.CarriedInventory.Outcome != GridReconstructionOutcome.Complete)
        {
            issues.Add(new(
                LootScanIssueKind.CarriedCoveragePartial,
                "carried.coverage-partial",
                "Carried capacity is incomplete, so no unsupported fit or swap is claimed."));
        }

        var recommendations = request.Recommendations.ToDictionary(item => item.Anchor);
        var policies = request.CarriedPolicies.ToDictionary(item => item.Anchor);
        var capacity = TryBuildCapacity(request, policies, out var built)
            ? built
            : null;
        if (capacity is null && request.CarriedInventory.Outcome == GridReconstructionOutcome.Complete)
        {
            issues.Add(new(
                LootScanIssueKind.CapacityUnavailable,
                "carried.capacity-unavailable",
                "Carried dimensions or occupied footprints are incomplete; placement is review-only."));
        }

        var unresolvedByAnchor = request.VisibleLoot.UnresolvedCells
            .GroupBy(item => item.Anchor)
            .ToDictionary(group => group.Key, group => group.First());
        if (request.VisibleLoot.Recognition is { } visible)
        {
            // Capacity is one shared budget. Plan higher-precedence/value candidates first and
            // reserve every accepted placement so two TAKE answers cannot claim the same cells.
            foreach (var cell in visible.Cells
                         .Where(item => !unresolvedByAnchor.ContainsKey(item.Anchor))
                         .OrderByDescending(item => PlanningPriority(item, recommendations))
                         .ThenByDescending(item => PlanningValue(item, recommendations, request))
                         .ThenBy(item => item.Anchor.Row)
                         .ThenBy(item => item.Anchor.Column))
            {
                cancellationToken.ThrowIfCancellationRequested();
                decisions.Add(EvaluateResolved(
                    request,
                    cell,
                    recommendations,
                    capacity,
                    placementBudget,
                    cancellationToken));
            }
        }

        foreach (var unresolved in unresolvedByAnchor.Values
                     .OrderBy(item => item.Anchor.Row)
                     .ThenBy(item => item.Anchor.Column))
        {
            cancellationToken.ThrowIfCancellationRequested();
            decisions.Add(Review(
                unresolved.Anchor,
                unresolved.Item,
                "item.evidence-incomplete",
                "Item identity, footprint, or attributes are unresolved; review before acting."));
        }

        var elapsed = _timeProvider.GetElapsedTime(started);
        var completeness = decisions.Count == 0 && request.VisibleLoot.Recognition is null
            ? ResultCompleteness.Unavailable
            : issues.Count > 0 || decisions.Any(item => item.Verdict == LootScanVerdict.Review)
                ? ResultCompleteness.Partial
                : ResultCompleteness.Complete;
        var status = new ResultStatus(
            completeness,
            InputFreshness(request),
            completeness switch
            {
                ResultCompleteness.Complete => "loot-scan.complete",
                ResultCompleteness.Partial => "loot-scan.partial",
                _ => "loot-scan.unavailable",
            });
        return new(
            request.ScanId,
            request.CaptureSessionId,
            request.CorrelationId,
            request.Context,
            request.ArtifactId,
            request.DecodeRevision,
            request.SourceContentSha256,
            request.ReviewedContentSha256,
            request.InitiatingDeviceId,
            request.EvaluatedUtc,
            status,
            decisions.ToArray(),
            issues.ToArray(),
            [new("decision-planning", Math.Max(0, (long)elapsed.TotalMilliseconds))]);
    }

    private LootScanDecision EvaluateResolved(
        LootScanRequest request,
        GridCellRecognition cell,
        IReadOnlyDictionary<GridCellAddress, LootScanCandidateRecommendation> recommendations,
        CapacityMap? capacity,
        PlacementWorkBudget placementBudget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IsReviewedFrameCurrent)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "capture.changed",
                "This advice belongs to an earlier screenshot revision.");
        }

        if (!TryExactItem(cell.Item, request.EvaluatedUtc, out var item, out var width, out var height))
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "item.evidence-incomplete",
                "Item identity and footprint must be resolved before capacity is calculated.");
        }

        if (!recommendations.TryGetValue(cell.Anchor, out var evaluated))
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "recommendation.missing",
                "No profile and economy recommendation was produced for this item.");
        }

        if (!evaluated.Binding.Matches(
                request.CaptureSessionId,
                request.ArtifactId,
                request.DecodeRevision,
                request.SourceContentSha256,
                cell.Anchor,
                item!.CanonicalId.Value!) ||
            evaluated.Recommendation.CaptureSessionId != request.CaptureSessionId)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "recommendation.binding-mismatch",
                "The advice belongs to a different capture revision or item; review this cell again.");
        }

        var recommendation = evaluated.Recommendation;
        var economics = ProjectEconomics(
            evaluated.Economics,
            request.EvaluatedUtc,
            checked(width * height));
        if (!string.Equals(recommendation.RulesetVersion, _policy.RulesetVersion, StringComparison.Ordinal))
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "recommendation.ruleset-mismatch",
                "The advice was produced by a different ruleset version.",
                recommendation,
                economics);
        }

        var advice = recommendation.Decision.Value;
        if (advice is null || recommendation.Decision.Status.Completeness != ResultCompleteness.Complete ||
            recommendation.Decision.Status.Freshness != FreshnessState.Current)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "recommendation.incomplete",
                "The recommendation has incomplete or stale evidence.",
                recommendation,
                economics);
        }

        if (!IsRecommendationTemporallyCurrent(recommendation, advice, request.EvaluatedUtc))
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "recommendation.expired",
                "The recommendation was produced in the future or is too old for its evidence class.",
                recommendation,
                economics);
        }

        var economicDominant = IsEconomicLootAdvice(advice);
        if (economicDominant && economics.Status.Completeness != ResultCompleteness.Complete)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "economics.incomplete",
                "The current economic inputs do not reproduce a trustworthy value-per-square decision for this item.",
                recommendation,
                economics);
        }

        if (advice.Action == RecommendationAction.Leave)
        {
            return new(
                cell.Anchor,
                cell.Item,
                LootScanVerdict.Leave,
                [new("recommendation.leave", advice.Summary)],
                recommendation,
                economics);
        }

        if (advice.Action is not (RecommendationAction.Take or RecommendationAction.Keep or RecommendationAction.Swap))
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "recommendation.requires-review",
                advice.Summary,
                recommendation,
                economics);
        }

        if (request.CarriedInventory.Outcome != GridReconstructionOutcome.Complete || capacity is null)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "carried.capacity-incomplete",
                "The visible carried grid is not complete enough to prove a fit.",
                recommendation,
                economics);
        }

        try
        {
            if (capacity.TryFindFree(width, height, placementBudget, out var freePlacement))
            {
                capacity.CommitPlacement(freePlacement!);
                return new(
                    cell.Anchor,
                    cell.Item,
                    LootScanVerdict.Take,
                    [new("capacity.visible-fit", "The item fits in verified visible carried space.")],
                    recommendation: recommendation,
                    economics: economics,
                    placement: freePlacement);
            }

            var swapSearch = capacity.FindBestSwap(width, height, placementBudget);
            return EvaluateSwap(cell, recommendation, economicDominant, economics, capacity, swapSearch);
        }
        catch (PlacementBudgetExceededException)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "capacity.search-budget-exhausted",
                "The bounded placement search reached its work limit; review placement manually.",
                recommendation,
                economics);
        }
    }

    private LootScanDecision EvaluateSwap(
        GridCellRecognition cell,
        RecommendationResult recommendation,
        bool economicDominant,
        LootScanEconomicProjection economics,
        CapacityMap capacity,
        SwapSearchResult swapSearch)
    {
        var swap = swapSearch.Best;
        if (swap is null)
        {
            if (swapSearch.HasUnresolvedPolicyOption)
            {
                return Review(
                    cell.Anchor,
                    cell.Item,
                    "swap.evidence-incomplete",
                    "A geometric swap may fit, but one or more carried-item protections, pins, bindings, or replacement values need review.",
                    recommendation,
                    economics);
            }

            return new(
                cell.Anchor,
                cell.Item,
                LootScanVerdict.Leave,
                [new("capacity.no-supported-fit", "No fit or bounded swap is supported by the visible carried grid.")],
                recommendation,
                economics);
        }

        var incomingValue = economics.BestNetValueRoubles;
        if (economicDominant && incomingValue is null)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "swap.incoming-value-unknown",
                "A swap was found, but the incoming value is not current enough to compare.",
                recommendation,
                economics);
        }

        if (economicDominant && incomingValue!.Value <= swap.ReplacementCostRoubles)
        {
            return new(
                cell.Anchor,
                cell.Item,
                LootScanVerdict.Leave,
                [new(
                    "swap.cost-exceeds-value",
                    $"The supported swap gives up {swap.ReplacementCostRoubles:N0} roubles for no economic gain.")],
                recommendation: recommendation,
                economics: economics);
        }

        capacity.CommitSwap(swap);
        return new(
            cell.Anchor,
            cell.Item,
            LootScanVerdict.Swap,
            [new(
                "capacity.bounded-swap",
                $"The item fits after replacing {swap.Drops.Count} verified droppable item(s).")],
            recommendation: recommendation,
            economics: economics,
            placement: swap.Placement,
            drops: swap.Drops,
            replacementCostRoubles: swap.ReplacementCostRoubles);
    }

    private static int PlanningPriority(
        GridCellRecognition cell,
        IReadOnlyDictionary<GridCellAddress, LootScanCandidateRecommendation> recommendations)
    {
        if (!recommendations.TryGetValue(cell.Anchor, out var evaluated) ||
            evaluated.Recommendation.Decision.Value is not { Reasons.Count: > 0 } decision ||
            evaluated.Recommendation.Decision.Status.Completeness != ResultCompleteness.Complete ||
            evaluated.Recommendation.Decision.Status.Freshness != FreshnessState.Current)
        {
            return int.MinValue;
        }

        return decision.Reasons[0].Priority;
    }

    private long PlanningValue(
        GridCellRecognition cell,
        IReadOnlyDictionary<GridCellAddress, LootScanCandidateRecommendation> recommendations,
        LootScanRequest request)
    {
        if (!recommendations.TryGetValue(cell.Anchor, out var evaluated) ||
            evaluated.Recommendation.Decision.Value is null)
        {
            return long.MinValue;
        }

        return BestNetValue(evaluated.Economics, request.EvaluatedUtc) ?? long.MinValue;
    }

    private static LootScanDecision Review(
        GridCellAddress anchor,
        EvidencedValue<RecognizedItem> item,
        string code,
        string explanation,
        RecommendationResult? recommendation = null,
        LootScanEconomicProjection? economics = null) => new(
            anchor,
            item,
        LootScanVerdict.Review,
        [new(code, explanation)],
        recommendation,
        economics);

    private bool TryBuildCapacity(
        LootScanRequest request,
        IReadOnlyDictionary<GridCellAddress, LootScanCarriedPolicy> policies,
        out CapacityMap? capacity)
    {
        capacity = null;
        var result = request.CarriedInventory;
        if (result.Outcome != GridReconstructionOutcome.Complete || result.Recognition is not { } recognition ||
            recognition.Geometry.Rows.Value is not { } rows || recognition.Geometry.Columns.Value is not { } columns ||
            !IsReliable(recognition.Geometry.Rows, request.EvaluatedUtc, _policy.MaximumInventoryAge, requireComplete: true) ||
            !IsReliable(recognition.Geometry.Columns, request.EvaluatedUtc, _policy.MaximumInventoryAge, requireComplete: true))
        {
            return false;
        }

        var items = new List<CapacityItem>(recognition.Cells.Count);
        foreach (var cell in recognition.Cells)
        {
            if (!TryExactItem(cell.Item, request.EvaluatedUtc, out var item, out var width, out var height))
            {
                return false;
            }

            policies.TryGetValue(cell.Anchor, out var policy);
            if (policy is not null && !policy.Binding.Matches(
                    request.CaptureSessionId,
                    request.ArtifactId,
                    request.DecodeRevision,
                    request.SourceContentSha256,
                    cell.Anchor,
                    item!.CanonicalId.Value!))
            {
                policy = null;
            }

            var replacementValue = policy is null
                ? null
                : ReliableValue(policy.ReplacementValueRoubles, request.EvaluatedUtc, _policy.MaximumPriceAge);
            var protectedReliable = policy is not null &&
                IsReliable(policy.ProtectedItem, request.EvaluatedUtc, _policy.MaximumInventoryAge, requireComplete: true);
            var pinnedReliable = policy is not null &&
                IsReliable(policy.Pinned, request.EvaluatedUtc, _policy.MaximumInventoryAge, requireComplete: true);
            var disposition = policy switch
            {
                null => CapacityItemDisposition.Unresolved,
                _ when protectedReliable && policy!.ProtectedItem.Value == true => CapacityItemDisposition.Retained,
                _ when pinnedReliable && policy!.Pinned.Value == true => CapacityItemDisposition.Retained,
                _ when protectedReliable && pinnedReliable &&
                    policy!.ProtectedItem.Value == false && policy.Pinned.Value == false &&
                    replacementValue is not null => CapacityItemDisposition.Droppable,
                _ => CapacityItemDisposition.Unresolved,
            };
            items.Add(new(
                cell.Anchor,
                width,
                height,
                cell.Item,
                disposition,
                replacementValue,
                policy?.ReplacementValueRoubles.Provenance));
        }

        capacity = CapacityMap.Create(rows, columns, items);
        return capacity is not null;
    }

    private bool TryExactItem(
        EvidencedValue<RecognizedItem> field,
        DateTimeOffset evaluatedUtc,
        out RecognizedItem? item,
        out int width,
        out int height)
    {
        item = field.Value;
        width = item?.WidthCells.Value ?? 0;
        height = item?.HeightCells.Value ?? 0;
        return IsReliable(field, evaluatedUtc, _policy.MaximumInventoryAge, requireComplete: true) &&
            item?.CanonicalId.Value is not null &&
            IsReliable(item.CanonicalId, evaluatedUtc, _policy.MaximumInventoryAge, requireComplete: true) &&
            IsReliable(item.WidthCells, evaluatedUtc, _policy.MaximumInventoryAge, requireComplete: true) &&
            IsReliable(item.HeightCells, evaluatedUtc, _policy.MaximumInventoryAge, requireComplete: true) &&
            width > 0 && height > 0;
    }

    private LootScanEconomicProjection ProjectEconomics(
        RecommendationEconomics economics,
        DateTimeOffset evaluatedUtc,
        int observedSquares)
    {
        var flea = ReliableValue(economics.FleaNetRoubles, evaluatedUtc, _policy.MaximumPriceAge);
        var trader = ReliableValue(economics.TraderRoubles, evaluatedUtc, _policy.MaximumPriceAge);
        var squares = ReliableValue(economics.OccupiedSquares, evaluatedUtc, _policy.MaximumPriceAge);
        var freshness = EconomicsFreshness(economics, evaluatedUtc);
        if ((flea is null && trader is null) || squares is null || squares.Value != observedSquares)
        {
            return new(
                economics,
                new ResultStatus(
                    ResultCompleteness.Partial,
                    freshness,
                    squares is not null && squares.Value != observedSquares
                        ? "economics.footprint-mismatch"
                        : "economics.review"),
                null,
                null,
                null,
                null,
                null);
        }

        var useFlea = flea.HasValue && (!trader.HasValue || flea.Value >= trader.Value);
        var total = useFlea ? flea!.Value : trader!.Value;
        var price = useFlea ? economics.FleaNetRoubles : economics.TraderRoubles;
        if (ContainsModelledEstimate(price.Provenance))
        {
            return new(
                economics,
                new ResultStatus(ResultCompleteness.Partial, freshness, "economics.model-review"),
                null,
                null,
                null,
                null,
                null);
        }

        var perSquare = total / squares.Value;
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            "loot-scan://value-per-square",
            evaluatedUtc,
            EvidenceConfidence.Unscored,
            new ProducerIdentity("loot-scan-planner", "2"),
            generatedUtc: evaluatedUtc,
            inputs: [price.Provenance, economics.OccupiedSquares.Provenance]);
        return new(
            economics,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "economics.complete"),
            total,
            perSquare,
            _policy.ValueBands.Classify(perSquare),
            useFlea ? "flea-net" : "trader",
            provenance);
    }

    private long? BestNetValue(RecommendationEconomics economics, DateTimeOffset evaluatedUtc)
    {
        var flea = ReliableValue(economics.FleaNetRoubles, evaluatedUtc, _policy.MaximumPriceAge);
        var trader = ReliableValue(economics.TraderRoubles, evaluatedUtc, _policy.MaximumPriceAge);
        return flea is null ? trader : trader is null ? flea : Math.Max(flea.Value, trader.Value);
    }

    private T? ReliableValue<T>(EvidencedValue<T?> field, DateTimeOffset evaluatedUtc, TimeSpan maximumAge)
        where T : struct =>
        field.Value is { } value && IsReliable(field, evaluatedUtc, maximumAge) ? value : null;

    private bool IsReliable<T>(
        EvidencedValue<T> field,
        DateTimeOffset evaluatedUtc,
        TimeSpan maximumAge,
        bool requireComplete = false) =>
        (!requireComplete || field.Status.Completeness == ResultCompleteness.Complete) &&
        (field.Status.Completeness is ResultCompleteness.Complete or ResultCompleteness.Partial) &&
        field.Candidates.Count == 0 &&
        field.Status.Freshness == FreshnessState.Current &&
        field.Provenance.EvidenceThroughUtc <= evaluatedUtc &&
        evaluatedUtc - field.Provenance.EvidenceThroughUtc <= maximumAge &&
        field.Provenance.Confidence.Score is { } score &&
        score >= _policy.MinimumEvidenceConfidence;

    private FreshnessState EconomicsFreshness(
        RecommendationEconomics economics,
        DateTimeOffset evaluatedUtc)
    {
        var prices = new[] { economics.FleaNetRoubles, economics.TraderRoubles };
        if (prices.Any(field => IsStaleOrExpired(field, evaluatedUtc, _policy.MaximumPriceAge)) ||
            IsStaleOrExpired(economics.OccupiedSquares, evaluatedUtc, _policy.MaximumPriceAge))
        {
            return FreshnessState.Stale;
        }

        return prices.Any(field => field.Status.Freshness == FreshnessState.Unknown) ||
            economics.OccupiedSquares.Status.Freshness == FreshnessState.Unknown
                ? FreshnessState.Unknown
                : FreshnessState.Current;
    }

    private static bool IsStaleOrExpired<T>(
        EvidencedValue<T> field,
        DateTimeOffset evaluatedUtc,
        TimeSpan maximumAge) =>
        field.Status.Freshness == FreshnessState.Stale ||
        IsOutsideTimeWindow(field.Provenance, evaluatedUtc, maximumAge);

    private static bool IsOutsideTimeWindow(
        EvidenceProvenance provenance,
        DateTimeOffset evaluatedUtc,
        TimeSpan maximumAge) =>
        provenance.EvidenceThroughUtc > evaluatedUtc ||
        evaluatedUtc - provenance.EvidenceThroughUtc > maximumAge;

    private static bool ContainsModelledEstimate(EvidenceProvenance provenance) =>
        provenance.SourceClass == EvidenceSourceClass.ModelledEstimate ||
        provenance.Inputs.Any(ContainsModelledEstimate);

    private FreshnessState InputFreshness(LootScanRequest request)
    {
        if (HasStaleInput(request))
        {
            return FreshnessState.Stale;
        }

        return HasUnknownFreshness(request)
            ? FreshnessState.Unknown
            : FreshnessState.Current;
    }

    private bool HasStaleInput(LootScanRequest request)
    {
        bool EconomicsIsStale(RecommendationEconomics economics) =>
            EconomicsFreshness(economics, request.EvaluatedUtc) == FreshnessState.Stale ||
            IsStaleOrExpired(economics.FleaGrossRoubles, request.EvaluatedUtc, _policy.MaximumPriceAge) ||
            IsStaleOrExpired(economics.FleaFeeRoubles, request.EvaluatedUtc, _policy.MaximumPriceAge) ||
            IsStaleOrExpired(economics.ConditionFraction, request.EvaluatedUtc, _policy.MaximumPriceAge);

        bool ItemIsStale(EvidencedValue<RecognizedItem> field)
        {
            if (IsStaleOrExpired(field, request.EvaluatedUtc, _policy.MaximumInventoryAge))
            {
                return true;
            }

            if (field.Value is not { } item)
            {
                return false;
            }

            return IsStaleOrExpired(item.CanonicalId, request.EvaluatedUtc, _policy.MaximumInventoryAge) ||
                IsStaleOrExpired(item.DisplayName, request.EvaluatedUtc, _policy.MaximumInventoryAge) ||
                IsStaleOrExpired(item.Quantity, request.EvaluatedUtc, _policy.MaximumInventoryAge) ||
                IsStaleOrExpired(item.WidthCells, request.EvaluatedUtc, _policy.MaximumInventoryAge) ||
                IsStaleOrExpired(item.HeightCells, request.EvaluatedUtc, _policy.MaximumInventoryAge) ||
                IsStaleOrExpired(item.Rotated, request.EvaluatedUtc, _policy.MaximumInventoryAge) ||
                IsStaleOrExpired(item.FoundInRaid, request.EvaluatedUtc, _policy.MaximumInventoryAge) ||
                IsStaleOrExpired(item.Condition, request.EvaluatedUtc, _policy.MaximumInventoryAge);
        }

        bool GridIsStale(GridReconstructionResult grid) =>
            grid.Recognition is { } recognition &&
            (IsStaleOrExpired(recognition.Geometry.Rows, request.EvaluatedUtc, _policy.MaximumInventoryAge) ||
             IsStaleOrExpired(recognition.Geometry.Columns, request.EvaluatedUtc, _policy.MaximumInventoryAge) ||
             recognition.Cells.Any(cell => ItemIsStale(cell.Item))) ||
            grid.UnresolvedCells.Any(cell => ItemIsStale(cell.Item));

        return GridIsStale(request.VisibleLoot) ||
            GridIsStale(request.CarriedInventory) ||
            request.Recommendations.Any(item =>
                item.Recommendation.Decision.Status.Freshness == FreshnessState.Stale ||
                (item.Recommendation.Decision.Value is { } decision &&
                 !IsRecommendationTemporallyCurrent(item.Recommendation, decision, request.EvaluatedUtc)) ||
                EconomicsIsStale(item.Economics)) ||
            request.CarriedPolicies.Any(policy =>
                IsStaleOrExpired(policy.ProtectedItem, request.EvaluatedUtc, _policy.MaximumInventoryAge) ||
                IsStaleOrExpired(policy.Pinned, request.EvaluatedUtc, _policy.MaximumInventoryAge) ||
                IsStaleOrExpired(policy.ReplacementValueRoubles, request.EvaluatedUtc, _policy.MaximumPriceAge));
    }

    private static bool HasUnknownFreshness(LootScanRequest request)
    {
        static bool Unknown<T>(EvidencedValue<T> field) =>
            field.Status.Freshness == FreshnessState.Unknown;

        static bool EconomicsUnknown(RecommendationEconomics economics) =>
            Unknown(economics.FleaGrossRoubles) ||
            Unknown(economics.FleaFeeRoubles) ||
            Unknown(economics.FleaNetRoubles) ||
            Unknown(economics.TraderRoubles) ||
            Unknown(economics.OccupiedSquares) ||
            Unknown(economics.ConditionFraction);

        static bool ItemUnknown(EvidencedValue<RecognizedItem> field) =>
            Unknown(field) ||
            (field.Value is { } item &&
             (Unknown(item.CanonicalId) ||
              Unknown(item.DisplayName) ||
              Unknown(item.Quantity) ||
              Unknown(item.WidthCells) ||
              Unknown(item.HeightCells) ||
              Unknown(item.Rotated) ||
              Unknown(item.FoundInRaid) ||
              Unknown(item.Condition)));

        static bool GridUnknown(GridReconstructionResult grid) =>
            grid.Recognition is { } recognition &&
            (Unknown(recognition.Geometry.Rows) ||
             Unknown(recognition.Geometry.Columns) ||
             recognition.Cells.Any(cell => ItemUnknown(cell.Item))) ||
            grid.UnresolvedCells.Any(cell => ItemUnknown(cell.Item));

        return GridUnknown(request.VisibleLoot) ||
            GridUnknown(request.CarriedInventory) ||
            request.Recommendations.Any(item =>
                item.Recommendation.Decision.Status.Freshness == FreshnessState.Unknown ||
                EconomicsUnknown(item.Economics)) ||
            request.CarriedPolicies.Any(policy =>
                Unknown(policy.ProtectedItem) ||
                Unknown(policy.Pinned) ||
                Unknown(policy.ReplacementValueRoubles));
    }

    private bool IsRecommendationTemporallyCurrent(
        RecommendationResult recommendation,
        RecommendationDecision advice,
        DateTimeOffset evaluatedUtc) =>
        !IsOutsideTimeWindow(
            recommendation.Decision.Provenance,
            evaluatedUtc,
            MaximumRecommendationAge(advice));

    private TimeSpan MaximumRecommendationAge(RecommendationDecision advice)
    {
        if (advice.Reasons.Any(reason => reason.Code.StartsWith("raid.", StringComparison.Ordinal)))
        {
            return MaximumVolatileRaidRecommendationAge;
        }

        return IsEconomicLootAdvice(advice)
            ? _policy.MaximumPriceAge
            : _policy.MaximumInventoryAge;
    }

    private static bool IsEconomicLootAdvice(RecommendationDecision advice)
    {
        var dominant = advice.Reasons[0];
        return dominant.Category == RecommendationReasonCategory.Economics ||
            dominant.Code.StartsWith("raid.", StringComparison.Ordinal);
    }

    private enum CapacityItemDisposition
    {
        Droppable = 1,
        Retained,
        Unresolved,
    }

    private sealed record CapacityItem(
        GridCellAddress Anchor,
        int Width,
        int Height,
        EvidencedValue<RecognizedItem> Evidence,
        CapacityItemDisposition Disposition,
        long? ReplacementValueRoubles,
        EvidenceProvenance? ReplacementValueProvenance);

    private sealed record SwapOption(
        LootScanPlacement Placement,
        IReadOnlyList<int> BlockerIndexes,
        IReadOnlyList<LootScanDropItem> Drops,
        long ReplacementCostRoubles);

    private sealed record SwapSearchResult(
        SwapOption? Best,
        bool HasUnresolvedPolicyOption);

    private sealed class PlacementWorkBudget(int maximumCellVisits, CancellationToken cancellationToken)
    {
        private int _remainingCellVisits = maximumCellVisits;

        public void VisitCell()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_remainingCellVisits == 0)
            {
                throw new PlacementBudgetExceededException();
            }

            _remainingCellVisits--;
        }
    }

    private sealed class PlacementBudgetExceededException : Exception
    {
    }

    private sealed class CapacityMap
    {
        private const int PlannedIncoming = -1;

        private readonly int?[,] _occupied;
        private readonly IReadOnlyList<CapacityItem> _items;

        private CapacityMap(int rows, int columns, int?[,] occupied, IReadOnlyList<CapacityItem> items)
        {
            Rows = rows;
            Columns = columns;
            _occupied = occupied;
            _items = items;
        }

        public int Rows { get; }

        public int Columns { get; }

        public static CapacityMap? Create(int rows, int columns, IReadOnlyList<CapacityItem> items)
        {
            if (rows is < 1 or > GridGeometry.MaxRows || columns is < 1 or > GridGeometry.MaxColumns)
            {
                return null;
            }

            var occupied = new int?[rows, columns];
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item.Anchor.Row > rows - item.Height || item.Anchor.Column > columns - item.Width)
                {
                    return null;
                }

                for (var row = item.Anchor.Row; row < item.Anchor.Row + item.Height; row++)
                {
                    for (var column = item.Anchor.Column; column < item.Anchor.Column + item.Width; column++)
                    {
                        if (occupied[row, column] is not null)
                        {
                            return null;
                        }

                        occupied[row, column] = index;
                    }
                }
            }

            return new(rows, columns, occupied, items);
        }

        public bool TryFindFree(
            int width,
            int height,
            PlacementWorkBudget budget,
            out LootScanPlacement? placement)
        {
            foreach (var orientation in Orientations(width, height))
            {
                for (var row = 0; row <= Rows - orientation.Height; row++)
                {
                    for (var column = 0; column <= Columns - orientation.Width; column++)
                    {
                        if (IsFree(row, column, orientation.Width, orientation.Height, budget))
                        {
                            placement = new(
                                new(row, column),
                                orientation.Width,
                                orientation.Height,
                                orientation.Rotate);
                            return true;
                        }
                    }
                }
            }

            placement = null;
            return false;
        }

        public void CommitPlacement(LootScanPlacement placement)
        {
            for (var row = placement.Anchor.Row; row < placement.Anchor.Row + placement.HeightCells; row++)
            {
                for (var column = placement.Anchor.Column; column < placement.Anchor.Column + placement.WidthCells; column++)
                {
                    if (_occupied[row, column] is not null)
                    {
                        throw new InvalidOperationException("A planned placement must still be free when committed.");
                    }

                    _occupied[row, column] = PlannedIncoming;
                }
            }
        }

        public SwapSearchResult FindBestSwap(int width, int height, PlacementWorkBudget budget)
        {
            SwapOption? best = null;
            var hasUnresolvedPolicyOption = false;
            foreach (var orientation in Orientations(width, height))
            {
                for (var row = 0; row <= Rows - orientation.Height; row++)
                {
                    for (var column = 0; column <= Columns - orientation.Width; column++)
                    {
                        if (!TryCollectBlockers(
                                row,
                                column,
                                orientation.Width,
                                orientation.Height,
                                budget,
                                out var blockers) ||
                            blockers.Count == 0)
                        {
                            continue;
                        }

                        var drops = new List<LootScanDropItem>(blockers.Count);
                        long cost = 0;
                        var supported = true;
                        var retained = false;
                        var unresolved = false;
                        foreach (var blocker in blockers.Order())
                        {
                            var carried = _items[blocker];
                            var value = carried.ReplacementValueRoubles;
                            if (carried.Disposition == CapacityItemDisposition.Retained)
                            {
                                retained = true;
                                supported = false;
                                break;
                            }

                            if (carried.Disposition == CapacityItemDisposition.Unresolved ||
                                value is null || value > long.MaxValue - cost ||
                                carried.ReplacementValueProvenance is null)
                            {
                                unresolved = true;
                                supported = false;
                                continue;
                            }

                            cost += value.Value;
                            drops.Add(new(
                                carried.Anchor,
                                carried.Evidence,
                                value.Value,
                                carried.ReplacementValueProvenance));
                        }

                        if (!retained && unresolved)
                        {
                            hasUnresolvedPolicyOption = true;
                        }

                        if (supported)
                        {
                            var option = new SwapOption(
                                new(new(row, column), orientation.Width, orientation.Height, orientation.Rotate),
                                blockers.Order().ToArray(),
                                drops,
                                cost);
                            if (best is null || IsBetter(option, best))
                            {
                                best = option;
                            }
                        }
                    }
                }
            }

            return new(best, hasUnresolvedPolicyOption);
        }

        public void CommitSwap(SwapOption swap)
        {
            foreach (var blocker in swap.BlockerIndexes)
            {
                var carried = _items[blocker];
                for (var row = carried.Anchor.Row; row < carried.Anchor.Row + carried.Height; row++)
                {
                    for (var column = carried.Anchor.Column; column < carried.Anchor.Column + carried.Width; column++)
                    {
                        if (_occupied[row, column] == blocker)
                        {
                            _occupied[row, column] = null;
                        }
                    }
                }
            }

            CommitPlacement(swap.Placement);
        }

        private bool IsFree(int row, int column, int width, int height, PlacementWorkBudget budget)
        {
            for (var currentRow = row; currentRow < row + height; currentRow++)
            {
                for (var currentColumn = column; currentColumn < column + width; currentColumn++)
                {
                    budget.VisitCell();
                    if (_occupied[currentRow, currentColumn] is not null)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private bool TryCollectBlockers(
            int row,
            int column,
            int width,
            int height,
            PlacementWorkBudget budget,
            out HashSet<int> blockers)
        {
            blockers = [];
            for (var currentRow = row; currentRow < row + height; currentRow++)
            {
                for (var currentColumn = column; currentColumn < column + width; currentColumn++)
                {
                    budget.VisitCell();
                    if (_occupied[currentRow, currentColumn] is not { } index)
                    {
                        continue;
                    }

                    if (index == PlannedIncoming)
                    {
                        return false;
                    }

                    blockers.Add(index);
                    if (blockers.Count > LootScanPlannerLimits.MaximumSwapItems)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsBetter(SwapOption candidate, SwapOption current)
        {
            if (candidate.ReplacementCostRoubles != current.ReplacementCostRoubles)
            {
                return candidate.ReplacementCostRoubles < current.ReplacementCostRoubles;
            }

            if (candidate.Drops.Count != current.Drops.Count)
            {
                return candidate.Drops.Count < current.Drops.Count;
            }

            if (candidate.Placement.RotateFromObserved != current.Placement.RotateFromObserved)
            {
                return !candidate.Placement.RotateFromObserved;
            }

            return candidate.Placement.Anchor.Row != current.Placement.Anchor.Row
                ? candidate.Placement.Anchor.Row < current.Placement.Anchor.Row
                : candidate.Placement.Anchor.Column < current.Placement.Anchor.Column;
        }

        private static IReadOnlyList<(int Width, int Height, bool Rotate)> Orientations(int width, int height) =>
            width == height
                ? [(width, height, false)]
                : [(width, height, false), (height, width, true)];
    }
}
