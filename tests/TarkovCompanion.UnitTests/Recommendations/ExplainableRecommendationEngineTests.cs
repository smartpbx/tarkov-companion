using TarkovCompanion.Application.Services.Recommendations;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Recommendations;
using V2Action = TarkovCompanion.Core.Abstractions.V2.RecommendationAction;

namespace TarkovCompanion.UnitTests.Recommendations;

public sealed class ExplainableRecommendationEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly InventoryProfileScope Scope = new(
        Guid.Parse("74000000-0000-0000-0000-000000000001"),
        "wipe-2026-09",
        "pvp");

    [Fact]
    public void SafetyProtectionNeedsAndPinsStayAheadOfEconomics()
    {
        var request = Request(
            profile: Profile(
                protectedItem: true,
                pinned: true,
                eventState: EventItemState.Allergic,
                needs: [Need("current-fir", RecommendationNeedPurpose.Quest, 0, 1, fir: true)]),
            inventory: Inventory(total: 0, fir: 0),
            economics: Economics(fleaNet: 2_000_000, trader: 1_000_000, squares: 1));

        var result = new ExplainableRecommendationEngine().Evaluate(request);
        var decision = Assert.IsType<RecommendationDecision>(result.Decision.Value);

        Assert.Equal(V2Action.AvoidConsume, decision.Action);
        Assert.Equal(ExplainableRecommendationPolicy.CurrentRulesetVersion, result.RulesetVersion);
        Assert.Equal(
            [
                "event.allergic",
                "item.protected",
                "need.quest-current-fir.current-fir",
                "profile.pinned",
                "economics.flea-net.exceptional",
            ],
            decision.Reasons.Select(reason => reason.Code));
        Assert.Equal(2_000_000, decision.OpportunityCostRoubles.Value);
        Assert.NotNull(decision.OpportunityCostLineage);
    }

    [Fact]
    public void CompatibleHoldingsAreAllocatedInPrecedenceOrderAndHorizonsAreApplied()
    {
        var needs = new[]
        {
            Need("hideout", RecommendationNeedPurpose.Hideout, 2, 1),
            Need("too-far", RecommendationNeedPurpose.Quest, 6, 1),
            Need("future", RecommendationNeedPurpose.Quest, 3, 1),
            Need("current-general", RecommendationNeedPurpose.Quest, 0, 1),
            Need("current-fir", RecommendationNeedPurpose.Quest, 0, 2, fir: true),
        };
        var request = Request(
            profile: Profile(needs: needs),
            inventory: Inventory(total: 2, fir: 1),
            economics: Economics(fleaNet: 120_000, trader: 80_000, squares: 2));

        var decision = new ExplainableRecommendationEngine().Evaluate(request).Decision.Value!;
        var needReasons = decision.Reasons
            .Where(reason => reason.Code.StartsWith("need.", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(V2Action.Keep, decision.Action);
        Assert.Equal(
            [
                "need.quest-current-fir.current-fir",
                "need.quest-future.future",
                "need.hideout.hideout",
            ],
            needReasons.Select(reason => reason.Code));
        Assert.Contains("after 1 compatible observed holding(s)", needReasons[0].Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain(decision.Reasons, reason => reason.Code.Contains("too-far", StringComparison.Ordinal));
        Assert.DoesNotContain(decision.Reasons, reason => reason.Code.Contains("current-general", StringComparison.Ordinal));
        Assert.Equal(120_000, decision.OpportunityCostRoubles.Value);
    }

    [Fact]
    public void PartialPositiveHoldingsSubtractButAnUnseenItemNeverBecomesZero()
    {
        var need = Need("quest", RecommendationNeedPurpose.Quest, 0, 3);
        var partialWithItem = Inventory(total: 1, fir: 0, complete: false);
        var partialWithoutItem = Inventory(total: null, fir: null, complete: false, includeItem: false);

        var withObserved = new ExplainableRecommendationEngine().Evaluate(Request(
            profile: Profile(needs: [need]),
            inventory: partialWithItem)).Decision.Value!;
        var unseen = new ExplainableRecommendationEngine().Evaluate(Request(
            profile: Profile(needs: [need]),
            inventory: partialWithoutItem)).Decision.Value!;

        Assert.Contains("Keep 2 more", withObserved.Reasons.Single(reason => reason.Code.Contains("quest-current", StringComparison.Ordinal)).Explanation, StringComparison.Ordinal);
        Assert.Contains(withObserved.Reasons, reason => reason.Code == "inventory.partial");
        Assert.Contains("Keep 3 more", unseen.Reasons.Single(reason => reason.Code.Contains("quest-current", StringComparison.Ordinal)).Explanation, StringComparison.Ordinal);
        Assert.Contains(unseen.Reasons, reason => reason.Code == "inventory.partial");
    }

    [Theory]
    [InlineData(EventItemState.Unknown, V2Action.SellOnFlea, null)]
    [InlineData(EventItemState.Untested, V2Action.Review, "event.untested")]
    [InlineData(EventItemState.Safe, V2Action.UseSoon, "event.safe")]
    [InlineData(EventItemState.Allergic, V2Action.AvoidConsume, "event.allergic")]
    public void EventStatesRemainDistinct(
        EventItemState eventState,
        V2Action expectedAction,
        string? expectedReason)
    {
        var decision = new ExplainableRecommendationEngine().Evaluate(Request(
            profile: Profile(eventState: eventState),
            economics: Economics(fleaNet: 100_000, trader: 80_000, squares: 2))).Decision.Value!;

        Assert.Equal(expectedAction, decision.Action);
        if (expectedReason is null)
        {
            Assert.DoesNotContain(decision.Reasons, reason => reason.Code.StartsWith("event.", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(decision.Reasons, reason => reason.Code == expectedReason);
        }
    }

    [Fact]
    public void MissingPriceOrFootprintProducesReviewNotAZeroValuation()
    {
        var missingPrice = new ExplainableRecommendationEngine().Evaluate(Request(
            economics: Economics(fleaNet: null, trader: null, squares: 2))).Decision.Value!;
        var missingFootprint = new ExplainableRecommendationEngine().Evaluate(Request(
            economics: Economics(fleaNet: 100_000, trader: 80_000, squares: null))).Decision.Value!;

        Assert.Equal(V2Action.Review, missingPrice.Action);
        Assert.Null(missingPrice.OpportunityCostRoubles.Value);
        Assert.Contains(missingPrice.Reasons, reason => reason.Code == "economics.price-missing");
        Assert.Equal(V2Action.Review, missingFootprint.Action);
        Assert.Null(missingFootprint.OpportunityCostRoubles.Value);
        Assert.Contains(missingFootprint.Reasons, reason => reason.Code == "economics.footprint-missing");
    }

    [Fact]
    public void ShuffledNeedsProduceTheSameDecisionAndOrderedReasons()
    {
        var needs = new[]
        {
            Need("c", RecommendationNeedPurpose.Hideout, 1, 1),
            Need("a", RecommendationNeedPurpose.Quest, 0, 1),
            Need("b", RecommendationNeedPurpose.Quest, 2, 1),
        };
        var engine = new ExplainableRecommendationEngine();
        var first = engine.Evaluate(Request(profile: Profile(needs: needs), inventory: Inventory(0, 0))).Decision.Value!;
        var second = engine.Evaluate(Request(profile: Profile(needs: needs.Reverse().ToArray()), inventory: Inventory(0, 0))).Decision.Value!;

        Assert.Equal(first.Action, second.Action);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.Reasons.Select(reason => reason.Code), second.Reasons.Select(reason => reason.Code));
        Assert.Equal(first.ChangesTheAnswer.Select(change => change.FactCode), second.ChangesTheAnswer.Select(change => change.FactCode));
    }

    [Fact]
    public void StaleInventoryIsNotSubtracted()
    {
        var staleAt = Now.Subtract(ExplainableRecommendationPolicy.Default.MaximumInventoryAge).AddMinutes(-1);
        var inventory = Inventory(5, 5, provenance: Provenance("inventory-stale", staleAt));

        var result = new ExplainableRecommendationEngine().Evaluate(Request(
            profile: Profile(needs: [Need("quest", RecommendationNeedPurpose.Quest, 0, 2, fir: true)]),
            inventory: inventory));
        var decision = result.Decision.Value!;

        Assert.Contains("Keep 2 more", decision.Reasons.Single(reason => reason.Code.Contains("quest-current-fir", StringComparison.Ordinal)).Explanation, StringComparison.Ordinal);
        Assert.Contains(decision.Reasons, reason => reason.Code == "inventory.stale");
        Assert.Equal(FreshnessState.Stale, result.Decision.Status.Freshness);
    }

    [Fact]
    public void ScarcityOutranksRaidContextAndEconomicsForLoot()
    {
        var result = new ExplainableRecommendationEngine().Evaluate(Request(
            economics: Economics(fleaNet: 10_000, trader: 8_000, squares: 2),
            scarcity: Scarcity(RecommendationObtainabilityBand.Scarce),
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                RecommendationRaidPhase.Extracting,
                RecommendationRaidRisk.Critical)));
        var decision = result.Decision.Value!;

        Assert.Equal(V2Action.Take, decision.Action);
        Assert.Equal(
            [
                "scarcity.obtainability.scarce",
                "raid.phase.extracting",
                "raid.risk.critical",
                "economics.flea-net.low",
            ],
            decision.Reasons.Select(reason => reason.Code));
        Assert.Equal(10_000, decision.OpportunityCostRoubles.Value);
        Assert.Contains(decision.ChangesTheAnswer, change => change.FactCode == "obtainability-improved");
    }

    [Fact]
    public void RaidRiskAndPhaseRaiseTheEconomicLootThreshold()
    {
        var economics = Economics(fleaNet: 40_000, trader: 30_000, squares: 2);
        var ordinary = new ExplainableRecommendationEngine().Evaluate(Request(
            economics: economics,
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext())).Decision.Value!;
        var exposed = new ExplainableRecommendationEngine().Evaluate(Request(
            economics: economics,
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                RecommendationRaidPhase.Late,
                RecommendationRaidRisk.High))).Decision.Value!;

        Assert.Equal(V2Action.Take, ordinary.Action);
        Assert.Equal(V2Action.Leave, exposed.Action);
        Assert.Equal("raid.phase.late", exposed.Reasons[0].Code);
        Assert.Equal("raid.risk.high", exposed.Reasons[1].Code);
        Assert.Equal("economics.flea-net.moderate", exposed.Reasons[2].Code);
        Assert.Null(Assert.Single(exposed.ChangesTheAnswer, change =>
            change.FactCode == "raid-risk-reduced").AlternativeAction);
        Assert.Null(Assert.Single(exposed.ChangesTheAnswer, change =>
            change.FactCode == "raid-phase-earlier").AlternativeAction);
        Assert.Equal(40_000, exposed.OpportunityCostRoubles.Value);
    }

    [Fact]
    public void RaidSensitivityChangesActionOnlyWhenThatAxisAloneChangesTheThreshold()
    {
        var riskBound = new ExplainableRecommendationEngine().Evaluate(Request(
            economics: Economics(fleaNet: 60_000, trader: 40_000, squares: 2),
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                RecommendationRaidPhase.Middle,
                RecommendationRaidRisk.High))).Decision.Value!;
        var phaseBound = new ExplainableRecommendationEngine().Evaluate(Request(
            economics: Economics(fleaNet: 40_000, trader: 30_000, squares: 2),
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                RecommendationRaidPhase.Late,
                RecommendationRaidRisk.Low))).Decision.Value!;

        Assert.Equal(V2Action.Take, Assert.Single(riskBound.ChangesTheAnswer, change =>
            change.FactCode == "raid-risk-reduced").AlternativeAction);
        Assert.Equal(V2Action.Take, Assert.Single(phaseBound.ChangesTheAnswer, change =>
            change.FactCode == "raid-phase-earlier").AlternativeAction);
    }

    [Fact]
    public void ExplicitProtectionAndNeedPrecedenceSuppressScarcityAlternativeActions()
    {
        var decisions = new[]
        {
            new ExplainableRecommendationEngine().Evaluate(Request(
                profile: Profile(explicitAction: V2Action.Leave))).Decision.Value!,
            new ExplainableRecommendationEngine().Evaluate(Request(
                profile: Profile(protectedItem: true))).Decision.Value!,
            new ExplainableRecommendationEngine().Evaluate(Request(
                profile: Profile(needs: [Need("current", RecommendationNeedPurpose.Quest, 0, 1)]),
                inventory: Inventory(total: 0, fir: 0))).Decision.Value!,
        };

        Assert.Equal([V2Action.Leave, V2Action.Keep, V2Action.Keep], decisions.Select(value => value.Action));
        Assert.All(decisions, decision => Assert.Null(Assert.Single(
            decision.ChangesTheAnswer,
            change => change.FactCode == "obtainability-worsened").AlternativeAction));
    }

    [Fact]
    public void ScarcityPrecedenceSuppressesRaidAlternativeActions()
    {
        var decision = new ExplainableRecommendationEngine().Evaluate(Request(
            economics: Economics(fleaNet: 60_000, trader: 40_000, squares: 2),
            scarcity: Scarcity(RecommendationObtainabilityBand.Scarce),
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                RecommendationRaidPhase.Middle,
                RecommendationRaidRisk.High))).Decision.Value!;

        Assert.Equal(V2Action.Take, decision.Action);
        Assert.Null(Assert.Single(decision.ChangesTheAnswer, change =>
            change.FactCode == "raid-risk-reduced").AlternativeAction);
        Assert.Null(Assert.Single(decision.ChangesTheAnswer, change =>
            change.FactCode == "raid-phase-later").AlternativeAction);
    }

    [Fact]
    public void EvidenceIssueSuppressesRaidAlternativeActions()
    {
        var decision = new ExplainableRecommendationEngine().Evaluate(Request(
            economics: Economics(fleaNet: 60_000, trader: 40_000, squares: 2),
            scarcity: Scarcity(null),
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                RecommendationRaidPhase.Middle,
                RecommendationRaidRisk.High))).Decision.Value!;

        Assert.Equal(V2Action.Review, decision.Action);
        Assert.Null(Assert.Single(decision.ChangesTheAnswer, change =>
            change.FactCode == "raid-risk-reduced").AlternativeAction);
    }

    [Fact]
    public void MissingRaidContextNeverBecomesLowRisk()
    {
        var result = new ExplainableRecommendationEngine().Evaluate(Request(
            useCase: RecommendationUseCase.Loot,
            raidContext: null));
        var decision = result.Decision.Value!;

        Assert.Equal(V2Action.Review, decision.Action);
        Assert.Contains(decision.Reasons, reason => reason.Code == "raid-context.missing");
        Assert.Equal(FreshnessState.Unknown, result.Decision.Status.Freshness);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("stale")]
    [InlineData("low-confidence")]
    [InlineData("ambiguous")]
    public void UntrustedScarcityProducesReviewInsteadOfAssumingAvailability(string condition)
    {
        var scarcity = condition switch
        {
            "unknown" => Scarcity(null),
            "stale" => Scarcity(
                RecommendationObtainabilityBand.Available,
                status: new ResultStatus(ResultCompleteness.Complete, FreshnessState.Stale)),
            "low-confidence" => Scarcity(
                RecommendationObtainabilityBand.Available,
                provenance: Provenance("scarcity-low", confidence: 0.4)),
            "ambiguous" => Scarcity(
                RecommendationObtainabilityBand.Available,
                candidates:
                [
                    new EvidenceCandidate<RecommendationObtainabilityBand?>(
                        "scarce",
                        "Scarce",
                        RecommendationObtainabilityBand.Scarce,
                        Provenance("scarcity-candidate")),
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };

        var result = new ExplainableRecommendationEngine().Evaluate(Request(scarcity: scarcity));
        var decision = result.Decision.Value!;

        Assert.Equal(V2Action.Review, decision.Action);
        Assert.Contains(decision.Reasons, reason => reason.Code.StartsWith("scarcity.", StringComparison.Ordinal));
        Assert.Equal(ResultCompleteness.Partial, result.Decision.Status.Completeness);
        Assert.Equal(
            condition == "stale" ? FreshnessState.Stale : FreshnessState.Current,
            result.Decision.Status.Freshness);
    }

    [Fact]
    public void StaleRaidRiskProducesReviewAndStaleStatus()
    {
        var result = new ExplainableRecommendationEngine().Evaluate(Request(
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                riskStatus: new ResultStatus(ResultCompleteness.Complete, FreshnessState.Stale))));

        Assert.Equal(V2Action.Review, result.Decision.Value!.Action);
        Assert.Equal(FreshnessState.Stale, result.Decision.Status.Freshness);
        Assert.Contains(result.Decision.Value.Reasons, reason => reason.Code == "raid-context.risk-untrusted");
    }

    [Fact]
    public void PolicyExpiredScarcityReportsStaleEvenWhenSourceLabelsItCurrent()
    {
        var result = new ExplainableRecommendationEngine().Evaluate(Request(
            scarcity: Scarcity(
                RecommendationObtainabilityBand.Available,
                provenance: Provenance("expired-scarcity", Now.AddDays(-7).AddTicks(-1)))));

        Assert.Equal(V2Action.Review, result.Decision.Value!.Action);
        Assert.Equal(FreshnessState.Stale, result.Decision.Status.Freshness);
        Assert.Contains(result.Decision.Value.Reasons, reason => reason.Code == "scarcity.untrusted");
    }

    [Fact]
    public void PolicyExpiredRaidContextReportsStaleEvenWhenSourceLabelsItCurrent()
    {
        var result = new ExplainableRecommendationEngine().Evaluate(Request(
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                phaseProvenance: Provenance("raid-phase-boundary", Now.AddMinutes(-15)),
                riskProvenance: Provenance("expired-raid-risk", Now.AddMinutes(-15).AddTicks(-1)))));

        Assert.Equal(V2Action.Review, result.Decision.Value!.Action);
        Assert.Equal(FreshnessState.Stale, result.Decision.Status.Freshness);
        Assert.Contains(result.Decision.Value.Reasons, reason => reason.Code == "raid-context.risk-untrusted");
    }

    [Fact]
    public void PolicyAgeBoundaryRemainsCurrent()
    {
        var result = new ExplainableRecommendationEngine().Evaluate(Request(
            scarcity: Scarcity(
                RecommendationObtainabilityBand.Available,
                provenance: Provenance("scarcity-boundary", Now.AddDays(-7))),
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                phaseProvenance: Provenance("raid-phase-boundary", Now.AddMinutes(-15)),
                riskProvenance: Provenance("raid-risk-boundary", Now.AddMinutes(-15)))));

        Assert.Equal(FreshnessState.Current, result.Decision.Status.Freshness);
        Assert.Equal(ResultCompleteness.Complete, result.Decision.Status.Completeness);
    }

    [Fact]
    public void FutureContextEvidenceIsRejected()
    {
        var future = Provenance("future-raid", Now.AddMinutes(1));
        var request = Request(
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(riskProvenance: future));

        Assert.Throws<ArgumentException>(() => new ExplainableRecommendationEngine().Evaluate(request));
    }

    [Fact]
    public void ContextContractsRejectUndefinedEnumsAndNonMonotonicThresholds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Scarcity((RecommendationObtainabilityBand)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => RaidContext(risk: (RecommendationRaidRisk)99));
        Assert.Throws<ArgumentException>(() => new RaidAdjustedLootThresholds(
            EconomicValueBand.High,
            EconomicValueBand.Moderate,
            EconomicValueBand.Exceptional,
            EconomicValueBand.Exceptional,
            EconomicValueBand.High,
            EconomicValueBand.Exceptional));
        Assert.Equal("recommendation-274.2", ExplainableRecommendationPolicy.CurrentRulesetVersion);
    }

    [Fact]
    public void DecisionLineageIncludesEveryEnablingScarcityAndNormalRaidFact()
    {
        var result = new ExplainableRecommendationEngine().Evaluate(Request(
            scarcity: Scarcity(
                RecommendationObtainabilityBand.Available,
                provenance: Provenance("scarcity-enabler", confidence: 0.76)),
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                phaseProvenance: Provenance("phase-enabler", confidence: 0.80),
                riskProvenance: Provenance("risk-enabler", confidence: 0.77))));

        var sources = Flatten(result.Decision.Provenance)
            .Select(provenance => provenance.SourceIdentifier)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(V2Action.Take, result.Decision.Value!.Action);
        Assert.Contains("fixture://scarcity-enabler", sources);
        Assert.Contains("fixture://phase-enabler", sources);
        Assert.Contains("fixture://risk-enabler", sources);
        Assert.Equal(0.76, result.Decision.Provenance.Confidence.Score!.Value, 6);
    }

    [Fact]
    public void CorrectionResolvesOriginalAmbiguityAndCarriesItsOwnOriginAndTime()
    {
        var correctedUtc = Now.AddMinutes(-1);
        var correction = new EvidenceCorrection<RecommendationObtainabilityBand?>(
            1,
            RecommendationObtainabilityBand.Available,
            RecommendationObtainabilityBand.Scarce,
            correctedUtc,
            CorrectionOriginClass.PairedDevice,
            "tablet-a",
            "Reviewed the catalog match.");
        var result = new ExplainableRecommendationEngine().Evaluate(Request(
            scarcity: Scarcity(
                RecommendationObtainabilityBand.Scarce,
                provenance: Provenance("scarcity-original", Now.AddDays(-8)),
                candidates:
                [
                    new EvidenceCandidate<RecommendationObtainabilityBand?>(
                        "available",
                        "Available",
                        RecommendationObtainabilityBand.Available,
                        Provenance("scarcity-candidate")),
                ],
                corrections: [correction])));

        var decision = result.Decision.Value!;
        var scarcityReason = Assert.Single(
            decision.Reasons,
            reason => reason.Code == "scarcity.obtainability.scarce");
        Assert.Equal(V2Action.Keep, decision.Action);
        Assert.Equal(EvidenceSourceClass.PairedDeviceAction, scarcityReason.Provenance.SourceClass);
        Assert.Equal(correctedUtc, scarcityReason.Provenance.ObservedUtc);
        Assert.Contains("tablet-a", scarcityReason.Provenance.SourceIdentifier, StringComparison.Ordinal);
    }

    [Fact]
    public void FutureCorrectionIsRejectedBeforeItCanChangeAdvice()
    {
        var correction = new EvidenceCorrection<RecommendationObtainabilityBand?>(
            1,
            RecommendationObtainabilityBand.Available,
            RecommendationObtainabilityBand.Scarce,
            Now.AddTicks(1),
            CorrectionOriginClass.User,
            "reviewer");
        var request = Request(scarcity: Scarcity(
            RecommendationObtainabilityBand.Scarce,
            corrections: [correction]));

        Assert.Throws<ArgumentException>(() => new ExplainableRecommendationEngine().Evaluate(request));
    }

    [Fact]
    public void DurableProfileChoicesDoNotExpireOnTheInventoryTtl()
    {
        var recordedUtc = Now.Subtract(ExplainableRecommendationPolicy.Default.MaximumInventoryAge).AddTicks(-1);
        var explicitDecision = new ExplainableRecommendationEngine().Evaluate(Request(profile: Profile(
            explicitAction: V2Action.Leave,
            explicitProvenance: Provenance("durable-explicit", recordedUtc)))).Decision.Value!;
        var protectedDecision = new ExplainableRecommendationEngine().Evaluate(Request(profile: Profile(
            protectedItem: true,
            protectedProvenance: Provenance("durable-protection", recordedUtc)))).Decision.Value!;
        var pinnedDecision = new ExplainableRecommendationEngine().Evaluate(Request(profile: Profile(
            pinned: true,
            pinnedProvenance: Provenance("durable-pin", recordedUtc)))).Decision.Value!;
        var wishlistDecision = new ExplainableRecommendationEngine().Evaluate(Request(profile: Profile(
            wishlist: true,
            wishlistProvenance: Provenance("durable-wishlist", recordedUtc)))).Decision.Value!;
        var allergicDecision = new ExplainableRecommendationEngine().Evaluate(Request(profile: Profile(
            eventState: EventItemState.Allergic,
            eventProvenance: Provenance("durable-allergy", recordedUtc)))).Decision.Value!;

        Assert.Equal(V2Action.Leave, explicitDecision.Action);
        Assert.Contains(explicitDecision.Reasons, reason => reason.Code == "override.explicit");
        Assert.Equal(V2Action.Keep, protectedDecision.Action);
        Assert.Contains(protectedDecision.Reasons, reason => reason.Code == "item.protected");
        Assert.Equal(V2Action.Keep, pinnedDecision.Action);
        Assert.Contains(pinnedDecision.Reasons, reason => reason.Code == "profile.pinned");
        Assert.Equal(V2Action.Keep, wishlistDecision.Action);
        Assert.Contains(wishlistDecision.Reasons, reason => reason.Code == "profile.wishlist");
        Assert.Equal(V2Action.AvoidConsume, allergicDecision.Action);
        Assert.Contains(allergicDecision.Reasons, reason => reason.Code == "event.allergic");
    }

    [Fact]
    public void DurableProfileChoicesStillRequireExplicitFreshnessConfidenceAndClarity()
    {
        var stale = new EvidencedValue<bool?>(
            "profile.protected",
            true,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Stale),
            Provenance("durable-stale"));
        var lowConfidence = Complete<bool?>(
            "profile.protected",
            true,
            Provenance("durable-low-confidence", confidence: 0.40));
        var ambiguous = new EvidencedValue<bool?>(
            "profile.protected",
            true,
            CompleteStatus,
            Provenance("durable-ambiguous"),
            candidates:
            [
                new EvidenceCandidate<bool?>(
                    "not-protected",
                    "Not protected",
                    false,
                    Provenance("durable-ambiguous-false")),
            ]);

        foreach (var field in new[] { stale, lowConfidence, ambiguous })
        {
            var result = new ExplainableRecommendationEngine().Evaluate(Request(
                profile: Profile(protectedField: field)));

            Assert.Equal(V2Action.Review, result.Decision.Value!.Action);
            Assert.Contains(result.Decision.Value.Reasons, reason => reason.Code == "profile.protection-untrusted");
            Assert.DoesNotContain(result.Decision.Value.Reasons, reason => reason.Code == "item.protected");
        }
    }

    [Fact]
    public void EconomicDecisionLineageIncludesNegativeProfileFactsAndBothComparedPrices()
    {
        var result = new ExplainableRecommendationEngine().Evaluate(Request(
            profile: Profile(
                protectedProvenance: Provenance("protection-false", confidence: 0.77),
                pinnedProvenance: Provenance("pin-false", confidence: 0.78),
                wishlistProvenance: Provenance("wishlist-false", confidence: 0.79),
                eventProvenance: Provenance("event-unknown", confidence: 0.80)),
            economics: Economics(
                fleaNet: 100_000,
                trader: 80_000,
                squares: 2,
                fleaNetProvenance: Provenance("flea-compared", confidence: 0.97),
                traderProvenance: Provenance("trader-compared", confidence: 0.76),
                squaresProvenance: Provenance("footprint-compared", confidence: 0.96))));
        var sources = Flatten(result.Decision.Provenance)
            .Select(provenance => provenance.SourceIdentifier)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(V2Action.SellOnFlea, result.Decision.Value!.Action);
        Assert.Contains("fixture://protection-false", sources);
        Assert.Contains("fixture://pin-false", sources);
        Assert.Contains("fixture://wishlist-false", sources);
        Assert.Contains("fixture://event-unknown", sources);
        Assert.Contains("fixture://flea-compared", sources);
        Assert.Contains("fixture://trader-compared", sources);
        Assert.Equal(0.76, result.Decision.Provenance.Confidence.Score!.Value, 6);
    }

    [Fact]
    public void FirFalseAndSatisfiedNeedFactsRemainInEconomicDecisionLineage()
    {
        var firFalse = new ExplainableRecommendationEngine().Evaluate(Request(
            profile: Profile(needs:
            [
                Need(
                    "fir-suppressed",
                    RecommendationNeedPurpose.Quest,
                    0,
                    1,
                    fir: true,
                    provenance: Provenance("fir-suppressed-need", confidence: 0.90)),
            ]),
            inventory: Inventory(
                0,
                0,
                provenance: Provenance("fir-false-snapshot", confidence: 0.95),
                totalProvenance: Provenance("fir-false-total", confidence: 0.94),
                firProvenance: Provenance("fir-false-count", confidence: 0.93)),
            candidateFoundInRaid: Complete<bool?>(
                "candidate.fir",
                false,
                Provenance("candidate-fir-false", confidence: 0.76))));
        var satisfied = new ExplainableRecommendationEngine().Evaluate(Request(
            profile: Profile(needs:
            [
                Need(
                    "satisfied",
                    RecommendationNeedPurpose.Quest,
                    0,
                    1,
                    provenance: Provenance("satisfied-need", confidence: 0.90)),
            ]),
            inventory: Inventory(
                1,
                0,
                provenance: Provenance("satisfied-snapshot", confidence: 0.75),
                totalProvenance: Provenance("satisfied-total", confidence: 0.80),
                firProvenance: Provenance("satisfied-fir", confidence: 0.85))));

        var firFalseSources = Flatten(firFalse.Decision.Provenance)
            .Select(provenance => provenance.SourceIdentifier)
            .ToHashSet(StringComparer.Ordinal);
        var satisfiedSources = Flatten(satisfied.Decision.Provenance)
            .Select(provenance => provenance.SourceIdentifier)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(V2Action.SellOnFlea, firFalse.Decision.Value!.Action);
        Assert.Contains("fixture://fir-suppressed-need", firFalseSources);
        Assert.Contains("fixture://candidate-fir-false", firFalseSources);
        Assert.Contains("fixture://fir-false-snapshot", firFalseSources);
        Assert.Contains("fixture://fir-false-total", firFalseSources);
        Assert.Contains("fixture://fir-false-count", firFalseSources);
        Assert.Equal(0.76, firFalse.Decision.Provenance.Confidence.Score!.Value, 6);

        Assert.Equal(V2Action.SellOnFlea, satisfied.Decision.Value!.Action);
        Assert.Contains("fixture://satisfied-need", satisfiedSources);
        Assert.Contains("fixture://satisfied-snapshot", satisfiedSources);
        Assert.Contains("fixture://satisfied-total", satisfiedSources);
        Assert.Contains("fixture://satisfied-fir", satisfiedSources);
        Assert.Equal(0.75, satisfied.Decision.Provenance.Confidence.Score!.Value, 6);
    }

    [Fact]
    public void OneReliabilityGateRejectsStaleNeedsAndAmbiguousFirPriceAndCounts()
    {
        var staleNeed = new ExplainableRecommendationEngine().Evaluate(Request(
            profile: Profile(needs:
            [
                Need(
                    "stale",
                    RecommendationNeedPurpose.Quest,
                    0,
                    1,
                    status: new ResultStatus(ResultCompleteness.Complete, FreshnessState.Stale)),
            ]),
            inventory: Inventory(0, 0)));

        var ambiguousFir = new EvidencedValue<bool?>(
            "candidate.fir",
            false,
            CompleteStatus,
            Provenance("candidate-fir-ambiguous"),
            candidates:
            [
                new EvidenceCandidate<bool?>("true", "Found in raid", true, Provenance("candidate-fir-true")),
            ]);
        var firResult = new ExplainableRecommendationEngine().Evaluate(Request(
            profile: Profile(needs: [Need("fir", RecommendationNeedPurpose.Quest, 0, 1, fir: true)]),
            inventory: Inventory(0, 0),
            candidateFoundInRaid: ambiguousFir));

        var ambiguousFlea = new RecommendationEconomics(
            Complete<long?>("economics.flea-gross", 120_000),
            Complete<long?>("economics.flea-fee", 20_000),
            new EvidencedValue<long?>(
                "economics.flea-net",
                100_000,
                CompleteStatus,
                Provenance("flea-ambiguous"),
                candidates:
                [
                    new EvidenceCandidate<long?>("low", "Low", 10_000, Provenance("flea-low")),
                ]),
            Unknown<long?>("economics.trader"),
            Complete<int?>("economics.squares", 1),
            Complete<double?>("economics.condition", 1));
        var priceResult = new ExplainableRecommendationEngine().Evaluate(Request(economics: ambiguousFlea));

        var totalCandidate = new EvidenceCandidate<int?>(
            "zero",
            "Zero",
            0,
            Provenance("inventory-total-zero"));
        var countResult = new ExplainableRecommendationEngine().Evaluate(Request(
            profile: Profile(needs: [Need("count", RecommendationNeedPurpose.Quest, 0, 1)]),
            inventory: Inventory(1, 0, totalCandidates: [totalCandidate])));

        var staleDecision = staleNeed.Decision.Value!;
        var firDecision = firResult.Decision.Value!;
        var priceDecision = priceResult.Decision.Value!;
        var countDecision = countResult.Decision.Value!;
        Assert.Equal(V2Action.Review, staleDecision.Action);
        Assert.Equal(FreshnessState.Stale, staleNeed.Decision.Status.Freshness);
        Assert.DoesNotContain(staleDecision.Reasons, reason => reason.Code.Contains("need.quest", StringComparison.Ordinal));
        Assert.Equal(V2Action.Review, firDecision.Action);
        Assert.Contains(firDecision.Reasons, reason => reason.Code == "candidate.fir-untrusted");
        Assert.Equal(V2Action.Review, priceDecision.Action);
        Assert.Contains(priceDecision.Reasons, reason => reason.Code == "economics.flea-net-untrusted");
        Assert.Equal(V2Action.Keep, countDecision.Action);
        Assert.Contains("Keep 1 more", countDecision.Reasons.Single(
            reason => reason.Code == "need.quest-current.count").Explanation, StringComparison.Ordinal);
        Assert.Contains(countDecision.Reasons, reason => reason.Code == "inventory.total-untrusted");
    }

    [Fact]
    public void RaidThresholdPermutationsChooseActionsAndDominantSummariesDeterministically()
    {
        var values = new Dictionary<EconomicValueBand, long>
        {
            [EconomicValueBand.Low] = 5_000,
            [EconomicValueBand.Moderate] = 10_000,
            [EconomicValueBand.High] = 25_000,
            [EconomicValueBand.Exceptional] = 50_000,
        };
        var engine = new ExplainableRecommendationEngine();
        var policy = ExplainableRecommendationPolicy.Default;

        foreach (var phase in Enum.GetValues<RecommendationRaidPhase>())
        {
            foreach (var risk in Enum.GetValues<RecommendationRaidRisk>())
            {
                foreach (var pair in values)
                {
                    var decision = engine.Evaluate(Request(
                        economics: Economics(fleaNet: pair.Value, trader: null, squares: 1),
                        useCase: RecommendationUseCase.Loot,
                        raidContext: RaidContext(phase, risk))).Decision.Value!;
                    var required = policy.LootThresholds.RequiredBand(phase, risk);
                    var expected = (int)pair.Key >= (int)required ? V2Action.Take : V2Action.Leave;
                    var contextChangedAction = (int)pair.Key >= (int)policy.LootThresholds.Normal &&
                                               (int)pair.Key < (int)required;

                    Assert.Equal(expected, decision.Action);
                    if (contextChangedAction)
                    {
                        Assert.Contains("raid", decision.Summary, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        Assert.Contains("roubles", decision.Summary, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }

        var split = engine.Evaluate(Request(
            economics: Economics(fleaNet: 25_000, trader: null, squares: 1),
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                RecommendationRaidPhase.Extracting,
                RecommendationRaidRisk.Elevated))).Decision.Value!;
        Assert.Contains("exceptional", split.Reasons.Single(reason => reason.Code == "raid.phase.extracting").Explanation, StringComparison.Ordinal);
        var riskReason = split.Reasons.Single(reason => reason.Code == "raid.risk.elevated");
        Assert.Contains("high", riskReason.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("exceptional", riskReason.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void RaidSummaryNamesTheActionBindingAxisAndUsesPhaseForAnExactTie()
    {
        var riskBound = new ExplainableRecommendationEngine().Evaluate(Request(
            economics: Economics(fleaNet: 25_000, trader: null, squares: 1),
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                RecommendationRaidPhase.Late,
                RecommendationRaidRisk.Critical))).Decision.Value!;
        var phaseBound = new ExplainableRecommendationEngine().Evaluate(Request(
            economics: Economics(fleaNet: 25_000, trader: null, squares: 1),
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                RecommendationRaidPhase.Extracting,
                RecommendationRaidRisk.Elevated))).Decision.Value!;
        var tied = new ExplainableRecommendationEngine().Evaluate(Request(
            economics: Economics(fleaNet: 10_000, trader: null, squares: 1),
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(
                RecommendationRaidPhase.Late,
                RecommendationRaidRisk.Elevated))).Decision.Value!;

        Assert.Equal(V2Action.Leave, riskBound.Action);
        Assert.Contains("critical", riskBound.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("late raid phase", riskBound.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(V2Action.Leave, phaseBound.Action);
        Assert.Contains("extracting raid phase", phaseBound.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("elevated raid-risk", phaseBound.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(V2Action.Leave, tied.Action);
        Assert.Contains("late raid phase", tied.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaximumFirNeedSetKeepsDecisionLineageWithinTheFrozenBounds()
    {
        var needs = Enumerable.Range(0, RecommendationProfileFacts.MaximumNeeds)
            .Select(index => Need(
                $"bounded-{index:D2}",
                RecommendationNeedPurpose.Quest,
                0,
                1,
                fir: true))
            .ToArray();
        var result = new ExplainableRecommendationEngine().Evaluate(Request(
            profile: Profile(needs: needs),
            inventory: Inventory(0, 0)));

        Assert.Equal(V2Action.Keep, result.Decision.Value!.Action);
        Assert.Equal(RecommendationProfileFacts.MaximumNeeds, result.Decision.Value.Reasons.Count(reason =>
            reason.Code.StartsWith("need.quest-current-fir.", StringComparison.Ordinal)));
        Assert.True(Flatten(result.Decision.Provenance).Skip(1).Count() <= EvidenceProvenance.MaxInputCount);
    }

    [Fact]
    public void ProvenanceCombinationDeduplicatesInputsAndRejectsUnrepresentableDepth()
    {
        var shared = Provenance("shared-context");
        var deduplicated = new ExplainableRecommendationEngine().Evaluate(Request(
            useCase: RecommendationUseCase.Loot,
            raidContext: RaidContext(phaseProvenance: shared, riskProvenance: shared)));

        Assert.Equal(
            1,
            deduplicated.Decision.Provenance.Inputs.Count(input => input == shared));

        var tooDeep = Request(scarcity: Scarcity(
            RecommendationObtainabilityBand.Scarce,
            provenance: NestedProvenance(EvidenceProvenance.MaxInputDepth)));
        var exception = Assert.Throws<ArgumentException>(() =>
            new ExplainableRecommendationEngine().Evaluate(tooDeep));
        Assert.Contains("provenance depth/count contract", exception.Message, StringComparison.Ordinal);

        var tooWide = Request(scarcity: Scarcity(
            RecommendationObtainabilityBand.Scarce,
            provenance: WideProvenance(EvidenceProvenance.MaxInputCount)));
        exception = Assert.Throws<ArgumentException>(() =>
            new ExplainableRecommendationEngine().Evaluate(tooWide));
        Assert.Contains("provenance depth/count contract", exception.Message, StringComparison.Ordinal);
    }

    private static ExplainableRecommendationRequest Request(
        RecommendationProfileFacts? profile = null,
        RecommendationEconomics? economics = null,
        ObservedInventoryEvidenceSnapshot? inventory = null,
        RecommendationScarcityFacts? scarcity = null,
        RecommendationUseCase useCase = RecommendationUseCase.Stash,
        RecommendationRaidContext? raidContext = null,
        EvidencedValue<bool?>? candidateFoundInRaid = null) => new(
        "recommendation-test",
        "item-a",
        useCase,
        Now,
        Scope,
        "data-snapshot-1",
        candidateFoundInRaid ?? Complete<bool?>("candidate.fir", true),
        profile ?? Profile(),
        economics ?? Economics(),
        scarcity ?? Scarcity(RecommendationObtainabilityBand.Available),
        inventory,
        raidContext: raidContext);

    private static RecommendationScarcityFacts Scarcity(
        RecommendationObtainabilityBand? band,
        EvidenceProvenance? provenance = null,
        ResultStatus? status = null,
        IReadOnlyList<EvidenceCandidate<RecommendationObtainabilityBand?>>? candidates = null,
        IReadOnlyList<EvidenceCorrection<RecommendationObtainabilityBand?>>? corrections = null) => new(
        new EvidencedValue<RecommendationObtainabilityBand?>(
            "scarcity.obtainability",
            band,
            status ?? (band is null
                ? new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current)
                : CompleteStatus),
            provenance ?? Provenance("scarcity"),
            candidates: candidates,
            corrections: corrections));

    private static RecommendationRaidContext RaidContext(
        RecommendationRaidPhase? phase = RecommendationRaidPhase.Middle,
        RecommendationRaidRisk? risk = RecommendationRaidRisk.Low,
        EvidenceProvenance? phaseProvenance = null,
        EvidenceProvenance? riskProvenance = null,
        ResultStatus? phaseStatus = null,
        ResultStatus? riskStatus = null) => new(
        new EvidencedValue<RecommendationRaidPhase?>(
            "raid.phase",
            phase,
            phaseStatus ?? (phase is null
                ? new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current)
                : CompleteStatus),
            phaseProvenance ?? Provenance("raid-phase")),
        new EvidencedValue<RecommendationRaidRisk?>(
            "raid.risk",
            risk,
            riskStatus ?? (risk is null
                ? new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current)
                : CompleteStatus),
            riskProvenance ?? Provenance("raid-risk")));

    private static RecommendationProfileFacts Profile(
        V2Action? explicitAction = null,
        bool protectedItem = false,
        bool pinned = false,
        bool wishlist = false,
        EventItemState eventState = EventItemState.Unknown,
        IReadOnlyList<RecommendationNeed>? needs = null,
        EvidenceProvenance? explicitProvenance = null,
        EvidenceProvenance? protectedProvenance = null,
        EvidenceProvenance? pinnedProvenance = null,
        EvidenceProvenance? wishlistProvenance = null,
        EvidenceProvenance? eventProvenance = null,
        EvidencedValue<bool?>? protectedField = null) => new(
        CompleteStatus,
        Provenance("profile"),
        explicitAction is { } action
            ? Complete<V2Action?>("profile.override", action, explicitProvenance)
            : Unknown<V2Action?>("profile.override"),
        protectedField ?? Complete<bool?>("profile.protected", protectedItem, protectedProvenance),
        Complete<bool?>("profile.pinned", pinned, pinnedProvenance),
        Complete<bool?>("profile.wishlist", wishlist, wishlistProvenance),
        Complete<EventItemState?>("profile.event", eventState, eventProvenance),
        needs ?? []);

    private static RecommendationNeed Need(
        string id,
        RecommendationNeedPurpose purpose,
        int steps,
        int quantity,
        bool fir = false,
        ResultStatus? status = null,
        EvidenceProvenance? provenance = null) => new(
        id,
        $"Need {id}",
        purpose,
        steps,
        quantity,
        fir,
        status ?? CompleteStatus,
        provenance ?? Provenance($"need-{id}"));

    private static RecommendationEconomics Economics(
        long? fleaNet = 100_000,
        long? trader = 80_000,
        int? squares = 2,
        EvidenceProvenance? fleaNetProvenance = null,
        EvidenceProvenance? traderProvenance = null,
        EvidenceProvenance? squaresProvenance = null) => new(
        Optional("economics.flea-gross", fleaNet is null ? null : fleaNet + 20_000),
        Optional<long>("economics.flea-fee", fleaNet is null ? null : 20_000),
        Optional("economics.flea-net", fleaNet, fleaNetProvenance),
        Optional("economics.trader", trader, traderProvenance),
        Optional("economics.squares", squares, squaresProvenance),
        Complete<double?>("economics.condition", 1));

    private static ObservedInventoryEvidenceSnapshot Inventory(
        int? total,
        int? fir,
        bool complete = true,
        bool includeItem = true,
        EvidenceProvenance? provenance = null,
        IReadOnlyList<EvidenceCandidate<int?>>? totalCandidates = null,
        IReadOnlyList<EvidenceCandidate<int?>>? firCandidates = null,
        EvidenceProvenance? totalProvenance = null,
        EvidenceProvenance? firProvenance = null)
    {
        var source = provenance ?? Provenance("inventory");
        var items = includeItem
            ? new[]
            {
                new ObservedItemCount(
                    "item-a",
                    total is { } totalValue
                        ? new EvidencedValue<int?>(
                            "inventory.item-a.total",
                            totalValue,
                            CompleteStatus,
                            totalProvenance ?? source,
                            candidates: totalCandidates)
                        : Unknown<int?>("inventory.item-a.total", source),
                    fir is { } firValue
                        ? new EvidencedValue<int?>(
                            "inventory.item-a.fir",
                            firValue,
                            CompleteStatus,
                            firProvenance ?? source,
                            candidates: firCandidates)
                        : Unknown<int?>("inventory.item-a.fir", source)),
            }
            : [];
        return new ObservedInventoryEvidenceSnapshot(
            Guid.Parse("74000000-0000-0000-0000-000000000002"),
            Scope,
            "data-snapshot-1",
            new ResultStatus(
                complete ? ResultCompleteness.Complete : ResultCompleteness.Partial,
                FreshnessState.Current),
            new EvidenceCoverage(sampleSize: complete ? 400 : 200, fraction: complete ? 1 : 0.5),
            source,
            items,
            complete ? 0 : 1);
    }

    private static EvidencedValue<T?> Optional<T>(
        string fieldId,
        T? value,
        EvidenceProvenance? provenance = null)
        where T : struct =>
        value is { } present
            ? Complete<T?>(fieldId, present, provenance)
            : Unknown<T?>(fieldId, provenance);

    private static EvidencedValue<T> Complete<T>(
        string fieldId,
        T value,
        EvidenceProvenance? provenance = null) => new(
        fieldId,
        value,
        CompleteStatus,
        provenance ?? Provenance(fieldId));

    private static EvidencedValue<T> Unknown<T>(
        string fieldId,
        EvidenceProvenance? provenance = null) => new(
        fieldId,
        default,
        new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current),
        provenance ?? Provenance(fieldId));

    private static EvidenceProvenance Provenance(
        string id,
        DateTimeOffset? observedUtc = null,
        double confidence = 0.98) => new(
        EvidenceSourceClass.PublicStructuredData,
        $"fixture://{id}",
        observedUtc ?? Now.AddMinutes(-5),
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, confidence),
        new ProducerIdentity("recommendation-fixture", "2"));

    private static EvidenceProvenance NestedProvenance(int depth)
    {
        var provenance = Provenance("nested-0");
        for (var index = 1; index < depth; index++)
        {
            provenance = new EvidenceProvenance(
                EvidenceSourceClass.DerivedCalculation,
                $"fixture://nested-{index}",
                Now.AddMinutes(-5),
                new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.98),
                new ProducerIdentity("recommendation-fixture", "2"),
                generatedUtc: Now.AddMinutes(-5),
                inputs: [provenance]);
        }

        return provenance;
    }

    private static EvidenceProvenance WideProvenance(int inputCount) => new(
        EvidenceSourceClass.DerivedCalculation,
        "fixture://wide",
        Now.AddMinutes(-5),
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.98),
        new ProducerIdentity("recommendation-fixture", "2"),
        generatedUtc: Now.AddMinutes(-5),
        inputs: Enumerable.Range(0, inputCount)
            .Select(index => Provenance($"wide-{index}"))
            .ToArray());

    private static IEnumerable<EvidenceProvenance> Flatten(EvidenceProvenance provenance) =>
        new[] { provenance }.Concat(provenance.Inputs.SelectMany(Flatten));

    private static ResultStatus CompleteStatus { get; } = new(
        ResultCompleteness.Complete,
        FreshnessState.Current);
}
