using System.Globalization;
using TarkovCompanion.Application.Services.Recommendations;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using RecommendationResult = TarkovCompanion.Core.Abstractions.V2.RecommendationResult;
using RecommendationAction = TarkovCompanion.Core.Abstractions.V2.RecommendationAction;
using V2RecommendationReason = TarkovCompanion.Core.Abstractions.V2.RecommendationReason;

namespace TarkovCompanion.Application.Services.LootScan;

/// <summary>
/// Produces reviewable loot advice from reconstructed grids and bound recommendation inputs. The
/// service evaluates those inputs itself and never turns the result into game input.
/// </summary>
public sealed class LootScanDecisionService
{
    private const int MaximumInputFieldIdLength = 256;
    private const int MaximumInputIdentifierLength = 2048;
    private const int MaximumInputDisplayNameLength = 512;
    private const int MaximumInputEvidenceEntries = 32;
    private const int MaximumInputStatusCodeLength = 256;

    private readonly TimeProvider _timeProvider;
    private readonly ExplainableRecommendationPolicy _policy;
    private readonly ExplainableRecommendationEngine _recommendationEngine;
    private readonly int _maximumPlacementCellVisits;
    private readonly int _maximumRecommendationWorkVisits;

    public LootScanDecisionService(
        TimeProvider? timeProvider = null,
        ExplainableRecommendationPolicy? policy = null,
        int maximumPlacementCellVisits = LootScanPlannerLimits.MaximumPlacementCellVisits,
        int maximumRecommendationWorkVisits = LootScanPlannerLimits.MaximumRecommendationWorkVisits)
    {
        if (maximumPlacementCellVisits is < 1 or > LootScanPlannerLimits.MaximumPlacementCellVisits)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPlacementCellVisits));
        }

        if (maximumRecommendationWorkVisits is < 1 or > LootScanPlannerLimits.MaximumRecommendationWorkVisits)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecommendationWorkVisits));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _policy = policy ?? ExplainableRecommendationPolicy.Default;
        _recommendationEngine = new ExplainableRecommendationEngine(_policy);
        _maximumPlacementCellVisits = maximumPlacementCellVisits;
        _maximumRecommendationWorkVisits = maximumRecommendationWorkVisits;
    }

    public LootScanResult Evaluate(LootScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var started = _timeProvider.GetTimestamp();
        var issues = new List<LootScanIssue>();
        var decisions = new List<LootScanDecision>();
        var placementBudget = new PlacementWorkBudget(_maximumPlacementCellVisits, cancellationToken);
        var recommendationBudget = new RecommendationWorkBudget(_maximumRecommendationWorkVisits, cancellationToken);

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

        var visibleCells = request.VisibleLoot.Recognition?.Cells.ToDictionary(item => item.Anchor) ?? [];
        var preparedRecommendations = PrepareRecommendations(
            request,
            visibleCells,
            recommendationBudget,
            cancellationToken);
        var recommendationGates = preparedRecommendations.ToDictionary(
            item => item.Key,
            item => item.Value.Gate);
        var policies = request.CarriedPolicies.ToDictionary(item => item.Anchor);
        var capacity = TryBuildCapacity(request, policies, cancellationToken, out var built)
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
            // A cell the reconstructor excluded (an overlap, a footprint off the grid) is not
            // among the recognized cells, so it stays here and is shown as review, never dropped.
            .Where(item => BlocksLootDecision(item) || !visibleCells.ContainsKey(item.Anchor))
            .GroupBy(item => item.Anchor)
            .ToDictionary(group => group.Key, group => group.First());
        if (request.VisibleLoot.Recognition is { } visible)
        {
            // Capacity is one shared budget. Plan higher-precedence/value candidates first and
            // reserve every accepted placement so two TAKE answers cannot claim the same cells.
            foreach (var cell in visible.Cells
                         .Where(item => !unresolvedByAnchor.ContainsKey(item.Anchor))
                         .OrderByDescending(item => PlanningPriority(item, recommendationGates))
                         .ThenByDescending(item => PlanningValue(item, preparedRecommendations))
                         .ThenBy(item => item.Anchor.Row)
                         .ThenBy(item => item.Anchor.Column))
            {
                cancellationToken.ThrowIfCancellationRequested();
                decisions.Add(EvaluateResolved(
                    request,
                    cell,
                    preparedRecommendations,
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
            InputFreshness(request, recommendationGates),
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
            [new("decision-planning", Math.Max(0, (long)elapsed.TotalMilliseconds))])
        {
            VisibleLootGrid = request.VisibleLoot.Recognition,
            CarriedGrid = request.CarriedInventory.Recognition,
        };
    }

    /// <summary>
    /// Whether what a cell is missing is something a loot decision needs.
    /// </summary>
    /// <remarks>
    /// The reconstructor lists a cell as unresolved when any attribute is unread, and a loot
    /// screen does not print whether an item is found-in-raid, so every cell the recognizer ever
    /// named arrived here unresolved and left as "review". The recommendation engine already
    /// asks for found-in-raid only when a need requires it and raises its own evidence issue
    /// then, so that one absence is left to it. Identity, footprint, rotation, count and
    /// condition still send a cell to review, because value and fit depend on them.
    /// </remarks>
    private static bool BlocksLootDecision(GridCellObservation unresolved)
    {
        static bool Read<T>(EvidencedValue<T> field) =>
            field.Status.Completeness == ResultCompleteness.Complete && field.Value is not null;

        return unresolved.Item.Value is not { } item ||
            unresolved.Item.Status.Completeness != ResultCompleteness.Complete ||
            !Read(item.CanonicalId) ||
            !Read(item.DisplayName) ||
            !Read(item.WidthCells) ||
            !Read(item.HeightCells) ||
            !Read(item.Rotated) ||
            !Read(item.Quantity) ||
            !Read(item.Condition);
    }

    private LootScanDecision EvaluateResolved(
        LootScanRequest request,
        GridCellRecognition cell,
        IReadOnlyDictionary<GridCellAddress, PreparedRecommendation> recommendations,
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

        if (!evaluated.Candidate.Binding.Matches(
                request.CaptureSessionId,
                request.ArtifactId,
                request.DecodeRevision,
                request.SourceContentSha256,
                cell.Anchor,
                item!.CanonicalId.Value!))
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "recommendation.binding-mismatch",
                "The advice belongs to a different capture revision or item; review this cell again.");
        }

        if (evaluated.Recommendation is null)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                evaluated.Gate.Code,
                evaluated.Gate.Explanation);
        }

        var recommendation = evaluated.Recommendation;
        var economics = evaluated.Economics!;
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

        var gate = evaluated.Gate;
        if (!gate.IsValid)
        {
            return Review(
                cell.Anchor,
                cell.Item,
                gate.Code,
                gate.Explanation,
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

        if (economicDominant && !IsEconomicDecisionConsistent(advice, economics))
        {
            return Review(
                cell.Anchor,
                cell.Item,
                "recommendation.economics-mismatch",
                "The recommendation does not match the bound sale channel, value band, or raid threshold.",
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
                    $"The supported swap gives up {swap.ReplacementCostRoubles.ToString("N0", CultureInfo.InvariantCulture)} roubles for no economic gain.")],
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
                $"The item fits after replacing {swap.Drops.Count.ToString(CultureInfo.InvariantCulture)} verified droppable item(s).")],
            recommendation: recommendation,
            economics: economics,
            placement: swap.Placement,
            drops: swap.Drops,
            replacementCostRoubles: swap.ReplacementCostRoubles);
    }

    private IReadOnlyDictionary<GridCellAddress, PreparedRecommendation> PrepareRecommendations(
        LootScanRequest request,
        IReadOnlyDictionary<GridCellAddress, GridCellRecognition> visibleCells,
        RecommendationWorkBudget budget,
        CancellationToken cancellationToken)
    {
        var prepared = new Dictionary<GridCellAddress, PreparedRecommendation>(request.Recommendations.Count);
        foreach (var candidate in request.Recommendations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!visibleCells.TryGetValue(candidate.Anchor, out var cell) ||
                    !TryExactItem(cell.Item, request.EvaluatedUtc, out var item, out var width, out var height) ||
                    candidate.ProfileScope != request.RecommendationContext.ProfileScope ||
                    !string.Equals(
                        candidate.DataSnapshotId,
                        request.RecommendationContext.DataSnapshotId,
                        StringComparison.Ordinal) ||
                    !candidate.Binding.Matches(
                        request.CaptureSessionId,
                        request.ArtifactId,
                        request.DecodeRevision,
                        request.SourceContentSha256,
                        candidate.Anchor,
                        item!.CanonicalId.Value!))
                {
                    prepared.Add(candidate.Anchor, PreparedRecommendation.Invalid(
                        candidate,
                        "recommendation.binding-mismatch",
                        "The recommendation inputs do not belong to this capture revision and item."));
                    continue;
                }

                var exactItem = item!;
                var candidateVisits = 0;
                if (!TryVisitRecommendationInputs(
                        request,
                        candidate,
                        exactItem,
                        budget,
                        ref candidateVisits))
                {
                    prepared.Add(candidate.Anchor, PreparedRecommendation.Invalid(
                        candidate,
                        "recommendation.input-too-large",
                        "Recommendation input identifiers or evidence exceed the bounded evaluation contract."));
                    continue;
                }

                var context = request.RecommendationContext;
                var engineRequest = new ExplainableRecommendationRequest(
                    candidate.RecommendationId,
                    exactItem.CanonicalId.Value!,
                    RecommendationUseCase.Loot,
                    request.EvaluatedUtc,
                    candidate.ProfileScope,
                    candidate.DataSnapshotId,
                    exactItem.FoundInRaid,
                    candidate.Profile,
                    candidate.Economics,
                    candidate.Scarcity,
                    context.Inventory,
                    request.CaptureSessionId,
                    context.RaidContext,
                    candidate.EventScope);
                var recommendation = _recommendationEngine.Evaluate(engineRequest, cancellationToken);
                var gate = recommendation.CaptureSessionId == request.CaptureSessionId &&
                           string.Equals(
                               recommendation.RecommendationId,
                               candidate.RecommendationId,
                               StringComparison.Ordinal)
                    ? ValidateRecommendation(
                        recommendation,
                        request.EvaluatedUtc,
                        budget,
                        ref candidateVisits)
                    : RecommendationGate.Invalid(
                        "recommendation.output-binding-mismatch",
                        "The evaluated recommendation did not preserve its request identity.");
                var economics = ProjectEconomics(
                    candidate.Economics,
                    request.EvaluatedUtc,
                    checked(width * height));
                prepared.Add(candidate.Anchor, new(candidate, recommendation, economics, gate));
            }
            catch (RecommendationWorkBudgetExceededException)
            {
                prepared.Add(candidate.Anchor, PreparedRecommendation.Invalid(
                    candidate,
                    "recommendation.validation-budget-exhausted",
                    "Recommendation evidence exceeded the bounded validation budget."));
            }
            catch (ArgumentException)
            {
                // Structurally valid outer scan data can still contain an input combination the
                // recommendation engine cannot represent inside its bounded evidence contract.
                // One bad item must not abort decisions for the rest of the reviewed screenshot.
                prepared.Add(candidate.Anchor, PreparedRecommendation.Invalid(
                    candidate,
                    "recommendation.evaluation-invalid",
                    "The bound recommendation inputs could not be evaluated safely."));
            }
        }

        return prepared;
    }

    private bool TryVisitRecommendationInputs(
        LootScanRequest request,
        LootScanCandidateRecommendation candidate,
        RecognizedItem item,
        RecommendationWorkBudget budget,
        ref int candidateVisits)
    {
        var context = request.RecommendationContext;
        if (!Within(candidate.RecommendationId, LootScanCandidateRecommendation.MaximumRecommendationIdLength) ||
            !Within(context.DataSnapshotId, LootScanRecommendationContext.MaximumDataSnapshotIdLength) ||
            !Within(context.ProfileScope.Generation, LootScanRecommendationContext.MaximumProfileDescriptorLength) ||
            !Within(context.ProfileScope.GameMode, LootScanRecommendationContext.MaximumProfileDescriptorLength) ||
            !Within(candidate.Profile.Status.Code, MaximumInputStatusCodeLength) ||
            !TryVisitProvenance(candidate.Profile.Provenance, budget, ref candidateVisits) ||
            !TryVisitField(candidate.Profile.ExplicitAction, budget, ref candidateVisits) ||
            !TryVisitField(candidate.Profile.ProtectedItem, budget, ref candidateVisits) ||
            !TryVisitField(candidate.Profile.Pinned, budget, ref candidateVisits) ||
            !TryVisitField(candidate.Profile.Wishlist, budget, ref candidateVisits) ||
            !TryVisitField(candidate.Profile.EventState.State, budget, ref candidateVisits) ||
            !TryVisitField(item.FoundInRaid, budget, ref candidateVisits) ||
            !TryVisitField(candidate.Economics.FleaGrossRoubles, budget, ref candidateVisits) ||
            !TryVisitField(candidate.Economics.FleaFeeRoubles, budget, ref candidateVisits) ||
            !TryVisitField(candidate.Economics.FleaNetRoubles, budget, ref candidateVisits) ||
            !TryVisitField(candidate.Economics.TraderRoubles, budget, ref candidateVisits) ||
            !TryVisitField(candidate.Economics.OccupiedSquares, budget, ref candidateVisits) ||
            !TryVisitField(candidate.Economics.ConditionFraction, budget, ref candidateVisits) ||
            !TryVisitField(candidate.Scarcity.Obtainability, budget, ref candidateVisits))
        {
            return false;
        }

        foreach (var need in candidate.Profile.Needs)
        {
            budget.Visit(ref candidateVisits);
            if (!Within(need.NeedId, LootScanCandidateRecommendation.MaximumNeedIdLength) ||
                !Within(need.DisplayName, LootScanCandidateRecommendation.MaximumNeedDisplayNameLength) ||
                !Within(need.Status.Code, MaximumInputStatusCodeLength) ||
                !TryVisitProvenance(need.Provenance, budget, ref candidateVisits))
            {
                return false;
            }
        }

        if (context.RaidContext is { } raidContext &&
            (!TryVisitField(raidContext.Phase, budget, ref candidateVisits) ||
             !TryVisitField(raidContext.Risk, budget, ref candidateVisits)))
        {
            return false;
        }

        if (context.Inventory is { } inventory)
        {
            if (!Within(inventory.Status.Code, MaximumInputStatusCodeLength) ||
                !Within(inventory.Coverage.Description, MaximumInputIdentifierLength) ||
                !TryVisitProvenance(inventory.Provenance, budget, ref candidateVisits))
            {
                return false;
            }

            if (inventory.Find(item.CanonicalId.Value!) is { } observed &&
                (!TryVisitField(observed.TotalQuantity, budget, ref candidateVisits) ||
                 !TryVisitField(observed.FoundInRaidQuantity, budget, ref candidateVisits)))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryVisitField<T>(
        EvidencedValue<T> field,
        RecommendationWorkBudget budget,
        ref int candidateVisits)
    {
        budget.Visit(ref candidateVisits);
        if (!Within(field.FieldId, MaximumInputFieldIdLength) ||
            !Within(field.Status.Code, MaximumInputStatusCodeLength) ||
            field.Candidates.Count > MaximumInputEvidenceEntries ||
            field.Corrections.Count > MaximumInputEvidenceEntries ||
            !TryVisitProvenance(field.Provenance, budget, ref candidateVisits))
        {
            return false;
        }

        foreach (var evidenceCandidate in field.Candidates)
        {
            budget.Visit(ref candidateVisits);
            if (!Within(evidenceCandidate.CandidateId, MaximumInputFieldIdLength) ||
                !Within(evidenceCandidate.DisplayName, MaximumInputDisplayNameLength) ||
                !TryVisitProvenance(evidenceCandidate.Provenance, budget, ref candidateVisits))
            {
                return false;
            }
        }

        foreach (var correction in field.Corrections)
        {
            budget.Visit(ref candidateVisits);
            if (!Within(correction.OriginIdentifier, MaximumInputFieldIdLength) ||
                !Within(correction.Reason, 1024))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryVisitProvenance(
        EvidenceProvenance provenance,
        RecommendationWorkBudget budget,
        ref int candidateVisits)
    {
        var pending = new Stack<EvidenceProvenance>();
        pending.Push(provenance);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            budget.Visit(ref candidateVisits);
            if (!Within(current.SourceIdentifier, MaximumInputIdentifierLength) ||
                !Within(current.Reference, MaximumInputIdentifierLength) ||
                !Within(current.Producer.Name, MaximumInputFieldIdLength) ||
                !Within(current.Producer.Version, MaximumInputFieldIdLength) ||
                !Within(current.Producer.ModelVersion, MaximumInputFieldIdLength) ||
                !Within(current.Confidence.CalibrationReference, MaximumInputIdentifierLength) ||
                !Within(current.Coverage?.Description, MaximumInputIdentifierLength))
            {
                return false;
            }

            for (var index = current.Inputs.Count - 1; index >= 0; index--)
            {
                pending.Push(current.Inputs[index]);
            }
        }

        return true;
    }

    private static bool Within(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength;

    private RecommendationGate ValidateRecommendation(
        RecommendationResult recommendation,
        DateTimeOffset evaluatedUtc,
        RecommendationWorkBudget budget,
        ref int candidateVisits)
    {
        if (!V2ContractVersion.Current.CanRead(recommendation.ContractVersion))
        {
            return RecommendationGate.Invalid(
                "recommendation.contract-version-unsupported",
                "The recommendation uses a contract version this planner cannot read.");
        }

        if (!string.Equals(recommendation.RulesetVersion, _policy.RulesetVersion, StringComparison.Ordinal))
        {
            return RecommendationGate.Invalid(
                "recommendation.ruleset-mismatch",
                "The advice was produced by a different ruleset version.");
        }

        var decision = recommendation.Decision;
        var freshness = decision.Status.Freshness;
        if (decision.Value is not { } advice ||
            decision.Status.Completeness != ResultCompleteness.Complete ||
            freshness != FreshnessState.Current ||
            decision.Candidates.Count > 0)
        {
            return RecommendationGate.Invalid(
                "recommendation.incomplete",
                "The recommendation has incomplete, ambiguous, or stale evidence.",
                freshness);
        }

        if (advice.Reasons.Count > LootScanPlannerLimits.MaximumRecommendationReasons ||
            advice.ChangesTheAnswer.Count > LootScanPlannerLimits.MaximumRecommendationSensitivities)
        {
            return RecommendationGate.Invalid(
                "recommendation.metadata-too-large",
                "The recommendation contains more reasons or alternatives than the planner can review safely.");
        }

        if (advice.Reasons.Count == 0)
        {
            return RecommendationGate.Invalid(
                "recommendation.reason-missing",
                "The recommendation does not name the rule that produced its action.");
        }

        budget.Visit(ref candidateVisits);
        var mappedReasons = new List<MappedRecommendationReason>(advice.Reasons.Count);
        foreach (var reason in advice.Reasons)
        {
            budget.Visit(ref candidateVisits);
            if (!TryMapRule(reason, out var rule))
            {
                return RecommendationGate.Invalid(
                    "recommendation.precedence-mismatch",
                    "A recommendation reason is not defined by the active ruleset.");
            }

            var expectedPriority = _policy.PriorityOf(rule);
            if (reason.Priority != expectedPriority)
            {
                return RecommendationGate.Invalid(
                    "recommendation.precedence-mismatch",
                    "A recommendation reason carries precedence that does not match the active ruleset.");
            }

            mappedReasons.Add(new(reason, rule, expectedPriority));
        }

        foreach (var _ in advice.ChangesTheAnswer)
        {
            budget.Visit(ref candidateVisits);
        }

        var canonicalOrder = mappedReasons
            .OrderByDescending(item => item.ExpectedPriority)
            .ThenBy(item => item.Reason.Code, StringComparer.Ordinal)
            .ToArray();
        if (!mappedReasons.Select(item => item.Reason).SequenceEqual(canonicalOrder.Select(item => item.Reason)))
        {
            return RecommendationGate.Invalid(
                "recommendation.precedence-mismatch",
                "Recommendation reasons are not in the deterministic order defined by the active ruleset.");
        }

        var dominant = canonicalOrder[0];
        if (!IsActionConsistent(dominant.Rule, advice.Action))
        {
            return RecommendationGate.Invalid(
                "recommendation.action-mismatch",
                "The recommendation action does not match its highest-precedence reason.");
        }

        if (!TryValidateProvenance(
                decision.Provenance,
                evaluatedUtc,
                MaximumAge(dominant.Rule),
                includeInputs: false,
                budget,
                ref candidateVisits,
                out var failure))
        {
            return RecommendationGate.FromEvidenceFailure(failure);
        }

        // The decision root carries the dominant rule's expiry. Its mixed input tree still has to
        // be current-or-past and trustworthy, but each reason below owns its semantic TTL. Applying
        // the root TTL recursively would expire durable profile facts or keep volatile facts alive.
        if (!TryValidateProvenance(
                decision.Provenance,
                evaluatedUtc,
                maximumAge: null,
                includeInputs: true,
                budget,
                ref candidateVisits,
                out failure))
        {
            return RecommendationGate.FromEvidenceFailure(failure);
        }

        foreach (var mapped in mappedReasons)
        {
            if (!TryValidateProvenance(
                    mapped.Reason.Provenance,
                    evaluatedUtc,
                    MaximumAge(mapped.Rule),
                    includeInputs: true,
                    budget,
                    ref candidateVisits,
                    out failure))
            {
                return RecommendationGate.FromEvidenceFailure(failure);
            }
        }

        var opportunityCost = advice.OpportunityCostRoubles;
        var hasOpportunityCost = opportunityCost.Value is not null ||
            opportunityCost.Candidates.Count > 0 ||
            opportunityCost.Corrections.Count > 0;
        if (hasOpportunityCost)
        {
            if (opportunityCost.Status.Completeness != ResultCompleteness.Complete ||
                opportunityCost.Status.Freshness != FreshnessState.Current ||
                opportunityCost.Candidates.Count > 0 ||
                advice.OpportunityCostLineage is not { } lineage)
            {
                return RecommendationGate.Invalid(
                    "recommendation.opportunity-cost-incomplete",
                    "Opportunity-cost evidence is incomplete, ambiguous, or stale.",
                    opportunityCost.Status.Freshness);
            }

            if (!TryValidateProvenance(
                    opportunityCost.Provenance,
                    evaluatedUtc,
                    _policy.MaximumPriceAge,
                    includeInputs: false,
                    budget,
                    ref candidateVisits,
                    out failure) ||
                !TryValidateProvenance(
                    lineage.Price,
                    evaluatedUtc,
                    _policy.MaximumPriceAge,
                    includeInputs: true,
                    budget,
                    ref candidateVisits,
                    out failure) ||
                !TryValidateProvenance(
                    lineage.Footprint,
                    evaluatedUtc,
                    _policy.MaximumInventoryAge,
                    includeInputs: true,
                    budget,
                    ref candidateVisits,
                    out failure))
            {
                return RecommendationGate.FromEvidenceFailure(failure);
            }

            foreach (var correction in opportunityCost.Corrections)
            {
                budget.Visit(ref candidateVisits);
                // A correction revises the value but has no provenance of its own. The original
                // price lineage therefore remains the conservative age clock; a correction cannot
                // renew old evidence, and only a correction from the future is invalid by itself.
                if (correction.CorrectedUtc > evaluatedUtc)
                {
                    return RecommendationGate.FromEvidenceFailure(ProvenanceFailure.Expired);
                }
            }
        }

        return RecommendationGate.Valid(canonicalOrder[0].ExpectedPriority);
    }

    private bool TryValidateProvenance(
        EvidenceProvenance provenance,
        DateTimeOffset evaluatedUtc,
        TimeSpan? maximumAge,
        bool includeInputs,
        RecommendationWorkBudget budget,
        ref int candidateVisits,
        out ProvenanceFailure failure)
    {
        var pending = new Stack<EvidenceProvenance>();
        pending.Push(provenance);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            budget.Visit(ref candidateVisits);
            if (IsOutsideTimeWindow(current, evaluatedUtc, maximumAge))
            {
                failure = ProvenanceFailure.Expired;
                return false;
            }

            if (current.SourceClass == EvidenceSourceClass.Unknown ||
                current.Confidence.Score is not { } score ||
                score < _policy.MinimumEvidenceConfidence)
            {
                failure = ProvenanceFailure.Unreliable;
                return false;
            }

            if (includeInputs)
            {
                for (var index = current.Inputs.Count - 1; index >= 0; index--)
                {
                    pending.Push(current.Inputs[index]);
                }
            }
        }

        failure = ProvenanceFailure.None;
        return true;
    }

    private bool TryMapRule(V2RecommendationReason reason, out ExplainableRecommendationRule rule)
    {
        rule = reason.Category switch
        {
            RecommendationReasonCategory.ExplicitOverride when reason.Code == "override.explicit" =>
                ExplainableRecommendationRule.ExplicitOverride,
            RecommendationReasonCategory.Safety when reason.Code == "event.allergic" => ExplainableRecommendationRule.EventAllergy,
            RecommendationReasonCategory.Safety when reason.Code == "item.protected" => ExplainableRecommendationRule.ProtectedItem,
            RecommendationReasonCategory.Safety when reason.Code == "event.untested" => ExplainableRecommendationRule.EventUntested,
            RecommendationReasonCategory.Safety when reason.Code == "event.safe" => ExplainableRecommendationRule.EventSafe,
            RecommendationReasonCategory.Safety when IsActiveRaidReasonCode(reason.Code) =>
                ExplainableRecommendationRule.RaidContext,
            RecommendationReasonCategory.CurrentFoundInRaidQuest
                when HasIdentifierSuffix(reason.Code, "need.quest-current-fir.") =>
                ExplainableRecommendationRule.CurrentFoundInRaidQuest,
            RecommendationReasonCategory.CurrentQuest
                when HasIdentifierSuffix(reason.Code, "need.quest-current.") =>
                ExplainableRecommendationRule.CurrentQuest,
            RecommendationReasonCategory.FutureQuest
                when HasIdentifierSuffix(reason.Code, "need.quest-future.") =>
                ExplainableRecommendationRule.FutureQuest,
            RecommendationReasonCategory.Hideout
                when HasIdentifierSuffix(reason.Code, "need.hideout.") =>
                ExplainableRecommendationRule.Hideout,
            RecommendationReasonCategory.CraftOrBarter
                when HasIdentifierSuffix(reason.Code, "need.craft-barter.") =>
                ExplainableRecommendationRule.CraftOrBarter,
            RecommendationReasonCategory.SpecialistUtility
                when HasIdentifierSuffix(reason.Code, "need.specialist.") =>
                ExplainableRecommendationRule.SpecialistUtility,
            RecommendationReasonCategory.PinOrWishlist when reason.Code == "profile.pinned" => ExplainableRecommendationRule.Pin,
            RecommendationReasonCategory.PinOrWishlist when reason.Code == "profile.wishlist" => ExplainableRecommendationRule.Wishlist,
            RecommendationReasonCategory.ScarcityOrObtainability
                when IsActiveScarcityReasonCode(reason.Code) =>
                ExplainableRecommendationRule.Scarcity,
            RecommendationReasonCategory.Economics
                when IsEconomicValueReasonCode(reason.Code) =>
                ExplainableRecommendationRule.Economics,
            RecommendationReasonCategory.EvidenceQuality => ExplainableRecommendationRule.EvidenceQuality,
            _ => default,
        };
        return rule != default;
    }

    private bool IsActiveRaidReasonCode(string code) => code switch
    {
        "raid.phase.late" =>
            (int)_policy.LootThresholds.RequiredBand(RecommendationRaidPhase.Late) > (int)_policy.LootThresholds.Normal,
        "raid.phase.extracting" =>
            (int)_policy.LootThresholds.RequiredBand(RecommendationRaidPhase.Extracting) > (int)_policy.LootThresholds.Normal,
        "raid.risk.elevated" =>
            (int)_policy.LootThresholds.RequiredBand(RecommendationRaidRisk.Elevated) > (int)_policy.LootThresholds.Normal,
        "raid.risk.high" =>
            (int)_policy.LootThresholds.RequiredBand(RecommendationRaidRisk.High) > (int)_policy.LootThresholds.Normal,
        "raid.risk.critical" =>
            (int)_policy.LootThresholds.RequiredBand(RecommendationRaidRisk.Critical) > (int)_policy.LootThresholds.Normal,
        _ => false,
    };

    private bool IsActiveScarcityReasonCode(string code)
    {
        const string prefix = "scarcity.obtainability.";
        if (!code.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var band = code[prefix.Length..] switch
        {
            "abundant" => RecommendationObtainabilityBand.Abundant,
            "available" => RecommendationObtainabilityBand.Available,
            "limited" => RecommendationObtainabilityBand.Limited,
            "scarce" => RecommendationObtainabilityBand.Scarce,
            _ => default,
        };
        return band != default && (int)band >= (int)_policy.MinimumScarcityToKeep;
    }

    private static bool HasIdentifierSuffix(string code, string prefix) =>
        code.StartsWith(prefix, StringComparison.Ordinal) && code.Length > prefix.Length;

    private static bool IsEconomicValueReasonCode(string code)
    {
        const string fleaPrefix = "economics.flea-net.";
        const string traderPrefix = "economics.trader.";
        var band = code.StartsWith(fleaPrefix, StringComparison.Ordinal)
            ? code[fleaPrefix.Length..]
            : code.StartsWith(traderPrefix, StringComparison.Ordinal)
                ? code[traderPrefix.Length..]
                : string.Empty;
        return band is "low" or "moderate" or "high" or "exceptional";
    }

    private static bool IsActionConsistent(
        ExplainableRecommendationRule dominantRule,
        RecommendationAction action) => dominantRule switch
    {
        // An explicit rule may intentionally select any contract action. Actions the Loot Scan
        // surface cannot execute are still accepted as authentic advice and projected to REVIEW.
        ExplainableRecommendationRule.ExplicitOverride => true,
        ExplainableRecommendationRule.EventAllergy => action == RecommendationAction.AvoidConsume,
        ExplainableRecommendationRule.EventUntested or ExplainableRecommendationRule.EvidenceQuality =>
            action == RecommendationAction.Review,
        ExplainableRecommendationRule.EventSafe => action == RecommendationAction.UseSoon,
        ExplainableRecommendationRule.ProtectedItem or
        ExplainableRecommendationRule.CurrentFoundInRaidQuest or
        ExplainableRecommendationRule.CurrentQuest or
        ExplainableRecommendationRule.FutureQuest or
        ExplainableRecommendationRule.Hideout or
        ExplainableRecommendationRule.CraftOrBarter or
        ExplainableRecommendationRule.SpecialistUtility or
        ExplainableRecommendationRule.Pin or
        ExplainableRecommendationRule.Wishlist or
        ExplainableRecommendationRule.Scarcity => action == RecommendationAction.Take,
        ExplainableRecommendationRule.RaidContext or ExplainableRecommendationRule.Economics =>
            action is RecommendationAction.Take or RecommendationAction.Leave,
        _ => false,
    };

    private TimeSpan? MaximumAge(ExplainableRecommendationRule rule) => rule switch
    {
        ExplainableRecommendationRule.EventAllergy or
        ExplainableRecommendationRule.ExplicitOverride or
        ExplainableRecommendationRule.ProtectedItem or
        ExplainableRecommendationRule.Pin or
        ExplainableRecommendationRule.Wishlist or
        ExplainableRecommendationRule.EventUntested or
        ExplainableRecommendationRule.EventSafe => null,
        ExplainableRecommendationRule.Scarcity => _policy.MaximumScarcityAge,
        ExplainableRecommendationRule.RaidContext => _policy.MaximumRaidContextAge,
        ExplainableRecommendationRule.Economics => _policy.MaximumPriceAge,
        _ => _policy.MaximumInventoryAge,
    };

    private static int PlanningPriority(
        GridCellRecognition cell,
        IReadOnlyDictionary<GridCellAddress, RecommendationGate> recommendationGates)
    {
        if (!recommendationGates.TryGetValue(cell.Anchor, out var gate) || !gate.IsValid)
        {
            return int.MinValue;
        }

        return gate.PlanningPriority;
    }

    private long PlanningValue(
        GridCellRecognition cell,
        IReadOnlyDictionary<GridCellAddress, PreparedRecommendation> recommendations)
    {
        if (!recommendations.TryGetValue(cell.Anchor, out var evaluated) ||
            !evaluated.Gate.IsValid ||
            evaluated.Recommendation?.Decision.Value is null ||
            evaluated.Economics is null)
        {
            return long.MinValue;
        }

        var economics = evaluated.Economics!;
        return economics.Status.Completeness == ResultCompleteness.Complete
            ? economics.BestNetValueRoubles ?? long.MinValue
            : long.MinValue;
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
        CancellationToken cancellationToken,
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
            cancellationToken.ThrowIfCancellationRequested();
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
                IsReliable(policy.ProtectedItem, request.EvaluatedUtc, maximumAge: null, requireComplete: true);
            var pinnedReliable = policy is not null &&
                IsReliable(policy.Pinned, request.EvaluatedUtc, maximumAge: null, requireComplete: true);
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
        var flea = ResolvePriceChannel(economics.FleaNetRoubles, evaluatedUtc);
        var trader = ResolvePriceChannel(economics.TraderRoubles, evaluatedUtc);
        var squares = ReliableValue(economics.OccupiedSquares, evaluatedUtc, _policy.MaximumPriceAge);
        var freshness = EconomicsFreshness(economics, evaluatedUtc);
        if (!flea.IsResolved || !trader.IsResolved ||
            (!flea.HasValue && !trader.HasValue) ||
            squares is null || squares.Value != observedSquares)
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

        var rawInputs = new[]
        {
            flea.Provenance!,
            trader.Provenance!,
            economics.OccupiedSquares.Provenance,
        };
        if (rawInputs.Any(ContainsModelledEstimate))
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

        if (!TryComposeProvenance(
                $"loot-scan://economic-price/flea-net/{(flea.HasValue ? "available" : "unavailable")}",
                evaluatedUtc,
                [flea.Provenance!],
                out var fleaRole) ||
            !TryComposeProvenance(
                $"loot-scan://economic-price/trader/{(trader.HasValue ? "available" : "unavailable")}",
                evaluatedUtc,
                [trader.Provenance!],
                out var traderRole) ||
            !TryComposeProvenance(
                "loot-scan://economic-footprint",
                evaluatedUtc,
                [economics.OccupiedSquares.Provenance],
                out var footprintRole) ||
            !TryComposeProvenance(
                "loot-scan://value-per-square",
                evaluatedUtc,
                [fleaRole!, traderRole!, footprintRole!],
                out var provenance))
        {
            return new(
                economics,
                new ResultStatus(ResultCompleteness.Partial, freshness, "economics.lineage-too-complex"),
                null,
                null,
                null,
                null,
                null);
        }

        var useFlea = flea.HasValue && (!trader.HasValue || flea.Value >= trader.Value);
        var total = useFlea ? flea.Value.GetValueOrDefault() : trader.Value.GetValueOrDefault();
        var perSquare = total / squares.Value;
        return new(
            economics,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "economics.complete"),
            total,
            perSquare,
            _policy.ValueBands.Classify(perSquare),
            useFlea ? "flea-net" : "trader",
            provenance);
    }

    private PriceChannelResolution ResolvePriceChannel(
        EvidencedValue<long?> field,
        DateTimeOffset evaluatedUtc)
    {
        if (field.Value is { } value &&
            IsReliable(field, evaluatedUtc, _policy.MaximumPriceAge, requireComplete: true))
        {
            return PriceChannelResolution.Available(value, field.Provenance);
        }

        var declaresUnavailable = field.Value is null &&
            field.Candidates.Count == 0 &&
            field.Corrections.Count == 0 &&
            field.Status.Completeness == ResultCompleteness.Unavailable &&
            field.Status.Freshness == FreshnessState.Current &&
            IsReliableProvenance(field.Provenance, evaluatedUtc, _policy.MaximumPriceAge);
        return declaresUnavailable
            ? PriceChannelResolution.Unavailable(field.Provenance)
            : PriceChannelResolution.Unresolved();
    }

    private static bool TryComposeProvenance(
        string sourceIdentifier,
        DateTimeOffset evaluatedUtc,
        IReadOnlyList<EvidenceProvenance> inputs,
        out EvidenceProvenance? provenance)
    {
        provenance = null;
        if (inputs.Count == 0 || inputs.Any(ContainsModelledEstimate) || !CanComposeProvenance(inputs))
        {
            return false;
        }

        if (!TryMinimumConfidence(inputs, out var minimumConfidence))
        {
            return false;
        }

        provenance = new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            sourceIdentifier,
            evaluatedUtc,
            new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, minimumConfidence),
            new ProducerIdentity("loot-scan-planner", "2"),
            generatedUtc: evaluatedUtc,
            inputs: inputs);
        return true;
    }

    private static bool TryMinimumConfidence(
        IReadOnlyList<EvidenceProvenance> inputs,
        out double minimumConfidence)
    {
        minimumConfidence = 1;
        var pending = new Stack<EvidenceProvenance>(inputs.Reverse());
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.Confidence.Score is not { } score)
            {
                return false;
            }

            minimumConfidence = Math.Min(minimumConfidence, score);
            for (var index = current.Inputs.Count - 1; index >= 0; index--)
            {
                pending.Push(current.Inputs[index]);
            }
        }

        return true;
    }

    private T? ReliableValue<T>(EvidencedValue<T?> field, DateTimeOffset evaluatedUtc, TimeSpan maximumAge)
        where T : struct =>
        field.Value is { } value && IsReliable(field, evaluatedUtc, maximumAge, requireComplete: true) ? value : null;

    private bool IsReliable<T>(
        EvidencedValue<T> field,
        DateTimeOffset evaluatedUtc,
        TimeSpan? maximumAge,
        bool requireComplete) =>
        (!requireComplete || field.Status.Completeness == ResultCompleteness.Complete) &&
        (field.Status.Completeness is ResultCompleteness.Complete or ResultCompleteness.Partial) &&
        field.Candidates.Count == 0 &&
        field.Status.Freshness == FreshnessState.Current &&
        !HasFutureCorrection(field, evaluatedUtc) &&
        IsReliableProvenance(field.Provenance, evaluatedUtc, maximumAge);

    private bool IsReliableProvenance(
        EvidenceProvenance provenance,
        DateTimeOffset evaluatedUtc,
        TimeSpan? maximumAge)
    {
        var pending = new Stack<EvidenceProvenance>();
        pending.Push(provenance);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.SourceClass == EvidenceSourceClass.Unknown ||
                IsOutsideTimeWindow(current, evaluatedUtc, maximumAge) ||
                current.Confidence.Score is not { } score ||
                score < _policy.MinimumEvidenceConfidence)
            {
                return false;
            }

            foreach (var input in current.Inputs)
            {
                pending.Push(input);
            }
        }

        return true;
    }

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
        TimeSpan? maximumAge) =>
        field.Status.Freshness == FreshnessState.Stale ||
        HasFutureCorrection(field, evaluatedUtc) ||
        IsOutsideTimeWindow(field.Provenance, evaluatedUtc, maximumAge);

    private static bool HasFutureCorrection<T>(
        EvidencedValue<T> field,
        DateTimeOffset evaluatedUtc) =>
        field.Corrections.Any(correction => correction.CorrectedUtc > evaluatedUtc);

    private static bool IsOutsideTimeWindow(
        EvidenceProvenance provenance,
        DateTimeOffset evaluatedUtc,
        TimeSpan? maximumAge) =>
        provenance.EvidenceThroughUtc > evaluatedUtc ||
        maximumAge is { } age && evaluatedUtc - provenance.EvidenceThroughUtc > age;

    private static bool ContainsModelledEstimate(EvidenceProvenance provenance) =>
        provenance.SourceClass == EvidenceSourceClass.ModelledEstimate ||
        provenance.Inputs.Any(ContainsModelledEstimate);

    private static bool CanComposeProvenance(IReadOnlyList<EvidenceProvenance> inputs)
    {
        var inputCount = 0;
        var pending = new Stack<(EvidenceProvenance Provenance, int Depth)>();
        for (var index = inputs.Count - 1; index >= 0; index--)
        {
            pending.Push((inputs[index], 2));
        }

        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            inputCount++;
            if (depth > EvidenceProvenance.MaxInputDepth || inputCount > EvidenceProvenance.MaxInputCount)
            {
                return false;
            }

            for (var index = current.Inputs.Count - 1; index >= 0; index--)
            {
                pending.Push((current.Inputs[index], depth + 1));
            }
        }

        return true;
    }

    private FreshnessState InputFreshness(
        LootScanRequest request,
        IReadOnlyDictionary<GridCellAddress, RecommendationGate> recommendationGates)
    {
        if (HasStaleInput(request, recommendationGates))
        {
            return FreshnessState.Stale;
        }

        return HasUnknownFreshness(request, recommendationGates)
            ? FreshnessState.Unknown
            : FreshnessState.Current;
    }

    private bool HasStaleInput(
        LootScanRequest request,
        IReadOnlyDictionary<GridCellAddress, RecommendationGate> recommendationGates)
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

        return recommendationGates.Values.Any(gate => gate.Freshness == FreshnessState.Stale) ||
            GridIsStale(request.VisibleLoot) ||
            GridIsStale(request.CarriedInventory) ||
            request.Recommendations.Any(item => EconomicsIsStale(item.Economics)) ||
            request.CarriedPolicies.Any(policy =>
                IsStaleOrExpired(policy.ProtectedItem, request.EvaluatedUtc, maximumAge: null) ||
                IsStaleOrExpired(policy.Pinned, request.EvaluatedUtc, maximumAge: null) ||
                IsStaleOrExpired(policy.ReplacementValueRoubles, request.EvaluatedUtc, _policy.MaximumPriceAge));
    }

    private static bool HasUnknownFreshness(
        LootScanRequest request,
        IReadOnlyDictionary<GridCellAddress, RecommendationGate> recommendationGates)
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

        return recommendationGates.Values.Any(gate => gate.Freshness == FreshnessState.Unknown) ||
            GridUnknown(request.VisibleLoot) ||
            GridUnknown(request.CarriedInventory) ||
            request.Recommendations.Any(item => EconomicsUnknown(item.Economics)) ||
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

    private TimeSpan? MaximumRecommendationAge(RecommendationDecision advice)
    {
        var dominant = advice.Reasons[0];
        if (TryMapRule(dominant, out var rule))
        {
            return MaximumAge(rule);
        }

        return _policy.MaximumInventoryAge;
    }

    private bool IsEconomicDecisionConsistent(
        RecommendationDecision advice,
        LootScanEconomicProjection economics)
    {
        if (economics.SelectedPriceBasis is not { } basis || economics.ValueBand is not { } band)
        {
            return false;
        }

        var expectedEconomicCode = $"economics.{basis}.{band.ToString().ToLowerInvariant()}";
        var economicReasons = advice.Reasons
            .Where(reason => reason.Category == RecommendationReasonCategory.Economics)
            .ToArray();
        if (economicReasons.Length != 1 ||
            !string.Equals(economicReasons[0].Code, expectedEconomicCode, StringComparison.Ordinal))
        {
            return false;
        }

        var requiredBand = _policy.LootThresholds.Normal;
        foreach (var reason in advice.Reasons.Where(reason => reason.Category == RecommendationReasonCategory.Safety))
        {
            var reasonBand = reason.Code switch
            {
                "raid.phase.late" => _policy.LootThresholds.RequiredBand(RecommendationRaidPhase.Late),
                "raid.phase.extracting" => _policy.LootThresholds.RequiredBand(RecommendationRaidPhase.Extracting),
                "raid.risk.elevated" => _policy.LootThresholds.RequiredBand(RecommendationRaidRisk.Elevated),
                "raid.risk.high" => _policy.LootThresholds.RequiredBand(RecommendationRaidRisk.High),
                "raid.risk.critical" => _policy.LootThresholds.RequiredBand(RecommendationRaidRisk.Critical),
                _ => requiredBand,
            };
            if ((int)reasonBand > (int)requiredBand)
            {
                requiredBand = reasonBand;
            }
        }

        var expectedAction = (int)band >= (int)requiredBand
            ? RecommendationAction.Take
            : RecommendationAction.Leave;
        return advice.Action == expectedAction;
    }

    private bool IsEconomicLootAdvice(RecommendationDecision advice)
    {
        var dominant = advice.Reasons[0];
        return TryMapRule(dominant, out var rule) &&
            rule is ExplainableRecommendationRule.Economics or ExplainableRecommendationRule.RaidContext;
    }

    private enum ProvenanceFailure
    {
        None = 0,
        Expired,
        Unreliable,
    }

    private sealed record MappedRecommendationReason(
        V2RecommendationReason Reason,
        ExplainableRecommendationRule Rule,
        int ExpectedPriority);

    private sealed record PreparedRecommendation(
        LootScanCandidateRecommendation Candidate,
        RecommendationResult? Recommendation,
        LootScanEconomicProjection? Economics,
        RecommendationGate Gate)
    {
        public static PreparedRecommendation Invalid(
            LootScanCandidateRecommendation candidate,
            string code,
            string explanation) => new(
                candidate,
                null,
                null,
                RecommendationGate.Invalid(code, explanation));
    }

    private sealed record PriceChannelResolution(
        bool IsResolved,
        long? Value,
        EvidenceProvenance? Provenance)
    {
        public bool HasValue => Value is not null;

        public static PriceChannelResolution Available(
            long value,
            EvidenceProvenance provenance) => new(true, value, provenance);

        public static PriceChannelResolution Unavailable(
            EvidenceProvenance provenance) => new(true, null, provenance);

        public static PriceChannelResolution Unresolved() => new(false, null, null);
    }

    private sealed record RecommendationGate(
        bool IsValid,
        string Code,
        string Explanation,
        int PlanningPriority,
        FreshnessState Freshness)
    {
        public static RecommendationGate Valid(int planningPriority) => new(
            true,
            "recommendation.valid",
            "Recommendation evidence is valid for planning.",
            planningPriority,
            FreshnessState.Current);

        public static RecommendationGate Invalid(
            string code,
            string explanation,
            FreshnessState freshness = FreshnessState.Current) => new(
                false,
                code,
                explanation,
                int.MinValue,
                freshness);

        public static RecommendationGate FromEvidenceFailure(ProvenanceFailure failure) => failure switch
        {
            ProvenanceFailure.Expired => Invalid(
                "recommendation.expired",
                "Recommendation evidence was produced in the future or is too old for its evidence class.",
                FreshnessState.Stale),
            ProvenanceFailure.Unreliable => Invalid(
                "recommendation.evidence-unreliable",
                "Recommendation evidence is unscored or below the confidence required for decisive advice."),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
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

    private sealed class RecommendationWorkBudget(int maximumVisits, CancellationToken cancellationToken)
    {
        private int _remainingVisits = maximumVisits;

        public void Visit(ref int candidateVisits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_remainingVisits == 0 ||
                candidateVisits == LootScanPlannerLimits.MaximumRecommendationEvidenceVisitsPerCandidate)
            {
                throw new RecommendationWorkBudgetExceededException();
            }

            _remainingVisits--;
            candidateVisits++;
        }
    }

    private sealed class RecommendationWorkBudgetExceededException : Exception
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
