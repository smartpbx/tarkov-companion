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
    public void FieldPreservesBoundsCandidatesProvenanceAndOrderedCorrections()
    {
        var provenance = V2ContractTestData.ScreenshotProvenance();
        var bounds = new EvidenceRegion(10, 20, 60, 60, EvidenceCoordinateSpace.SourcePixels);
        var candidate = new EvidenceCandidate<string>("item-a", "Candidate A", "item-a", provenance, bounds);
        var correction = new EvidenceCorrection<string>(
            1,
            "item-a",
            "item-b",
            V2ContractTestData.ObservedUtc.AddMinutes(1),
            CorrectionOriginClass.User,
            "local-user",
            "Selected the second candidate");

        var field = V2ContractTestData.Complete(
            "item.canonicalId",
            "item-a",
            provenance,
            bounds,
            [candidate],
            [correction]);

        Assert.Equal(bounds, field.Bounds);
        Assert.Same(provenance, field.Provenance);
        Assert.Equal("item-a", field.Candidates.Single().Value);
        Assert.Equal("item-a", field.Corrections.Single().OriginalValue);
        Assert.Equal("item-b", field.Corrections.Single().CorrectedValue);
        Assert.Equal("2.0", field.Provenance.Producer.Version);
    }

    [Theory]
    [InlineData(ResultCompleteness.Unknown)]
    [InlineData(ResultCompleteness.Unavailable)]
    public void UndeterminedQuantityIsNotCollapsedToZero(ResultCompleteness completeness)
    {
        var status = new ResultStatus(completeness, FreshnessState.Unknown);
        var undetermined = new EvidencedValue<int?>(
            "item.quantity",
            null,
            status,
            V2ContractTestData.ScreenshotProvenance());

        Assert.Null(undetermined.Value);
        Assert.Throws<ArgumentException>(() => new EvidencedValue<int?>(
            "item.quantity",
            0,
            status,
            V2ContractTestData.ScreenshotProvenance()));
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
    public void CorrectionHistoryMustBeContiguous()
    {
        var correction = new EvidenceCorrection<string>(
            2,
            "item-a",
            "item-b",
            V2ContractTestData.ObservedUtc,
            CorrectionOriginClass.PairedDevice,
            "tablet-1");

        Assert.Throws<ArgumentException>(() => V2ContractTestData.Complete(
            "item.canonicalId",
            "item-a",
            corrections: [correction]));
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
}
