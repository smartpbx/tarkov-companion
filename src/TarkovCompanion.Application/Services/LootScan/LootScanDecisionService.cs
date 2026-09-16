using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using RecommendationResult = TarkovCompanion.Core.Abstractions.V2.RecommendationResult;

namespace TarkovCompanion.Application.Services.LootScan;

/// <summary>
/// Produces reviewable loot advice from already reconstructed grids and evaluated item advice.
/// It never turns the result into game input.
/// </summary>
public sealed class LootScanDecisionService
{
    private readonly TimeProvider _timeProvider;
    private readonly ExplainableRecommendationPolicy _policy;

    public LootScanDecisionService(
        TimeProvider? timeProvider = null,
        ExplainableRecommendationPolicy? policy = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _policy = policy ?? ExplainableRecommendationPolicy.Default;
    }

    public LootScanResult Evaluate(LootScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var started = _timeProvider.GetTimestamp();
        var issues = new List<LootScanIssue>();
        var decisions = new List<LootScanDecision>();

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
                decisions.Add(EvaluateResolved(request, cell, recommendations, capacity, cancellationToken));
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
            FreshnessState.Current,
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
        var economics = ProjectEconomics(evaluated.Economics, request.EvaluatedUtc);
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

        if (capacity.TryFindFree(width, height, cancellationToken, out var freePlacement))
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

        var swap = capacity.FindBestSwap(width, height, cancellationToken);
        if (swap is null)
        {
            return new(
                cell.Anchor,
                cell.Item,
                LootScanVerdict.Leave,
                [new("capacity.no-supported-fit", "No fit or bounded swap is supported by the visible carried grid.")],
                recommendation,
                economics);
        }

        var incomingValue = economics.BestNetValueRoubles;
        var economicOnly = advice.Reasons[0].Category == RecommendationReasonCategory.Economics;
        if (economicOnly && incomingValue is null)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "swap.incoming-value-unknown",
                "A swap was found, but the incoming value is not current enough to compare.",
                recommendation,
                economics);
        }

        if (economicOnly && incomingValue!.Value <= swap.ReplacementCostRoubles)
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
            var droppable = policy is not null &&
                IsReliable(policy.ProtectedItem, request.EvaluatedUtc, _policy.MaximumInventoryAge, requireComplete: true) &&
                policy.ProtectedItem.Value == false &&
                IsReliable(policy.Pinned, request.EvaluatedUtc, _policy.MaximumInventoryAge, requireComplete: true) &&
                policy.Pinned.Value == false &&
                replacementValue is not null;
            items.Add(new(
                cell.Anchor,
                width,
                height,
                cell.Item,
                droppable,
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
        DateTimeOffset evaluatedUtc)
    {
        var flea = ReliableValue(economics.FleaNetRoubles, evaluatedUtc, _policy.MaximumPriceAge);
        var trader = ReliableValue(economics.TraderRoubles, evaluatedUtc, _policy.MaximumPriceAge);
        var squares = ReliableValue(economics.OccupiedSquares, evaluatedUtc, _policy.MaximumPriceAge);
        if ((flea is null && trader is null) || squares is null)
        {
            return new(
                economics,
                new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current, "economics.review"),
                null,
                null,
                null,
                null,
                null);
        }

        var useFlea = flea.HasValue && (!trader.HasValue || flea.Value >= trader.Value);
        var total = useFlea ? flea!.Value : trader!.Value;
        var price = useFlea ? economics.FleaNetRoubles : economics.TraderRoubles;
        if (price.Provenance.SourceClass == EvidenceSourceClass.ModelledEstimate)
        {
            return new(
                economics,
                new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current, "economics.model-review"),
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
        field.Status.Freshness == FreshnessState.Current &&
        field.Provenance.EvidenceThroughUtc <= evaluatedUtc &&
        evaluatedUtc - field.Provenance.EvidenceThroughUtc <= maximumAge &&
        field.Provenance.Confidence.Score is { } score &&
        score >= _policy.MinimumEvidenceConfidence;

    private sealed record CapacityItem(
        GridCellAddress Anchor,
        int Width,
        int Height,
        EvidencedValue<RecognizedItem> Evidence,
        bool IsKnownDroppable,
        long? ReplacementValueRoubles,
        EvidenceProvenance? ReplacementValueProvenance);

    private sealed record SwapOption(
        LootScanPlacement Placement,
        IReadOnlyList<int> BlockerIndexes,
        IReadOnlyList<LootScanDropItem> Drops,
        long ReplacementCostRoubles);

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
            CancellationToken cancellationToken,
            out LootScanPlacement? placement)
        {
            foreach (var orientation in Orientations(width, height))
            {
                for (var row = 0; row <= Rows - orientation.Height; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var column = 0; column <= Columns - orientation.Width; column++)
                    {
                        if (IsFree(row, column, orientation.Width, orientation.Height))
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

        public SwapOption? FindBestSwap(int width, int height, CancellationToken cancellationToken)
        {
            SwapOption? best = null;
            foreach (var orientation in Orientations(width, height))
            {
                for (var row = 0; row <= Rows - orientation.Height; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var column = 0; column <= Columns - orientation.Width; column++)
                    {
                        if (!TryCollectBlockers(
                                row,
                                column,
                                orientation.Width,
                                orientation.Height,
                                out var blockers) ||
                            blockers.Count == 0)
                        {
                            continue;
                        }

                        var drops = new List<LootScanDropItem>(blockers.Count);
                        long cost = 0;
                        var supported = true;
                        foreach (var blocker in blockers.Order())
                        {
                            var carried = _items[blocker];
                            var value = carried.ReplacementValueRoubles;
                            if (!carried.IsKnownDroppable || value is null || value > long.MaxValue - cost ||
                                carried.ReplacementValueProvenance is null)
                            {
                                supported = false;
                                break;
                            }

                            cost += value.Value;
                            drops.Add(new(
                                carried.Anchor,
                                carried.Evidence,
                                value.Value,
                                carried.ReplacementValueProvenance));
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

            return best;
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

        private bool IsFree(int row, int column, int width, int height)
        {
            for (var currentRow = row; currentRow < row + height; currentRow++)
            {
                for (var currentColumn = column; currentColumn < column + width; currentColumn++)
                {
                    if (_occupied[currentRow, currentColumn] is not null)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private bool TryCollectBlockers(int row, int column, int width, int height, out HashSet<int> blockers)
        {
            blockers = [];
            for (var currentRow = row; currentRow < row + height; currentRow++)
            {
                for (var currentColumn = column; currentColumn < column + width; currentColumn++)
                {
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

        private static bool IsBetter(SwapOption candidate, SwapOption current) =>
            candidate.ReplacementCostRoubles < current.ReplacementCostRoubles ||
            candidate.ReplacementCostRoubles == current.ReplacementCostRoubles &&
            (candidate.Drops.Count < current.Drops.Count ||
             candidate.Drops.Count == current.Drops.Count &&
             (!candidate.Placement.RotateFromObserved && current.Placement.RotateFromObserved ||
              candidate.Placement.RotateFromObserved == current.Placement.RotateFromObserved &&
              (candidate.Placement.Anchor.Row < current.Placement.Anchor.Row ||
               candidate.Placement.Anchor.Row == current.Placement.Anchor.Row &&
               candidate.Placement.Anchor.Column < current.Placement.Anchor.Column))));

        private static IReadOnlyList<(int Width, int Height, bool Rotate)> Orientations(int width, int height) =>
            width == height
                ? [(width, height, false)]
                : [(width, height, false), (height, width, true)];
    }
}
