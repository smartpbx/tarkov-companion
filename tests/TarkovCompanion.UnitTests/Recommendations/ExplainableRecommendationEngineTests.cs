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

        var decision = new ExplainableRecommendationEngine().Evaluate(Request(
            profile: Profile(needs: [Need("quest", RecommendationNeedPurpose.Quest, 0, 2, fir: true)]),
            inventory: inventory)).Decision.Value!;

        Assert.Contains("Keep 2 more", decision.Reasons.Single(reason => reason.Code.Contains("quest-current-fir", StringComparison.Ordinal)).Explanation, StringComparison.Ordinal);
        Assert.Contains(decision.Reasons, reason => reason.Code == "inventory.stale");
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
        var decision = new ExplainableRecommendationEngine().Evaluate(Request(
            useCase: RecommendationUseCase.Loot,
            raidContext: null)).Decision.Value!;

        Assert.Equal(V2Action.Review, decision.Action);
        Assert.Contains(decision.Reasons, reason => reason.Code == "raid-context.missing");
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

    private static ExplainableRecommendationRequest Request(
        RecommendationProfileFacts? profile = null,
        RecommendationEconomics? economics = null,
        ObservedInventoryEvidenceSnapshot? inventory = null,
        RecommendationScarcityFacts? scarcity = null,
        RecommendationUseCase useCase = RecommendationUseCase.Stash,
        RecommendationRaidContext? raidContext = null) => new(
        "recommendation-test",
        "item-a",
        useCase,
        Now,
        Scope,
        "data-snapshot-1",
        Complete<bool?>("candidate.fir", true),
        profile ?? Profile(),
        economics ?? Economics(),
        scarcity ?? Scarcity(RecommendationObtainabilityBand.Available),
        inventory,
        raidContext: raidContext);

    private static RecommendationScarcityFacts Scarcity(
        RecommendationObtainabilityBand? band,
        EvidenceProvenance? provenance = null,
        ResultStatus? status = null,
        IReadOnlyList<EvidenceCandidate<RecommendationObtainabilityBand?>>? candidates = null) => new(
        new EvidencedValue<RecommendationObtainabilityBand?>(
            "scarcity.obtainability",
            band,
            status ?? (band is null
                ? new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current)
                : CompleteStatus),
            provenance ?? Provenance("scarcity"),
            candidates: candidates));

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
        IReadOnlyList<RecommendationNeed>? needs = null) => new(
        CompleteStatus,
        Provenance("profile"),
        explicitAction is { } action
            ? Complete<V2Action?>("profile.override", action)
            : Unknown<V2Action?>("profile.override"),
        Complete<bool?>("profile.protected", protectedItem),
        Complete<bool?>("profile.pinned", pinned),
        Complete<bool?>("profile.wishlist", wishlist),
        Complete<EventItemState?>("profile.event", eventState),
        needs ?? []);

    private static RecommendationNeed Need(
        string id,
        RecommendationNeedPurpose purpose,
        int steps,
        int quantity,
        bool fir = false) => new(
        id,
        $"Need {id}",
        purpose,
        steps,
        quantity,
        fir,
        CompleteStatus,
        Provenance($"need-{id}"));

    private static RecommendationEconomics Economics(
        long? fleaNet = 100_000,
        long? trader = 80_000,
        int? squares = 2) => new(
        Optional("economics.flea-gross", fleaNet is null ? null : fleaNet + 20_000),
        Optional<long>("economics.flea-fee", fleaNet is null ? null : 20_000),
        Optional("economics.flea-net", fleaNet),
        Optional("economics.trader", trader),
        Optional("economics.squares", squares),
        Complete<double?>("economics.condition", 1));

    private static ObservedInventoryEvidenceSnapshot Inventory(
        int? total,
        int? fir,
        bool complete = true,
        bool includeItem = true,
        EvidenceProvenance? provenance = null)
    {
        var source = provenance ?? Provenance("inventory");
        var items = includeItem
            ? new[]
            {
                new ObservedItemCount(
                    "item-a",
                    Optional("inventory.item-a.total", total, source),
                    Optional("inventory.item-a.fir", fir, source)),
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

    private static ResultStatus CompleteStatus { get; } = new(
        ResultCompleteness.Complete,
        FreshnessState.Current);
}
