using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.Application.Services.LootScan;

/// <summary>
/// Produces reviewable loot advice from already reconstructed grids and evaluated item advice.
/// It never turns the result into game input.
/// </summary>
public sealed class LootScanDecisionService(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public LootScanResult Evaluate(LootScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
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
        var capacity = TryBuildCapacity(request.CarriedInventory, policies, out var built)
            ? built
            : null;
        if (capacity is null && request.CarriedInventory.Outcome == GridReconstructionOutcome.Complete)
        {
            issues.Add(new(
                LootScanIssueKind.CapacityUnavailable,
                "carried.capacity-unavailable",
                "Carried dimensions or occupied footprints are incomplete; placement is review-only."));
        }

        if (request.VisibleLoot.Recognition is { } visible)
        {
            // Capacity is one shared budget. Plan higher-precedence/value candidates first and
            // reserve every accepted placement so two TAKE answers cannot claim the same cells.
            foreach (var cell in visible.Cells
                         .OrderByDescending(item => PlanningPriority(item, recommendations))
                         .ThenByDescending(item => PlanningValue(item, recommendations, request.EvaluatedUtc))
                         .ThenBy(item => item.Anchor.Row)
                         .ThenBy(item => item.Anchor.Column))
            {
                decisions.Add(EvaluateResolved(request, cell, recommendations, capacity));
            }
        }

        foreach (var unresolved in request.VisibleLoot.UnresolvedCells
                     .OrderBy(item => item.Anchor.Row)
                     .ThenBy(item => item.Anchor.Column))
        {
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
            request.ArtifactId,
            request.DecodeRevision,
            request.InitiatingDeviceId,
            status,
            decisions.ToArray(),
            issues.ToArray(),
            [new("decision-planning", Math.Max(0, (long)elapsed.TotalMilliseconds))]);
    }

    private static LootScanDecision EvaluateResolved(
        LootScanRequest request,
        GridCellRecognition cell,
        IReadOnlyDictionary<GridCellAddress, LootScanCandidateRecommendation> recommendations,
        CapacityMap? capacity)
    {
        if (!request.IsReviewedFrameCurrent)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "capture.changed",
                "This advice belongs to an earlier screenshot revision.");
        }

        if (!TryExactItem(cell.Item, out _, out var width, out var height))
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

        var recommendation = evaluated.Recommendation;
        var advice = recommendation.Decision.Value;
        if (advice is null || recommendation.Decision.Status.Completeness != ResultCompleteness.Complete ||
            recommendation.Decision.Status.Freshness != FreshnessState.Current)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "recommendation.incomplete",
                "The recommendation has incomplete or stale evidence.",
                recommendation);
        }

        if (advice.Action == RecommendationAction.Leave)
        {
            return new(
                cell.Anchor,
                cell.Item,
                LootScanVerdict.Leave,
                [new("recommendation.leave", advice.Summary)],
                recommendation);
        }

        if (advice.Action is not (RecommendationAction.Take or RecommendationAction.Keep or RecommendationAction.Swap))
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "recommendation.requires-review",
                advice.Summary,
                recommendation);
        }

        if (request.CarriedInventory.Outcome != GridReconstructionOutcome.Complete || capacity is null)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "carried.capacity-incomplete",
                "The visible carried grid is not complete enough to prove a fit.",
                recommendation);
        }

        if (capacity.TryFindFree(width, height, out var freePlacement))
        {
            capacity.CommitPlacement(freePlacement!);
            return new(
                cell.Anchor,
                cell.Item,
                LootScanVerdict.Take,
                [new("capacity.visible-fit", "The item fits in verified visible carried space.")],
                recommendation,
                freePlacement);
        }

        var swap = capacity.FindBestSwap(width, height, request.EvaluatedUtc);
        if (swap is null)
        {
            return new(
                cell.Anchor,
                cell.Item,
                LootScanVerdict.Leave,
                [new("capacity.no-supported-fit", "No fit or bounded swap is supported by the visible carried grid.")],
                recommendation);
        }

        var incomingValue = ReliableMoney(advice.OpportunityCostRoubles, request.EvaluatedUtc);
        var economicOnly = advice.Reasons[0].Category == RecommendationReasonCategory.Economics;
        if (economicOnly && incomingValue is null)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "swap.incoming-value-unknown",
                "A swap was found, but the incoming value is not current enough to compare.",
                recommendation);
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
                recommendation,
                replacementCostRoubles: swap.ReplacementCostRoubles);
        }

        capacity.CommitSwap(swap);
        return new(
            cell.Anchor,
            cell.Item,
            LootScanVerdict.Swap,
            [new(
                "capacity.bounded-swap",
                $"The item fits after replacing {swap.Drops.Count} verified droppable item(s).")],
            recommendation,
            swap.Placement,
            swap.Drops,
            swap.ReplacementCostRoubles);
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

    private static long PlanningValue(
        GridCellRecognition cell,
        IReadOnlyDictionary<GridCellAddress, LootScanCandidateRecommendation> recommendations,
        DateTimeOffset evaluatedUtc)
    {
        if (!recommendations.TryGetValue(cell.Anchor, out var evaluated) ||
            evaluated.Recommendation.Decision.Value is not { } decision)
        {
            return long.MinValue;
        }

        return ReliableMoney(decision.OpportunityCostRoubles, evaluatedUtc) ?? long.MinValue;
    }

    private static LootScanDecision Review(
        GridCellAddress anchor,
        EvidencedValue<RecognizedItem> item,
        string code,
        string explanation,
        RecommendationResult? recommendation = null) => new(
            anchor,
            item,
            LootScanVerdict.Review,
            [new(code, explanation)],
            recommendation);

    private static bool TryBuildCapacity(
        GridReconstructionResult result,
        IReadOnlyDictionary<GridCellAddress, LootScanCarriedPolicy> policies,
        out CapacityMap? capacity)
    {
        capacity = null;
        if (result.Outcome != GridReconstructionOutcome.Complete || result.Recognition is not { } recognition ||
            recognition.Geometry.Rows.Value is not { } rows || recognition.Geometry.Columns.Value is not { } columns ||
            recognition.Geometry.Rows.Status.Completeness != ResultCompleteness.Complete ||
            recognition.Geometry.Columns.Status.Completeness != ResultCompleteness.Complete)
        {
            return false;
        }

        var items = new List<CapacityItem>(recognition.Cells.Count);
        foreach (var cell in recognition.Cells)
        {
            if (!TryExactItem(cell.Item, out _, out var width, out var height))
            {
                return false;
            }

            policies.TryGetValue(cell.Anchor, out var policy);
            items.Add(new(cell.Anchor, width, height, cell.Item, policy));
        }

        capacity = CapacityMap.Create(rows, columns, items);
        return capacity is not null;
    }

    private static bool TryExactItem(
        EvidencedValue<RecognizedItem> field,
        out RecognizedItem? item,
        out int width,
        out int height)
    {
        item = field.Value;
        width = item?.WidthCells.Value ?? 0;
        height = item?.HeightCells.Value ?? 0;
        return field.Status.Completeness == ResultCompleteness.Complete &&
            field.Status.Freshness == FreshnessState.Current &&
            item?.CanonicalId.Value is not null &&
            item.CanonicalId.Status.Completeness == ResultCompleteness.Complete &&
            item.WidthCells.Status.Completeness == ResultCompleteness.Complete &&
            item.HeightCells.Status.Completeness == ResultCompleteness.Complete &&
            width > 0 && height > 0;
    }

    private static long? ReliableMoney(EvidencedValue<long?> field, DateTimeOffset evaluatedUtc) =>
        field.Value is { } value &&
        field.Status.Completeness == ResultCompleteness.Complete &&
        field.Status.Freshness == FreshnessState.Current &&
        field.Provenance.EvidenceThroughUtc <= evaluatedUtc
            ? value
            : null;

    private sealed record CapacityItem(
        GridCellAddress Anchor,
        int Width,
        int Height,
        EvidencedValue<RecognizedItem> Evidence,
        LootScanCarriedPolicy? Policy);

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

        public bool TryFindFree(int width, int height, out LootScanPlacement? placement)
        {
            foreach (var orientation in Orientations(width, height))
            {
                for (var row = 0; row <= Rows - orientation.Height; row++)
                {
                    for (var column = 0; column <= Columns - orientation.Width; column++)
                    {
                        if (Blockers(row, column, orientation.Width, orientation.Height).Count == 0)
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

        public SwapOption? FindBestSwap(int width, int height, DateTimeOffset evaluatedUtc)
        {
            var options = new List<SwapOption>();
            foreach (var orientation in Orientations(width, height))
            {
                for (var row = 0; row <= Rows - orientation.Height; row++)
                {
                    for (var column = 0; column <= Columns - orientation.Width; column++)
                    {
                        var blockers = Blockers(row, column, orientation.Width, orientation.Height);
                        if (blockers.Count is < 1 or > LootScanPlannerLimits.MaximumSwapItems)
                        {
                            continue;
                        }

                        var drops = new List<LootScanDropItem>(blockers.Count);
                        long cost = 0;
                        var supported = true;
                        foreach (var blocker in blockers.Order())
                        {
                            if (blocker == PlannedIncoming)
                            {
                                supported = false;
                                break;
                            }

                            var carried = _items[blocker];
                            var policy = carried.Policy;
                            var value = policy is not null
                                ? ReliableMoney(policy.ReplacementValueRoubles, evaluatedUtc)
                                : null;
                            if (policy?.IsKnownDroppable != true || value is null || value > long.MaxValue - cost)
                            {
                                supported = false;
                                break;
                            }

                            cost += value.Value;
                            drops.Add(new(
                                carried.Anchor,
                                carried.Evidence,
                                value.Value,
                                policy.ReplacementValueRoubles.Provenance));
                        }

                        if (supported)
                        {
                            options.Add(new(
                                new(new(row, column), orientation.Width, orientation.Height, orientation.Rotate),
                                blockers.Order().ToArray(),
                                drops,
                                cost));
                        }
                    }
                }
            }

            return options
                .OrderBy(option => option.ReplacementCostRoubles)
                .ThenBy(option => option.Drops.Count)
                .ThenBy(option => option.Placement.RotateFromObserved)
                .ThenBy(option => option.Placement.Anchor.Row)
                .ThenBy(option => option.Placement.Anchor.Column)
                .FirstOrDefault();
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

        private HashSet<int> Blockers(int row, int column, int width, int height)
        {
            var blockers = new HashSet<int>();
            for (var currentRow = row; currentRow < row + height; currentRow++)
            {
                for (var currentColumn = column; currentColumn < column + width; currentColumn++)
                {
                    if (_occupied[currentRow, currentColumn] is { } index)
                    {
                        blockers.Add(index);
                    }
                }
            }

            return blockers;
        }

        private static IReadOnlyList<(int Width, int Height, bool Rotate)> Orientations(int width, int height) =>
            width == height
                ? [(width, height, false)]
                : [(width, height, false), (height, width, true)];
    }
}
