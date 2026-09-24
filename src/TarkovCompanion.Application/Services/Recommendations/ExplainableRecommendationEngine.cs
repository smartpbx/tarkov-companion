using System.Globalization;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
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
    private const int MaximumEvidenceEntries = 32;

    private readonly ExplainableRecommendationPolicy _policy = policy ?? ExplainableRecommendationPolicy.Default;

    public V2RecommendationResult Evaluate(
        ExplainableRecommendationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEvidenceTimes(request, cancellationToken);

        var reasons = new List<ReasonDraft>();
        var sensitivities = new List<RecommendationSensitivity>();
        var evidenceIssues = new SortedDictionary<string, EvidenceIssue>(StringComparer.Ordinal);
        var decisionInputs = new List<EvidenceProvenance>();
        var profile = request.Profile;

        var profileAssessment = AssessEvidence(
            profile.Status,
            profile.Provenance,
            request.EvaluatedUtc,
            _policy.MaximumInventoryAge,
            allowPartial: false);
        if (!profileAssessment.IsReliable)
        {
            AddIssue(
                evidenceIssues,
                "profile.incomplete",
                Say(AdviceSentence.ProfileIncomplete, "Profile progress is incomplete or stale; recheck progression before discarding the item."),
                profileAssessment);
        }
        else
        {
            decisionInputs.Add(profileAssessment.Provenance);
        }

        // A complete state can say that no override exists. A missing state cannot: treating both
        // as nullable actions would let an unknown higher-precedence rule fall through to economics.
        var explicitEvidence = InspectRequiredProfileField(
            profile.ExplicitAction,
            request,
            evidenceIssues,
            "profile.override-untrusted",
            Say(AdviceSentence.OverrideUntrusted, "The explicit item rule is ambiguous, stale, incomplete, or below the confidence threshold."));
        var explicitAction = explicitEvidence?.Value.Action;
        if (explicitEvidence is { } trustedExplicitAction)
        {
            decisionInputs.Add(trustedExplicitAction.Provenance);
        }

        if (explicitAction is { } overridden)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.ExplicitOverride,
                RecommendationReasonCategory.ExplicitOverride,
                "override.explicit",
                Say(
                    AdviceSentence.OverrideExplicit,
                    $"Your explicit item rule says {ActionText(overridden)}.",
                    AdviceWords.Of<AdviceActionWord>(overridden)),
                explicitEvidence!.Provenance));
            sensitivities.Add(Sensitivity("override-removed", Say(AdviceSentence.OverrideRemoved, "Removing the explicit item rule may change this recommendation."), null));
        }

        var eventEvidence = InspectEventState(
            profile.EventState.State,
            request,
            evidenceIssues,
            "profile.event-state-untrusted",
            Say(AdviceSentence.EventStateUntrusted, "Event-item state is ambiguous, stale, incomplete, below the confidence threshold, or not a user-confirmed outcome."));
        var eventState = eventEvidence?.Value;
        if (eventEvidence is { } trustedEventState)
        {
            decisionInputs.Add(trustedEventState.Provenance);
        }

        if (eventState == EventItemState.Allergic)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.EventAllergy,
                RecommendationReasonCategory.Safety,
                "event.allergic",
                Say(AdviceSentence.EventAllergic, "A prior result marked this event item allergic; do not consume it."),
                eventEvidence!.Provenance));
            sensitivities.Add(Sensitivity(
                "event-state-corrected",
                Say(AdviceSentence.EventStateCorrected, "A reviewed correction to the recorded event result would change the safety advice."),
                V2RecommendationAction.Review));
        }

        var protectedEvidence = InspectRequiredProfileField(
            profile.ProtectedItem,
            request,
            evidenceIssues,
            "profile.protection-untrusted",
            Say(AdviceSentence.ProtectionUntrusted, "Item protection is ambiguous, stale, incomplete, or below the confidence threshold."));
        var isProtected = protectedEvidence?.Value == true;
        if (protectedEvidence is { } trustedProtection)
        {
            decisionInputs.Add(trustedProtection.Provenance);
        }

        if (isProtected)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.ProtectedItem,
                RecommendationReasonCategory.Safety,
                "item.protected",
                Say(AdviceSentence.ItemProtected, "This item is protected from discard or sale recommendations."),
                protectedEvidence!.Provenance));
            sensitivities.Add(Sensitivity("protection-removed", Say(AdviceSentence.ProtectionRemoved, "Removing protection may expose economic actions."), null));
        }

        var trustedNeeds = InspectNeeds(request, evidenceIssues);
        var inventory = trustedNeeds.Count > 0
            ? InspectInventory(request, evidenceIssues)
            : InventoryInspection.Unknown;
        decisionInputs.AddRange(inventory.DecisionInputs);
        var allocation = AllocateNeeds(request, trustedNeeds, inventory, evidenceIssues);
        var applicableNeeds = allocation.Outstanding;
        decisionInputs.AddRange(allocation.DecisionInputs);
        foreach (var need in applicableNeeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rule = RuleFor(need.Need);
            var category = CategoryFor(rule);
            var horizon = need.Need.StepsAhead == 0 ? "current" : $"{need.Need.StepsAhead} step(s) ahead";
            var horizonWords = need.Need.StepsAhead == 0
                ? new Phrase(AdviceSentence.HorizonCurrent)
                : new Phrase(AdviceSentence.HorizonAhead, need.Need.StepsAhead);
            var fir = need.Need.RequiresFoundInRaid ? " found-in-raid" : string.Empty;
            var holdings = need.Allocated > 0
                ? $" after {need.Allocated} compatible observed holding(s)"
                : string.Empty;
            var needInputs = new[] { need.Provenance }
                .Concat(need.SupportingProvenance)
                .ToArray();
            var provenance = needInputs.Length == 1
                ? needInputs[0]
                : CombineProvenance(
                    $"recommendation.need.{need.Need.NeedId}",
                    request.EvaluatedUtc,
                    needInputs);
            reasons.Add(new(
                rule,
                category,
                $"need.{NeedCode(need.Need)}.{need.Need.NeedId}",
                Say(
                    (need.Need.RequiresFoundInRaid, need.Allocated > 0) switch
                    {
                        (false, false) => AdviceSentence.NeedKeep,
                        (false, true) => AdviceSentence.NeedKeepAfterHoldings,
                        (true, false) => AdviceSentence.NeedKeepFoundInRaid,
                        (true, true) => AdviceSentence.NeedKeepFoundInRaidAfterHoldings,
                    },
                    $"Keep {need.Outstanding} more{fir} for {need.Need.DisplayName} ({horizon}){holdings}.",
                    need.Allocated > 0
                        ? [need.Outstanding, need.Need.DisplayName, horizonWords, need.Allocated]
                        : [need.Outstanding, need.Need.DisplayName, horizonWords]),
                provenance));
            sensitivities.Add(Sensitivity(
                $"need-completed.{need.Need.NeedId}",
                Say(
                    AdviceSentence.NeedCompleted,
                    $"Completing or satisfying {need.Need.DisplayName} may expose the next lower-priority use.",
                    need.Need.DisplayName),
                null));
        }

        var pinnedEvidence = InspectRequiredProfileField(
            profile.Pinned,
            request,
            evidenceIssues,
            "profile.pinned-untrusted",
            Say(AdviceSentence.PinnedUntrusted, "Pinned-item state is ambiguous, stale, incomplete, or below the confidence threshold."));
        var isPinned = pinnedEvidence?.Value == true;
        if (pinnedEvidence is { } trustedPin)
        {
            decisionInputs.Add(trustedPin.Provenance);
        }

        if (isPinned)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.Pin,
                RecommendationReasonCategory.PinOrWishlist,
                "profile.pinned",
                Say(AdviceSentence.Pinned, "You pinned this item."),
                pinnedEvidence!.Provenance));
            sensitivities.Add(Sensitivity("pin-removed", Say(AdviceSentence.PinRemoved, "Unpinning the item may expose its economic recommendation."), null));
        }

        var wishlistEvidence = InspectRequiredProfileField(
            profile.Wishlist,
            request,
            evidenceIssues,
            "profile.wishlist-untrusted",
            Say(AdviceSentence.WishlistUntrusted, "Wishlist state is ambiguous, stale, incomplete, or below the confidence threshold."));
        var isWishlisted = wishlistEvidence?.Value == true;
        if (wishlistEvidence is { } trustedWishlist)
        {
            decisionInputs.Add(trustedWishlist.Provenance);
        }

        if (isWishlisted)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.Wishlist,
                RecommendationReasonCategory.PinOrWishlist,
                "profile.wishlist",
                Say(AdviceSentence.Wishlist, "This item is on your wishlist."),
                wishlistEvidence!.Provenance));
            sensitivities.Add(Sensitivity("wishlist-removed", Say(AdviceSentence.WishlistRemoved, "Removing the item from the wishlist may change the answer."), null));
        }

        if (eventState == EventItemState.Untested)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.EventUntested,
                RecommendationReasonCategory.Safety,
                "event.untested",
                Say(AdviceSentence.EventUntested, "This event item is untested; review it before consuming."),
                eventEvidence!.Provenance));
            sensitivities.Add(Sensitivity("event-tested", Say(AdviceSentence.EventTested, "Recording the tested result will replace the review advice."), null));
        }
        else if (eventState == EventItemState.Safe)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.EventSafe,
                RecommendationReasonCategory.Safety,
                "event.safe",
                Say(AdviceSentence.EventSafe, "A prior result marked this event item safe to consume."),
                eventEvidence!.Provenance));
        }

        var scarcity = InspectScarcity(request, evidenceIssues);
        if (scarcity.Provenance is { } reliableScarcity)
        {
            decisionInputs.Add(reliableScarcity);
        }
        if (scarcity.ShouldKeep && scarcity.Band is { } scarcityBand && scarcity.Provenance is { } scarcityProvenance)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.Scarcity,
                RecommendationReasonCategory.ScarcityOrObtainability,
                $"scarcity.obtainability.{scarcityBand.ToString().ToLowerInvariant()}",
                Say(
                    AdviceSentence.Scarcity,
                    $"Current evidence classifies this item as {scarcityBand.ToString().ToLowerInvariant()} to obtain.",
                    AdviceWords.Of<AdviceObtainabilityWord>(scarcityBand)),
                scarcityProvenance));
        }

        var raidContext = InspectRaidContext(request, evidenceIssues);
        decisionInputs.AddRange(raidContext.DecisionInputs);
        if (raidContext.IsAvailable)
        {
            AddRaidContextReasons(raidContext, reasons);
        }

        var economics = InspectEconomics(request.Economics, request.EvaluatedUtc, evidenceIssues);
        if (economics is { } economic)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.Economics,
                RecommendationReasonCategory.Economics,
                $"economics.{economic.SourceCode}.{economic.Band.ToString().ToLowerInvariant()}",
                EconomicExplanation(economic),
                economic.ExplanationProvenance));
            sensitivities.Add(Sensitivity(
                "price-or-footprint-updated",
                Say(AdviceSentence.PriceOrFootprintUpdated, "A newer net price or corrected footprint can move the item into another value-per-square band."),
                null));
        }

        foreach (var issue in evidenceIssues.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                Say(AdviceSentence.EvidenceInsufficient, "There is not enough current evidence to recommend a keep, take, leave, or sale action."),
                profile.Provenance));
        }

        var action = SelectAction(
            request.UseCase,
            eventState,
            explicitAction,
            isProtected,
            applicableNeeds.Count > 0,
            isPinned || isWishlisted,
            scarcity.ShouldKeep,
            raidContext,
            economics,
            evidenceIssues.Count > 0);
        V2RecommendationAction SelectAlternative(
            bool alternativeScarcityKeep,
            RaidContextInspection alternativeRaidContext) =>
            SelectAction(
                request.UseCase,
                eventState,
                explicitAction,
                isProtected,
                applicableNeeds.Count > 0,
                isPinned || isWishlisted,
                alternativeScarcityKeep,
                alternativeRaidContext,
                economics,
                evidenceIssues.Count > 0);
        AddScarcitySensitivities(scarcity, raidContext, action, SelectAlternative, sensitivities);
        AddRaidContextSensitivities(
            request,
            scarcity,
            raidContext,
            economics,
            action,
            SelectAlternative,
            sensitivities);
        var dominantRule = DominantRule(
            eventState,
            explicitAction,
            isProtected,
            applicableNeeds,
            isPinned,
            isWishlisted,
            scarcity,
            raidContext,
            economics,
            evidenceIssues.Count > 0);
        var orderedDrafts = reasons
            .OrderByDescending(reason => _policy.PriorityOf(reason.Rule))
            .ThenBy(reason => reason.Code, StringComparer.Ordinal)
            .ToArray();
        var orderedReasons = orderedDrafts
            .Select(reason => new V2RecommendationReason(
                reason.Category,
                reason.Code,
                reason.Explanation.English,
                _policy.PriorityOf(reason.Rule),
                reason.Provenance).WithWords(reason.Explanation.Words))
            .ToArray();
        var summaryReason = SelectSummaryReason(dominantRule, raidContext, orderedDrafts);

        var (opportunityCost, opportunityLineage) = CreateOpportunityCost(
            request,
            economics,
            dominantRule == ExplainableRecommendationRule.Economics);
        var summary = Summary(action, summaryReason.Explanation);
        var decision = new RecommendationDecision(
            action,
            summary.English,
            orderedReasons,
            opportunityCost,
            opportunityLineage,
            sensitivities
                .DistinctBy(sensitivity => sensitivity.FactCode, StringComparer.Ordinal)
                .OrderBy(sensitivity => sensitivity.FactCode, StringComparer.Ordinal)
                .ToArray()).WithSummaryWords(summary.Words);
        var decisionProvenance = CombineProvenance(
            "recommendation.decision",
            request.EvaluatedUtc,
            orderedReasons
                .Select(reason => reason.Provenance)
                .Concat(decisionInputs)
                .ToArray());
        var completeness = evidenceIssues.Count == 0
            ? ResultCompleteness.Complete
            : ResultCompleteness.Partial;
        var freshness = DecisionFreshness(evidenceIssues.Values);
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

    /// <summary>
    /// Evaluates one photographed flea offer against the catalog's flea-net and trader exits.
    /// </summary>
    /// <remarks>
    /// This is a distinct entry point because an offer is an acquisition question, not an item
    /// already held in a profile. Reusing the stash request would make pins and quest needs decide
    /// whether a quoted purchase is profitable. It still emits the same versioned result, reason,
    /// sensitivity and provenance shape as the other recommendation use cases.
    /// </remarks>
    public V2RecommendationResult EvaluateFleaOffer(
        FleaOfferRecommendationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var issues = new SortedDictionary<string, EvidenceIssue>(StringComparer.Ordinal);
        var identity = AssessEvidence(
            request.ItemIdentity.Status,
            request.ItemIdentity.Provenance,
            request.EvaluatedUtc,
            _policy.MaximumPriceAge,
            allowPartial: false);
        if (!identity.IsReliable || string.IsNullOrWhiteSpace(request.ItemIdentity.Value))
        {
            AddIssue(
                issues,
                "identity.untrusted",
                Say(AdviceSentence.IdentityUntrusted, "The photographed item's identity is ambiguous, stale, incomplete, or below the confidence threshold."),
                identity);
        }

        var offer = InspectEvidence(
            request.OfferPriceRoubles,
            request.EvaluatedUtc,
            _policy.MaximumPriceAge,
            allowPartial: false);
        if (!offer.IsReliable)
        {
            AddIssue(
                issues,
                "offer.price-untrusted",
                Say(AdviceSentence.OfferPriceUntrusted, "The photographed offer price is ambiguous, stale, incomplete, or below the confidence threshold."),
                offer.Assessment);
        }

        var economics = InspectEconomics(request.ResaleEconomics, request.EvaluatedUtc, issues);
        var reasons = new List<ReasonDraft>();
        V2RecommendationAction action;
        if (identity.IsReliable && offer.Trusted is { } trustedOffer && economics is { } resale && issues.Count == 0)
        {
            var margin = resale.TotalValue - trustedOffer.Value;
            var profitable = margin > 0;
            var comparison = CombineProvenance(
                "recommendation.flea-offer-comparison",
                request.EvaluatedUtc,
                [identity.Provenance, trustedOffer.Provenance, resale.ExplanationProvenance]);
            reasons.Add(new(
                ExplainableRecommendationRule.Economics,
                RecommendationReasonCategory.Economics,
                $"economics.offer.{(profitable ? "profit" : "no-profit")}.{resale.SourceCode}",
                FleaOfferExplanation(trustedOffer.Value, resale, margin),
                comparison));
            action = profitable ? V2RecommendationAction.Take : V2RecommendationAction.Leave;
        }
        else
        {
            action = V2RecommendationAction.Review;
        }

        foreach (var issue in issues.Values)
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
            reasons.Add(new(
                ExplainableRecommendationRule.EvidenceQuality,
                RecommendationReasonCategory.EvidenceQuality,
                "evidence.insufficient",
                Say(AdviceSentence.OfferEvidenceInsufficient, "There is not enough current evidence to compare this photographed offer."),
                request.ItemIdentity.Provenance));
        }

        var ordered = reasons
            .OrderByDescending(reason => _policy.PriorityOf(reason.Rule))
            .ThenBy(reason => reason.Code, StringComparer.Ordinal)
            .Select(reason => new V2RecommendationReason(
                reason.Category,
                reason.Code,
                reason.Explanation.English,
                _policy.PriorityOf(reason.Rule),
                reason.Provenance).WithWords(reason.Explanation.Words))
            .ToArray();
        var top = ordered[0];
        var absentCost = new EvidencedValue<long?>(
            "recommendation.opportunity-cost-roubles",
            null,
            new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, "opportunity-cost.unavailable"),
            top.Provenance);
        var summary = Summary(action, new Said(top.Explanation, top.Words!));
        var decision = new RecommendationDecision(
            action,
            summary.English,
            ordered,
            absentCost,
            null,
            [Sensitivity(
                "offer-or-resale-price-updated",
                Say(AdviceSentence.OfferOrResaleUpdated, "A newer photographed offer or catalog resale price can change this comparison."),
                null)]).WithSummaryWords(summary.Words);
        // A complete offer has one economic reason whose lineage is already near the contract's
        // depth bound (offer + identity + flea gross/fee/net). Wrapping that single tree again
        // adds no evidence and can make a valid comparison unrepresentable.
        var decisionProvenance = ordered.Length == 1
            ? ordered[0].Provenance
            : CombineProvenance(
                "recommendation.flea-offer-decision",
                request.EvaluatedUtc,
                ordered.Select(reason => reason.Provenance).ToArray());
        var decisionEvidence = new EvidencedValue<RecommendationDecision>(
            "recommendation.decision",
            decision,
            new ResultStatus(
                issues.Count == 0 ? ResultCompleteness.Complete : ResultCompleteness.Partial,
                DecisionFreshness(issues.Values),
                issues.Count == 0 ? "recommendation.complete" : "recommendation.partial"),
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
            AddIssue(
                issues,
                "inventory.missing",
                Say(AdviceSentence.InventoryMissing, "No inventory snapshot was available for holdings subtraction."),
                request.Profile.Provenance,
                FreshnessState.Unknown);
            return InventoryInspection.Unknown;
        }

        if (snapshot.Scope != request.ProfileScope ||
            !string.Equals(snapshot.DataSnapshotId, request.DataSnapshotId, StringComparison.Ordinal))
        {
            AddIssue(
                issues,
                "inventory.incompatible",
                Say(AdviceSentence.InventoryIncompatible, "The inventory snapshot belongs to a different profile or data snapshot and was not subtracted."),
                snapshot.Provenance,
                snapshot.Status.Freshness);
            return InventoryInspection.Unknown;
        }

        var snapshotAssessment = AssessEvidence(
            snapshot.Status,
            snapshot.Provenance,
            request.EvaluatedUtc,
            _policy.MaximumInventoryAge,
            allowPartial: true);
        if (!snapshotAssessment.IsReliable)
        {
            AddIssue(
                issues,
                snapshotAssessment.Failure == EvidenceFailure.Stale
                    ? "inventory.stale"
                    : "inventory.untrusted",
                Say(AdviceSentence.InventoryUntrusted, "The inventory snapshot is incomplete, stale, or below the confidence threshold and was not subtracted."),
                snapshotAssessment);
            return InventoryInspection.Unknown;
        }

        var observed = snapshot.Find(request.ItemId);
        if (observed is null)
        {
            var provesZero = snapshot.Status.Completeness == ResultCompleteness.Complete &&
                             snapshot.Coverage.Fraction == 1 &&
                             snapshot.UnresolvedCells == 0;
            if (!provesZero)
            {
                AddIssue(
                    issues,
                    "inventory.partial",
                    Say(AdviceSentence.InventoryUnseen, "The scanned inventory does not cover enough space to treat an unseen item as zero held."),
                    snapshot.Provenance,
                    snapshot.Status.Freshness);
                return InventoryInspection.Unknown;
            }

            var zero = new TrustedValue<int>(0, snapshot.Provenance);
            return new InventoryInspection(zero, zero, [snapshot.Provenance]);
        }

        var total = InspectEvidence(
            observed.TotalQuantity,
            request.EvaluatedUtc,
            _policy.MaximumInventoryAge,
            allowPartial: false);
        var foundInRaid = InspectEvidence(
            observed.FoundInRaidQuantity,
            request.EvaluatedUtc,
            _policy.MaximumInventoryAge,
            allowPartial: false);
        if (!total.IsReliable)
        {
            AddIssue(
                issues,
                "inventory.total-untrusted",
                Say(AdviceSentence.InventoryTotalUntrusted, "The observed total holding count is unknown, ambiguous, stale, or below the confidence threshold."),
                total.Assessment);
        }

        if (!foundInRaid.IsReliable)
        {
            AddIssue(
                issues,
                "inventory.fir-untrusted",
                Say(AdviceSentence.InventoryFirUntrusted, "The observed found-in-raid holding count is unknown, ambiguous, stale, or below the confidence threshold."),
                foundInRaid.Assessment);
        }

        var partial = snapshot.Status.Completeness != ResultCompleteness.Complete ||
                      snapshot.Coverage.Fraction != 1 ||
                      snapshot.UnresolvedCells != 0;
        if (partial)
        {
            AddIssue(
                issues,
                "inventory.partial",
                Say(AdviceSentence.InventoryPartial, "Holdings subtraction uses positive observations only because inventory coverage is partial."),
                snapshot.Provenance,
                snapshot.Status.Freshness);
        }

        var decisionInputs = new[]
            {
                snapshot.Provenance,
                total.Trusted?.Provenance,
                foundInRaid.Trusted?.Provenance,
            }
            .OfType<EvidenceProvenance>()
            .Distinct()
            .ToArray();
        return new InventoryInspection(
            total.Trusted,
            foundInRaid.Trusted,
            decisionInputs);
    }

    private IReadOnlyList<TrustedNeed> InspectNeeds(
        ExplainableRecommendationRequest request,
        IDictionary<string, EvidenceIssue> issues)
    {
        var trusted = new List<TrustedNeed>();
        foreach (var need in request.Profile.Needs.Where(IsInHorizon))
        {
            var assessment = AssessEvidence(
                need.Status,
                need.Provenance,
                request.EvaluatedUtc,
                _policy.MaximumInventoryAge,
                allowPartial: false);
            if (!assessment.IsReliable)
            {
                AddIssue(
                    issues,
                    $"need.untrusted.{need.NeedId}",
                    Say(
                        AdviceSentence.NeedUntrusted,
                        $"{need.DisplayName} is incomplete, stale, or below the confidence threshold and was not used.",
                        need.DisplayName),
                    assessment);
                continue;
            }

            trusted.Add(new TrustedNeed(need, assessment.Provenance));
        }

        return trusted;
    }

    private NeedAllocationResult AllocateNeeds(
        ExplainableRecommendationRequest request,
        IReadOnlyList<TrustedNeed> trustedNeeds,
        InventoryInspection inventory,
        IDictionary<string, EvidenceIssue> issues)
    {
        var hasFirNeed = trustedNeeds.Any(need => need.Need.RequiresFoundInRaid);
        var candidateFir = hasFirNeed
            ? InspectEvidence(
                request.CandidateFoundInRaid,
                request.EvaluatedUtc,
                _policy.MaximumInventoryAge,
                allowPartial: false)
            : EvidenceInspection<bool>.NotRequired(request.CandidateFoundInRaid.Provenance);
        if (hasFirNeed && !candidateFir.IsReliable)
        {
            AddIssue(
                issues,
                "candidate.fir-untrusted",
                Say(AdviceSentence.CandidateFirUntrusted, "Found-in-raid status is unknown, ambiguous, stale, or below the confidence threshold."),
                candidateFir.Assessment);
        }

        var decisionInputs = new List<EvidenceProvenance>();
        if (candidateFir.Trusted is { } trustedCandidateFir)
        {
            decisionInputs.Add(trustedCandidateFir.Provenance);
        }

        var needs = trustedNeeds
            .OrderByDescending(need => _policy.PriorityOf(RuleFor(need.Need)))
            .ThenBy(need => need.Need.StepsAhead)
            .ThenBy(need => need.Need.NeedId, StringComparer.Ordinal)
            .ToArray();

        var total = inventory.Total?.Value;
        var fir = inventory.FoundInRaid?.Value;
        var nonFir = total is { } knownTotal && fir is { } knownFir
            ? Math.Max(0, knownTotal - knownFir)
            : total;
        EvidenceProvenance? nonFirProvenance = null;
        var result = new List<AllocatedNeed>();
        foreach (var trustedNeed in needs)
        {
            var need = trustedNeed.Need;
            var supporting = new List<EvidenceProvenance>();
            if (need.RequiresFoundInRaid && candidateFir.Trusted?.Value != true)
            {
                decisionInputs.Add(trustedNeed.Provenance);
                continue;
            }

            if (need.RequiresFoundInRaid)
            {
                supporting.Add(candidateFir.Trusted!.Provenance);
            }

            var allocated = 0;
            if (need.RequiresFoundInRaid && fir is { } firAvailable)
            {
                allocated = Math.Min(need.RequiredQuantity, firAvailable);
                if (allocated > 0)
                {
                    supporting.Add(inventory.FoundInRaid!.Provenance);
                }
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
                    if (fromNonFir > 0)
                    {
                        nonFirProvenance ??= total is not null && fir is not null
                            ? CombineProvenance(
                                "recommendation.inventory.non-fir",
                                request.EvaluatedUtc,
                                [inventory.Total!.Provenance, inventory.FoundInRaid!.Provenance])
                            : inventory.Total?.Provenance;
                        if (nonFirProvenance is { } source)
                        {
                            supporting.Add(source);
                        }
                    }
                    nonFir = nonFirAvailable - fromNonFir;
                    if (total is { } totalAvailable)
                    {
                        total = Math.Max(0, totalAvailable - fromNonFir);
                    }
                }

                var remaining = need.RequiredQuantity - allocated;
                if (remaining > 0 && fir is { } remainingFir)
                {
                    var fromFir = Math.Min(remaining, remainingFir);
                    allocated += fromFir;
                    if (fromFir > 0)
                    {
                        supporting.Add(inventory.FoundInRaid!.Provenance);
                    }
                    fir = remainingFir - fromFir;
                    if (total is { } totalAvailable)
                    {
                        total = Math.Max(0, totalAvailable - fromFir);
                    }
                }
                else if (remaining > 0 && total is { } totalAvailable && nonFir is null)
                {
                    var fromTotal = Math.Min(remaining, totalAvailable);
                    allocated += fromTotal;
                    if (fromTotal > 0)
                    {
                        supporting.Add(inventory.Total!.Provenance);
                    }
                    total = totalAvailable - fromTotal;
                }
            }

            var outstanding = need.RequiredQuantity - allocated;
            if (outstanding > 0)
            {
                result.Add(new(
                    need,
                    trustedNeed.Provenance,
                    outstanding,
                    allocated,
                    supporting.Distinct().ToArray()));
            }
            else
            {
                // A satisfied need is absent from the visible reason list, but it still enabled the
                // lower-precedence answer and therefore remains decision material.
                decisionInputs.Add(trustedNeed.Provenance);
            }
        }

        return new NeedAllocationResult(
            result,
            decisionInputs.Distinct().ToArray());
    }

    private ScarcityInspection InspectScarcity(
        ExplainableRecommendationRequest request,
        IDictionary<string, EvidenceIssue> issues)
    {
        var field = request.Scarcity.Obtainability;
        var band = InspectEvidence(
            field,
            request.EvaluatedUtc,
            _policy.MaximumScarcityAge,
            allowPartial: false);
        if (!band.IsReliable)
        {
            var code = field.Value is null
                ? "scarcity.unknown"
                : "scarcity.untrusted";
            Said explanation = field.Value is null
                ? Say(AdviceSentence.ScarcityUnknown, "Obtainability is unknown; the item was not assumed to be common.")
                : Say(AdviceSentence.ScarcityUntrusted, "Obtainability is partial, stale, ambiguous, or below the confidence threshold and was not used.");
            AddIssue(issues, code, explanation, band.Assessment);
            return ScarcityInspection.Unknown;
        }

        var trustedBand = band.Trusted!;
        return new(
            trustedBand.Value,
            (int)trustedBand.Value >= (int)_policy.MinimumScarcityToKeep,
            trustedBand.Provenance);
    }

    private RaidContextInspection InspectRaidContext(
        ExplainableRecommendationRequest request,
        IDictionary<string, EvidenceIssue> issues)
    {
        if (request.UseCase != RecommendationUseCase.Loot)
        {
            return RaidContextInspection.NotApplicable(_policy.LootThresholds.Normal);
        }

        if (request.RaidContext is not { } context)
        {
            AddIssue(
                issues,
                "raid-context.missing",
                Say(AdviceSentence.RaidContextMissing, "Raid phase and risk were not supplied; economic loot advice remains review-only."),
                request.Profile.Provenance,
                FreshnessState.Unknown);
            return RaidContextInspection.Unavailable(_policy.LootThresholds.Normal);
        }

        var phase = InspectEvidence(
            context.Phase,
            request.EvaluatedUtc,
            _policy.MaximumRaidContextAge,
            allowPartial: false);
        var risk = InspectEvidence(
            context.Risk,
            request.EvaluatedUtc,
            _policy.MaximumRaidContextAge,
            allowPartial: false);
        if (!phase.IsReliable)
        {
            AddIssue(
                issues,
                context.Phase.Value is null ? "raid-context.phase-unknown" : "raid-context.phase-untrusted",
                Say(AdviceSentence.RaidPhaseUntrusted, "Raid phase is unknown, stale, ambiguous, or below the confidence threshold."),
                phase.Assessment);
        }

        if (!risk.IsReliable)
        {
            AddIssue(
                issues,
                context.Risk.Value is null ? "raid-context.risk-unknown" : "raid-context.risk-untrusted",
                Say(AdviceSentence.RaidRiskUntrusted, "Raid risk is unknown, stale, ambiguous, or below the confidence threshold."),
                risk.Assessment);
        }

        if (!phase.IsReliable || !risk.IsReliable)
        {
            return RaidContextInspection.Unavailable(_policy.LootThresholds.Normal);
        }

        var trustedPhase = phase.Trusted!;
        var trustedRisk = risk.Trusted!;
        var phaseValue = trustedPhase.Value;
        var riskValue = trustedRisk.Value;
        return new(
            true,
            phaseValue,
            riskValue,
            _policy.LootThresholds.RequiredBand(phaseValue, riskValue),
            _policy.LootThresholds.RequiredBand(phaseValue),
            _policy.LootThresholds.RequiredBand(riskValue),
            trustedPhase.Provenance,
            trustedRisk.Provenance);
    }

    private void AddRaidContextReasons(
        RaidContextInspection context,
        ICollection<ReasonDraft> reasons)
    {
        if ((int)context.PhaseRequiredBand > (int)_policy.LootThresholds.Normal &&
            context.Phase is { } phase &&
            context.PhaseProvenance is { } phaseProvenance)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.RaidContext,
                RecommendationReasonCategory.Safety,
                $"raid.phase.{phase.ToString().ToLowerInvariant()}",
                Say(
                    AdviceSentence.RaidPhase,
                    $"The {phase.ToString().ToLowerInvariant()} raid phase sets the ordinary-loot minimum to the {context.PhaseRequiredBand.ToString().ToLowerInvariant()} value band.",
                    AdviceWords.Of<AdvicePhaseWord>(phase),
                    AdviceWords.Of<AdviceBandWord>(context.PhaseRequiredBand)),
                phaseProvenance));
        }

        if ((int)context.RiskRequiredBand > (int)_policy.LootThresholds.Normal &&
            context.Risk is { } risk &&
            context.RiskProvenance is { } riskProvenance)
        {
            reasons.Add(new(
                ExplainableRecommendationRule.RaidContext,
                RecommendationReasonCategory.Safety,
                $"raid.risk.{risk.ToString().ToLowerInvariant()}",
                Say(
                    AdviceSentence.RaidRisk,
                    $"The {risk.ToString().ToLowerInvariant()} raid-risk setting sets the ordinary-loot minimum to the {context.RiskRequiredBand.ToString().ToLowerInvariant()} value band.",
                    AdviceWords.Of<AdviceRiskWord>(risk),
                    AdviceWords.Of<AdviceBandWord>(context.RiskRequiredBand)),
                riskProvenance));
        }
    }

    private static void AddScarcitySensitivities(
        ScarcityInspection scarcity,
        RaidContextInspection raidContext,
        V2RecommendationAction currentAction,
        Func<bool, RaidContextInspection, V2RecommendationAction> selectAlternative,
        ICollection<RecommendationSensitivity> sensitivities)
    {
        if (scarcity.ShouldKeep)
        {
            var improvedAction = selectAlternative(false, raidContext);
            sensitivities.Add(Sensitivity(
                "obtainability-improved",
                Say(AdviceSentence.ObtainabilityImproved, "If this item becomes easier to obtain, economics may become the deciding reason."),
                improvedAction != currentAction ? improvedAction : null));
        }
        else if (scarcity.Band is not null)
        {
            var worsenedAction = selectAlternative(true, raidContext);
            sensitivities.Add(Sensitivity(
                "obtainability-worsened",
                Say(AdviceSentence.ObtainabilityWorsened, "If this item becomes scarce to obtain, the recommendation may change to keep or take it."),
                worsenedAction != currentAction ? worsenedAction : null));
        }
    }

    private void AddRaidContextSensitivities(
        ExplainableRecommendationRequest request,
        ScarcityInspection scarcity,
        RaidContextInspection context,
        EconomicInspection? economics,
        V2RecommendationAction currentAction,
        Func<bool, RaidContextInspection, V2RecommendationAction> selectAlternative,
        ICollection<RecommendationSensitivity> sensitivities)
    {
        if (request.UseCase != RecommendationUseCase.Loot || !context.IsAvailable)
        {
            return;
        }

        if (economics is null ||
            context.Phase is not { } phase ||
            context.Risk is not { } risk)
        {
            return;
        }

        if (context.Risk != RecommendationRaidRisk.Low)
        {
            var lowerRisk = context with
            {
                Risk = RecommendationRaidRisk.Low,
                RiskRequiredBand = _policy.LootThresholds.RequiredBand(RecommendationRaidRisk.Low),
                RequiredBand = _policy.LootThresholds.RequiredBand(phase, RecommendationRaidRisk.Low),
            };
            var lowerRiskAction = selectAlternative(scarcity.ShouldKeep, lowerRisk);
            sensitivities.Add(Sensitivity(
                "raid-risk-reduced",
                Say(AdviceSentence.RaidRiskReduced, "Lowering the current raid risk may lower the economic band required to take this item."),
                lowerRiskAction != currentAction ? lowerRiskAction : null));
        }
        else
        {
            var higherRisk = context with
            {
                Risk = RecommendationRaidRisk.Critical,
                RiskRequiredBand = _policy.LootThresholds.RequiredBand(RecommendationRaidRisk.Critical),
                RequiredBand = _policy.LootThresholds.RequiredBand(phase, RecommendationRaidRisk.Critical),
            };
            var higherRiskAction = selectAlternative(scarcity.ShouldKeep, higherRisk);
            sensitivities.Add(Sensitivity(
                "raid-risk-increased",
                Say(AdviceSentence.RaidRiskIncreased, "Higher raid risk may make ordinary economic loot a leave."),
                higherRiskAction != currentAction ? higherRiskAction : null));
        }

        if (context.Phase is RecommendationRaidPhase.Late or RecommendationRaidPhase.Extracting)
        {
            var earlierPhase = context with
            {
                Phase = RecommendationRaidPhase.Middle,
                PhaseRequiredBand = _policy.LootThresholds.RequiredBand(RecommendationRaidPhase.Middle),
                RequiredBand = _policy.LootThresholds.RequiredBand(RecommendationRaidPhase.Middle, risk),
            };
            var earlierPhaseAction = selectAlternative(scarcity.ShouldKeep, earlierPhase);
            sensitivities.Add(Sensitivity(
                "raid-phase-earlier",
                Say(AdviceSentence.RaidPhaseEarlier, "An earlier raid phase may lower the economic band required to take this item."),
                earlierPhaseAction != currentAction ? earlierPhaseAction : null));
        }
        else
        {
            var laterPhase = context with
            {
                Phase = RecommendationRaidPhase.Extracting,
                PhaseRequiredBand = _policy.LootThresholds.RequiredBand(RecommendationRaidPhase.Extracting),
                RequiredBand = _policy.LootThresholds.RequiredBand(RecommendationRaidPhase.Extracting, risk),
            };
            var laterPhaseAction = selectAlternative(scarcity.ShouldKeep, laterPhase);
            sensitivities.Add(Sensitivity(
                "raid-phase-later",
                Say(AdviceSentence.RaidPhaseLater, "A later raid phase may make ordinary economic loot a leave."),
                laterPhaseAction != currentAction ? laterPhaseAction : null));
        }
    }

    private EconomicInspection? InspectEconomics(
        RecommendationEconomics economics,
        DateTimeOffset evaluatedUtc,
        IDictionary<string, EvidenceIssue> issues)
    {
        var footprint = InspectEvidence(
            economics.OccupiedSquares,
            evaluatedUtc,
            _policy.MaximumPriceAge,
            allowPartial: false);
        var flea = InspectEconomicPrice(
            economics.FleaNetRoubles,
            evaluatedUtc,
            _policy.MaximumPriceAge);
        var trader = InspectEconomicPrice(
            economics.TraderRoubles,
            evaluatedUtc,
            _policy.MaximumPriceAge);
        var gross = InspectEvidence(
            economics.FleaGrossRoubles,
            evaluatedUtc,
            _policy.MaximumPriceAge,
            allowPartial: false);
        var fee = InspectEvidence(
            economics.FleaFeeRoubles,
            evaluatedUtc,
            _policy.MaximumPriceAge,
            allowPartial: false);
        var condition = InspectEvidence(
            economics.ConditionFraction,
            evaluatedUtc,
            _policy.MaximumPriceAge,
            allowPartial: false);
        var fleaRole = CombineProvenance(
            $"recommendation.economic-price.flea-net.{flea.RoleState}",
            evaluatedUtc,
            [flea.Assessment.Provenance]);
        var traderRole = CombineProvenance(
            $"recommendation.economic-price.trader.{trader.RoleState}",
            evaluatedUtc,
            [trader.Assessment.Provenance]);

        if (!footprint.IsReliable)
        {
            AddIssue(
                issues,
                "economics.footprint-missing",
                Say(AdviceSentence.FootprintMissing, "Occupied squares are missing, stale, ambiguous, or below the confidence threshold; value per square was not invented."),
                footprint.Assessment);
        }

        if (!flea.IsResolved)
        {
            AddIssue(
                issues,
                "economics.flea-net-untrusted",
                Say(
                    AdviceSentence.FleaNetUntrusted,
                    "The flea-net value or its reported absence is stale, ambiguous, incomplete, unauthoritative, or below the confidence threshold.",
                    new Phrase(AdviceSentence.PriceDoubt)),
                flea.Assessment);
        }

        if (!trader.IsResolved)
        {
            AddIssue(
                issues,
                "economics.trader-untrusted",
                Say(
                    AdviceSentence.TraderUntrusted,
                    "The trader value or its reported absence is stale, ambiguous, incomplete, unauthoritative, or below the confidence threshold.",
                    new Phrase(AdviceSentence.PriceDoubt)),
                trader.Assessment);
        }

        if (!flea.HasValue && !trader.HasValue)
        {
            AddIssue(
                issues,
                "economics.price-missing",
                Say(AdviceSentence.PriceMissing, "No current trustworthy flea net or trader value is available; gross value is not treated as net."),
                CombineProvenance(
                    "recommendation.economic-price.unavailable",
                    evaluatedUtc,
                    [fleaRole, traderRole]),
                WorstFreshness([flea.Assessment.Freshness, trader.Assessment.Freshness]));
            return null;
        }

        if (!footprint.IsReliable)
        {
            return null;
        }

        var useFlea = flea.HasValue &&
                      (!trader.HasValue || flea.Trusted!.Value >= trader.Trusted!.Value);
        var price = useFlea ? flea.Trusted! : trader.Trusted!;
        var value = price.Value;
        var trustedFootprint = footprint.Trusted!;
        var footprintValue = trustedFootprint.Value;
        var valuePerSquare = value / footprintValue;
        // Selecting a sale channel depends on both price fields: either two compared figures or
        // one figure and the explicit absence/unreliability of the other. The named wrappers keep
        // both roles distinct even when a single catalog snapshot backs both fields.
        var priceRole = CombineProvenance(
            $"recommendation.economic-price.{(useFlea ? "flea-net" : "trader")}",
            evaluatedUtc,
            [fleaRole, traderRole]);
        var footprintRole = CombineProvenance(
            "recommendation.economic-footprint",
            evaluatedUtc,
            [trustedFootprint.Provenance]);
        var calculation = CombineProvenance(
            "recommendation.value-per-square",
            evaluatedUtc,
            [priceRole, footprintRole]);
        var explanationInputs = new List<EvidenceProvenance> { calculation };
        AddExplanationInput(
            explanationInputs,
            gross.Trusted,
            "recommendation.economic-detail.flea-gross",
            evaluatedUtc);
        AddExplanationInput(
            explanationInputs,
            fee.Trusted,
            "recommendation.economic-detail.flea-fee",
            evaluatedUtc);
        AddExplanationInput(
            explanationInputs,
            condition.Trusted,
            "recommendation.economic-detail.condition",
            evaluatedUtc);
        var explanationProvenance = explanationInputs.Count == 1
            ? calculation
            : CombineProvenance(
                "recommendation.economic-explanation",
                evaluatedUtc,
                explanationInputs);
        return new(
            value,
            footprintValue,
            valuePerSquare,
            _policy.ValueBands.Classify(valuePerSquare),
            useFlea ? "flea-net" : "trader",
            useFlea ? V2RecommendationAction.SellOnFlea : V2RecommendationAction.SellToTrader,
            gross.Trusted?.Value,
            fee.Trusted?.Value,
            flea.Trusted?.Value,
            trader.Trusted?.Value,
            condition.Trusted?.Value,
            priceRole,
            footprintRole,
            calculation,
            explanationProvenance);
    }

    private EconomicPriceInspection InspectEconomicPrice(
        EvidencedValue<long?> field,
        DateTimeOffset evaluatedUtc,
        TimeSpan maximumAge)
    {
        // Unavailable is the explicit negative fact: the provider established that this sale
        // channel has no current value. Unknown is unresolved and must not silently become the
        // negative comparison that allows the other channel to produce decisive advice.
        if (!HasClaim(field) && field.Status.Completeness == ResultCompleteness.Unavailable)
        {
            var assessment = AssessEvidence(
                field.Status,
                field.Provenance,
                evaluatedUtc,
                maximumAge,
                allowPartial: false,
                allowUnavailable: true);
            return new EconomicPriceInspection(null, assessment, DeclaresUnavailable: true);
        }

        var inspected = InspectEvidence(
            field,
            evaluatedUtc,
            maximumAge,
            allowPartial: false);
        return new EconomicPriceInspection(
            inspected.Trusted,
            inspected.Assessment,
            DeclaresUnavailable: false);
    }

    private static void AddExplanationInput<T>(
        ICollection<EvidenceProvenance> inputs,
        TrustedValue<T>? value,
        string sourceIdentifier,
        DateTimeOffset evaluatedUtc)
        where T : struct
    {
        if (value is null)
        {
            return;
        }

        inputs.Add(CombineProvenance(sourceIdentifier, evaluatedUtc, [value.Provenance]));
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
        bool scarcityKeep,
        RaidContextInspection raidContext,
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

        if (scarcityKeep)
        {
            return KeepOrTake(useCase);
        }

        if (economics is null || hasEvidenceIssues)
        {
            return V2RecommendationAction.Review;
        }

        if (useCase == RecommendationUseCase.Loot)
        {
            return (int)economics.Band >= (int)raidContext.RequiredBand
                ? V2RecommendationAction.Take
                : V2RecommendationAction.Leave;
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
        ScarcityInspection scarcity,
        RaidContextInspection raidContext,
        EconomicInspection? economics,
        bool hasEvidenceIssues)
    {
        if (eventState == EventItemState.Allergic) return ExplainableRecommendationRule.EventAllergy;
        if (explicitAction is not null) return ExplainableRecommendationRule.ExplicitOverride;
        if (isProtected) return ExplainableRecommendationRule.ProtectedItem;
        if (needs.Count > 0) return needs.Select(need => RuleFor(need.Need)).OrderByDescending(_policy.PriorityOf).First();
        if (isPinned) return ExplainableRecommendationRule.Pin;
        if (isWishlisted) return ExplainableRecommendationRule.Wishlist;
        if (eventState == EventItemState.Untested) return ExplainableRecommendationRule.EventUntested;
        if (eventState == EventItemState.Safe) return ExplainableRecommendationRule.EventSafe;
        if (scarcity.ShouldKeep) return ExplainableRecommendationRule.Scarcity;
        if (hasEvidenceIssues) return ExplainableRecommendationRule.EvidenceQuality;
        if (economics is { } economic && raidContext.ChangesEconomicAction(economic.Band, _policy.LootThresholds.Normal))
        {
            return ExplainableRecommendationRule.RaidContext;
        }

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

    private static ReasonDraft SelectSummaryReason(
        ExplainableRecommendationRule? dominantRule,
        RaidContextInspection raidContext,
        IReadOnlyList<ReasonDraft> orderedReasons)
    {
        if (dominantRule is not { } rule)
        {
            return orderedReasons[0];
        }

        if (rule == ExplainableRecommendationRule.RaidContext)
        {
            // The stricter axis is the one that actually establishes the combined threshold. Phase
            // wins an exact tie so identical policies produce one stable summary on every runtime.
            var bindingPrefix = (int)raidContext.RiskRequiredBand > (int)raidContext.PhaseRequiredBand
                ? "raid.risk."
                : "raid.phase.";
            var bindingReason = orderedReasons.FirstOrDefault(reason =>
                reason.Rule == rule && reason.Code.StartsWith(bindingPrefix, StringComparison.Ordinal));
            if (bindingReason is not null)
            {
                return bindingReason;
            }
        }

        return orderedReasons.FirstOrDefault(reason => reason.Rule == rule) ?? orderedReasons[0];
    }

    private static Said Summary(V2RecommendationAction action, Said topReason) =>
        new($"{action}: {topReason.English}", new Phrase(AdviceWords.Of<AdviceSummary>(action), topReason.Words));

    /// <summary>A sentence the engine gives: its fixed English, and the same words as a code the App says.</summary>
    private static Said Say(AdviceSentence code, string english, params object?[] arguments) =>
        new(english, new Phrase(code, arguments));

    private static RecommendationSensitivity Sensitivity(
        string factCode,
        Said explanation,
        V2RecommendationAction? alternativeAction) =>
        new RecommendationSensitivity(factCode, explanation.English, alternativeAction).WithWords(explanation.Words);

    private static Said EconomicExplanation(EconomicInspection economics)
    {
        var details = new List<Said>();
        if (economics.FleaGrossRoubles is { } gross) details.Add(Say(AdviceSentence.DetailFleaGross, $"flea gross {gross.ToString("N0", CultureInfo.InvariantCulture)}", gross));
        if (economics.FleaFeeRoubles is { } fee) details.Add(Say(AdviceSentence.DetailFee, $"fee {fee.ToString("N0", CultureInfo.InvariantCulture)}", fee));
        if (economics.FleaNetRoubles is { } net) details.Add(Say(AdviceSentence.DetailFleaNet, $"flea net {net.ToString("N0", CultureInfo.InvariantCulture)}", net));
        if (economics.TraderRoubles is { } trader) details.Add(Say(AdviceSentence.DetailTrader, $"trader {trader.ToString("N0", CultureInfo.InvariantCulture)}", trader));
        if (economics.ConditionFraction is { } condition) details.Add(Say(AdviceSentence.DetailCondition, $"condition {condition.ToString("P0", CultureInfo.InvariantCulture)}", WholePercent(condition)));
        var suffix = details.Count == 0 ? string.Empty : $" ({string.Join(", ", details.Select(detail => detail.English))})";
        var english = $"{economics.TotalValue.ToString("N0", CultureInfo.InvariantCulture)} roubles across {economics.Footprint} square(s) is {economics.ValuePerSquare.ToString("N0", CultureInfo.InvariantCulture)} per square, in the {economics.Band.ToString().ToLowerInvariant()} band{suffix}.";
        var band = AdviceWords.Of<AdviceBandWord>(economics.Band);
        return details.Count == 0
            ? Say(AdviceSentence.Economics, english, economics.TotalValue, economics.Footprint, economics.ValuePerSquare, band)
            : Say(AdviceSentence.EconomicsDetailed, english, economics.TotalValue, economics.Footprint, economics.ValuePerSquare, band, DetailList(details));
    }

    /// <summary>The details as one phrase, each joined to the rest the way the English joins them with ", ".</summary>
    private static Phrase DetailList(IReadOnlyList<Said> details)
    {
        var list = details[^1].Words;
        for (var index = details.Count - 2; index >= 0; index--)
        {
            list = new Phrase(AdviceSentence.DetailList, details[index].Words, list);
        }

        return list;
    }

    /// <summary>
    /// A fraction as the whole percent the invariant "P0" format writes, so "condition 60 %" reads
    /// the same in English once the App says it; the English keeps the invariant space before "%".
    /// </summary>
    private static long WholePercent(double fraction) =>
        (long)Math.Round(fraction * 100, MidpointRounding.AwayFromZero);

    private static Said FleaOfferExplanation(long offerPrice, EconomicInspection resale, long margin)
    {
        var fleaChannel = resale.SourceCode == "flea-net";
        var channel = fleaChannel ? "flea after its fee" : "best trader";
        var channelWords = new Phrase(fleaChannel ? AdviceSentence.ChannelFleaAfterFee : AdviceSentence.ChannelBestTrader);
        var result = margin > 0
            ? Say(AdviceSentence.MarginMore, $"{margin.ToString("N0", CultureInfo.InvariantCulture)} roubles more than it costs", margin)
            : margin == 0
                ? Say(AdviceSentence.MarginSame, "the same as it costs")
                : Say(AdviceSentence.MarginLess, $"{Math.Abs(margin).ToString("N0", CultureInfo.InvariantCulture)} roubles less than it costs", Math.Abs(margin));
        var condition = resale.ConditionFraction is { } fraction
            ? $" at {fraction.ToString("P0", CultureInfo.InvariantCulture)} condition"
            : string.Empty;
        var english = $"The offer costs {offerPrice.ToString("N0", CultureInfo.InvariantCulture)} roubles{condition}; the {channel} returns {resale.TotalValue.ToString("N0", CultureInfo.InvariantCulture)}, {result.English}.";
        return resale.ConditionFraction is { } known
            ? Say(AdviceSentence.OfferAtCondition, english, offerPrice, WholePercent(known), channelWords, resale.TotalValue, result.Words)
            : Say(AdviceSentence.Offer, english, offerPrice, channelWords, resale.TotalValue, result.Words);
    }

    private TrustedValue<T>? InspectRequiredProfileField<T>(
        EvidencedValue<T?> field,
        ExplainableRecommendationRequest request,
        IDictionary<string, EvidenceIssue> issues,
        string issueCode,
        Said issueExplanation)
        where T : struct
    {
        // Overrides, protection, pins, wishlist membership, and recorded event outcomes are durable
        // profile choices. Their explicit freshness status still applies, but an inventory TTL must
        // not silently expire them merely because the user made the choice more than a day ago.
        var inspection = InspectEvidence(
            field,
            request.EvaluatedUtc,
            maximumAge: null,
            allowPartial: false);
        if (!inspection.IsReliable)
        {
            AddIssue(issues, issueCode, issueExplanation, inspection.Assessment);
        }

        return inspection.Trusted;
    }

    private TrustedValue<EventItemState>? InspectEventState(
        EvidencedValue<EventItemState?> field,
        ExplainableRecommendationRequest request,
        IDictionary<string, EvidenceIssue> issues,
        string issueCode,
        Said issueExplanation)
    {
        var inspection = InspectEvidence(
            field,
            request.EvaluatedUtc,
            maximumAge: null,
            allowPartial: false);
        if (!inspection.IsReliable)
        {
            AddIssue(issues, issueCode, issueExplanation, inspection.Assessment);
            return null;
        }

        var trusted = inspection.Trusted!;
        if (trusted.Value is not (EventItemState.Safe or EventItemState.Allergic))
        {
            return trusted;
        }

        // Recognition and catalog data can establish that an event item is applicable or
        // untested, but they cannot establish what happened after this player consumed it.
        // A correction's declared origin is checked directly so the generic correction
        // provenance mapping cannot turn a desktop or imported correction into a user outcome.
        var userConfirmed = field.Corrections.Count > 0
            ? field.Corrections[^1].OriginClass is CorrectionOriginClass.User or CorrectionOriginClass.PairedDevice
            : field.Provenance.SourceClass is EvidenceSourceClass.UserEntered or EvidenceSourceClass.PairedDeviceAction;
        if (userConfirmed)
        {
            return trusted;
        }

        var authorityFailure = inspection.Assessment with
        {
            IsReliable = false,
            Failure = EvidenceFailure.Unauthoritative,
        };
        AddIssue(issues, issueCode, issueExplanation, authorityFailure);
        return null;
    }

    private EvidenceInspection<T> InspectEvidence<T>(
        EvidencedValue<T?> field,
        DateTimeOffset evaluatedUtc,
        TimeSpan? maximumAge,
        bool allowPartial)
        where T : struct
    {
        var provenance = EffectiveProvenance(field);
        var assessment = AssessEvidence(
            field.Status,
            provenance,
            evaluatedUtc,
            maximumAge,
            allowPartial);

        // An unresolved candidate is the evidence that makes a field ambiguous. Preserve every
        // candidate in the decision lineage instead of reporting ambiguity with only the selected
        // value's provenance and an overstated confidence score.
        if (field.Candidates.Count > 0 && field.Corrections.Count == 0)
        {
            var candidateAssessments = field.Candidates
                .Select((candidate, index) => (
                    Candidate: candidate,
                    Index: index,
                    Assessment: AssessEvidence(
                        field.Status,
                        candidate.Provenance,
                        evaluatedUtc,
                        maximumAge,
                        allowPartial)))
                .ToArray();
            var primaryRole = CombineProvenance(
                $"recommendation.evidence-ambiguity.{field.FieldId}.primary",
                evaluatedUtc,
                [provenance]);
            var candidateRoles = candidateAssessments
                .Select(candidate => CombineProvenance(
                    $"recommendation.evidence-ambiguity.{field.FieldId}.candidate.{candidate.Index.ToString("D2", CultureInfo.InvariantCulture)}.{candidate.Candidate.CandidateId}",
                    evaluatedUtc,
                    [candidate.Assessment.Provenance]))
                .ToArray();
            var ambiguityProvenance = CombineProvenance(
                $"recommendation.evidence-ambiguity.{field.FieldId}",
                evaluatedUtc,
                new[] { primaryRole }
                    .Concat(candidateRoles)
                    .ToArray());
            return new EvidenceInspection<T>(
                null,
                new ReliabilityAssessment(
                    false,
                    EvidenceFailure.Ambiguous,
                    WorstFreshness(candidateAssessments
                        .Select(candidate => candidate.Assessment.Freshness)
                        .Prepend(assessment.Freshness)),
                    ambiguityProvenance));
        }

        if (!assessment.IsReliable)
        {
            return new EvidenceInspection<T>(null, assessment);
        }

        if (field.Value is not { } value)
        {
            return new EvidenceInspection<T>(
                null,
                assessment with { IsReliable = false, Failure = EvidenceFailure.Missing });
        }

        // An append-only correction selects the current value without erasing the original
        // candidates. Collection bounds are also checked before evaluation in AddEvidenceTimes.
        if (field.Candidates.Count > MaximumEvidenceEntries ||
            field.Corrections.Count > MaximumEvidenceEntries)
        {
            return new EvidenceInspection<T>(
                null,
                assessment with { IsReliable = false, Failure = EvidenceFailure.Ambiguous });
        }

        return new EvidenceInspection<T>(new TrustedValue<T>(value, provenance), assessment);
    }

    private ReliabilityAssessment AssessEvidence(
        ResultStatus status,
        EvidenceProvenance provenance,
        DateTimeOffset evaluatedUtc,
        TimeSpan? maximumAge,
        bool allowPartial,
        bool allowUnavailable = false)
    {
        var completeEnough = status.Completeness == ResultCompleteness.Complete ||
                             (allowPartial && status.Completeness == ResultCompleteness.Partial) ||
                             (allowUnavailable && status.Completeness == ResultCompleteness.Unavailable);
        if (!completeEnough)
        {
            return new(false, EvidenceFailure.Incomplete, status.Freshness, provenance);
        }

        if (status.Freshness == FreshnessState.Stale)
        {
            return new(false, EvidenceFailure.Stale, FreshnessState.Stale, provenance);
        }

        if (status.Freshness == FreshnessState.Unknown)
        {
            return new(false, EvidenceFailure.UnknownFreshness, FreshnessState.Unknown, provenance);
        }

        if (provenance.EvidenceThroughUtc > evaluatedUtc)
        {
            return new(false, EvidenceFailure.Future, FreshnessState.Unknown, provenance);
        }

        if (maximumAge is { } age && evaluatedUtc - provenance.EvidenceThroughUtc > age)
        {
            return new(false, EvidenceFailure.Stale, FreshnessState.Stale, provenance);
        }

        if (ContainsUnknownSource(provenance))
        {
            return new(false, EvidenceFailure.Unauthoritative, FreshnessState.Current, provenance);
        }

        if (!MeetsConfidence(provenance))
        {
            return new(false, EvidenceFailure.LowConfidence, FreshnessState.Current, provenance);
        }

        return new(true, EvidenceFailure.None, FreshnessState.Current, provenance);
    }

    private static EvidenceProvenance EffectiveProvenance<T>(EvidencedValue<T?> field)
        where T : struct
    {
        if (field.Corrections.Count == 0)
        {
            return field.Provenance;
        }

        var correction = field.Corrections[^1];
        var sourceClass = correction.OriginClass == CorrectionOriginClass.PairedDevice
            ? EvidenceSourceClass.PairedDeviceAction
            : EvidenceSourceClass.UserEntered;
        return new EvidenceProvenance(
            sourceClass,
            $"correction:{field.FieldId}:{correction.OriginClass}:{correction.OriginIdentifier}:{correction.Sequence.ToString(CultureInfo.InvariantCulture)}",
            correction.CorrectedUtc,
            EvidenceConfidence.Certain,
            new ProducerIdentity("Tarkov Companion evidence correction", "2"),
            reference: field.Provenance.SourceIdentifier);
    }

    private static bool HasClaim<T>(EvidencedValue<T?> field)
        where T : struct =>
        field.Value is not null || field.Candidates.Count > 0 || field.Corrections.Count > 0;

    private bool MeetsConfidence(EvidenceProvenance provenance) =>
        provenance.Confidence.Score is { } score && score >= _policy.MinimumEvidenceConfidence;

    private static FreshnessState DecisionFreshness(IEnumerable<EvidenceIssue> issues)
    {
        return WorstFreshness(issues.Select(issue => issue.Freshness));
    }

    private static FreshnessState WorstFreshness(IEnumerable<FreshnessState> values)
    {
        var freshness = values.ToArray();
        if (freshness.Contains(FreshnessState.Stale))
        {
            return FreshnessState.Stale;
        }

        return freshness.Contains(FreshnessState.Unknown)
            ? FreshnessState.Unknown
            : FreshnessState.Current;
    }

    private static void AddIssue(
        IDictionary<string, EvidenceIssue> issues,
        string code,
        Said explanation,
        ReliabilityAssessment assessment) =>
        AddIssue(issues, code, explanation, assessment.Provenance, assessment.Freshness);

    private static void AddIssue(
        IDictionary<string, EvidenceIssue> issues,
        string code,
        Said explanation,
        EvidenceProvenance provenance,
        FreshnessState freshness = FreshnessState.Current)
    {
        issues.TryAdd(code, new(code, explanation, provenance, freshness));
    }

    private static EvidenceProvenance CombineProvenance(
        string sourceIdentifier,
        DateTimeOffset evaluatedUtc,
        IReadOnlyList<EvidenceProvenance> inputs)
    {
        var distinctInputs = inputs.Distinct().ToArray();
        if (distinctInputs.Length == 0)
        {
            throw new ArgumentException("A recommendation calculation must name its inputs.", nameof(inputs));
        }

        if (distinctInputs.Any(input => input.EvidenceThroughUtc > evaluatedUtc))
        {
            throw new ArgumentException("Recommendation evidence cannot be newer than its evaluation time.", nameof(inputs));
        }

        var depth = 1 + distinctInputs.Max(ProvenanceDepth);
        var count = distinctInputs.Sum(input => 1 + ProvenanceInputCount(input));
        if (depth > EvidenceProvenance.MaxInputDepth || count > EvidenceProvenance.MaxInputCount)
        {
            throw new ArgumentException(
                "Recommendation evidence cannot be represented within the provenance depth/count contract; the decision was rejected.",
                nameof(inputs));
        }

        var containsModel = distinctInputs.Any(ContainsModelledEstimate);
        var containsUnknown = distinctInputs.Any(ContainsUnknownSource);
        var scores = distinctInputs.Select(input => input.Confidence.Score).ToArray();
        var confidence = containsModel
            ? new EvidenceConfidence(
                EvidenceConfidenceKind.ProviderScore,
                containsUnknown ? 0 : scores.Min(score => score ?? 0))
            : containsUnknown
                ? EvidenceConfidence.Unscored
                : scores.All(score => score is not null)
                    ? new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, scores.Min()!.Value)
                    : EvidenceConfidence.Unscored;
        var producer = new ProducerIdentity(
            "Tarkov Companion recommendation engine",
            ExplainableRecommendationPolicy.CurrentRulesetVersion,
            containsModel ? ExplainableRecommendationPolicy.CurrentRulesetVersion : null);

        if (containsModel)
        {
            var evidenceThrough = distinctInputs.Max(input => input.EvidenceThroughUtc);
            var fractions = distinctInputs.Select(input => input.Coverage?.Fraction).ToArray();
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
                inputs: distinctInputs);
        }

        return new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            sourceIdentifier,
            evaluatedUtc,
            confidence,
            producer,
            generatedUtc: evaluatedUtc,
            inputs: distinctInputs);
    }

    private static bool ContainsModelledEstimate(EvidenceProvenance provenance) =>
        provenance.SourceClass == EvidenceSourceClass.ModelledEstimate ||
        provenance.Inputs.Any(ContainsModelledEstimate);

    private static bool ContainsUnknownSource(EvidenceProvenance provenance) =>
        provenance.SourceClass == EvidenceSourceClass.Unknown ||
        provenance.Inputs.Any(ContainsUnknownSource);

    private static int ProvenanceDepth(EvidenceProvenance provenance) =>
        1 + (provenance.Inputs.Count == 0 ? 0 : provenance.Inputs.Max(ProvenanceDepth));

    private static int ProvenanceInputCount(EvidenceProvenance provenance) =>
        provenance.Inputs.Count + provenance.Inputs.Sum(ProvenanceInputCount);

    private static void ValidateEvidenceTimes(
        ExplainableRecommendationRequest request,
        CancellationToken cancellationToken)
    {
        var provenances = new List<EvidenceProvenance>
        {
            request.Profile.Provenance,
        };
        var correctionTimes = new List<DateTimeOffset>();
        AddEvidenceTimes(request.Profile.ExplicitAction, provenances, correctionTimes);
        AddEvidenceTimes(request.Profile.ProtectedItem, provenances, correctionTimes);
        AddEvidenceTimes(request.Profile.Pinned, provenances, correctionTimes);
        AddEvidenceTimes(request.Profile.Wishlist, provenances, correctionTimes);
        AddEvidenceTimes(request.Profile.EventState.State, provenances, correctionTimes);
        AddEvidenceTimes(request.CandidateFoundInRaid, provenances, correctionTimes);
        AddEvidenceTimes(request.Economics.FleaGrossRoubles, provenances, correctionTimes);
        AddEvidenceTimes(request.Economics.FleaFeeRoubles, provenances, correctionTimes);
        AddEvidenceTimes(request.Economics.FleaNetRoubles, provenances, correctionTimes);
        AddEvidenceTimes(request.Economics.TraderRoubles, provenances, correctionTimes);
        AddEvidenceTimes(request.Economics.OccupiedSquares, provenances, correctionTimes);
        AddEvidenceTimes(request.Economics.ConditionFraction, provenances, correctionTimes);
        AddEvidenceTimes(request.Scarcity.Obtainability, provenances, correctionTimes);
        foreach (var need in request.Profile.Needs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            provenances.Add(need.Provenance);
        }

        if (request.Inventory is { } inventory)
        {
            provenances.Add(inventory.Provenance);
            // Only the observed count for this recommendation's item can affect its allocation.
            // Walking every item kind for every loot candidate would multiply one shared snapshot
            // into unbounded scan work without adding decision evidence.
            if (inventory.Find(request.ItemId) is { } item)
            {
                AddEvidenceTimes(item.TotalQuantity, provenances, correctionTimes);
                AddEvidenceTimes(item.FoundInRaidQuantity, provenances, correctionTimes);
            }
        }

        if (request.RaidContext is { } raidContext)
        {
            AddEvidenceTimes(raidContext.Phase, provenances, correctionTimes);
            AddEvidenceTimes(raidContext.Risk, provenances, correctionTimes);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (provenances.Any(provenance => provenance.EvidenceThroughUtc > request.EvaluatedUtc) ||
            correctionTimes.Any(correctedUtc => correctedUtc > request.EvaluatedUtc))
        {
            throw new ArgumentException("Recommendation evidence cannot be newer than the evaluation time.", nameof(request));
        }
    }

    private static void AddEvidenceTimes<T>(
        EvidencedValue<T?> field,
        ICollection<EvidenceProvenance> provenances,
        ICollection<DateTimeOffset> correctionTimes)
        where T : struct
    {
        if (field.Candidates.Count > MaximumEvidenceEntries ||
            field.Corrections.Count > MaximumEvidenceEntries)
        {
            throw new ArgumentException(
                $"Recommendation evidence cannot exceed {MaximumEvidenceEntries} candidates or corrections per field.",
                nameof(field));
        }

        provenances.Add(field.Provenance);
        foreach (var candidate in field.Candidates)
        {
            provenances.Add(candidate.Provenance);
        }

        foreach (var correction in field.Corrections)
        {
            correctionTimes.Add(correction.CorrectedUtc);
        }
    }

    private sealed record ReasonDraft(
        ExplainableRecommendationRule Rule,
        RecommendationReasonCategory Category,
        string Code,
        Said Explanation,
        EvidenceProvenance Provenance);

    /// <summary>A sentence as it is stored (fixed English) and as it is said (a code the App words).</summary>
    private sealed record Said(string English, Phrase Words);

    private sealed record EvidenceIssue(
        string Code,
        Said Explanation,
        EvidenceProvenance Provenance,
        FreshnessState Freshness);

    private enum EvidenceFailure
    {
        None = 0,
        Missing,
        Incomplete,
        Ambiguous,
        Stale,
        UnknownFreshness,
        LowConfidence,
        Future,
        Unauthoritative,
    }

    private sealed record ReliabilityAssessment(
        bool IsReliable,
        EvidenceFailure Failure,
        FreshnessState Freshness,
        EvidenceProvenance Provenance);

    private sealed record TrustedValue<T>(T Value, EvidenceProvenance Provenance)
        where T : struct;

    private sealed record EvidenceInspection<T>(
        TrustedValue<T>? Trusted,
        ReliabilityAssessment Assessment)
        where T : struct
    {
        public bool IsReliable => Assessment.IsReliable;

        public static EvidenceInspection<T> NotRequired(EvidenceProvenance provenance) => new(
            null,
            new ReliabilityAssessment(true, EvidenceFailure.None, FreshnessState.Current, provenance));
    }

    private sealed record EconomicPriceInspection(
        TrustedValue<long>? Trusted,
        ReliabilityAssessment Assessment,
        bool DeclaresUnavailable)
    {
        public bool HasValue => Trusted is not null;

        public bool IsConfirmedUnavailable => DeclaresUnavailable && Assessment.IsReliable;

        public bool IsResolved => HasValue || IsConfirmedUnavailable;

        public string RoleState => HasValue
            ? "available"
            : IsConfirmedUnavailable
                ? "unavailable"
                : DeclaresUnavailable
                    ? "unavailable-untrusted"
                    : "unresolved";
    }

    private sealed record InventoryInspection(
        TrustedValue<int>? Total,
        TrustedValue<int>? FoundInRaid,
        IReadOnlyList<EvidenceProvenance> DecisionInputs)
    {
        public static InventoryInspection Unknown { get; } = new(null, null, []);
    }

    private sealed record TrustedNeed(RecommendationNeed Need, EvidenceProvenance Provenance);

    private sealed record NeedAllocationResult(
        IReadOnlyList<AllocatedNeed> Outstanding,
        IReadOnlyList<EvidenceProvenance> DecisionInputs);

    private sealed record AllocatedNeed(
        RecommendationNeed Need,
        EvidenceProvenance Provenance,
        int Outstanding,
        int Allocated,
        IReadOnlyList<EvidenceProvenance> SupportingProvenance);

    private sealed record ScarcityInspection(
        RecommendationObtainabilityBand? Band,
        bool ShouldKeep,
        EvidenceProvenance? Provenance)
    {
        public static ScarcityInspection Unknown { get; } = new(null, false, null);
    }

    private sealed record RaidContextInspection(
        bool IsAvailable,
        RecommendationRaidPhase? Phase,
        RecommendationRaidRisk? Risk,
        EconomicValueBand RequiredBand,
        EconomicValueBand PhaseRequiredBand,
        EconomicValueBand RiskRequiredBand,
        EvidenceProvenance? PhaseProvenance,
        EvidenceProvenance? RiskProvenance)
    {
        public static RaidContextInspection NotApplicable(EconomicValueBand requiredBand) =>
            new(false, null, null, requiredBand, requiredBand, requiredBand, null, null);

        public static RaidContextInspection Unavailable(EconomicValueBand requiredBand) =>
            new(false, null, null, requiredBand, requiredBand, requiredBand, null, null);

        public IReadOnlyList<EvidenceProvenance> DecisionInputs =>
            new[] { PhaseProvenance, RiskProvenance }
                .OfType<EvidenceProvenance>()
                .Distinct()
                .ToArray();

        public bool ChangesEconomicAction(EconomicValueBand actual, EconomicValueBand normal) =>
            (int)RequiredBand > (int)normal &&
            (int)actual >= (int)normal &&
            (int)actual < (int)RequiredBand;
    }

    private sealed record EconomicInspection(
        long TotalValue,
        int Footprint,
        long ValuePerSquare,
        EconomicValueBand Band,
        string SourceCode,
        V2RecommendationAction SaleAction,
        long? FleaGrossRoubles,
        long? FleaFeeRoubles,
        long? FleaNetRoubles,
        long? TraderRoubles,
        double? ConditionFraction,
        EvidenceProvenance PriceRole,
        EvidenceProvenance FootprintRole,
        EvidenceProvenance CalculationProvenance,
        EvidenceProvenance ExplanationProvenance);
}
