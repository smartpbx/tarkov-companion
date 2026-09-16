using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Strategy.Data;
using TarkovCompanion.Infrastructure.Strategy.Datasets;

namespace TarkovCompanion.UnitTests.StrategyRuntime;

public sealed class HistoricalTrafficRuntimeServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly TrafficCompatibilityScope Scope = new(
        "customs",
        "0.16.9",
        ProfileGameMode.Pvp,
        "wipe-2026-2",
        "all-players");
    private static readonly TrafficPartitionPolicy Policy = new(
        "traffic-split-v1",
        "runtime-fixture-salt",
        8_000,
        1_000,
        1_000);

    [Fact]
    public void ObservedClockIsAgedAndHeldOutRowsCannotInfluenceTheShownPrediction()
    {
        var publication = Publication(
        [
            Input("train-contact", TrafficDataPartition.Train, TrafficObservationClass.Contact, 10),
            Input("tune-no-contact", TrafficDataPartition.Tune, TrafficObservationClass.NoContact, 20),
            Input("held-contact", TrafficDataPartition.HeldOut, TrafficObservationClass.Contact, 900),
        ]);
        var request = Request(
            new RaidClockReading(TimeSpan.FromMinutes(30), RaidClockBasis.ObservedOnExtractScreen, Now),
            Now.AddMinutes(10));

        var result = new HistoricalTrafficRuntimeService().Evaluate(publication, request);

        Assert.Equal(HistoricalTrafficRuntimeStatus.Partial, result.Status);
        Assert.Equal(RaidPhase.Mid, result.Phase);
        Assert.Equal(1_200, result.ElapsedSeconds);
        Assert.Equal(1d / 3d, Assert.Single(result.Encounters).Estimate.Value!.Probability, 6);
        Assert.Equal(30, result.Encounters[0].Estimate.Provenance.Coverage!.SampleSize);
        Assert.Equal("traffic-model-v1", result.Receipt!.ModelVersion);
        Assert.DoesNotContain("live", result.Encounters[0].IntelligenceId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownRowsRemainDistinctFromNoContactAndAllNoContactStillProducesZeroPressure()
    {
        var publication = Publication(
        [
            Input("train-unknown", TrafficDataPartition.Train, TrafficObservationClass.Unknown, 100),
            Input("tune-no-contact", TrafficDataPartition.Tune, TrafficObservationClass.NoContact, 20),
            Input("held-contact", TrafficDataPartition.HeldOut, TrafficObservationClass.Contact, 30),
        ]);

        var result = new HistoricalTrafficRuntimeService().Evaluate(
            publication,
            Request(new RaidClockReading(TimeSpan.FromMinutes(20), RaidClockBasis.CountedFromRaidStart, Now), Now));

        Assert.True(result.HasPredictions);
        Assert.Equal(0, Assert.Single(result.Zones).Estimate.Value!.RelativeIntensity);
        Assert.Equal(0, Assert.Single(result.Encounters).Estimate.Value!.Probability);
        Assert.Contains("100 explicit unknown", result.Zones[0].Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingClockOrExactCompatibilityFailsWithoutGeneralizing()
    {
        var publication = Publication(
        [
            Input("train-contact", TrafficDataPartition.Train, TrafficObservationClass.Contact, 10),
            Input("tune-no-contact", TrafficDataPartition.Tune, TrafficObservationClass.NoContact, 20),
            Input("held-contact", TrafficDataPartition.HeldOut, TrafficObservationClass.Contact, 30),
        ]);
        var service = new HistoricalTrafficRuntimeService();

        var noClock = service.Evaluate(publication, Request(null, Now));
        var incompatibleScope = new TrafficCompatibilityScope(
            Scope.MapId,
            Scope.GameVersion,
            Scope.GameMode,
            "another-wipe",
            Scope.CohortId);
        var otherWipe = service.Evaluate(
            publication,
            new HistoricalTrafficRuntimeRequest(
                incompatibleScope,
                new RaidClockReading(TimeSpan.FromMinutes(20), RaidClockBasis.CountedFromRaidStart, Now),
                TimeSpan.FromMinutes(40),
                Now,
                TimeSpan.FromDays(30)));

        Assert.Equal(HistoricalTrafficRuntimeStatus.RaidPhaseUnknown, noClock.Status);
        Assert.False(noClock.HasPredictions);
        Assert.Equal(HistoricalTrafficRuntimeStatus.IncompatibleModel, otherWipe.Status);
        Assert.False(otherWipe.HasPredictions);
    }

    [Fact]
    public void PredictionReceiptChangesWithTheExactShownInstantAndRetainsExactValues()
    {
        var publication = Publication(
        [
            Input("train-contact", TrafficDataPartition.Train, TrafficObservationClass.Contact, 10),
            Input("tune-no-contact", TrafficDataPartition.Tune, TrafficObservationClass.NoContact, 20),
            Input("held-contact", TrafficDataPartition.HeldOut, TrafficObservationClass.Contact, 30),
        ]);
        var service = new HistoricalTrafficRuntimeService();
        var clock = new RaidClockReading(TimeSpan.FromMinutes(20), RaidClockBasis.CountedFromRaidStart, Now);

        var first = service.Evaluate(publication, Request(clock, Now));
        var same = service.Evaluate(publication, Request(clock, Now));
        var later = service.Evaluate(publication, Request(clock, Now.AddSeconds(1)));

        Assert.Equal(first.Receipt!.PredictionId, same.Receipt!.PredictionId);
        Assert.NotEqual(first.Receipt.PredictionId, later.Receipt!.PredictionId);
        Assert.Equal(first.Zones, first.Receipt.Zones);
        Assert.Equal(first.Corridors, first.Receipt.Corridors);
        Assert.Equal(first.Encounters, first.Receipt.Encounters);
    }

    [Fact]
    public void ModelOlderThanTheConfiguredWindowRemainsUsableButIsExplicitlyPartialAndStale()
    {
        var publication = Publication(
        [
            Input("train-contact", TrafficDataPartition.Train, TrafficObservationClass.Contact, 10),
            Input("tune-no-contact", TrafficDataPartition.Tune, TrafficObservationClass.NoContact, 20),
            Input("held-contact", TrafficDataPartition.HeldOut, TrafficObservationClass.Contact, 30),
        ]);
        var request = new HistoricalTrafficRuntimeRequest(
            Scope,
            new RaidClockReading(TimeSpan.FromMinutes(20), RaidClockBasis.CountedFromRaidStart, Now),
            TimeSpan.FromMinutes(40),
            Now,
            TimeSpan.FromHours(12));

        var result = new HistoricalTrafficRuntimeService().Evaluate(publication, request);

        Assert.Equal(HistoricalTrafficRuntimeStatus.Partial, result.Status);
        Assert.All(result.Zones, value => Assert.Equal(FreshnessState.Stale, value.Estimate.Status.Freshness));
        Assert.All(result.Encounters, value => Assert.Equal(FreshnessState.Stale, value.Estimate.Status.Freshness));
        Assert.Contains("older", result.Guidance, StringComparison.Ordinal);
    }

    private static HistoricalTrafficRuntimeRequest Request(RaidClockReading? clock, DateTimeOffset evaluatedUtc) => new(
        Scope,
        clock,
        TimeSpan.FromMinutes(40),
        evaluatedUtc,
        TimeSpan.FromDays(30));

    private static TrafficModelPublication Publication(IReadOnlyList<TrafficAggregateInput> records)
    {
        var provenance = Historical();
        var source = new TrafficDatasetSource(
            "fixture-aggregate",
            "Fixture historical aggregate",
            TrafficDataSourceKind.HistoricalAggregate,
            TrafficDatasetVisibility.DistributableAggregate,
            provenance,
            "CC-BY-4.0",
            TrafficConsentBasis.ReviewedTermsOrConsent,
            "Synthetic bounded aggregate without identity or exact coordinates",
            [
                TrafficDataAllowedUse.HistoricalLayer,
                TrafficDataAllowedUse.RuntimeInference,
                TrafficDataAllowedUse.RoutePlanning,
                TrafficDataAllowedUse.ModelTraining,
                TrafficDataAllowedUse.ModelTuning,
                TrafficDataAllowedUse.HeldOutEvaluation,
                TrafficDataAllowedUse.DistributableSnapshot,
            ],
            [Scope],
            Now.AddDays(-1),
            "fixture://traffic-aggregate");
        var sampleSize = records.Sum(record => record.SampleCount);
        var build = TrafficModelBuilder.Build(
            new TrafficModelBuildRequest(
                "runtime-build-v1",
                "traffic-dataset",
                "dataset-v1",
                TrafficDatasetVisibility.DistributableAggregate,
                Now.AddDays(-2),
                Now.AddDays(-1),
                "traffic-transform-v1",
                "traffic-runtime",
                "traffic-model-v1",
                "json-v1",
                "runtime-calibration-v1",
                new EvidenceConfidence(
                    EvidenceConfidenceKind.CalibratedEstimate,
                    0.8,
                    "runtime-calibration-v1"),
                new EvidenceCoverage(sampleSize, 0.75, "Runtime fixture coverage"),
                Policy,
                [source],
                records,
                [new TrafficCoverageGap(Scope, "interior-gap", "One interior is outside fixture coverage.")]),
            "{}"u8.ToArray());
        var signature = new TrafficArtifactSignature(
            TrafficSignatureAlgorithm.EcdsaP256Sha256,
            "fixture-key",
            Digest("manifest"),
            Convert.ToBase64String(new byte[64]),
            Now);
        return new TrafficModelPublication(build.Dataset, build.Report, build.Manifest, signature);
    }

    private static TrafficAggregateInput Input(
        string id,
        TrafficDataPartition partition,
        TrafficObservationClass observation,
        long sampleCount) => new(
        Digest($"record-{id}"),
        "fixture-aggregate",
        GroupFor(partition, id),
        Scope,
        new TrafficSpatialReference(Scope.MapId, regionId: "dorms"),
        new TrafficPhaseWindow(RaidPhase.Mid, 600, 1_800),
        observation,
        sampleCount,
        Historical());

    private static EvidenceProvenance Historical()
    {
        var publicInput = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "fixture://public-map",
            Now.AddDays(-4),
            EvidenceConfidence.Certain,
            new ProducerIdentity("fixture-map", "1"));
        return new EvidenceProvenance(
            EvidenceSourceClass.HistoricalAggregate,
            "fixture://historical-aggregate",
            Now.AddDays(-2),
            new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.8, "runtime-calibration-v1"),
            new ProducerIdentity("fixture-builder", "1", "traffic-model-v1"),
            Now.AddDays(-3),
            Now.AddDays(-2),
            new EvidenceCoverage(960, 0.75, "Synthetic historical aggregate"),
            inputs: [publicInput]);
    }

    private static string GroupFor(TrafficDataPartition partition, string discriminator)
    {
        for (var index = 0; index < 100_000; index++)
        {
            var candidate = Digest($"group-{discriminator}-{index}");
            if (TrafficPartitioner.Assign(candidate, Policy) == partition)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not construct a deterministic partition fixture.");
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
