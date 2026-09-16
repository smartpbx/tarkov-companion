using System.Globalization;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Recommendations;
using V2RecommendationAction = TarkovCompanion.Core.Abstractions.V2.RecommendationAction;
using V2RecommendationReason = TarkovCompanion.Core.Abstractions.V2.RecommendationReason;
using V2RecommendationResult = TarkovCompanion.Core.Abstractions.V2.RecommendationResult;

namespace TarkovCompanion.Application.Services.Recommendations;

/// <summary>
/// Pure, deterministic recommendation evaluation. It returns inspectable advice only; no result
/// is an instruction to interact with the game or automate an inventory action.
/// </summary>
public sealed class ExplainableRecommendationEngine(
    ExplainableRecommendationPolicy? policy = null)
{
    private readonly ExplainableRecommendationPolicy _policy = policy ?? ExplainableRecommendationPolicy.Default;

    public V2RecommendationResult Evaluate(ExplainableRecommendationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEvidenceTimes(request);

        var reasons = new List<ReasonDraft>();
        var sensitivities = new List<RecommendationSensitivity>();
        var evidenceIssues = new SortedDictionary<string, EvidenceIssue>(StringComparer.Ordinal);
        var profile = request.Profile;

        if (profile.Status.Completeness != ResultCompleteness.Complete ||
            profile.Status.Freshness != FreshnessState.Current)
        {
            AddIssue(
                evidenceIssues,
                "profile.incomplete",
                "Profile progress is incomplete or stale; recheck progression before discarding the item.",
                profile.Provenance);
        }

        var explicitAction = CurrentValue(profile.ExplicitAction);
        if (explicitAction is { } overridden)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.ExplicitOverride,
                RecommendationReasonCategory.ExplicitOverride,
                "override.explicit",
                $"Your explicit item rule says {ActionText(overridden)}.",
                profile.ExplicitAction.Provenance));
            sensitivities.Add(new("override-removed", "Removing the explicit item rule may change this recommendation.", null));
        }

        var eventState = CurrentValue(profile.EventState);
        if (eventState == EventItemState.Allergic)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.EventAllergy,
                RecommendationReasonCategory.Safety,
                "event.allergic",
                "A prior result marked this event item allergic; do not consume it.",
                profile.EventState.Provenance));
            sensitivities.Add(new(
                "event-state-corrected",
                "A reviewed correction to the recorded event result would change the safety advice.",
                V2RecommendationAction.Review));
        }

        var isProtected = CurrentValue(profile.ProtectedItem) == true;
        if (isProtected)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.ProtectedItem,
                RecommendationReasonCategory.Safety,
                "item.protected",
                "This item is protected from discard or sale recommendations.",
                profile.ProtectedItem.Provenance));
            sensitivities.Add(new("protection-removed", "Removing protection may expose economic actions.", null));
        }

        var inventory = profile.Needs.Any(IsInHorizon)
            ? InspectInventory(request, evidenceIssues)
            : InventoryInspection.Unknown;
        var applicableNeeds = AllocateNeeds(request, inventory, evidenceIssues);
        foreach (var need in applicableNeeds)
        {
            var rule = RuleFor(need.Need);
            var category = CategoryFor(rule);
            var horizon = need.Need.StepsAhead == 0 ? "current" : $"{need.Need.StepsAhead} step(s) ahead";
            var fir = need.Need.RequiresFoundInRaid ? " found-in-raid" : string.Empty;
            var holdings = need.Allocated > 0
                ? $" after {need.Allocated} compatible observed holding(s)"
                : string.Empty;
            var provenance = need.Allocated > 0 && inventory.CountProvenance is { } countProvenance
                ? CombineProvenance(
                    $"recommendation.need.{need.Need.NeedId}",
                    request.EvaluatedUtc,
                    [need.Need.Provenance, countProvenance])
                : need.Need.Provenance;
            reasons.Add(new(
                rule,
                category,
                $"need.{NeedCode(need.Need)}.{need.Need.NeedId}",
                $"Keep {need.Outstanding} more{fir} for {need.Need.DisplayName} ({horizon}){holdings}.",
                provenance));
            sensitivities.Add(new(
                $"need-completed.{need.Need.NeedId}",
                $"Completing or satisfying {need.Need.DisplayName} may expose the next lower-priority use.",
                null));
        }

        var isPinned = CurrentValue(profile.Pinned) == true;
        if (isPinned)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.Pin,
                RecommendationReasonCategory.PinOrWishlist,
                "profile.pinned",
                "You pinned this item.",
                profile.Pinned.Provenance));
            sensitivities.Add(new("pin-removed", "Unpinning the item may expose its economic recommendation.", null));
        }

        var isWishlisted = CurrentValue(profile.Wishlist) == true;
        if (isWishlisted)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.Wishlist,
                RecommendationReasonCategory.PinOrWishlist,
                "profile.wishlist",
                "This item is on your wishlist.",
                profile.Wishlist.Provenance));
            sensitivities.Add(new("wishlist-removed", "Removing the item from the wishlist may change the answer.", null));
        }

        if (eventState == EventItemState.Untested)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.EventUntested,
                RecommendationReasonCategory.Safety,
                "event.untested",
                "This event item is untested; review it before consuming.",
                profile.EventState.Provenance));
            sensitivities.Add(new("event-tested", "Recording the tested result will replace the review advice.", null));
        }
        else if (eventState == EventItemState.Safe)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.EventSafe,
                RecommendationReasonCategory.Safety,
                "event.safe",
                "A prior result marked this event item safe to consume.",
                profile.EventState.Provenance));
        }

        var economics = InspectEconomics(request, evidenceIssues);
        if (economics is { } economic)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.Economics,
                RecommendationReasonCategory.Economics,
                $"economics.{economic.SourceCode}.{economic.Band.ToString().ToLowerInvariant()}",
                EconomicExplanation(request.Economics, economic),
                economic.CalculationProvenance));
            sensitivities.Add(new(
                "price-or-footprint-updated",
                "A newer net price or corrected footprint can move the item into another value-per-square band.",
                null));
        }

        foreach (var issue in evidenceIssues.Values)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.EvidenceQuality,
                RecommendationReasonCategory.EvidenceQuality,
                issue.Code,
                issue.Explanation,
                issue.Provenance));
        }

        if (reasons.Count == 0)
        {
            // Economics and profile fields are still evidence even when they contain no usable value.
            reasons.Add(new(
                ExplainableRecommendationRule.EvidenceQuality,
                RecommendationReasonCategory.EvidenceQuality,
                "evidence.insufficient",
                "There is not enough current evidence to recommend a keep, take, leave, or sale action.",
                profile.Provenance));
        }

        var action = SelectAction(
            request.UseCase,
            eventState,
            explicitAction,
            isProtected,
            applicableNeeds.Count > 0,
            isPinned || isWishlisted,
            economics,
            evidenceIssues.Count > 0);
        var dominantRule = DominantRule(
            eventState,
            explicitAction,
            isProtected,
            applicableNeeds,
            isPinned,
            isWishlisted,
            economics);
        var orderedReasons = reasons
            .OrderByDescending(reason => _policy.PriorityOf(reason.Rule))
            .ThenBy(reason => reason.Code, StringComparer.Ordinal)
            .Select(reason => new V2RecommendationReason(
                reason.Category,
                reason.Code,
                reason.Explanation,
                _policy.PriorityOf(reason.Rule),
                reason.Provenance))
            .ToArray();

        var (opportunityCost, opportunityLineage) = CreateOpportunityCost(
            request,
            economics,
            dominantRule == ExplainableRecommendationRule.Economics);
        var decision = new RecommendationDecision(
            action,
            Summary(action, orderedReasons[0].Explanation),
            orderedReasons,
            opportunityCost,
            opportunityLineage,
            sensitivities
                .DistinctBy(sensitivity => sensitivity.FactCode, StringComparer.Ordinal)
                .OrderBy(sensitivity => sensitivity.FactCode, StringComparer.Ordinal)
                .ToArray());
        var decisionProvenance = CombineProvenance(
            "recommendation.decision",
            request.EvaluatedUtc,
            orderedReasons.Select(reason => reason.Provenance).ToArray());
        var completeness = evidenceIssues.Count == 0
            ? ResultCompleteness.Complete
            : ResultCompleteness.Partial;
        var freshness = profile.Status.Freshness == FreshnessState.Stale
            ? FreshnessState.Stale
            : FreshnessState.Current;
        var decisionEvidence = new EvidencedValue<RecommendationDecision>(
            "recommendation.decision",
            decision,
            new ResultStatus(completeness, freshness,
                evidenceIssues.Count == 0 ? "recommendation.complete" : "recommendation.partial"),
            decisionProvenance);

        return new V2RecommendationResult(
            request.RecommendationId,
            V2ContractVersion.Current,
            _policy.RulesetVersion,
            request.CaptureSessionId,
            decisionEvidence);
    }

    private InventoryInspection InspectInventory(
        ExplainableRecommendationRequest request,
        IDictionary<string, EvidenceIssue> issues)
    {
        var snapshot = request.Inventory;
        if (snapshot is null)
        {
            AddIssue(issues, "inventory.missing", "No inventory snapshot was available for holdings subtraction.", request.Profile.Provenance);
            return InventoryInspection.Unknown;
        }

        if (snapshot.Scope != request.ProfileScope ||
            !string.Equals(snapshot.DataSnapshotId, request.DataSnapshotId, StringComparison.Ordinal))
        {
            AddIssue(issues, "inventory.incompatible", "The inventory snapshot belongs to a different profile or data snapshot and was not subtracted.", snapshot.Provenance);
            return InventoryInspection.Unknown;
        }

        if (!IsFresh(snapshot.Status, snapshot.Provenance, request.EvaluatedUtc, _policy.MaximumInventoryAge) ||
            !MeetsConfidence(snapshot.Provenance))
        {
            AddIssue(issues, "inventory.stale", "The inventory snapshot is stale or below the confidence threshold and was not subtracted.", snapshot.Provenance);
            return InventoryInspection.Unknown;
        }

        var observed = snapshot.Find(request.ItemId);
        if (observed is null)
        {
            var provesZero = snapshot.Status.Completeness == ResultCompleteness.Complete &&
                             snapshot.Coverage.Fraction == 1;
            if (!provesZero)
            {
                AddIssue(issues, "inventory.partial", "The scanned inventory does not cover enough space to treat an unseen item as zero held.", snapshot.Provenance);
                return new InventoryInspection(null, null, false, true, snapshot.Provenance, null);
            }

            return new InventoryInspection(0, 0, true, false, snapshot.Provenance, snapshot.Provenance);
        }

        var total = ReliableCount(observed.TotalQuantity, request.EvaluatedUtc);
        var foundInRaid = ReliableCount(observed.FoundInRaidQuantity, request.EvaluatedUtc);
        if (total is null || foundInRaid is null)
        {
            AddIssue(issues, "inventory.count-partial", "One or more observed holding counts are unknown; only positive compatible counts were subtracted.", snapshot.Provenance);
        }

        if (snapshot.Status.Completeness != ResultCompleteness.Complete || snapshot.Coverage.Fraction != 1 || snapshot.UnresolvedCells > 0)
        {
            AddIssue(issues, "inventory.partial", "Holdings subtraction uses positive observations only because inventory coverage is partial.", snapshot.Provenance);
        }

        return new InventoryInspection(
            total,
            foundInRaid,
            total is not null,
            snapshot.Status.Completeness != ResultCompleteness.Complete || snapshot.Coverage.Fraction != 1,
            snapshot.Provenance,
            observed.TotalQuantity.Provenance);
    }

    private IReadOnlyList<AllocatedNeed> AllocateNeeds(
        ExplainableRecommendationRequest request,
        InventoryInspection inventory,
        IDictionary<string, EvidenceIssue> issues)
    {
        var candidateFir = CurrentValue(request.CandidateFoundInRaid);
        var needs = request.Profile.Needs
            .Where(need => IsInHorizon(need))
            .Where(need => need.Status.Completeness is ResultCompleteness.Complete or ResultCompleteness.Partial)
            .OrderByDescending(need => _policy.PriorityOf(RuleFor(need)))
            .ThenBy(need => need.StepsAhead)
            .ThenBy(need => need.NeedId, StringComparer.Ordinal)
            .ToArray();

        if (request.Profile.Needs.Any(need => need.Status.Completeness is ResultCompleteness.Unknown or ResultCompleteness.Unavailable))
        {
            AddIssue(issues, "needs.incomplete", "Some progression requirements are unavailable and cannot be evaluated.", request.Profile.Provenance);
        }

        var total = inventory.Total;
        var fir = inventory.FoundInRaid;
        var nonFir = total is { } knownTotal && fir is { } knownFir
            ? Math.Max(0, knownTotal - knownFir)
            : total;
        var result = new List<AllocatedNeed>();
        foreach (var need in needs)
        {
            if (need.RequiresFoundInRaid && candidateFir != true)
            {
                if (candidateFir is null)
                {
                    AddIssue(issues, "candidate.fir-unknown", "Found-in-raid status is unknown, so the item cannot be claimed to satisfy a FIR objective.", request.CandidateFoundInRaid.Provenance);
                }

                continue;
            }

            var allocated = 0;
            if (need.RequiresFoundInRaid && fir is { } firAvailable)
            {
                allocated = Math.Min(need.RequiredQuantity, firAvailable);
                fir = firAvailable - allocated;
                if (total is { } totalAvailable)
                {
                    total = Math.Max(0, totalAvailable - allocated);
                }
            }
            else if (!need.RequiresFoundInRaid)
            {
                if (nonFir is { } nonFirAvailable)
                {
                    var fromNonFir = Math.Min(need.RequiredQuantity, nonFirAvailable);
                    allocated += fromNonFir;
                    nonFir = nonFirAvailable - fromNonFir;
                    if (total is { } totalAvailable)
                    {
                        total = Math.Max(0, totalAvailable - fromNonFir);
                    }
                }

                var remaining = need.RequiredQuantity - allocated;
                if (remaining > 0 && fir is { } firAvailable)
                {
                    var fromFir = Math.Min(remaining, firAvailable);
                    allocated += fromFir;
                    fir = firAvailable - fromFir;
                    if (total is { } totalAvailable)
                    {
                        total = Math.Max(0, totalAvailable - fromFir);
                    }
                }
                else if (remaining > 0 && total is { } totalAvailable && nonFir is null)
                {
                    var fromTotal = Math.Min(remaining, totalAvailable);
                    allocated += fromTotal;
                    total = totalAvailable - fromTotal;
                }
            }

            var outstanding = need.RequiredQuantity - allocated;
            if (outstanding > 0)
            {
                result.Add(new(need, outstanding, allocated));
            }
        }

        return result;
    }

    private EconomicInspection? InspectEconomics(
        ExplainableRecommendationRequest request,
        IDictionary<string, EvidenceIssue> issues)
    {
        var economics = request.Economics;
        var footprint = ReliableValue(economics.OccupiedSquares, request.EvaluatedUtc, _policy.MaximumPriceAge);
        var flea = ReliableValue(economics.FleaNetRoubles, request.EvaluatedUtc, _policy.MaximumPriceAge);
        var trader = ReliableValue(economics.TraderRoubles, request.EvaluatedUtc, _policy.MaximumPriceAge);

        if (footprint is null)
        {
            AddIssue(issues, "economics.footprint-missing", "Occupied squares are missing, stale, or uncertain; value per square was not invented.", economics.OccupiedSquares.Provenance);
        }

        if (flea is null && trader is null)
        {
            var provenance = economics.FleaNetRoubles.Provenance.EvidenceThroughUtc >= economics.TraderRoubles.Provenance.EvidenceThroughUtc
                ? economics.FleaNetRoubles.Provenance
                : economics.TraderRoubles.Provenance;
            AddIssue(issues, "economics.price-missing", "No current trustworthy flea net or trader value is available; gross value is not treated as net.", provenance);
            return null;
        }

        if (footprint is null)
        {
            return null;
        }

        var useFlea = flea is not null && (trader is null || flea >= trader);
        var value = useFlea ? flea!.Value : trader!.Value;
        var priceEvidence = useFlea ? economics.FleaNetRoubles : economics.TraderRoubles;
        var valuePerSquare = value / footprint.Value;
        var priceRole = CombineProvenance(
            $"recommendation.economic-price.{(useFlea ? "flea-net" : "trader")}",
            request.EvaluatedUtc,
            [priceEvidence.Provenance]);
        var footprintRole = CombineProvenance(
            "recommendation.economic-footprint",
            request.EvaluatedUtc,
            [economics.OccupiedSquares.Provenance]);
        var calculation = CombineProvenance(
            "recommendation.value-per-square",
            request.EvaluatedUtc,
            [priceRole, footprintRole]);
        return new(
            value,
            footprint.Value,
            valuePerSquare,
            _policy.ValueBands.Classify(valuePerSquare),
            useFlea ? "flea-net" : "trader",
            useFlea ? V2RecommendationAction.SellOnFlea : V2RecommendationAction.SellToTrader,
            priceRole,
            footprintRole,
            calculation);
    }

    private (EvidencedValue<long?> Cost, OpportunityCostLineage? Lineage) CreateOpportunityCost(
        ExplainableRecommendationRequest request,
        EconomicInspection? economics,
        bool economicsDominated)
    {
        if (economics is null || economicsDominated)
        {
            var provenance = economics?.CalculationProvenance ?? request.Profile.Provenance;
            return (
                new EvidencedValue<long?>(
                    "recommendation.opportunity-cost-roubles",
                    null,
                    new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, "opportunity-cost.unavailable"),
                    provenance),
                null);
        }

        var costProvenance = CombineProvenance(
            "recommendation.opportunity-cost",
            request.EvaluatedUtc,
            [economics.PriceRole, economics.FootprintRole]);
        return (
            new EvidencedValue<long?>(
                "recommendation.opportunity-cost-roubles",
                economics.TotalValue,
                new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "opportunity-cost.complete"),
                costProvenance),
            new OpportunityCostLineage(economics.PriceRole, economics.FootprintRole));
    }

    private V2RecommendationAction SelectAction(
        RecommendationUseCase useCase,
        EventItemState? eventState,
        V2RecommendationAction? explicitAction,
        bool isProtected,
        bool hasNeed,
        bool pinnedOrWishlisted,
        EconomicInspection? economics,
        bool hasEvidenceIssues)
    {
        if (eventState == EventItemState.Allergic)
        {
            return V2RecommendationAction.AvoidConsume;
        }

        if (explicitAction is { } overridden)
        {
            return overridden;
        }

        if (isProtected || hasNeed || pinnedOrWishlisted)
        {
            return KeepOrTake(useCase);
        }

        if (eventState == EventItemState.Untested)
        {
            return V2RecommendationAction.Review;
        }

        if (eventState == EventItemState.Safe)
        {
            return V2RecommendationAction.UseSoon;
        }

        if (economics is null || hasEvidenceIssues)
        {
            return V2RecommendationAction.Review;
        }

        if (useCase == RecommendationUseCase.Loot)
        {
            return economics.Band == EconomicValueBand.Low
                ? V2RecommendationAction.Leave
                : V2RecommendationAction.Take;
        }

        return economics.SaleAction;
    }

    private ExplainableRecommendationRule? DominantRule(
        EventItemState? eventState,
        V2RecommendationAction? explicitAction,
        bool isProtected,
        IReadOnlyList<AllocatedNeed> needs,
        bool isPinned,
        bool isWishlisted,
        EconomicInspection? economics)
    {
        if (eventState == EventItemState.Allergic) return ExplainableRecommendationRule.EventAllergy;
        if (explicitAction is not null) return ExplainableRecommendationRule.ExplicitOverride;
        if (isProtected) return ExplainableRecommendationRule.ProtectedItem;
        if (needs.Count > 0) return needs.Select(need => RuleFor(need.Need)).OrderByDescending(_policy.PriorityOf).First();
        if (isPinned) return ExplainableRecommendationRule.Pin;
        if (isWishlisted) return ExplainableRecommendationRule.Wishlist;
        if (eventState == EventItemState.Untested) return ExplainableRecommendationRule.EventUntested;
        if (eventState == EventItemState.Safe) return ExplainableRecommendationRule.EventSafe;
        return economics is null ? null : ExplainableRecommendationRule.Economics;
    }

    private ExplainableRecommendationRule RuleFor(RecommendationNeed need) => need.Purpose switch
    {
        RecommendationNeedPurpose.Quest when need.StepsAhead == 0 && need.RequiresFoundInRaid =>
            ExplainableRecommendationRule.CurrentFoundInRaidQuest,
        RecommendationNeedPurpose.Quest when need.StepsAhead == 0 => ExplainableRecommendationRule.CurrentQuest,
        RecommendationNeedPurpose.Quest => ExplainableRecommendationRule.FutureQuest,
        RecommendationNeedPurpose.Hideout => ExplainableRecommendationRule.Hideout,
        RecommendationNeedPurpose.CraftOrBarter => ExplainableRecommendationRule.CraftOrBarter,
        RecommendationNeedPurpose.SpecialistUtility => ExplainableRecommendationRule.SpecialistUtility,
        _ => throw new ArgumentOutOfRangeException(nameof(need)),
    };

    private bool IsInHorizon(RecommendationNeed need) => need.Purpose switch
    {
        RecommendationNeedPurpose.Quest => need.StepsAhead <= _policy.FutureQuestSteps,
        RecommendationNeedPurpose.Hideout => need.StepsAhead <= _policy.FutureHideoutSteps,
        RecommendationNeedPurpose.CraftOrBarter or RecommendationNeedPurpose.SpecialistUtility => true,
        _ => false,
    };

    private static RecommendationReasonCategory CategoryFor(ExplainableRecommendationRule rule) => rule switch
    {
        ExplainableRecommendationRule.CurrentFoundInRaidQuest => RecommendationReasonCategory.CurrentFoundInRaidQuest,
        ExplainableRecommendationRule.CurrentQuest => RecommendationReasonCategory.CurrentQuest,
        ExplainableRecommendationRule.FutureQuest => RecommendationReasonCategory.FutureQuest,
        ExplainableRecommendationRule.Hideout => RecommendationReasonCategory.Hideout,
        ExplainableRecommendationRule.CraftOrBarter => RecommendationReasonCategory.CraftOrBarter,
        ExplainableRecommendationRule.SpecialistUtility => RecommendationReasonCategory.SpecialistUtility,
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    private static string NeedCode(RecommendationNeed need) => need.Purpose switch
    {
        RecommendationNeedPurpose.Quest when need.StepsAhead == 0 && need.RequiresFoundInRaid => "quest-current-fir",
        RecommendationNeedPurpose.Quest when need.StepsAhead == 0 => "quest-current",
        RecommendationNeedPurpose.Quest => "quest-future",
        RecommendationNeedPurpose.Hideout => "hideout",
        RecommendationNeedPurpose.CraftOrBarter => "craft-barter",
        RecommendationNeedPurpose.SpecialistUtility => "specialist",
        _ => throw new ArgumentOutOfRangeException(nameof(need)),
    };

    private static V2RecommendationAction KeepOrTake(RecommendationUseCase useCase) =>
        useCase == RecommendationUseCase.Loot ? V2RecommendationAction.Take : V2RecommendationAction.Keep;

    private static string ActionText(V2RecommendationAction action) => action.ToString().ToLowerInvariant();

    private static string Summary(V2RecommendationAction action, string topReason) =>
        $"{action}: {topReason}";

    private static string EconomicExplanation(RecommendationEconomics inputs, EconomicInspection economics)
    {
        var details = new List<string>();
        if (inputs.FleaGrossRoubles.Value is { } gross) details.Add($"flea gross {gross.ToString("N0", CultureInfo.InvariantCulture)}");
        if (inputs.FleaFeeRoubles.Value is { } fee) details.Add($"fee {fee.ToString("N0", CultureInfo.InvariantCulture)}");
        if (inputs.FleaNetRoubles.Value is { } net) details.Add($"flea net {net.ToString("N0", CultureInfo.InvariantCulture)}");
        if (inputs.TraderRoubles.Value is { } trader) details.Add($"trader {trader.ToString("N0", CultureInfo.InvariantCulture)}");
        if (inputs.ConditionFraction.Value is { } condition) details.Add($"condition {condition.ToString("P0", CultureInfo.InvariantCulture)}");
        var suffix = details.Count == 0 ? string.Empty : $" ({string.Join(", ", details)})";
        return $"{economics.TotalValue.ToString("N0", CultureInfo.InvariantCulture)} roubles across {economics.Footprint} square(s) is {economics.ValuePerSquare.ToString("N0", CultureInfo.InvariantCulture)} per square, in the {economics.Band.ToString().ToLowerInvariant()} band{suffix}.";
    }

    private static T? CurrentValue<T>(EvidencedValue<T?> field)
        where T : struct =>
        field.Status.Completeness is ResultCompleteness.Complete or ResultCompleteness.Partial &&
        field.Status.Freshness == FreshnessState.Current
            ? field.Value
            : null;

    private int? ReliableCount(EvidencedValue<int?> field, DateTimeOffset evaluatedUtc) =>
        ReliableValue(field, evaluatedUtc, _policy.MaximumInventoryAge);

    private T? ReliableValue<T>(EvidencedValue<T?> field, DateTimeOffset evaluatedUtc, TimeSpan maximumAge)
        where T : struct =>
        field.Value is { } value &&
        IsFresh(field.Status, field.Provenance, evaluatedUtc, maximumAge) &&
        MeetsConfidence(field.Provenance)
            ? value
            : null;

    private bool MeetsConfidence(EvidenceProvenance provenance) =>
        provenance.Confidence.Score is { } score && score >= _policy.MinimumEvidenceConfidence;

    private static bool IsFresh(
        ResultStatus status,
        EvidenceProvenance provenance,
        DateTimeOffset evaluatedUtc,
        TimeSpan maximumAge) =>
        status.Completeness is ResultCompleteness.Complete or ResultCompleteness.Partial &&
        status.Freshness == FreshnessState.Current &&
        provenance.EvidenceThroughUtc <= evaluatedUtc &&
        evaluatedUtc - provenance.EvidenceThroughUtc <= maximumAge;

    private static void AddIssue(
        IDictionary<string, EvidenceIssue> issues,
        string code,
        string explanation,
        EvidenceProvenance provenance)
    {
        issues.TryAdd(code, new(code, explanation, provenance));
    }

    private static EvidenceProvenance CombineProvenance(
        string sourceIdentifier,
        DateTimeOffset evaluatedUtc,
        IReadOnlyList<EvidenceProvenance> inputs)
    {
        if (inputs.Count == 0)
        {
            throw new ArgumentException("A recommendation calculation must name its inputs.", nameof(inputs));
        }

        if (inputs.Any(input => input.EvidenceThroughUtc > evaluatedUtc))
        {
            throw new ArgumentException("Recommendation evidence cannot be newer than its evaluation time.", nameof(inputs));
        }

        var containsModel = inputs.Any(ContainsModelledEstimate);
        var scores = inputs.Select(input => input.Confidence.Score).ToArray();
        var confidence = containsModel
            ? new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, scores.Min(score => score ?? 0))
            : scores.All(score => score is not null)
                ? new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, scores.Min()!.Value)
                : EvidenceConfidence.Unscored;
        var producer = new ProducerIdentity(
            "Tarkov Companion recommendation engine",
            ExplainableRecommendationPolicy.CurrentRulesetVersion,
            containsModel ? ExplainableRecommendationPolicy.CurrentRulesetVersion : null);

        if (containsModel)
        {
            var evidenceThrough = inputs.Max(input => input.EvidenceThroughUtc);
            var fractions = inputs.Select(input => input.Coverage?.Fraction).ToArray();
            var coverage = new EvidenceCoverage(
                fraction: fractions.All(fraction => fraction is not null) ? fractions.Min() : null,
                description: "Coverage propagated from recommendation inputs.");
            return new EvidenceProvenance(
                EvidenceSourceClass.ModelledEstimate,
                sourceIdentifier,
                evaluatedUtc,
                confidence,
                producer,
                dataThroughUtc: evidenceThrough,
                generatedUtc: evaluatedUtc,
                coverage: coverage,
                inputs: inputs);
        }

        return new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            sourceIdentifier,
            evaluatedUtc,
            confidence,
            producer,
            generatedUtc: evaluatedUtc,
            inputs: inputs);
    }

    private static bool ContainsModelledEstimate(EvidenceProvenance provenance) =>
        provenance.SourceClass == EvidenceSourceClass.ModelledEstimate ||
        provenance.Inputs.Any(ContainsModelledEstimate);

    private static void ValidateEvidenceTimes(ExplainableRecommendationRequest request)
    {
        var provenances = new List<EvidenceProvenance>
        {
            request.Profile.Provenance,
            request.Profile.ExplicitAction.Provenance,
            request.Profile.ProtectedItem.Provenance,
            request.Profile.Pinned.Provenance,
            request.Profile.Wishlist.Provenance,
            request.Profile.EventState.Provenance,
            request.CandidateFoundInRaid.Provenance,
            request.Economics.FleaGrossRoubles.Provenance,
            request.Economics.FleaFeeRoubles.Provenance,
            request.Economics.FleaNetRoubles.Provenance,
            request.Economics.TraderRoubles.Provenance,
            request.Economics.OccupiedSquares.Provenance,
            request.Economics.ConditionFraction.Provenance,
        };
        provenances.AddRange(request.Profile.Needs.Select(need => need.Provenance));
        if (request.Inventory is { } inventory)
        {
            provenances.Add(inventory.Provenance);
            provenances.AddRange(inventory.Items.SelectMany(item =>
                new[] { item.TotalQuantity.Provenance, item.FoundInRaidQuantity.Provenance }));
        }

        if (provenances.Any(provenance => provenance.EvidenceThroughUtc > request.EvaluatedUtc))
        {
            throw new ArgumentException("Recommendation evidence cannot be newer than the evaluation time.", nameof(request));
        }
    }

    private sealed record ReasonDraft(
        ExplainableRecommendationRule Rule,
        RecommendationReasonCategory Category,
        string Code,
        string Explanation,
        EvidenceProvenance Provenance);

    private sealed record EvidenceIssue(string Code, string Explanation, EvidenceProvenance Provenance);

    private sealed record InventoryInspection(
        int? Total,
        int? FoundInRaid,
        bool CountKnown,
        bool Partial,
        EvidenceProvenance? SnapshotProvenance,
        EvidenceProvenance? CountProvenance)
    {
        public static InventoryInspection Unknown { get; } = new(null, null, false, true, null, null);
    }

    private sealed record AllocatedNeed(RecommendationNeed Need, int Outstanding, int Allocated);

    private sealed record EconomicInspection(
        long TotalValue,
        int Footprint,
        long ValuePerSquare,
        EconomicValueBand Band,
        string SourceCode,
        V2RecommendationAction SaleAction,
        EvidenceProvenance PriceRole,
        EvidenceProvenance FootprintRole,
        EvidenceProvenance CalculationProvenance);
}
