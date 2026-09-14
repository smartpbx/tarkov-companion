using System.Text.Json;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

public sealed class ModelledIntelligenceContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = V2ContractJson.Options;

    private static readonly EncounterLikelihood Likelihood = new("customs", "dorms", RaidPhase.Mid, 0.62);

    [Fact]
    public void ModelledEstimateRequiresModelledProvenance()
    {
        Assert.Throws<ArgumentException>(() => new ModelledIntelligence<EncounterLikelihood>(
            "traffic-customs-mid",
            V2ContractTestData.Complete("traffic.encounter", Likelihood, V2ContractTestData.ScreenshotProvenance()),
            [Input()],
            "Static route pressure estimate"));
    }

    [Fact]
    public void ModelledEstimateRoundTripsWithItsEvidenceClass()
    {
        var json = JsonSerializer.Serialize(Estimate(), JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ModelledIntelligence<EncounterLikelihood>>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(EvidenceSourceClass.ModelledEstimate, roundTrip.Estimate.Provenance.SourceClass);
        Assert.Equal("traffic-model-4", roundTrip.Estimate.Provenance.Producer.ModelVersion);
        Assert.Equal(240, roundTrip.Estimate.Provenance.Coverage!.SampleSize);
        Assert.Equal(Likelihood, roundTrip.Estimate.Value);
        Assert.DoesNotContain("LiveDetection", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("LiveDetection")]
    [InlineData(nameof(EvidenceSourceClass.ExternalVisiblePixels))]
    [InlineData(nameof(EvidenceSourceClass.GameWrittenScreenshot))]
    [InlineData(nameof(EvidenceSourceClass.HistoricalAggregate))]
    public void SerializedEstimateCannotBeRelabelled(string relabel)
    {
        var json = JsonSerializer.Serialize(Estimate(), JsonOptions)
            .Replace("\"ModelledEstimate\"", $"\"{relabel}\"", StringComparison.Ordinal);

        var failure = Record.Exception(() =>
            JsonSerializer.Deserialize<ModelledIntelligence<EncounterLikelihood>>(json, JsonOptions));

        Assert.NotNull(failure);
        Assert.True(
            failure is JsonException || failure.GetBaseException() is ArgumentException,
            failure.ToString());
    }

    [Fact]
    public void EstimateCandidatesCannotCarryObservationalProvenance()
    {
        var disguised = new EvidenceCandidate<EncounterLikelihood>(
            "high", "High", Likelihood with { }, V2ContractTestData.ScreenshotProvenance());

        Assert.Throws<ArgumentException>(() => new ModelledIntelligence<EncounterLikelihood>(
            "traffic-customs-mid",
            V2ContractTestData.Complete(
                "traffic.encounter",
                Likelihood,
                V2ContractTestData.ModelProvenance(EvidenceSourceClass.ModelledEstimate),
                candidates: [disguised]),
            [Input()],
            "Static route pressure estimate"));
    }

    [Fact]
    public void HistoricalAggregateRequiresHistoricalProvenance()
    {
        var historical = new HistoricalIntelligence<ZoneTrafficIntensity>(
            "customs-route-samples",
            V2ContractTestData.Complete(
                "traffic.zone",
                new ZoneTrafficIntensity("customs", "dorms", RaidPhase.Late, 0.4),
                V2ContractTestData.ModelProvenance(EvidenceSourceClass.HistoricalAggregate)),
            [Input(IntelligenceInputKind.PrivateLocalFeedback, LocalLog(V2ContractTestData.ObservedUtc.AddDays(-3)))]);

        Assert.Equal(EvidenceSourceClass.HistoricalAggregate, historical.Value.Provenance.SourceClass);
    }

    [Fact]
    public void InputsAreTypedAllowedAndNoNewerThanTheOutput()
    {
        Assert.Throws<ArgumentException>(() =>
            Input(IntelligenceInputKind.PrivateLocalFeedback, V2ContractTestData.ScreenshotProvenance()));
        Assert.Throws<ArgumentException>(() =>
            Input(IntelligenceInputKind.HistoricalAggregate, V2ContractTestData.ModelProvenance(EvidenceSourceClass.ModelledEstimate)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Input((IntelligenceInputKind)0, V2ContractTestData.PublicDataProvenance()));
        Assert.Throws<ArgumentException>(() => Estimate(
            Input(IntelligenceInputKind.PrivateLocalFeedback, LocalLog(V2ContractTestData.ObservedUtc.AddMinutes(-5)))));
        Assert.Throws<ArgumentException>(() => Estimate([]));
    }

    [Fact]
    public void EnvelopesAcceptOnlyAllowlistedIntelligencePayloads()
    {
        Assert.Throws<ArgumentException>(() => new ModelledIntelligence<EnemyPositionGuess>(
            "enemy",
            V2ContractTestData.Complete(
                "enemy",
                new EnemyPositionGuess(1, 2),
                V2ContractTestData.ModelProvenance(EvidenceSourceClass.ModelledEstimate)),
            [Input()],
            "Not allowed"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncounterLikelihood("customs", "dorms", RaidPhase.Mid, 1.2));
    }

    private static ModelledIntelligence<EncounterLikelihood> Estimate(params IntelligenceInputReference[] inputs) => new(
        "traffic-customs-mid",
        V2ContractTestData.Complete(
            "traffic.encounter",
            Likelihood,
            V2ContractTestData.ModelProvenance(EvidenceSourceClass.ModelledEstimate)),
        inputs,
        "Static route pressure estimate");

    private static ModelledIntelligence<EncounterLikelihood> Estimate() => Estimate(Input());

    private static IntelligenceInputReference Input(
        IntelligenceInputKind kind = IntelligenceInputKind.StaticMapData,
        EvidenceProvenance? provenance = null) => new(
        "public-map-topology",
        kind,
        provenance ?? V2ContractTestData.PublicDataProvenance());

    private static EvidenceProvenance LocalLog(DateTimeOffset observedUtc) => new(
        EvidenceSourceClass.GameWrittenLog,
        "fixture://own-raid-log",
        observedUtc,
        EvidenceConfidence.Certain,
        new ProducerIdentity("fixture-log", "2"));

    private sealed record EnemyPositionGuess(double X, double Y) : IIntelligencePayload;
}
