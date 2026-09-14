using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

public sealed class ModelledIntelligenceContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = V2ContractJson.Options;

    private static readonly EncounterLikelihood Likelihood = new("customs", "dorms", RaidPhase.Mid, 0.62);

    private static readonly DateTimeOffset PastUtc = V2ContractTestData.ObservedUtc.AddDays(-3);

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
        var estimate = Estimate();
        var json = JsonSerializer.Serialize(estimate, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ModelledIntelligence<EncounterLikelihood>>(json, JsonOptions);

        Assert.NotNull(roundTrip);
        Assert.Equal(EvidenceSourceClass.ModelledEstimate, roundTrip.Estimate.Provenance.SourceClass);
        Assert.Equal("traffic-model-4", roundTrip.Estimate.Provenance.Producer.ModelVersion);
        Assert.Equal(240, roundTrip.Estimate.Provenance.Coverage!.SampleSize);
        Assert.Equal(Likelihood, roundTrip.Estimate.Value);
        Assert.Equal(estimate.Estimate.Provenance, roundTrip.Estimate.Provenance);
        Assert.Equal(roundTrip.Inputs.Single().Provenance, roundTrip.Estimate.Provenance.Inputs.Single());
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

        AssertRejected<ModelledIntelligence<EncounterLikelihood>>(json);
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
                Model(EvidenceSourceClass.ModelledEstimate, Input().Provenance),
                candidates: [disguised]),
            [Input()],
            "Static route pressure estimate"));
    }

    [Fact]
    public void HistoricalAggregateRequiresHistoricalProvenance()
    {
        var feedback = Input(IntelligenceInputKind.PrivateLocalFeedback, Direct(EvidenceSourceClass.GameWrittenLog));
        var historical = new HistoricalIntelligence<ZoneTrafficIntensity>(
            "customs-route-samples",
            V2ContractTestData.Complete(
                "traffic.zone",
                new ZoneTrafficIntensity("customs", "dorms", RaidPhase.Late, 0.4),
                Model(EvidenceSourceClass.HistoricalAggregate, feedback.Provenance)),
            [feedback]);

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
            Input(IntelligenceInputKind.PrivateLocalFeedback, Direct(EvidenceSourceClass.GameWrittenLog, V2ContractTestData.ObservedUtc.AddMinutes(-5)))));
        Assert.Throws<ArgumentException>(() => Estimate([]));
    }

    [Theory]
    [InlineData(EvidenceSourceClass.GameWrittenScreenshot)]
    [InlineData(EvidenceSourceClass.ExternalVisiblePixels)]
    [InlineData(EvidenceSourceClass.ScreenshotFilename)]
    [InlineData(EvidenceSourceClass.PairedDeviceAction)]
    [InlineData(EvidenceSourceClass.Unknown)]
    public void AnAllowedAggregateCannotHideDisallowedEvidence(EvidenceSourceClass hidden)
    {
        var direct = Model(EvidenceSourceClass.HistoricalAggregate, Direct(hidden));
        var nested = Model(EvidenceSourceClass.HistoricalAggregate, Model(EvidenceSourceClass.HistoricalAggregate, Direct(hidden)));
        var derived = Model(EvidenceSourceClass.HistoricalAggregate, Derived(Direct(EvidenceSourceClass.PublicStructuredData)));

        Assert.Throws<ArgumentException>(() => Input(IntelligenceInputKind.HistoricalAggregate, direct));
        Assert.Throws<ArgumentException>(() => Input(IntelligenceInputKind.HistoricalAggregate, nested));
        Assert.Throws<ArgumentException>(() => Input(IntelligenceInputKind.HistoricalAggregate, derived));

        var allowed = Input(
            IntelligenceInputKind.HistoricalAggregate,
            Model(EvidenceSourceClass.HistoricalAggregate, Direct(EvidenceSourceClass.GameWrittenLog), Direct(EvidenceSourceClass.CuratedData)));
        Assert.Equal(allowed.Provenance, Estimate(allowed).Estimate.Provenance.Inputs.Single());
    }

    [Theory]
    [InlineData(EvidenceSourceClass.GameWrittenScreenshot)]
    [InlineData(EvidenceSourceClass.PairedDeviceAction)]
    public void TheValueCannotNameInputsTheListDoesNotAdvertise(EvidenceSourceClass hidden)
    {
        var listed = Input();
        var log = Input(IntelligenceInputKind.PrivateLocalFeedback, Direct(EvidenceSourceClass.GameWrittenLog), "own-raid-log");

        // A disallowed input in the value's own lineage, beside an allowlisted list.
        Assert.Throws<ArgumentException>(() => Build(Model(EvidenceSourceClass.ModelledEstimate, Direct(hidden)), listed));
        Assert.Throws<ArgumentException>(() => Build(Model(EvidenceSourceClass.ModelledEstimate, listed.Provenance, Direct(hidden)), listed));
        Assert.Throws<ArgumentException>(() => Build(
            Model(EvidenceSourceClass.ModelledEstimate, Model(EvidenceSourceClass.HistoricalAggregate, Direct(hidden))),
            listed));

        // A list naming more, fewer, or reordered inputs than the value.
        Assert.Throws<ArgumentException>(() => Build(Model(EvidenceSourceClass.ModelledEstimate), listed));
        Assert.Throws<ArgumentException>(() => Build(Model(EvidenceSourceClass.ModelledEstimate, listed.Provenance), listed, log));
        Assert.Throws<ArgumentException>(() => Build(Model(EvidenceSourceClass.ModelledEstimate, log.Provenance, listed.Provenance), listed, log));

        // A candidate carries its own lineage and is held to the same list.
        Assert.Throws<ArgumentException>(() => Build(
            Model(EvidenceSourceClass.ModelledEstimate, listed.Provenance),
            [new EvidenceCandidate<EncounterLikelihood>(
                "high", "High", Likelihood, Model(EvidenceSourceClass.ModelledEstimate, Direct(hidden)))],
            listed));

        Assert.NotNull(Build(Model(EvidenceSourceClass.ModelledEstimate, listed.Provenance, log.Provenance), listed, log));
    }

    [Fact]
    public void EachInputIsNamedOnceAndTheListIsBounded()
    {
        var first = Input();
        var sameId = Input(IntelligenceInputKind.CuratedKnowledge, Direct(EvidenceSourceClass.CuratedData));
        var sameProvenance = Input(provenance: first.Provenance, evidenceId: "public-map-topology-copy");

        Assert.Throws<ArgumentException>(() => Build(
            Model(EvidenceSourceClass.ModelledEstimate, first.Provenance, sameId.Provenance), first, sameId));
        Assert.Throws<ArgumentException>(() => Build(
            Model(EvidenceSourceClass.ModelledEstimate, first.Provenance, first.Provenance), first, sameProvenance));

        var oversized = Enumerable.Range(0, EvidenceProvenance.MaxInputCount + 1)
            .Select(index => Input(provenance: Direct(EvidenceSourceClass.PublicStructuredData, identifier: $"fixture://catalog/{index}"), evidenceId: $"input-{index}"))
            .ToArray();
        Assert.Throws<ArgumentException>(() => Build(Model(EvidenceSourceClass.ModelledEstimate, oversized[0].Provenance), oversized));
    }

    [Theory]
    [InlineData(EvidenceSourceClass.GameWrittenScreenshot)]
    [InlineData(EvidenceSourceClass.PairedDeviceAction)]
    public void HostileJsonCannotHideEvidenceBeneathTheAdvertisedList(EvidenceSourceClass hidden)
    {
        var hiddenNode = JsonSerializer.SerializeToNode(Direct(hidden), JsonOptions)!;

        // The value's lineage swapped while the wrapper list still advertises public data.
        var swapped = EstimateNode();
        swapped["estimate"]!["provenance"]!["inputs"]![0] = hiddenNode.DeepClone();
        AssertRejected<ModelledIntelligence<EncounterLikelihood>>(swapped.ToJsonString());

        // The value's lineage emptied, so only the advertised list would describe it.
        var emptied = EstimateNode();
        emptied["estimate"]!["provenance"]!["inputs"] = new JsonArray();
        AssertRejected<ModelledIntelligence<EncounterLikelihood>>(emptied.ToJsonString());

        // An extra entry appended to the value's lineage only.
        var appended = EstimateNode();
        appended["estimate"]!["provenance"]!["inputs"]!.AsArray().Add(hiddenNode.DeepClone());
        AssertRejected<ModelledIntelligence<EncounterLikelihood>>(appended.ToJsonString());

        // Both copies agree, but the evidence sits beneath an allowed historical aggregate.
        var aggregate = JsonSerializer.SerializeToNode(Model(EvidenceSourceClass.HistoricalAggregate), JsonOptions)!;
        aggregate["inputs"] = new JsonArray(hiddenNode.DeepClone());
        var beneath = EstimateNode();
        beneath["inputs"]![0]!["kind"] = nameof(IntelligenceInputKind.HistoricalAggregate);
        beneath["inputs"]![0]!["provenance"] = aggregate.DeepClone();
        beneath["estimate"]!["provenance"]!["inputs"]![0] = aggregate.DeepClone();
        AssertRejected<ModelledIntelligence<EncounterLikelihood>>(beneath.ToJsonString());
    }

    [Fact]
    public void InputListsCannotBeChangedAfterValidation()
    {
        var inputs = new[] { Input() };
        var lineage = new[] { inputs[0].Provenance };
        var estimate = Build(Model(EvidenceSourceClass.ModelledEstimate, lineage), inputs);
        var original = inputs[0];

        inputs[0] = Input(IntelligenceInputKind.CuratedKnowledge, Direct(EvidenceSourceClass.CuratedData));
        lineage[0] = inputs[0].Provenance;

        Assert.Same(original, estimate.Inputs.Single());
        Assert.Equal(original.Provenance, estimate.Estimate.Provenance.Inputs.Single());
        Assert.False(estimate.Inputs is IntelligenceInputReference[]);
        Assert.Throws<NotSupportedException>(() => ((IList<IntelligenceInputReference>)estimate.Inputs)[0] = inputs[0]);
        Assert.Throws<NotSupportedException>(() => ((ICollection<IntelligenceInputReference>)estimate.Inputs).Add(inputs[0]));
        Assert.Throws<NotSupportedException>(() => ((IList<EvidenceProvenance>)estimate.Estimate.Provenance.Inputs)[0] = lineage[0]);
    }

    [Fact]
    public void EnvelopesAcceptOnlyAllowlistedIntelligencePayloads()
    {
        Assert.Throws<ArgumentException>(() => new ModelledIntelligence<EnemyPositionGuess>(
            "enemy",
            V2ContractTestData.Complete(
                "enemy",
                new EnemyPositionGuess(1, 2),
                Model(EvidenceSourceClass.ModelledEstimate, Input().Provenance)),
            [Input()],
            "Not allowed"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncounterLikelihood("customs", "dorms", RaidPhase.Mid, 1.2));
    }

    private static ModelledIntelligence<EncounterLikelihood> Estimate(params IntelligenceInputReference[] inputs) =>
        Build(Model(EvidenceSourceClass.ModelledEstimate, inputs.Select(input => input.Provenance).ToArray()), inputs);

    private static ModelledIntelligence<EncounterLikelihood> Estimate() => Estimate(Input());

    private static ModelledIntelligence<EncounterLikelihood> Build(
        EvidenceProvenance provenance,
        params IntelligenceInputReference[] inputs) =>
        Build(provenance, [], inputs);

    private static ModelledIntelligence<EncounterLikelihood> Build(
        EvidenceProvenance provenance,
        IReadOnlyList<EvidenceCandidate<EncounterLikelihood>> candidates,
        params IntelligenceInputReference[] inputs) => new(
        "traffic-customs-mid",
        V2ContractTestData.Complete("traffic.encounter", Likelihood, provenance, candidates: candidates),
        inputs,
        "Static route pressure estimate");

    private static JsonNode EstimateNode() => JsonSerializer.SerializeToNode(Estimate(), JsonOptions)!;

    private static IntelligenceInputReference Input(
        IntelligenceInputKind kind = IntelligenceInputKind.StaticMapData,
        EvidenceProvenance? provenance = null,
        string evidenceId = "public-map-topology") => new(
        evidenceId,
        kind,
        provenance ?? V2ContractTestData.PublicDataProvenance());

    private static EvidenceProvenance Model(EvidenceSourceClass sourceClass, params EvidenceProvenance[] inputs) => new(
        sourceClass,
        "fixture://traffic-model",
        V2ContractTestData.ObservedUtc,
        new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.72, "calibration-2026-09"),
        new ProducerIdentity("fixture-model", "2.0", "traffic-model-4"),
        V2ContractTestData.ObservedUtc.AddDays(-1),
        V2ContractTestData.ObservedUtc.AddHours(-1),
        new EvidenceCoverage(240, 0.80, "Two hundred forty historical route samples"),
        inputs: inputs);

    private static EvidenceProvenance Derived(params EvidenceProvenance[] inputs) => new(
        EvidenceSourceClass.DerivedCalculation,
        "fixture://derived",
        PastUtc,
        EvidenceConfidence.Unscored,
        new ProducerIdentity("fixture-derived", "2"),
        generatedUtc: PastUtc,
        inputs: inputs);

    private static EvidenceProvenance Direct(
        EvidenceSourceClass sourceClass,
        DateTimeOffset? observedUtc = null,
        string? identifier = null) => new(
        sourceClass,
        identifier ?? $"fixture://{sourceClass}",
        observedUtc ?? PastUtc.AddHours(-1),
        EvidenceConfidence.Certain,
        new ProducerIdentity("fixture-direct", "2"));

    private static void AssertRejected<T>(string json)
    {
        var failure = Record.Exception(() => JsonSerializer.Deserialize<T>(json, JsonOptions));

        Assert.NotNull(failure);
        Assert.True(
            failure is JsonException || failure.GetBaseException() is ArgumentException,
            failure.ToString());
    }

    private sealed record EnemyPositionGuess(double X, double Y) : IIntelligencePayload;
}
