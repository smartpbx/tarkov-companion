using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

public sealed class RecognitionContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = V2ContractJson.Options;

    [Fact]
    public void ResultAndGridCellUseTheSameEvidenceEnvelopeAsFields()
    {
        var grid = V2ContractTestData.Grid(V2ContractTestData.Cell(0, 0));
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
        var original = ExtractResult(rawHeader, ObservedClock(V2ContractTestData.CapturedUtc));

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ExtractMapRecognitionResult>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        var payload = roundTrip.Recognition.Result.Value!;
        var clock = payload.RaidTimeRemaining.Value!;
        Assert.Equal(rawHeader, payload.RawOcrLines.Single().Text.Value);
        Assert.Equal(RaidClockBasis.ObservedOnExtractScreen, clock.Basis);
        Assert.Equal(TimeSpan.FromMinutes(28) + TimeSpan.FromSeconds(10), clock.Remaining);
        Assert.Equal(V2ContractTestData.CapturedUtc, clock.AsOfUtc);
        Assert.Equal(roundTrip.Recognition.Header.CapturedUtc, clock.AsOfUtc);
        Assert.Equal(V2ContractTestData.ObservedUtc, payload.RaidTimeRemaining.Provenance.ObservedUtc);
        Assert.Equal(EvidenceSourceClass.GameWrittenScreenshot, payload.RaidTimeRemaining.Provenance.SourceClass);
        Assert.Equal(original.Recognition.Header.SessionId, roundTrip.Recognition.Header.SessionId);
        Assert.Equal(original.Recognition.Header.ContractVersion, roundTrip.Recognition.Header.ContractVersion);
        Assert.Contains("\"ObservedOnExtractScreen\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservedClockMustBeAsOfCaptureAndComeFromPixels()
    {
        Assert.Throws<ArgumentException>(() =>
            ExtractResult("0:28:10", ObservedClock(V2ContractTestData.ObservedUtc)));

        var fromLog = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenLog,
            "fixture://application-log",
            V2ContractTestData.ObservedUtc,
            EvidenceConfidence.Certain,
            new ProducerIdentity("fixture-log", "2"));
        Assert.Throws<ArgumentException>(() => ExtractPayload(
            "0:28:10",
            V2ContractTestData.Complete(
                "extract.raidTimeRemaining",
                new RaidClockReading(TimeSpan.FromMinutes(28), RaidClockBasis.ObservedOnExtractScreen, V2ContractTestData.CapturedUtc),
                fromLog)));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RaidClockReading(TimeSpan.FromMinutes(28), default, V2ContractTestData.CapturedUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RaidClockReading(TimeSpan.FromMinutes(-1), RaidClockBasis.CountedFromRaidStart, V2ContractTestData.CapturedUtc));
    }

    [Fact]
    public void ObservedClockStopsShortOfAnHourSoTheUnknownMarkerCannotPass()
    {
        var justUnder = TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1);

        Assert.Equal(TimeSpan.FromHours(1), RaidClockReading.MaxObservedRemaining);
        Assert.Equal(justUnder, new RaidClockReading(justUnder, RaidClockBasis.ObservedOnExtractScreen, V2ContractTestData.CapturedUtc).Remaining);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RaidClockReading(TimeSpan.FromHours(1), RaidClockBasis.ObservedOnExtractScreen, V2ContractTestData.CapturedUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RaidClockReading(new TimeSpan(22, 22, 22), RaidClockBasis.ObservedOnExtractScreen, V2ContractTestData.CapturedUtc));

        // A counted clock is arithmetic from a known raid start, not a reading, and keeps its semantics.
        Assert.Equal(
            TimeSpan.FromHours(1),
            new RaidClockReading(TimeSpan.FromHours(1), RaidClockBasis.CountedFromRaidStart, V2ContractTestData.CapturedUtc).Remaining);
    }

    [Theory]
    [InlineData("22:22:22")]
    [InlineData("01:00:00")]
    [InlineData("1.00:00:00")]
    public void HostileJsonCannotCarryAnObservedClockOfAnHourOrMore(string remaining)
    {
        var json = JsonSerializer.Serialize(ExtractResult("0:28:10", ObservedClock(V2ContractTestData.CapturedUtc)), JsonOptions);
        var node = JsonNode.Parse(json)!;
        var clock = node["recognition"]!["result"]!["value"]!["raidTimeRemaining"]!["value"]!;
        Assert.Equal("00:28:10", clock["remaining"]!.GetValue<string>());

        clock["remaining"] = remaining;

        Assert.ThrowsAny<ArgumentException>(() =>
            JsonSerializer.Deserialize<ExtractMapRecognitionResult>(node.ToJsonString(), JsonOptions));
    }

    [Fact]
    public void UnreadClockIsAbsentRatherThanABasis()
    {
        var result = ExtractResult("Find an extraction point", V2ContractTestData.Unknown<RaidClockReading>("extract.raidTimeRemaining"));

        Assert.Null(result.Recognition.Result.Value!.RaidTimeRemaining.Value);
        Assert.DoesNotContain("Unknown", Enum.GetNames<RaidClockBasis>());
    }

    [Fact]
    public void TransitAndPendingExtractsAreDistinct()
    {
        var transit = new ExtractRecognition(
            V2ContractTestData.Complete("extract.slot", "TRANSIT@2"),
            V2ContractTestData.Complete<ExtractKind?>("extract.kind", ExtractKind.Transit),
            V2ContractTestData.Unknown<string>("extract.id"),
            V2ContractTestData.Complete("extract.name", "Transit to Reserve"),
            V2ContractTestData.Complete("extract.destination", "reserve"),
            V2ContractTestData.Complete<ExtractAvailability?>("extract.availability", ExtractAvailability.Pending));

        var roundTrip = JsonSerializer.Deserialize<ExtractRecognition>(JsonSerializer.Serialize(transit, JsonOptions), JsonOptions)!;

        Assert.Equal(ExtractKind.Transit, roundTrip.Kind.Value);
        Assert.Equal(ExtractAvailability.Pending, roundTrip.Availability.Value);
        Assert.Equal("reserve", roundTrip.DestinationMapId.Value);
        Assert.Throws<ArgumentException>(() => new ExtractRecognition(
            transit.SlotLabel,
            V2ContractTestData.Complete<ExtractKind?>("extract.kind", ExtractKind.Exfil),
            transit.CanonicalId,
            transit.DisplayName,
            transit.DestinationMapId,
            transit.Availability));
    }

    [Fact]
    public void TypedResultRejectsTheWrongOrAnIncompleteDetectedContext()
    {
        var item = V2ContractTestData.Complete("result.item", V2ContractTestData.Item());

        Assert.Throws<ArgumentException>(() => new ItemRecognitionResult(
            new RecognitionResultEnvelope<RecognizedItem>(V2ContractTestData.Header(RecognizedContext.Stash), item)));

        var partialContext = new RecognitionResultHeader(
            "result-1",
            V2ContractVersion.Current,
            V2ContractTestData.SessionId,
            "artifact-1",
            V2ContractTestData.CapturedUtc,
            ScanIntent.Auto,
            new EvidencedValue<RecognizedContext?>(
                "context",
                RecognizedContext.Item,
                new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current),
                V2ContractTestData.ScreenshotProvenance()));
        Assert.Throws<ArgumentException>(() => new ItemRecognitionResult(
            new RecognitionResultEnvelope<RecognizedItem>(partialContext, item)));
    }

    [Fact]
    public void AutoUncertaintyIsATypedResultWithContextCandidates()
    {
        var provenance = V2ContractTestData.ScreenshotProvenance();
        var bounds = new EvidenceRegion(0, 0, 1920, 1080, EvidenceCoordinateSpace.SourcePixels);
        var context = new EvidencedValue<RecognizedContext?>(
            "context",
            null,
            new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current),
            provenance,
            bounds,
            [
                new EvidenceCandidate<RecognizedContext?>("stash", "Stash", RecognizedContext.Stash, provenance),
                new EvidenceCandidate<RecognizedContext?>("loot", "Loot", RecognizedContext.Loot, provenance),
            ]);
        var header = new RecognitionResultHeader(
            "result-2", V2ContractVersion.Current, V2ContractTestData.SessionId, "artifact-2",
            V2ContractTestData.CapturedUtc, ScanIntent.Auto, context);
        var payload = V2ContractTestData.Complete(
            "result.unresolved",
            new UnresolvedContextRecognition([new RawOcrLine(V2ContractTestData.Complete("raw.0", "STASH"))]));

        var result = new UnresolvedContextRecognitionResult(new RecognitionResultEnvelope<UnresolvedContextRecognition>(header, payload));
        var roundTrip = JsonSerializer.Deserialize<UnresolvedContextRecognitionResult>(
            JsonSerializer.Serialize(result, JsonOptions), JsonOptions)!;

        Assert.Null(roundTrip.Recognition.Header.DetectedContext.Value);
        Assert.Equal(
            [RecognizedContext.Stash, RecognizedContext.Loot],
            roundTrip.Recognition.Header.DetectedContext.Candidates.Select(candidate => candidate.Value));
        Assert.Throws<ArgumentException>(() => new UnresolvedContextRecognitionResult(
            new RecognitionResultEnvelope<UnresolvedContextRecognition>(V2ContractTestData.Header(RecognizedContext.Stash), payload)));
    }

    [Fact]
    public void EnvelopeAcceptsOnlyAllowlistedPayloads()
    {
        Assert.Throws<ArgumentException>(() => new RecognitionResultEnvelope<LiveEnemyPosition>(
            V2ContractTestData.Header(RecognizedContext.Item),
            V2ContractTestData.Complete("result.enemy", new LiveEnemyPosition(10, 20))));
    }

    [Fact]
    public void HealthDisplayThatWasNotReadIsNotAbsentOrHealthy()
    {
        var unread = new EvidencedValue<bool?>(
            "health.displayPresent",
            null,
            new ResultStatus(ResultCompleteness.Unavailable, FreshnessState.Unknown),
            V2ContractTestData.ScreenshotProvenance());

        Assert.Null(unread.Value);
        Assert.Null(new CharacterRegionReading(CharacterRegion.Head, null, null).State);
        Assert.DoesNotContain("Unknown", Enum.GetNames<CharacterRegionState>());
        Assert.Throws<ArgumentException>(() => new EvidencedValue<bool?>(
            "health.displayPresent",
            false,
            new ResultStatus(ResultCompleteness.Unavailable, FreshnessState.Unknown),
            V2ContractTestData.ScreenshotProvenance()));
    }

    private static EvidencedValue<RaidClockReading> ObservedClock(DateTimeOffset asOfUtc) => V2ContractTestData.Complete(
        "extract.raidTimeRemaining",
        new RaidClockReading(TimeSpan.FromMinutes(28) + TimeSpan.FromSeconds(10), RaidClockBasis.ObservedOnExtractScreen, asOfUtc),
        bounds: new EvidenceRegion(500, 100, 400, 20, EvidenceCoordinateSpace.SourcePixels));

    private static ExtractMapRecognition ExtractPayload(string rawLine, EvidencedValue<RaidClockReading> clock) => new(
        V2ContractTestData.Complete("extract.mapId", "customs"),
        [],
        [new RawOcrLine(V2ContractTestData.Complete(
            "extract.raw.0",
            rawLine,
            bounds: new EvidenceRegion(500, 100, 400, 20, EvidenceCoordinateSpace.SourcePixels)))],
        clock);

    private static ExtractMapRecognitionResult ExtractResult(string rawLine, EvidencedValue<RaidClockReading> clock) => new(
        new RecognitionResultEnvelope<ExtractMapRecognition>(
            V2ContractTestData.Header(RecognizedContext.ExtractsAndMap),
            V2ContractTestData.Complete("result.extractMap", ExtractPayload(rawLine, clock))));

    private sealed record LiveEnemyPosition(double X, double Y) : IRecognitionPayload;
}
