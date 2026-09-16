using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Strategy.Data;

namespace TarkovCompanion.UnitTests.StrategyData;

public sealed class TrafficDataContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);
    private static readonly TrafficCompatibilityScope Scope = new("customs", "0.16.9", ProfileGameMode.Pvp, "wipe-2026-2", "all-players");
    private static readonly TrafficSpatialReference Location = new("customs", regionId: "dorms");
    private static readonly TrafficPhaseWindow Window = new(RaidPhase.Mid, 900, 1_800);
    private static readonly TrafficPartitionPolicy Policy = new("traffic-split-v1", "fixture-salt", 8_000, 1_000, 1_000);
    private static readonly JsonSerializerOptions Json = TrafficDataJson.Options;

    [Fact]
    public void FeedbackIsExplicitLocalBoundedAndPredictionAttributed()
    {
        var feedback = Feedback(TrafficObservationClass.Unknown);

        Assert.Equal(TrafficObservationClass.Unknown, feedback.Observation);
        Assert.Equal("prediction-17", feedback.EvaluatedPredictionId);
        Assert.Equal("traffic-model-4", feedback.EvaluatedModelVersion);
        Assert.Null(typeof(HistoricalTrafficFeedback).GetProperty("PlayerId"));
        Assert.Null(typeof(HistoricalTrafficFeedback).GetProperty("Profile"));
        Assert.Null(typeof(HistoricalTrafficFeedback).GetProperty("CurrentRaid"));
        Assert.Null(typeof(TrafficSpatialReference).GetProperty("X"));
        Assert.Null(typeof(TrafficSpatialReference).GetProperty("Y"));

        Assert.Throws<ArgumentException>(() => new HistoricalTrafficFeedback(
            Guid.NewGuid(),
            TrafficDataBounds.LocalFeedbackSourceId,
            Consent(),
            Scope,
            Location,
            Window,
            TrafficObservationClass.Contact,
            Direct(EvidenceSourceClass.GameWrittenLog),
            Now,
            "prediction-17",
            "traffic-model-4"));
    }

    [Fact]
    public void PrivateFeedbackRequiresOptInAndCannotAuthorizeDistribution()
    {
        Assert.Throws<ArgumentException>(() => Source(
            TrafficDataSourceKind.PrivateLocalFeedback,
            UserFeedback(),
            TrafficConsentBasis.ReviewedTermsOrConsent,
            [TrafficDataAllowedUse.AggregateContribution]));

        Assert.Throws<ArgumentException>(() => Source(
            TrafficDataSourceKind.PrivateLocalFeedback,
            UserFeedback(),
            TrafficConsentBasis.ExplicitLocalOptIn,
            [TrafficDataAllowedUse.AggregateContribution, TrafficDataAllowedUse.DistributableSnapshot]));

        var local = Source(
            TrafficDataSourceKind.PrivateLocalFeedback,
            UserFeedback(),
            TrafficConsentBasis.ExplicitLocalOptIn,
            [TrafficDataAllowedUse.AggregateContribution]);

        Assert.Equal(TrafficConsentBasis.ExplicitLocalOptIn, local.ConsentBasis);
    }

    [Fact]
    public void FeedbackWireSchemaRequiresConsentAndRejectsIdentityFields()
    {
        var node = JsonSerializer.SerializeToNode(Feedback(TrafficObservationClass.NoContact), Json)!;
        node.AsObject().Remove("consent");
        AssertRejected<HistoricalTrafficFeedback>(node.ToJsonString());

        node = JsonSerializer.SerializeToNode(Feedback(TrafficObservationClass.Unknown), Json)!;
        node["profileId"] = "prohibited-identity";
        AssertRejected<HistoricalTrafficFeedback>(node.ToJsonString());
    }

    [Fact]
    public void FeedbackHistoryIsAppendOnlyCorrectableAndRevocable()
    {
        var first = Feedback(TrafficObservationClass.Contact);
        var submitted = new TrafficFeedbackEvent(
            Guid.NewGuid(), first.FeedbackId, 1, TrafficFeedbackEventKind.Submitted, Now, TrafficFeedbackActor.LocalUser, first);
        var correctedValue = Feedback(TrafficObservationClass.Avoided, first.FeedbackId);
        var corrected = new TrafficFeedbackEvent(
            Guid.NewGuid(), first.FeedbackId, 2, TrafficFeedbackEventKind.Corrected, Now.AddMinutes(1),
            TrafficFeedbackActor.LocalUser, correctedValue, submitted.EventId);
        var revoked = new TrafficFeedbackEvent(
            Guid.NewGuid(), first.FeedbackId, 3, TrafficFeedbackEventKind.Revoked, Now.AddMinutes(2),
            TrafficFeedbackActor.LocalConsentPolicy, null, corrected.EventId);
        var history = new TrafficFeedbackHistory(first.FeedbackId, [submitted, corrected, revoked]);

        Assert.Equal(TrafficObservationClass.Contact, submitted.Value!.Observation);
        Assert.Equal(TrafficObservationClass.Avoided, corrected.Value!.Observation);
        Assert.Null(revoked.Value);
        Assert.True(history.IsRevoked);
        Assert.Null(history.Current);
        Assert.Throws<ArgumentException>(() => new TrafficFeedbackEvent(
            Guid.NewGuid(), first.FeedbackId, 1, TrafficFeedbackEventKind.Revoked, Now, TrafficFeedbackActor.LocalUser, null));
        Assert.Throws<ArgumentException>(() => new TrafficFeedbackHistory(first.FeedbackId, [submitted, revoked]));
    }

    [Fact]
    public void DatasetRequiresThreePartitionsAndReconciledCounts()
    {
        var dataset = Dataset();

        Assert.Equal(3, dataset.Partitions.Count);
        Assert.Equal(60, dataset.Partitions.Sum(partition => partition.SampleCount));
        Assert.Equal(3, dataset.Records.Count);

        Assert.Throws<ArgumentException>(() => Dataset(partitions: Partitions().Take(2).ToArray()));
        Assert.Throws<ArgumentException>(() => Dataset(partitions:
        [
            new(TrafficDataPartition.Train, 1, 999, Hash('1')),
            .. Partitions().Skip(1),
        ]));
    }

    [Fact]
    public void LeakageGroupCannotCrossTrainTuneOrHeldOut()
    {
        var records = Records();
        records[1] = records[1] with { };
        records[1] = new TrafficAggregateRecord(
            records[1].RecordId,
            records[1].SourceId,
            records[0].PartitionGroupId,
            records[1].Partition,
            records[1].Scope,
            records[1].Location,
            records[1].Window,
            records[1].Observation,
            records[1].SampleCount,
            records[1].Provenance);

        Assert.Throws<ArgumentException>(() => Dataset(records: records));
    }

    [Fact]
    public void PartitionAssignmentIsStableForTheWholeLeakageGroup()
    {
        var group = GroupFor(TrafficDataPartition.HeldOut, "stable");

        Assert.Equal(TrafficDataPartition.HeldOut, TrafficPartitioner.Assign(group, Policy));
        Assert.Equal(
            TrafficPartitioner.Assign(group, Policy),
            TrafficPartitioner.Assign(group, new TrafficPartitionPolicy("traffic-split-v1", "fixture-salt", 8_000, 1_000, 1_000)));
    }

    [Fact]
    public void PartitionSharesCannotWrapThroughIntOverflow()
    {
        Assert.Throws<ArgumentException>(() => new TrafficPartitionPolicy(
            "overflow-policy",
            "fixture-salt",
            int.MaxValue,
            int.MaxValue,
            10_002));
    }

    [Fact]
    public void CompatibilityDoesNotAcceptUnknownOrWildcardCells()
    {
        Assert.Throws<ArgumentException>(() => new TrafficCompatibilityScope(
            "customs", "unknown", ProfileGameMode.Pvp, "wipe-2026-2", "all-players"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrafficCompatibilityScope(
            "customs", "0.16.9", ProfileGameMode.Unknown, "wipe-2026-2", "all-players"));
    }

    [Fact]
    public void DistributableDatasetRejectsRawPrivateSourceRows()
    {
        var privateSource = Source(
            TrafficDataSourceKind.PrivateLocalFeedback,
            UserFeedback(),
            TrafficConsentBasis.ExplicitLocalOptIn,
            [TrafficDataAllowedUse.AggregateContribution]);
        var records = Records(privateSource.SourceId);

        Assert.Throws<ArgumentException>(() => Dataset(
            visibility: TrafficDatasetVisibility.DistributableAggregate,
            sources: [privateSource],
            records: records));

        Assert.Throws<ArgumentException>(() => Dataset(
            visibility: TrafficDatasetVisibility.PrivateLocal,
            sources: [privateSource],
            records: records));
    }

    [Fact]
    public void DistributableCellsRequireAPrivacySampleFloor()
    {
        var records = Records();
        records[0] = Record("train", TrafficDataPartition.Train, TrafficObservationClass.Contact, 1);
        var partitions = Partitions();
        partitions[0] = new TrafficPartitionDigest(TrafficDataPartition.Train, 1, 1, Hash('1'));

        Assert.Throws<ArgumentException>(() => Dataset(records: records, partitions: partitions));
    }

    [Fact]
    public void DatasetRoundTripRetainsCompatibilityEvidenceAndUnknownClass()
    {
        var dataset = Dataset(records:
        [
            Record("train", TrafficDataPartition.Train, TrafficObservationClass.Unknown, 10),
            Record("tune", TrafficDataPartition.Tune, TrafficObservationClass.NoContact, 20),
            Record("held", TrafficDataPartition.HeldOut, TrafficObservationClass.Contact, 30),
        ]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dataset, Json);
        var roundTrip = JsonSerializer.Deserialize<GovernedTrafficDataset>(bytes, Json)!;

        Assert.Equal(dataset.ContentSha256, roundTrip.ContentSha256);
        Assert.Equal(ProfileGameMode.Pvp, roundTrip.Records[0].Scope.GameMode);
        Assert.Equal(TrafficObservationClass.Unknown, roundTrip.Records[0].Observation);
        Assert.Equal(dataset.Records[0].Provenance, roundTrip.Records[0].Provenance);
        Assert.Equal("dorms", roundTrip.Records[0].Location.RegionId);
        Assert.Null(roundTrip.Records[0].Location.CorridorId);
    }

    [Fact]
    public void HostileJsonCannotAddLiveOrIdentityFields()
    {
        var node = JsonSerializer.SerializeToNode(Dataset(), Json)!;
        node["records"]![0]!["playerId"] = "somebody-else";
        node["records"]![0]!["currentRaid"] = true;
        node["records"]![0]!["x"] = 123.45;

        AssertRejected<GovernedTrafficDataset>(node.ToJsonString());
    }

    [Fact]
    public void CallerCollectionsCannotMutateTheValidatedDataset()
    {
        var records = Records();
        var sources = new[] { AggregateSource() };
        var partitions = Partitions();
        var dataset = Dataset(sources: sources, records: records, partitions: partitions);
        var original = dataset.Records[0];

        records[0] = Record("replacement", TrafficDataPartition.Train, TrafficObservationClass.Contact, 10);
        sources[0] = Source(
            TrafficDataSourceKind.StaticPublicFacts,
            Direct(EvidenceSourceClass.PublicStructuredData),
            TrafficConsentBasis.PublicDataTerms,
            [TrafficDataAllowedUse.RuntimeInference, TrafficDataAllowedUse.DistributableSnapshot]);
        partitions[0] = new(TrafficDataPartition.Train, 0, 0, Hash('9'));

        Assert.Same(original, dataset.Records[0]);
        Assert.False(dataset.Records is TrafficAggregateRecord[]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<TrafficAggregateRecord>)dataset.Records)[0] = records[0]);
    }

    [Fact]
    public void RuntimeArtifactPinsDatasetModelCalibrationCoverageAndEveryPartition()
    {
        var manifest = new TrafficModelArtifactManifest(
            TrafficDataBounds.CurrentSchemaVersion,
            "traffic-runtime",
            "traffic-model-4",
            "traffic-dataset",
            "2026.09.16",
            Hash('a'),
            Hash('c'),
            Hash('b'),
            "json-v1",
            "traffic-transform-2",
            "held-out-calibration-2026-09",
            Now.AddDays(-2),
            Now.AddHours(-12),
            new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.75, "held-out-calibration-2026-09"),
            new EvidenceCoverage(60, 0.75, "Three governed partitions"),
            [Scope],
            [new TrafficCoverageGap(Scope, "factory-interior-missing", "Interior coverage is unavailable.")],
            Partitions());

        Assert.Equal("traffic-model-4", manifest.ModelVersion);
        Assert.Equal(0.75, manifest.Confidence.Score!.Value, 6);
        Assert.Equal(3, manifest.Partitions.Count);
        Assert.Throws<ArgumentException>(() => new TrafficModelArtifactManifest(
            manifest.SchemaVersion,
            manifest.ModelId,
            manifest.ModelVersion,
            manifest.DatasetId,
            manifest.DatasetVersion,
            manifest.DatasetSha256,
            manifest.BuildReportSha256,
            manifest.ArtifactSha256,
            manifest.ArtifactFormat,
            manifest.TransformVersion,
            manifest.CalibrationReference,
            manifest.DataThroughUtc,
            manifest.GeneratedUtc,
            manifest.Confidence,
            manifest.Coverage,
            manifest.CompatibilityScopes,
            manifest.CoverageGaps,
            Partitions().Take(2).ToArray()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ABCDEF")]
    [InlineData("ABCDEF")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void DigestsMustBeCanonicalLowercaseSha256(string digest)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TrafficPartitionDigest(
            TrafficDataPartition.Train, 1, 1, digest));
    }

    private static GovernedTrafficDataset Dataset(
        TrafficDatasetVisibility visibility = TrafficDatasetVisibility.DistributableAggregate,
        TrafficDatasetSource[]? sources = null,
        TrafficAggregateRecord[]? records = null,
        TrafficPartitionDigest[]? partitions = null) => new(
        TrafficDataBounds.CurrentSchemaVersion,
        "traffic-dataset",
        "2026.09.16",
        visibility,
        Now.AddDays(-2),
        Now.AddHours(1),
        "traffic-transform-2",
        Hash('a'),
        Policy,
        sources ?? [AggregateSource()],
        records ?? Records(),
        partitions ?? Partitions(),
        [new TrafficCoverageGap(Scope, "factory-interior-missing", "Factory interior coverage is not in this fixture.")]);

    private static TrafficDatasetSource AggregateSource() => Source(
        TrafficDataSourceKind.HistoricalAggregate,
        Historical(),
        TrafficConsentBasis.ReviewedTermsOrConsent,
        [
            TrafficDataAllowedUse.HistoricalLayer,
            TrafficDataAllowedUse.RuntimeInference,
            TrafficDataAllowedUse.RoutePlanning,
            TrafficDataAllowedUse.ModelTraining,
            TrafficDataAllowedUse.ModelTuning,
            TrafficDataAllowedUse.HeldOutEvaluation,
            TrafficDataAllowedUse.DistributableSnapshot,
        ]);

    private static TrafficDatasetSource Source(
        TrafficDataSourceKind kind,
        EvidenceProvenance provenance,
        TrafficConsentBasis consent,
        IReadOnlyList<TrafficDataAllowedUse> uses) => new(
        kind == TrafficDataSourceKind.PrivateLocalFeedback ? TrafficDataBounds.LocalFeedbackSourceId : "aggregate-source",
        kind.ToString(),
        kind,
        kind == TrafficDataSourceKind.PrivateLocalFeedback
            ? TrafficDatasetVisibility.PrivateLocal
            : TrafficDatasetVisibility.DistributableAggregate,
        provenance,
        kind == TrafficDataSourceKind.PrivateLocalFeedback ? "private-local" : "CC-BY-4.0",
        consent,
        "Synthetic bounded fixture",
        uses,
        [Scope],
        Now,
        "fixture://traffic-source");

    private static TrafficAggregateRecord[] Records(string sourceId = "aggregate-source") =>
    [
        Record("train", TrafficDataPartition.Train, TrafficObservationClass.Contact, 10, sourceId),
        Record("tune", TrafficDataPartition.Tune, TrafficObservationClass.NoContact, 20, sourceId),
        Record("held", TrafficDataPartition.HeldOut, TrafficObservationClass.Avoided, 30, sourceId),
    ];

    private static TrafficAggregateRecord Record(
        string id,
        TrafficDataPartition partition,
        TrafficObservationClass observation,
        long sampleCount,
        string sourceId = "aggregate-source") => new(
        Digest($"record-{id}"),
        sourceId,
        GroupFor(partition, id),
        partition,
        Scope,
        Location,
        Window,
        observation,
        sampleCount,
        Historical());

    private static TrafficPartitionDigest[] Partitions() =>
    [
        new(TrafficDataPartition.Train, 1, 10, Hash('1')),
        new(TrafficDataPartition.Tune, 1, 20, Hash('2')),
        new(TrafficDataPartition.HeldOut, 1, 30, Hash('3')),
    ];

    private static HistoricalTrafficFeedback Feedback(
        TrafficObservationClass observation,
        Guid? feedbackId = null) => new(
        feedbackId ?? Guid.NewGuid(),
        TrafficDataBounds.LocalFeedbackSourceId,
        Consent(),
        Scope,
        Location,
        Window,
        observation,
        UserFeedback(),
        Now,
        "prediction-17",
        "traffic-model-4");

    private static TrafficContributionConsent Consent() => new(
        Guid.Parse("22222222-2222-4222-8222-222222222222"),
        "traffic-notice-v1",
        Now.AddDays(-4),
        [TrafficDataAllowedUse.AggregateContribution]);

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

    private static EvidenceProvenance Direct(EvidenceSourceClass sourceClass) => new(
        sourceClass,
        $"fixture://{sourceClass}",
        Now.AddDays(-3),
        EvidenceConfidence.Certain,
        new ProducerIdentity("traffic-fixture", "1"));

    private static EvidenceProvenance UserFeedback() => new(
        EvidenceSourceClass.UserEntered,
        TrafficDataBounds.LocalFeedbackSourceId,
        Now.AddDays(-3),
        EvidenceConfidence.Certain,
        new ProducerIdentity("traffic-feedback", "1"));

    private static EvidenceProvenance Historical()
    {
        var input = Direct(EvidenceSourceClass.PublicStructuredData);
        return new EvidenceProvenance(
            EvidenceSourceClass.HistoricalAggregate,
            "fixture://historical-aggregate",
            Now,
            new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.75, "held-out-calibration-2026-09"),
            new ProducerIdentity("traffic-builder", "1", "traffic-model-4"),
            Now.AddDays(-2),
            Now.AddDays(-1),
            new EvidenceCoverage(60, 0.75, "Synthetic governed sample"),
            inputs: [input]);
    }

    private static string Hash(char value) => new(value, 64);

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void AssertRejected<T>(string json)
    {
        var failure = Record.Exception(() => JsonSerializer.Deserialize<T>(json, Json));

        Assert.NotNull(failure);
        Assert.True(
            failure is JsonException || failure.GetBaseException() is ArgumentException,
            failure.ToString());
    }
}
