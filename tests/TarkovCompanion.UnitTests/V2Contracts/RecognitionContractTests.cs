using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

public sealed class RecognitionContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void ResultAndGridCellUseTheSameEvidenceEnvelopeAsFields()
    {
        var item = Item();
        var cell = new GridCellRecognition(
            new GridCellAddress(0, 0),
            V2ContractTestData.Complete("grid.0.0", item));
        var grid = new GridRecognition(
            new GridGeometry(
                V2ContractTestData.Complete("grid.rows", 1),
                V2ContractTestData.Complete("grid.columns", 1),
                V2ContractTestData.Complete("grid.cellWidth", 63),
                V2ContractTestData.Complete("grid.cellHeight", 63)),
            [cell]);
        var envelope = new RecognitionResultEnvelope<GridRecognition>(
            V2ContractTestData.Header(RecognizedContext.Grid),
            V2ContractTestData.Complete("result.grid", grid));

        var result = new GridRecognitionResult(envelope);

        Assert.IsType<EvidencedValue<GridRecognition>>(result.Recognition.Result);
        Assert.IsType<EvidencedValue<RecognizedItem>>(result.Recognition.Result.Value!.Cells.Single().Item);
        Assert.All(typeof(RecognizedItem).GetProperties(), property =>
            Assert.True(
                property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericTypeDefinition() == typeof(EvidencedValue<>),
                $"{property.Name} is not an evidenced field."));
    }

    [Fact]
    public void ExtractRawLinesAndObservedClockSurviveSerializationVerbatim()
    {
        const string rawHeader = "Find an extraction point 0:28:10";
        var bounds = new EvidenceRegion(500, 100, 400, 20, EvidenceCoordinateSpace.SourcePixels);
        var extraction = new ExtractMapRecognition(
            V2ContractTestData.Complete("extract.mapId", "customs"),
            [],
            [new RawOcrLine(V2ContractTestData.Complete("extract.raw.0", rawHeader, bounds: bounds))],
            V2ContractTestData.Complete(
                "extract.raidTimeRemaining",
                new RaidClockReading(TimeSpan.FromMinutes(28) + TimeSpan.FromSeconds(10), RaidClockBasis.ObservedOnExtractScreen),
                bounds: bounds));
        var original = new ExtractMapRecognitionResult(
            new RecognitionResultEnvelope<ExtractMapRecognition>(
                V2ContractTestData.Header(RecognizedContext.ExtractsAndMap),
                V2ContractTestData.Complete("result.extractMap", extraction, bounds: bounds)));

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ExtractMapRecognitionResult>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        var payload = roundTrip.Recognition.Result.Value!;
        Assert.Equal(rawHeader, payload.RawOcrLines.Single().Text.Value);
        Assert.Equal(RaidClockBasis.ObservedOnExtractScreen, payload.RaidTimeRemaining.Value!.Basis);
        Assert.Equal(TimeSpan.FromMinutes(28) + TimeSpan.FromSeconds(10), payload.RaidTimeRemaining.Value.Remaining);
        Assert.Equal(V2ContractTestData.ObservedUtc, payload.RaidTimeRemaining.Provenance.ObservedUtc);
    }

    [Fact]
    public void TypedResultRejectsTheWrongDetectedContext()
    {
        var envelope = new RecognitionResultEnvelope<RecognizedItem>(
            V2ContractTestData.Header(RecognizedContext.Stash),
            V2ContractTestData.Complete("result.item", Item()));

        Assert.Throws<ArgumentException>(() => new ItemRecognitionResult(envelope));
    }

    private static RecognizedItem Item() => new(
        V2ContractTestData.Complete("item.id", "item-a"),
        V2ContractTestData.Complete("item.name", "Item A"),
        V2ContractTestData.Complete("item.quantity", 1),
        V2ContractTestData.Complete("item.slots", 1),
        V2ContractTestData.Complete("item.foundInRaid", true));
}
