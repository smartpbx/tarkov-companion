using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Strategy.Data;
using TarkovCompanion.Infrastructure.Strategy.Datasets;

namespace TarkovCompanion.UnitTests.StrategyData;

public sealed class TrafficModelPackageImporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TrafficCompatibilityScope Scope = new("customs", "0.16.9", ProfileGameMode.Pvp, "wipe-2026-2", "all-players");
    private static readonly TrafficPartitionPolicy Policy = new("traffic-split-v1", "fixture-salt", 8_000, 1_000, 1_000);

    [Fact]
    public async Task SignedPackageImportsAndUnknownScopeStaysUnavailable()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = Verifier(key);
        var importer = new TrafficModelPackageImporter(verifier);
        var package = Package(key, "2026.09.16", "artifact-one");

        var accepted = await importer.ImportAsync(package.Streams(), Scope, CancellationToken.None);
        var incompatible = await importer.ImportAsync(
            package.Streams(),
            new TrafficCompatibilityScope("customs", "0.17.0", ProfileGameMode.Pvp, "wipe-2026-2", "all-players"),
            CancellationToken.None);

        Assert.True(accepted.IsAccepted);
        Assert.NotNull(accepted.Publication);
        Assert.Equal(TrafficModelImportDisposition.Incompatible, incompatible.Disposition);
        Assert.Equal("coverage-unknown-for-scope", incompatible.ReasonCode);
    }

    [Fact]
    public async Task UnknownIdentityOrLiveFieldsAreQuarantinedBeforeIntegrityAcceptance()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = Verifier(key);
        var importer = new TrafficModelPackageImporter(verifier);
        var package = Package(key, "2026.09.16", "artifact-one");
        var dataset = JsonNode.Parse(package.Dataset)!;
        dataset["records"]![0]!["playerId"] = "prohibited-identity";
        dataset["records"]![0]!["currentRaid"] = true;
        dataset["records"]![0]!["liveCoordinates"] = new JsonObject { ["x"] = 1, ["y"] = 2 };
        var hostile = package with { Dataset = Encoding.UTF8.GetBytes(dataset.ToJsonString()) };

        var result = await importer.ImportAsync(hostile.Streams(), Scope, CancellationToken.None);

        Assert.Equal(TrafficModelImportDisposition.Quarantined, result.Disposition);
        Assert.Null(result.Publication);
        Assert.Equal("invalid-or-hostile-package", result.ReasonCode);
    }

    [Fact]
    public async Task OversizedMemberIsQuarantinedWithoutMaterializingAPublication()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = Verifier(key);
        var package = Package(key, "2026.09.16", "artifact-one");
        var importer = new TrafficModelPackageImporter(
            verifier,
            new TrafficModelImportLimits(MaximumDatasetBytes: package.Dataset.Length - 1));

        var result = await importer.ImportAsync(package.Streams(), Scope, CancellationToken.None);

        Assert.Equal(TrafficModelImportDisposition.Quarantined, result.Disposition);
        Assert.Null(result.Publication);
    }

    [Fact]
    public async Task DuplicateJsonPropertiesAreQuarantined()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = Verifier(key);
        var importer = new TrafficModelPackageImporter(verifier);
        var package = Package(key, "2026.09.16", "artifact-one");
        var duplicate = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(package.Manifest).Replace(
                "{\"schemaVersion\":1,",
                "{\"schemaVersion\":1,\"schemaVersion\":1,",
                StringComparison.Ordinal));

        var result = await importer.ImportAsync(
            (package with { Manifest = duplicate }).Streams(),
            Scope,
            CancellationToken.None);

        Assert.Equal(TrafficModelImportDisposition.Quarantined, result.Disposition);
        Assert.Null(result.Publication);
    }

    [Fact]
    public async Task ConcurrentImportsShareATrustedSignatureKeySafely()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = Verifier(key);
        var importer = new TrafficModelPackageImporter(verifier);
        var package = Package(key, "2026.09.16", "artifact-one");

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            importer.ImportAsync(package.Streams(), Scope, CancellationToken.None)));

        Assert.All(results, result => Assert.True(result.IsAccepted));
    }

    [Fact]
    public async Task DetachedSigningTimestampIsInformationalRatherThanAFreshnessAuthority()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = Verifier(key);
        var importer = new TrafficModelPackageImporter(verifier);
        var package = Package(key, "2026.09.16", "artifact-one");
        var envelope = JsonNode.Parse(package.Signature)!;
        envelope["signedUtc"] = Now.AddYears(-1).ToString("O", CultureInfo.InvariantCulture);

        var result = await importer.ImportAsync(
            (package with { Signature = Encoding.UTF8.GetBytes(envelope.ToJsonString()) }).Streams(),
            Scope,
            CancellationToken.None);

        Assert.True(result.IsAccepted);
    }

    [Fact]
    public async Task RefusedInstallCannotMoveCurrentOrLastKnownGoodHeads()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = Verifier(key);
        var importer = new TrafficModelPackageImporter(verifier);
        var root = Path.Combine(Path.GetTempPath(), $"traffic-store-{Guid.NewGuid():N}");
        try
        {
            using var store = new TrafficSnapshotStore(new TrafficSnapshotStoreOptions(root), importer);
            var valid = Package(key, "2026.09.16", "artifact-one");
            var installed = await store.InstallAsync(valid.Streams(), Scope, CancellationToken.None);
            var corrupt = valid with { Artifact = Encoding.UTF8.GetBytes("tampered") };

            var refused = await store.InstallAsync(corrupt.Streams(), Scope, CancellationToken.None);

            Assert.Equal(TrafficModelImportDisposition.Quarantined, refused.Disposition);
            Assert.Equal(installed.CurrentReceiptSha256, refused.CurrentReceiptSha256);
            Assert.Equal(installed.LastKnownGoodReceiptSha256, refused.LastKnownGoodReceiptSha256);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "quarantine"), "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CorruptCurrentSnapshotRollsBackToPreviouslyVerifiedVersion()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = Verifier(key);
        var importer = new TrafficModelPackageImporter(verifier);
        var root = Path.Combine(Path.GetTempPath(), $"traffic-store-{Guid.NewGuid():N}");
        try
        {
            using var store = new TrafficSnapshotStore(new TrafficSnapshotStoreOptions(root), importer);
            var first = await store.InstallAsync(
                Package(key, "2026.09.16", "artifact-one").Streams(),
                Scope,
                CancellationToken.None);
            var second = await store.InstallAsync(
                Package(key, "2026.09.17", "artifact-two", Now.AddDays(1), Now.AddDays(-1)).Streams(),
                Scope,
                CancellationToken.None);
            await File.WriteAllTextAsync(
                Path.Combine(root, "versions", second.CurrentReceiptSha256!, "model.artifact"),
                "corrupt",
                CancellationToken.None);

            var loaded = await store.LoadAsync(Scope, CancellationToken.None);

            Assert.Equal(TrafficModelImportDisposition.Accepted, loaded.Disposition);
            Assert.Equal("rolled-back-to-last-known-good", loaded.ReasonCode);
            Assert.Equal(first.CurrentReceiptSha256, loaded.CurrentReceiptSha256);
            Assert.Equal("2026.09.16", loaded.Publication!.Manifest.DatasetVersion);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PreexistingCorruptContentAddressCannotBePublishedByAValidInstall()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = Verifier(key);
        var importer = new TrafficModelPackageImporter(verifier);
        var root = Path.Combine(Path.GetTempPath(), $"traffic-store-{Guid.NewGuid():N}");
        try
        {
            using var store = new TrafficSnapshotStore(new TrafficSnapshotStoreOptions(root), importer);
            var first = Package(key, "2026.09.16", "artifact-one");
            var installed = await store.InstallAsync(first.Streams(), Scope, CancellationToken.None);
            var second = Package(key, "2026.09.17", "artifact-two", Now.AddDays(1), Now.AddDays(-1));
            var inspected = await importer.ImportAsync(second.Streams(), Scope, CancellationToken.None);
            Assert.True(inspected.IsAccepted);
            var occupied = Path.Combine(root, "versions", inspected.ReceiptSha256);
            Directory.CreateDirectory(occupied);
            await File.WriteAllTextAsync(
                Path.Combine(occupied, "model.artifact"),
                "corrupt-preexisting-content",
                CancellationToken.None);

            var refused = await store.InstallAsync(second.Streams(), Scope, CancellationToken.None);

            Assert.Equal(TrafficModelImportDisposition.Quarantined, refused.Disposition);
            Assert.Equal(installed.CurrentReceiptSha256, refused.CurrentReceiptSha256);
            Assert.Equal(installed.LastKnownGoodReceiptSha256, refused.LastKnownGoodReceiptSha256);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReplayedOlderPackageCannotReplaceCurrentButExplicitRollbackCan()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = Verifier(key);
        var importer = new TrafficModelPackageImporter(verifier);
        var root = Path.Combine(Path.GetTempPath(), $"traffic-store-{Guid.NewGuid():N}");
        try
        {
            using var store = new TrafficSnapshotStore(new TrafficSnapshotStoreOptions(root), importer);
            var older = Package(key, "2026.09.16", "artifact-one");
            var first = await store.InstallAsync(older.Streams(), Scope, CancellationToken.None);
            var second = await store.InstallAsync(
                Package(key, "2026.09.17", "artifact-two", Now.AddDays(1), Now.AddDays(-1)).Streams(),
                Scope,
                CancellationToken.None);
            var statePath = Path.Combine(root, "snapshot-state.json");
            var stateBeforeReplay = await File.ReadAllBytesAsync(statePath, CancellationToken.None);

            var replayed = await store.InstallAsync(older.Streams(), Scope, CancellationToken.None);

            Assert.Equal(TrafficModelImportDisposition.Quarantined, replayed.Disposition);
            Assert.Equal("snapshot-data-through-regression", replayed.ReasonCode);
            Assert.Equal(second.CurrentReceiptSha256, replayed.CurrentReceiptSha256);
            Assert.Equal(first.CurrentReceiptSha256, replayed.LastKnownGoodReceiptSha256);
            Assert.Equal(
                stateBeforeReplay,
                await File.ReadAllBytesAsync(statePath, CancellationToken.None));

            var rolledBack = await store.RollbackAsync(CancellationToken.None);

            Assert.Equal("explicit-rollback", rolledBack.ReasonCode);
            Assert.Equal(first.CurrentReceiptSha256, rolledBack.CurrentReceiptSha256);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConflictingContentAtTheSameGenerationCannotReplaceCurrent()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = Verifier(key);
        var importer = new TrafficModelPackageImporter(verifier);
        var root = Path.Combine(Path.GetTempPath(), $"traffic-store-{Guid.NewGuid():N}");
        try
        {
            using var store = new TrafficSnapshotStore(new TrafficSnapshotStoreOptions(root), importer);
            var installed = await store.InstallAsync(
                Package(key, "2026.09.16", "artifact-one").Streams(),
                Scope,
                CancellationToken.None);

            var refused = await store.InstallAsync(
                Package(key, "2026.09.17", "artifact-two").Streams(),
                Scope,
                CancellationToken.None);

            Assert.Equal(TrafficModelImportDisposition.Quarantined, refused.Disposition);
            Assert.Equal("snapshot-generation-conflict", refused.ReasonCode);
            Assert.Equal(installed.CurrentReceiptSha256, refused.CurrentReceiptSha256);
            Assert.Equal(installed.LastKnownGoodReceiptSha256, refused.LastKnownGoodReceiptSha256);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GeneratedTimeRegressionIsRefusedWhenDataThroughDoesNotRegress()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var verifier = Verifier(key);
        var importer = new TrafficModelPackageImporter(verifier);
        var root = Path.Combine(Path.GetTempPath(), $"traffic-store-{Guid.NewGuid():N}");
        try
        {
            using var store = new TrafficSnapshotStore(new TrafficSnapshotStoreOptions(root), importer);
            var installed = await store.InstallAsync(
                Package(key, "2026.09.17", "artifact-two", Now.AddDays(1), Now.AddDays(-2)).Streams(),
                Scope,
                CancellationToken.None);

            var refused = await store.InstallAsync(
                Package(key, "2026.09.16", "artifact-one").Streams(),
                Scope,
                CancellationToken.None);

            Assert.Equal(TrafficModelImportDisposition.Quarantined, refused.Disposition);
            Assert.Equal("snapshot-generation-regression", refused.ReasonCode);
            Assert.Equal(installed.CurrentReceiptSha256, refused.CurrentReceiptSha256);
            Assert.Equal(installed.LastKnownGoodReceiptSha256, refused.LastKnownGoodReceiptSha256);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void BuilderAssignmentIsStableAndDoesNotTrustInputOrder()
    {
        var forward = UnsignedBuild("2026.09.16", "artifact-one", reverse: false);
        var reversed = UnsignedBuild("2026.09.16", "artifact-one", reverse: true);

        Assert.Equal(forward.Dataset.ContentSha256, reversed.Dataset.ContentSha256);
        Assert.Equal(forward.DatasetJson, reversed.DatasetJson);
        Assert.Equal(forward.ReportJson, reversed.ReportJson);
        Assert.All(forward.Dataset.Records, record =>
            Assert.Equal(TrafficPartitioner.Assign(record.PartitionGroupId, Policy), record.Partition));
    }

    private static SignedPackage Package(
        ECDsa key,
        string version,
        string artifactText,
        DateTimeOffset? generatedUtc = null,
        DateTimeOffset? dataThroughUtc = null)
    {
        var build = UnsignedBuild(version, artifactText, reverse: false, generatedUtc, dataThroughUtc);
        var manifestHash = SHA256.HashData(build.ManifestJson);
        var signature = new TrafficArtifactSignature(
            TrafficSignatureAlgorithm.EcdsaP256Sha256,
            "fixture-key",
            Convert.ToHexStringLower(manifestHash),
            Convert.ToBase64String(key.SignHash(manifestHash, DSASignatureFormat.Rfc3279DerSequence)),
            build.Manifest.GeneratedUtc.AddMinutes(1));
        return new SignedPackage(
            build.DatasetJson,
            build.ReportJson,
            build.ManifestJson,
            JsonSerializer.SerializeToUtf8Bytes(signature, TrafficDataJson.Options),
            build.Artifact);
    }

    private static TrafficModelBuildResult UnsignedBuild(
        string version,
        string artifactText,
        bool reverse,
        DateTimeOffset? generatedUtc = null,
        DateTimeOffset? dataThroughUtc = null)
    {
        generatedUtc ??= Now;
        dataThroughUtc ??= Now.AddDays(-2);
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
            [Scope],
            Now,
            "fixture://aggregate");
        var records = new[]
        {
            Input("held", TrafficDataPartition.HeldOut, 30, provenance),
            Input("train", TrafficDataPartition.Train, 10, provenance),
            Input("tune", TrafficDataPartition.Tune, 20, provenance),
        };
        if (reverse)
        {
            Array.Reverse(records);
        }

        return TrafficModelBuilder.Build(
            new TrafficModelBuildRequest(
                $"build-{version}",
                "traffic-dataset",
                version,
                TrafficDatasetVisibility.DistributableAggregate,
                dataThroughUtc.Value,
                generatedUtc.Value,
                "traffic-transform-2",
                "traffic-runtime",
                $"traffic-model-{version}",
                "json-v1",
                "held-out-calibration-2026-09",
                new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.75, "held-out-calibration-2026-09"),
                new EvidenceCoverage(60, 0.75, "Three governed partitions"),
                Policy,
                [source],
                records,
                [new TrafficCoverageGap(Scope, "factory-interior-missing", "Interior coverage is unavailable.")]),
            Encoding.UTF8.GetBytes(artifactText));
    }

    private static TrafficAggregateInput Input(
        string id,
        TrafficDataPartition partition,
        long sampleCount,
        EvidenceProvenance provenance) => new(
        Digest($"record-{id}"),
        "fixture-aggregate",
        GroupFor(partition, id),
        Scope,
        new TrafficSpatialReference("customs", regionId: "dorms"),
        new TrafficPhaseWindow(RaidPhase.Mid, 900, 1_800),
        TrafficObservationClass.Contact,
        sampleCount,
        provenance);

    private static EvidenceProvenance Historical()
    {
        var input = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "fixture://public-map",
            Now.AddDays(-3),
            EvidenceConfidence.Certain,
            new ProducerIdentity("fixture", "1"));
        return new EvidenceProvenance(
            EvidenceSourceClass.HistoricalAggregate,
            "fixture://historical-aggregate",
            Now,
            new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.75, "held-out-calibration-2026-09"),
            new ProducerIdentity("traffic-builder", "1", "traffic-model-fixture"),
            Now.AddDays(-2),
            Now.AddDays(-1),
            new EvidenceCoverage(60, 0.75, "Synthetic governed sample"),
            inputs: [input]);
    }

    private static EcdsaTrafficArtifactSignatureVerifier Verifier(ECDsa key) => new(
        new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["fixture-key"] = key.ExportSubjectPublicKeyInfo(),
        });

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

    private sealed record SignedPackage(
        byte[] Dataset,
        byte[] Report,
        byte[] Manifest,
        byte[] Signature,
        byte[] Artifact)
    {
        public TrafficModelPackageStreams Streams() => new(
            new MemoryStream(Dataset, writable: false),
            new MemoryStream(Report, writable: false),
            new MemoryStream(Manifest, writable: false),
            new MemoryStream(Signature, writable: false),
            new MemoryStream(Artifact, writable: false));
    }
}
