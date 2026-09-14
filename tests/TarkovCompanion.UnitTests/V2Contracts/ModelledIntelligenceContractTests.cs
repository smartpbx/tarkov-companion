using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

public sealed class ModelledIntelligenceContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void ModelledEstimateRequiresModelledProvenance()
    {
        var observedValue = V2ContractTestData.Complete(
            "traffic.pressure",
            0.62,
            V2ContractTestData.ScreenshotProvenance());

        Assert.Throws<ArgumentException>(() => new ModelledIntelligence<double>(
            "traffic-customs-mid",
            observedValue,
            [Input()],
            "Static route pressure estimate"));
    }

    [Fact]
    public void ModelledEstimateRoundTripsOnlyWithItsEvidenceClass()
    {
        var estimate = new ModelledIntelligence<double>(
            "traffic-customs-mid",
            V2ContractTestData.Complete(
                "traffic.pressure",
                0.62,
                V2ContractTestData.ModelProvenance(EvidenceSourceClass.ModelledEstimate)),
            [Input()],
            "Static route pressure estimate");

        var json = JsonSerializer.Serialize(estimate, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ModelledIntelligence<double>>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(EvidenceSourceClass.ModelledEstimate, roundTrip.Estimate.Provenance.SourceClass);
        Assert.Equal("traffic-model-4", roundTrip.Estimate.Provenance.Producer.ModelVersion);
        Assert.Equal(240, roundTrip.Estimate.Provenance.Coverage!.SampleSize);
        Assert.DoesNotContain("LiveDetection", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownLiveEvidenceDiscriminatorCannotDeserialize()
    {
        var estimate = new ModelledIntelligence<double>(
            "traffic-customs-mid",
            V2ContractTestData.Complete(
                "traffic.pressure",
                0.62,
                V2ContractTestData.ModelProvenance(EvidenceSourceClass.ModelledEstimate)),
            [Input()],
            "Static route pressure estimate");
        var json = JsonSerializer.Serialize(estimate, JsonOptions)
            .Replace("ModelledEstimate", "LiveDetection", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ModelledIntelligence<double>>(json, JsonOptions));
    }

    [Theory]
    [InlineData(nameof(EvidenceSourceClass.ExternalVisiblePixels))]
    [InlineData(nameof(EvidenceSourceClass.GameWrittenScreenshot))]
    [InlineData(nameof(EvidenceSourceClass.HistoricalAggregate))]
    public void SerializedEstimateCannotBeRelabelledAsAnotherEvidenceClass(string relabel)
    {
        var estimate = new ModelledIntelligence<double>(
            "traffic-customs-mid",
            V2ContractTestData.Complete(
                "traffic.pressure",
                0.62,
                V2ContractTestData.ModelProvenance(EvidenceSourceClass.ModelledEstimate)),
            [Input()],
            "Static route pressure estimate");
        var json = JsonSerializer.Serialize(estimate, JsonOptions)
            .Replace("\"ModelledEstimate\"", $"\"{relabel}\"", StringComparison.Ordinal);

        var failure = Record.Exception(() =>
            JsonSerializer.Deserialize<ModelledIntelligence<double>>(json, JsonOptions));

        Assert.NotNull(failure);
        Assert.IsAssignableFrom<ArgumentException>(failure.GetBaseException());
    }

    [Fact]
    public void EstimateCandidatesCannotCarryObservationalProvenance()
    {
        var modelled = V2ContractTestData.ModelProvenance(EvidenceSourceClass.ModelledEstimate);
        var disguised = new EvidenceCandidate<double>(
            "pressure-high",
            "High pressure",
            0.9,
            V2ContractTestData.ScreenshotProvenance());

        Assert.Throws<ArgumentException>(() => new ModelledIntelligence<double>(
            "traffic-customs-mid",
            V2ContractTestData.Complete("traffic.pressure", 0.62, modelled, candidates: [disguised]),
            [Input()],
            "Static route pressure estimate"));
    }

    [Fact]
    public void HistoricalAggregateRequiresHistoricalProvenance()
    {
        var historical = new HistoricalIntelligence<int>(
            "customs-route-samples",
            V2ContractTestData.Complete(
                "traffic.sampleCount",
                240,
                V2ContractTestData.ModelProvenance(EvidenceSourceClass.HistoricalAggregate)),
            [Input()]);

        Assert.Equal(EvidenceSourceClass.HistoricalAggregate, historical.Value.Provenance.SourceClass);
    }

    private static IntelligenceInputReference Input() => new(
        "public-map-topology",
        V2ContractTestData.PublicDataProvenance());
}
