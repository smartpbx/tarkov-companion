using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

public sealed class RecommendationContractTests
{
    private static readonly EvidenceProvenance Price = V2ContractTestData.PublicDataProvenance();

    private static readonly EvidenceProvenance Footprint = V2ContractTestData.ScreenshotProvenance();

    private static readonly OpportunityCostLineage Lineage = new(Price, Footprint);

    [Fact]
    public void DecisionKeepsCategorizedReasonsOpportunityCostAndAnswerChangingFacts()
    {
        var perSquare = Derived(Price, Footprint);
        var decision = new RecommendationDecision(
            RecommendationAction.Keep,
            "Keep for Gunsmith; selling would give up about 40k.",
            [
                new RecommendationReason(RecommendationReasonCategory.Economics, "value-per-square", "About 20k per square.", 1, perSquare),
                new RecommendationReason(RecommendationReasonCategory.CurrentFoundInRaidQuest, "quest-gunsmith", "Needed found in raid.", 10, Price),
            ],
            V2ContractTestData.Complete<long?>("decision.opportunityCost", 40_000, perSquare),
            Lineage,
            [new RecommendationSensitivity("quest-turned-in", "If the quest is already turned in", RecommendationAction.SellOnFlea)]);
        var result = Result(decision);

        var roundTrip = JsonSerializer.Deserialize<RecommendationResult>(
            JsonSerializer.Serialize(result, V2ContractJson.Options), V2ContractJson.Options)!;
        var restored = roundTrip.Decision.Value!;

        Assert.Equal(RecommendationReasonCategory.CurrentFoundInRaidQuest, restored.Reasons[0].Category);
        Assert.Equal(40_000, restored.OpportunityCostRoubles.Value);
        Assert.Equal(2, restored.OpportunityCostRoubles.Provenance.Inputs.Count);
        Assert.Equal(Lineage, restored.OpportunityCostLineage);
        Assert.Equal(RecommendationAction.SellOnFlea, restored.ChangesTheAnswer.Single().AlternativeAction);
    }

    [Fact]
    public void UnknownOpportunityCostIsAbsentAndDecisionsNeedReasons()
    {
        var reason = Reason();

        var review = new RecommendationDecision(
            RecommendationAction.Review, "Review: price unknown.", [reason],
            V2ContractTestData.Unknown<long?>("decision.opportunityCost"), null, []);

        Assert.Null(review.OpportunityCostRoubles.Value);
        Assert.Null(review.OpportunityCostLineage);
        Assert.Throws<ArgumentException>(() => new RecommendationDecision(
            RecommendationAction.Keep, "No reason", [], V2ContractTestData.Unknown<long?>("decision.opportunityCost"), null, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecommendationReason(
            default, "code", "explanation", 1, V2ContractTestData.PublicDataProvenance()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecommendationDecision(
            (RecommendationAction)77, "Bad", [reason], V2ContractTestData.Unknown<long?>("decision.opportunityCost"), null, []));
    }

    [Fact]
    public void OpportunityCostFigureMustBeComputedFromItsNamedPriceAndFootprint()
    {
        // Direct evidence is a reading, not the cost of a choice, even when a lineage is offered.
        Assert.Throws<ArgumentException>(() => Decision(Cost(40_000, Price), Lineage));
        Assert.Throws<ArgumentException>(() => Decision(Cost(40_000, Footprint), Lineage));
        Assert.Throws<ArgumentException>(() => Decision(Cost(40_000, V2ContractTestData.ModelProvenance(EvidenceSourceClass.HistoricalAggregate)), Lineage));

        // Any figure, including an ambiguity's candidates or a correction, needs the lineage.
        Assert.Throws<ArgumentException>(() => Decision(Cost(40_000, Derived(Price, Footprint)), null));
        Assert.Throws<ArgumentException>(() => Decision(
            new EvidencedValue<long?>(
                "decision.opportunityCost",
                null,
                new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current),
                Derived(Price, Footprint),
                candidates: [new EvidenceCandidate<long?>("low", "About 30k", 30_000, Derived(Price, Footprint))]),
            null));
        Assert.Throws<ArgumentException>(() => Decision(
            V2ContractTestData.Complete<long?>(
                "decision.opportunityCost",
                45_000,
                Derived(Price, Footprint),
                corrections: [new EvidenceCorrection<long?>(1, 40_000, 45_000, V2ContractTestData.ObservedUtc, CorrectionOriginClass.User, "local-user")]),
            null));

        // A candidate figure is held to the same rule as the value.
        Assert.Throws<ArgumentException>(() => Decision(
            V2ContractTestData.Complete<long?>(
                "decision.opportunityCost",
                40_000,
                Derived(Price, Footprint),
                candidates: [new EvidenceCandidate<long?>("low", "About 30k", 30_000, Price)]),
            Lineage));

        Assert.Equal(Lineage, Decision(Cost(40_000, Derived(Price, Footprint)), Lineage).OpportunityCostLineage);
    }

    [Fact]
    public void OpportunityCostLineageRejectsMissingSwappedAndDuplicatedRoles()
    {
        var quantity = V2ContractTestData.PublicDataProvenance(V2ContractTestData.ObservedUtc.AddDays(-3));

        Assert.Throws<ArgumentException>(() => Decision(Cost(40_000, Derived(Price)), Lineage));
        Assert.Throws<ArgumentException>(() => Decision(Cost(40_000, Derived(Footprint)), Lineage));
        Assert.Throws<ArgumentException>(() => Decision(Cost(40_000, Derived(Footprint, Price)), Lineage));
        Assert.Throws<ArgumentException>(() => Decision(Cost(40_000, Derived(Price, Footprint, Price)), Lineage));
        Assert.Throws<ArgumentException>(() => Decision(Cost(40_000, Derived(Price, Footprint, Derived(Footprint))), Lineage));
        Assert.Throws<ArgumentException>(() => Decision(Cost(40_000, Derived(Price, Footprint)), new OpportunityCostLineage(Footprint, Price)));
        Assert.Throws<ArgumentException>(() => new OpportunityCostLineage(Price, V2ContractTestData.PublicDataProvenance()));
        Assert.Throws<ArgumentException>(() => new OpportunityCostLineage(Price, Direct(EvidenceSourceClass.Unknown)));

        // The roles may sit deeper in a bounded tree, beside inputs that name no role.
        Assert.NotNull(Decision(Cost(40_000, Derived(Derived(Price, Footprint), quantity)), Lineage));

        // A model may compute the figure; its inputs cannot be newer than its data-through time.
        var catalogPrice = Direct(EvidenceSourceClass.PublicStructuredData, "fixture://catalog/price");
        var catalogFootprint = Direct(EvidenceSourceClass.PublicStructuredData, "fixture://catalog/dimensions");
        var modelled = new OpportunityCostLineage(catalogPrice, catalogFootprint);
        Assert.NotNull(Decision(Cost(40_000, Modelled(catalogPrice, catalogFootprint)), modelled));
        Assert.Throws<ArgumentException>(() => Decision(Cost(40_000, Modelled(catalogFootprint, catalogPrice)), modelled));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("swapped")]
    [InlineData("extra-role")]
    [InlineData("direct-cost")]
    [InlineData("null-role")]
    public void HostileJsonCannotDetachTheCostFromItsLineage(string attack)
    {
        var root = JsonSerializer.SerializeToNode(Result(Decision(Cost(40_000, Derived(Price, Footprint)), Lineage)), V2ContractJson.Options)!;
        var decision = root["decision"]!["value"]!.AsObject();
        var lineage = decision["opportunityCostLineage"]!.AsObject();

        switch (attack)
        {
            case "missing":
                decision.Remove("opportunityCostLineage");
                break;
            case "null":
                decision["opportunityCostLineage"] = null;
                break;
            case "swapped":
                var price = lineage["price"]!.DeepClone();
                lineage["price"] = lineage["footprint"]!.DeepClone();
                lineage["footprint"] = price;
                break;
            case "extra-role":
                lineage["quantity"] = lineage["price"]!.DeepClone();
                break;
            case "direct-cost":
                decision["opportunityCostRoubles"]!["provenance"] = JsonSerializer.SerializeToNode(Price, V2ContractJson.Options);
                break;
            case "null-role":
                lineage["footprint"] = null;
                break;
        }

        var failure = Record.Exception(() => JsonSerializer.Deserialize<RecommendationResult>(root.ToJsonString(), V2ContractJson.Options));

        Assert.NotNull(failure);
        Assert.True(failure is JsonException || failure.GetBaseException() is ArgumentException, failure.ToString());
    }

    [Fact]
    public void DecisionListsCannotBeChangedAfterValidation()
    {
        var reasons = new[] { Reason() };
        var sensitivities = new[] { new RecommendationSensitivity("quest-turned-in", "If turned in", RecommendationAction.SellOnFlea) };
        var decision = new RecommendationDecision(
            RecommendationAction.Review, "Review", reasons, V2ContractTestData.Unknown<long?>("decision.opportunityCost"), null, sensitivities);

        reasons[0] = Reason("replaced");
        sensitivities[0] = new RecommendationSensitivity("replaced", "Replaced", null);

        Assert.Equal("stale-price", decision.Reasons.Single().Code);
        Assert.Equal("quest-turned-in", decision.ChangesTheAnswer.Single().FactCode);
        Assert.False(decision.Reasons is RecommendationReason[]);
        Assert.Throws<NotSupportedException>(() => ((IList<RecommendationReason>)decision.Reasons)[0] = reasons[0]);
        Assert.Throws<NotSupportedException>(() => ((ICollection<RecommendationSensitivity>)decision.ChangesTheAnswer).Add(sensitivities[0]));
    }

    private static RecommendationReason Reason(string code = "stale-price") => new(
        RecommendationReasonCategory.EvidenceQuality, code, "Price is stale.", 1, V2ContractTestData.PublicDataProvenance());

    private static RecommendationDecision Decision(EvidencedValue<long?> cost, OpportunityCostLineage? lineage) => new(
        RecommendationAction.Keep, "Keep", [Reason()], cost, lineage, []);

    private static RecommendationResult Result(RecommendationDecision decision) => new(
        "recommendation-1",
        V2ContractVersion.Current,
        "ruleset-2026.09",
        V2ContractTestData.SessionId,
        V2ContractTestData.Complete("decision", decision, Derived(Price, Footprint)));

    private static EvidencedValue<long?> Cost(long roubles, EvidenceProvenance provenance) =>
        V2ContractTestData.Complete<long?>("decision.opportunityCost", roubles, provenance);

    private static EvidenceProvenance Derived(params EvidenceProvenance[] inputs) => new(
        EvidenceSourceClass.DerivedCalculation,
        "economics://value-per-square",
        V2ContractTestData.ObservedUtc,
        EvidenceConfidence.Unscored,
        new ProducerIdentity("fixture-economics", "2"),
        generatedUtc: V2ContractTestData.ObservedUtc,
        inputs: inputs);

    private static EvidenceProvenance Modelled(params EvidenceProvenance[] inputs) => new(
        EvidenceSourceClass.ModelledEstimate,
        "economics://expected-sale",
        V2ContractTestData.ObservedUtc,
        new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.7, "calibration-2026-09"),
        new ProducerIdentity("fixture-economics", "2", "sale-model-1"),
        V2ContractTestData.ObservedUtc.AddDays(-1),
        V2ContractTestData.ObservedUtc.AddHours(-1),
        new EvidenceCoverage(sampleSize: 80),
        inputs: inputs);

    private static EvidenceProvenance Direct(EvidenceSourceClass sourceClass, string identifier = "fixture://direct") => new(
        sourceClass,
        identifier,
        V2ContractTestData.ObservedUtc.AddDays(-3),
        EvidenceConfidence.Certain,
        new ProducerIdentity("fixture-direct", "2"));
}
