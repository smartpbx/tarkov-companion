using System.Text.Json;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

public sealed class RecommendationContractTests
{
    [Fact]
    public void DecisionKeepsCategorizedReasonsOpportunityCostAndAnswerChangingFacts()
    {
        var price = V2ContractTestData.PublicDataProvenance();
        var footprint = V2ContractTestData.ScreenshotProvenance();
        var perSquare = new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            "economics://value-per-square",
            V2ContractTestData.ObservedUtc,
            EvidenceConfidence.Unscored,
            new ProducerIdentity("fixture-economics", "2"),
            generatedUtc: V2ContractTestData.ObservedUtc,
            inputs: [price, footprint]);
        var decision = new RecommendationDecision(
            RecommendationAction.Keep,
            "Keep for Gunsmith; selling would give up about 40k.",
            [
                new RecommendationReason(RecommendationReasonCategory.Economics, "value-per-square", "About 20k per square.", 1, perSquare),
                new RecommendationReason(RecommendationReasonCategory.CurrentFoundInRaidQuest, "quest-gunsmith", "Needed found in raid.", 10, price),
            ],
            V2ContractTestData.Complete<long?>("decision.opportunityCost", 40_000, perSquare),
            [new RecommendationSensitivity("quest-turned-in", "If the quest is already turned in", RecommendationAction.SellOnFlea)]);
        var result = new RecommendationResult(
            "recommendation-1",
            V2ContractVersion.Current,
            "ruleset-2026.09",
            V2ContractTestData.SessionId,
            V2ContractTestData.Complete("decision", decision, perSquare));

        var roundTrip = JsonSerializer.Deserialize<RecommendationResult>(
            JsonSerializer.Serialize(result, V2ContractJson.Options), V2ContractJson.Options)!;
        var restored = roundTrip.Decision.Value!;

        Assert.Equal(RecommendationReasonCategory.CurrentFoundInRaidQuest, restored.Reasons[0].Category);
        Assert.Equal(40_000, restored.OpportunityCostRoubles.Value);
        Assert.Equal(2, restored.OpportunityCostRoubles.Provenance.Inputs.Count);
        Assert.Equal(RecommendationAction.SellOnFlea, restored.ChangesTheAnswer.Single().AlternativeAction);
    }

    [Fact]
    public void UnknownOpportunityCostIsAbsentAndDecisionsNeedReasons()
    {
        var reason = new RecommendationReason(
            RecommendationReasonCategory.EvidenceQuality, "stale-price", "Price is stale.", 1, V2ContractTestData.PublicDataProvenance());

        var review = new RecommendationDecision(
            RecommendationAction.Review, "Review: price unknown.", [reason],
            V2ContractTestData.Unknown<long?>("decision.opportunityCost"), []);

        Assert.Null(review.OpportunityCostRoubles.Value);
        Assert.Throws<ArgumentException>(() => new RecommendationDecision(
            RecommendationAction.Keep, "No reason", [], V2ContractTestData.Unknown<long?>("decision.opportunityCost"), []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecommendationReason(
            default, "code", "explanation", 1, V2ContractTestData.PublicDataProvenance()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecommendationDecision(
            (RecommendationAction)77, "Bad", [reason], V2ContractTestData.Unknown<long?>("decision.opportunityCost"), []));
    }
}
