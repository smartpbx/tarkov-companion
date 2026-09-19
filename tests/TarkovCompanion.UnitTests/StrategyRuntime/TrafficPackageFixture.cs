using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Strategy.Data;
using TarkovCompanion.Infrastructure.Strategy.Datasets;

namespace TarkovCompanion.UnitTests.StrategyRuntime;

/// <summary>
/// A signed traffic package written the way tools/TrafficModelBuilder writes one, from synthetic
/// aggregates that name no player and no coordinate.
/// </summary>
internal static class TrafficPackageFixture
{
    public const string KeyId = "fixture-key";
    public const string GameVersion = "1.1.5.0.47242";
    public const string Wipe = "wipe-2026-2";

    public static readonly DateTimeOffset GeneratedUtc = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset DataThroughUtc = GeneratedUtc.AddDays(-2);

    private static readonly TrafficPartitionPolicy Policy = new("traffic-split-v1", "fixture-salt", 8_000, 1_000, 1_000);

    public static TrafficCompatibilityScope Scope(
        string map = "customs",
        string gameVersion = GameVersion,
        ProfileGameMode mode = ProfileGameMode.Pvp,
        string wipe = Wipe,
        string cohort = "all-players") => new(map, gameVersion, mode, wipe, cohort);

    public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The trusted-keys file body for <paramref name="key"/>.</summary>
    public static string TrustedKeysJson(ECDsa key) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [KeyId] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
        });

    public static TrafficModelPackageStreams Streams(ECDsa key, params TrafficCompatibilityScope[] scopes)
    {
        var files = Build(key, scopes);
        return new TrafficModelPackageStreams(
            new MemoryStream(files[0]),
            new MemoryStream(files[1]),
            new MemoryStream(files[2]),
            new MemoryStream(files[3]),
            new MemoryStream(files[4]));
    }

    /// <summary>Writes the five package files into <paramref name="directory"/>.</summary>
    public static void WritePackage(string directory, ECDsa key, params TrafficCompatibilityScope[] scopes)
    {
        Directory.CreateDirectory(directory);
        var files = Build(key, scopes);
        for (var index = 0; index < files.Length; index++)
        {
            File.WriteAllBytes(Path.Combine(directory, InstalledTrafficPublicationSource.PackageFileNames[index]), files[index]);
        }
    }

    private static byte[][] Build(ECDsa key, IReadOnlyList<TrafficCompatibilityScope> scopes)
    {
        var provenance = Historical();
        var source = new TrafficDatasetSource(
            "fixture-aggregate",
            "Fixture aggregate",
            TrafficDataSourceKind.HistoricalAggregate,
            TrafficDatasetVisibility.DistributableAggregate,
            provenance,
            "CC-BY-4.0",
            TrafficConsentBasis.ReviewedTermsOrConsent,
            "Synthetic aggregate with no identities or exact coordinates",
            [
                TrafficDataAllowedUse.HistoricalLayer,
                TrafficDataAllowedUse.RuntimeInference,
                TrafficDataAllowedUse.RoutePlanning,
                TrafficDataAllowedUse.ModelTraining,
                TrafficDataAllowedUse.ModelTuning,
                TrafficDataAllowedUse.HeldOutEvaluation,
                TrafficDataAllowedUse.DistributableSnapshot,
            ],
            scopes,
            GeneratedUtc,
            "fixture://aggregate");
        var records = scopes.SelectMany((scope, index) => new[]
        {
            Input($"held-{index}", TrafficDataPartition.HeldOut, 30, scope, provenance),
            Input($"train-{index}", TrafficDataPartition.Train, 10, scope, provenance),
            Input($"tune-{index}", TrafficDataPartition.Tune, 20, scope, provenance),
        }).ToArray();
        var build = TrafficModelBuilder.Build(
            new TrafficModelBuildRequest(
                "build-fixture",
                "traffic-dataset",
                "dataset-1",
                TrafficDatasetVisibility.DistributableAggregate,
                DataThroughUtc,
                GeneratedUtc,
                "traffic-transform-2",
                "traffic-runtime",
                "traffic-model-1",
                "json-v1",
                "held-out-calibration-2026-09",
                new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.75, "held-out-calibration-2026-09"),
                new EvidenceCoverage(records.Sum(record => record.SampleCount), 0.75, "Three governed partitions"),
                Policy,
                [source],
                records,
                [new TrafficCoverageGap(scopes[0], "factory-interior-missing", "Interior coverage is unavailable.")]),
            Encoding.UTF8.GetBytes("fixture-model-artifact"));
        var manifestHash = SHA256.HashData(build.ManifestJson);
        var signature = new TrafficArtifactSignature(
            TrafficSignatureAlgorithm.EcdsaP256Sha256,
            KeyId,
            Convert.ToHexStringLower(manifestHash),
            Convert.ToBase64String(key.SignHash(manifestHash, DSASignatureFormat.Rfc3279DerSequence)),
            build.Manifest.GeneratedUtc.AddMinutes(1));
        return
        [
            build.DatasetJson,
            build.ReportJson,
            build.ManifestJson,
            JsonSerializer.SerializeToUtf8Bytes(signature, TrafficDataJson.Options),
            build.Artifact,
        ];
    }

    private static TrafficAggregateInput Input(
        string id,
        TrafficDataPartition partition,
        long sampleCount,
        TrafficCompatibilityScope scope,
        EvidenceProvenance provenance) => new(
        Digest($"record-{id}"),
        "fixture-aggregate",
        GroupFor(partition, id),
        scope,
        new TrafficSpatialReference(scope.MapId, regionId: "old-gas-station"),
        new TrafficPhaseWindow(RaidPhase.Mid, 900, 1_800),
        TrafficObservationClass.Contact,
        sampleCount,
        provenance);

    private static EvidenceProvenance Historical()
    {
        var input = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "fixture://public-map",
            GeneratedUtc.AddDays(-3),
            EvidenceConfidence.Certain,
            new ProducerIdentity("fixture", "1"));
        return new EvidenceProvenance(
            EvidenceSourceClass.HistoricalAggregate,
            "fixture://historical-aggregate",
            GeneratedUtc,
            new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.75, "held-out-calibration-2026-09"),
            new ProducerIdentity("traffic-builder", "1", "traffic-model-fixture"),
            GeneratedUtc.AddDays(-2),
            GeneratedUtc.AddDays(-1),
            new EvidenceCoverage(60, 0.75, "Synthetic governed sample"),
            inputs: [input]);
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
