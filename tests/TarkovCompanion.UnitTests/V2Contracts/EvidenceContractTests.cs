using System.Text.Json;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

public sealed class EvidenceContractTests
{
    [Fact]
    public void UnscoredConfidenceIsDifferentFromAZeroScore()
    {
        var zero = new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0);

        Assert.Equal(EvidenceConfidenceKind.Unscored, EvidenceConfidence.Unscored.Kind);
        Assert.Null(EvidenceConfidence.Unscored.Score);
        Assert.Equal(0, zero.Score);
        Assert.NotEqual(EvidenceConfidence.Unscored, zero);
    }

    [Fact]
    public void ProvenanceNormalizesAllTimestampsToUtc()
    {
        var observed = new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.FromHours(-4));
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "fixture://catalog",
            observed,
            EvidenceConfidence.Certain,
            new ProducerIdentity("fixture", "2"),
            observed.AddMinutes(-2),
            observed.AddMinutes(-1));

        Assert.Equal(TimeSpan.Zero, provenance.ObservedUtc.Offset);
        Assert.Equal(TimeSpan.Zero, provenance.DataThroughUtc?.Offset);
        Assert.Equal(TimeSpan.Zero, provenance.GeneratedUtc?.Offset);
    }

    [Theory]
    [InlineData(EvidenceSourceClass.HistoricalAggregate)]
    [InlineData(EvidenceSourceClass.ModelledEstimate)]
    public void IntelligenceSourcesRequireCompleteProvenance(EvidenceSourceClass sourceClass)
    {
        Assert.Throws<ArgumentException>(() => new EvidenceProvenance(
            sourceClass,
            "fixture://incomplete",
            V2ContractTestData.ObservedUtc,
            EvidenceConfidence.Unscored,
            new ProducerIdentity("fixture", "2")));
    }

    [Fact]
    public void PartialAndStaleAreIndependentStates()
    {
        var status = new ResultStatus(ResultCompleteness.Partial, FreshnessState.Stale);

        Assert.Equal(ResultCompleteness.Partial, status.Completeness);
        Assert.Equal(FreshnessState.Stale, status.Freshness);
        Assert.Equal(4, Enum.GetValues<ResultCompleteness>().Length);
        Assert.Equal(3, Enum.GetValues<FreshnessState>().Length);
    }

    [Fact]
    public void UndefinedEnumValuesAreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResultStatus((ResultCompleteness)42, FreshnessState.Current));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvidenceConfidence(default(EvidenceConfidenceKind)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvidenceRegion(0, 0, 1, 1, default));
    }

    [Theory]
    [InlineData(ResultCompleteness.Unknown)]
    [InlineData(ResultCompleteness.Unavailable)]
    public void UndeterminedQuantityIsNotCollapsedToZero(ResultCompleteness completeness)
    {
        var status = new ResultStatus(completeness, FreshnessState.Unknown);
        var undetermined = new EvidencedValue<int?>("item.quantity", null, status, V2ContractTestData.ScreenshotProvenance());

        Assert.Null(undetermined.Value);
        Assert.Throws<ArgumentException>(() =>
            new EvidencedValue<int?>("item.quantity", 0, status, V2ContractTestData.ScreenshotProvenance()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void NonNullableValueTypeCannotPretendToBeUnknown(int value)
    {
        Assert.Throws<ArgumentException>(() => new EvidencedValue<int>(
            "item.quantity",
            value,
            new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Unknown),
            V2ContractTestData.ScreenshotProvenance()));
        Assert.Throws<ArgumentException>(() => new EvidencedValue<bool>(
            "item.foundInRaid",
            false,
            new ResultStatus(ResultCompleteness.Unavailable, FreshnessState.Unknown),
            V2ContractTestData.ScreenshotProvenance()));
    }

    [Fact]
    public void CompleteResultMustCarryItsValue()
    {
        Assert.Throws<ArgumentException>(() => V2ContractTestData.Complete<string?>("item.name", null));
        Assert.Throws<ArgumentException>(() => V2ContractTestData.Complete<int?>("item.quantity", null));
    }

    [Fact]
    public void CorrectionChainKeepsRecognizedAndCurrentValues()
    {
        var provenance = V2ContractTestData.ScreenshotProvenance();
        var bounds = new EvidenceRegion(10, 20, 60, 60, EvidenceCoordinateSpace.SourcePixels);
        var candidate = new EvidenceCandidate<string>("item-a", "Candidate A", "item-a", provenance, bounds);
        var field = V2ContractTestData.Complete(
            "item.canonicalId",
            "item-c",
            provenance,
            bounds,
            [candidate],
            [
                Correction(1, "item-a", "item-b", 1),
                Correction(2, "item-b", "item-c", 2),
            ]);

        Assert.Equal(bounds, field.Bounds);
        Assert.Same(provenance, field.Provenance);
        Assert.Equal("item-a", field.RecognizedValue);
        Assert.Equal("item-c", field.Value);
        Assert.Equal("item-a", field.Candidates.Single().Value);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<EvidenceCandidate<string>>)field.Candidates).Add(candidate));
    }

    [Fact]
    public void CorrectionHistoryCannotBeSplicedReorderedOrStale()
    {
        Assert.Throws<ArgumentException>(() => V2ContractTestData.Complete(
            "item.canonicalId", "item-b", corrections: [Correction(2, "item-a", "item-b", 1)]));
        Assert.Throws<ArgumentException>(() => V2ContractTestData.Complete(
            "item.canonicalId", "item-c", corrections: [Correction(1, "item-a", "item-b", 1), Correction(2, "item-x", "item-c", 2)]));
        Assert.Throws<ArgumentException>(() => V2ContractTestData.Complete(
            "item.canonicalId", "item-c", corrections: [Correction(1, "item-a", "item-b", 2), Correction(2, "item-b", "item-c", 1)]));
        Assert.Throws<ArgumentException>(() => V2ContractTestData.Complete(
            "item.canonicalId", "item-b", corrections: [Correction(1, "item-a", "item-b", -1)]));
        Assert.Throws<ArgumentException>(() => V2ContractTestData.Complete(
            "item.canonicalId", "item-a", corrections: [Correction(1, "item-a", "item-b", 1)]));
    }

    [Fact]
    public void OnlyClosedLeafCompositeValuesCanBeCorrectedWhole()
    {
        var item = V2ContractTestData.Item();

        Assert.Throws<ArgumentException>(() => V2ContractTestData.Complete(
            "grid.0.0",
            item,
            corrections: [new EvidenceCorrection<RecognizedItem>(1, null, item, V2ContractTestData.ObservedUtc, CorrectionOriginClass.User, "local-user")]));

        var node = JsonSerializer.SerializeToNode(V2ContractTestData.Complete("grid.0.0", item), V2ContractJson.Options)!;
        node["corrections"] = JsonSerializer.SerializeToNode(
            new[]
            {
                new EvidenceCorrection<RecognizedItem>(
                    1, item, item, V2ContractTestData.ObservedUtc,
                    CorrectionOriginClass.User, "local-user"),
            },
            V2ContractJson.Options);

        Assert.ThrowsAny<ArgumentException>(() =>
            JsonSerializer.Deserialize<EvidencedValue<RecognizedItem>>(node.ToJsonString(), V2ContractJson.Options));
    }

    [Fact]
    public void ClosedLeafCompositeCorrectionsRoundTripWithTheirHistory()
    {
        AssertLeafCorrectionRoundTrip(
            ItemConditionReading.NotApplicable,
            new ItemConditionReading(ItemConditionKind.Durability, 38, 50));
        AssertLeafCorrectionRoundTrip(
            new RaidClockReading(TimeSpan.FromMinutes(29), RaidClockBasis.ObservedOnExtractScreen, V2ContractTestData.CapturedUtc),
            new RaidClockReading(TimeSpan.FromMinutes(28), RaidClockBasis.ObservedOnExtractScreen, V2ContractTestData.CapturedUtc));
        AssertLeafCorrectionRoundTrip(
            new CharacterRegionReading(CharacterRegion.LeftArm, CharacterRegionState.Healthy, 1),
            new CharacterRegionReading(CharacterRegion.LeftArm, CharacterRegionState.Injured, 0.55));
    }

    [Fact]
    public void DerivedCalculationNamesInputsAndCannotLaunderAnEstimate()
    {
        var price = V2ContractTestData.PublicDataProvenance();
        var footprint = V2ContractTestData.ScreenshotProvenance();

        var perSquare = Derived(price, footprint);

        Assert.Equal(2, perSquare.Inputs.Count);
        Assert.Throws<ArgumentException>(() => Derived());
        Assert.Throws<ArgumentException>(() =>
            Derived(price, V2ContractTestData.ModelProvenance(EvidenceSourceClass.ModelledEstimate)));
        Assert.Throws<ArgumentException>(() => new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "fixture://direct",
            V2ContractTestData.ObservedUtc,
            EvidenceConfidence.Certain,
            new ProducerIdentity("fixture", "2"),
            inputs: [price]));
        Assert.Throws<ArgumentException>(() => new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            "fixture://value-per-square",
            V2ContractTestData.ObservedUtc,
            EvidenceConfidence.Unscored,
            new ProducerIdentity("fixture-economics", "2"),
            generatedUtc: V2ContractTestData.ObservedUtc.AddHours(-1),
            inputs: [footprint]));
    }

    [Fact]
    public void ProvenanceEqualityComparesItsInputTree()
    {
        var price = V2ContractTestData.PublicDataProvenance();
        var footprint = V2ContractTestData.ScreenshotProvenance();
        var perSquare = Derived(price, footprint);

        var roundTrip = JsonSerializer.Deserialize<EvidenceProvenance>(
            JsonSerializer.Serialize(perSquare, V2ContractJson.Options), V2ContractJson.Options);

        Assert.Equal(perSquare, Derived(V2ContractTestData.PublicDataProvenance(), V2ContractTestData.ScreenshotProvenance()));
        Assert.Equal(perSquare.GetHashCode(), Derived(price, footprint).GetHashCode());
        Assert.Equal(perSquare, roundTrip);
        Assert.NotEqual(perSquare, Derived(footprint, price));
        Assert.NotEqual(perSquare, Derived(price));
        Assert.NotEqual(perSquare, Derived(price, V2ContractTestData.PublicDataProvenance(V2ContractTestData.ObservedUtc.AddDays(-3))));
    }

    [Fact]
    public void ProvenanceInputsCannotBeChangedAfterValidation()
    {
        var inputs = new[] { V2ContractTestData.PublicDataProvenance(), V2ContractTestData.ScreenshotProvenance() };
        var perSquare = Derived(inputs);

        inputs[1] = V2ContractTestData.ModelProvenance(EvidenceSourceClass.ModelledEstimate);

        Assert.Equal(EvidenceSourceClass.GameWrittenScreenshot, perSquare.Inputs[1].SourceClass);
        Assert.False(perSquare.Inputs is EvidenceProvenance[]);
        Assert.Throws<NotSupportedException>(() => ((IList<EvidenceProvenance>)perSquare.Inputs)[1] = inputs[1]);
        Assert.Throws<NotSupportedException>(() => ((ICollection<EvidenceProvenance>)perSquare.Inputs).Clear());
    }

    [Fact]
    public void ScanIntentIsTheFrozenSet()
    {
        Assert.Equal(
            [
                nameof(ScanIntent.Auto),
                nameof(ScanIntent.Loot),
                nameof(ScanIntent.Stash),
                nameof(ScanIntent.Ammo),
                nameof(ScanIntent.Keys),
                nameof(ScanIntent.QuestItems),
                nameof(ScanIntent.ExtractsAndMap),
                nameof(ScanIntent.HealthAndCharacter),
                nameof(ScanIntent.Flea),
            ],
            Enum.GetNames<ScanIntent>());
    }

    private static EvidenceCorrection<string> Correction(long sequence, string from, string to, int minutesAfterObservation) => new(
        sequence,
        from,
        to,
        V2ContractTestData.ObservedUtc.AddMinutes(minutesAfterObservation),
        CorrectionOriginClass.User,
        "local-user");

    private static void AssertLeafCorrectionRoundTrip<T>(T original, T corrected)
        where T : class
    {
        var field = V2ContractTestData.Complete(
            "leaf",
            corrected,
            corrections:
            [
                new EvidenceCorrection<T>(
                    1, original, corrected, V2ContractTestData.ObservedUtc,
                    CorrectionOriginClass.User, "local-user"),
            ]);

        var roundTrip = JsonSerializer.Deserialize<EvidencedValue<T>>(
            JsonSerializer.Serialize(field, V2ContractJson.Options), V2ContractJson.Options)!;

        Assert.Equal(original, roundTrip.RecognizedValue);
        Assert.Equal(corrected, roundTrip.Value);
        Assert.Equal(original, roundTrip.Corrections.Single().OriginalValue);
    }

    private static EvidenceProvenance Derived(params EvidenceProvenance[] inputs) => new(
        EvidenceSourceClass.DerivedCalculation,
        "fixture://value-per-square",
        V2ContractTestData.ObservedUtc,
        EvidenceConfidence.Unscored,
        new ProducerIdentity("fixture-economics", "2"),
        generatedUtc: V2ContractTestData.ObservedUtc,
        inputs: inputs);
}
