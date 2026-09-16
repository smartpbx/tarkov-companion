using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.LootSpawns;

public sealed class HighValueLootRuntimeSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Startup_loads_the_durable_head_and_projects_it_through_the_typed_layer()
    {
        var bundle = Bundle(Now, "generation-one");
        var source = Source(new MemoryStore(bundle), new StubRefresh());

        await source.InitializeAsync(CancellationToken.None);
        var result = source.Build(Request(Now.AddMinutes(1)));

        Assert.Same(bundle, source.LastKnownGood);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("fixture-spawn", entry.Spawn.SpawnId);
        Assert.Equal(850_000, entry.MaximumValue);
        Assert.Single(result.Objects);
        Assert.Equal("customs", result.MapId);
        Assert.Equal("fixture-transform-v1", result.TransformVersion);
    }

    [Fact]
    public async Task Quarantined_refresh_without_a_readable_replacement_never_clears_the_loaded_head()
    {
        var bundle = Bundle(Now, "generation-one");
        var refresh = new StubRefresh
        {
            Result = new(
                LootSpawnSourceImportDisposition.QuarantinedRetainedLastKnownGood,
                null,
                null,
                [new("source.refresh-failed", "Fixture refresh failed.")]),
        };
        var source = Source(new MemoryStore(bundle), refresh);
        await source.InitializeAsync(CancellationToken.None);

        await source.RefreshAsync(force: true, CancellationToken.None);

        Assert.Same(bundle, source.LastKnownGood);
        Assert.True(refresh.LastForce);
        Assert.Single(source.Build(Request(Now.AddMinutes(1))).Entries);
    }

    [Fact]
    public async Task Published_refresh_atomically_advances_the_runtime_head()
    {
        var first = Bundle(Now, "generation-one");
        var second = Bundle(Now.AddMinutes(10), "generation-two");
        var refresh = new StubRefresh
        {
            Result = new(
                LootSpawnSourceImportDisposition.Published,
                second,
                second,
                []),
        };
        var source = Source(new MemoryStore(first), refresh);
        await source.InitializeAsync(CancellationToken.None);

        await source.RefreshAsync(force: false, CancellationToken.None);

        Assert.Same(second, source.LastKnownGood);
        Assert.False(refresh.LastForce);
        Assert.False(source.NeedsRefresh(Now.AddMinutes(11), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Missing_or_wrong_transform_fails_closed_without_scene_objects()
    {
        var missing = Source(new MemoryStore(null), new StubRefresh());

        var unavailable = missing.Build(Request(Now));

        Assert.Empty(unavailable.Entries);
        Assert.Empty(unavailable.Objects);
        Assert.Equal(ResultCompleteness.Unavailable, unavailable.Status.Completeness);
        Assert.Equal("snapshot.missing", Assert.Single(unavailable.Diagnostics).Code);
        Assert.True(missing.NeedsRefresh(Now, TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task Transform_mismatch_with_a_loaded_head_is_withheld_by_the_layer_guard()
    {
        var source = Source(new MemoryStore(Bundle(Now, "generation-one")), new StubRefresh());
        await source.InitializeAsync(CancellationToken.None);

        var result = source.Build(Request(Now.AddMinutes(1)) with
        {
            TransformVersion = "different-transform",
        });

        Assert.Empty(result.Entries);
        Assert.Empty(result.Objects);
        Assert.Equal(ResultCompleteness.Unavailable, result.Status.Completeness);
        Assert.Equal("snapshot.map-transform-mismatch", Assert.Single(result.Diagnostics).Code);
    }

    private static HighValueLootRuntimeSource Source(
        ILootSpawnSourcePublicationStore store,
        ILootSpawnSourceRefreshService refresh) => new(
        store,
        refresh,
        new HighValueLootLayerService());

    private static HighValueLootRuntimeLayerRequest Request(DateTimeOffset evaluatedUtc) => new(
        "customs",
        "fixture-transform-v1",
        new MapSceneBounds(0, 0, 100, 100),
        evaluatedUtc,
        HighValueLootFilter.Default,
        ["ground"]);

    private static LootSpawnSourceBundle Bundle(DateTimeOffset importedUtc, string generation)
    {
        var datasetVersion = $"fixture-{generation}";
        var generatedUtc = importedUtc.AddMinutes(-2);
        var dataThroughUtc = importedUtc.AddMinutes(-3);
        var confidence = new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9);
        var producer = new ProducerIdentity("runtime source fixture", "1");
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "json.tarkov.dev/regular/maps",
            importedUtc,
            confidence,
            producer,
            dataThroughUtc,
            generatedUtc,
            new EvidenceCoverage(1, 1, "one measured fixture spawn"),
            "https://json.tarkov.dev/regular/maps");
        var current = new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current);
        var unknown = new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, "source.unknown");
        var candidate = new LootSpawnCandidate(
            "item-gpu",
            "Graphics card",
            "electronics",
            new EvidencedValue<long?>("flea-gross", 900_000, current, provenance),
            new EvidencedValue<long?>("flea-net", 850_000, current, provenance),
            new EvidencedValue<long?>("best-trader", 200_000, current, provenance),
            new EvidencedValue<int?>("occupied-squares", 2, current, provenance));
        var record = new LootSpawnRecord(
            "fixture-spawn",
            "customs",
            "Fixture spawn",
            new LootSpawnLocation(LootSpawnPrecision.ExactPoint, [new MapScenePoint(10, 20)], ["ground"]),
            LootSpawnPoolKind.SingleKnownItem,
            [candidate],
            new EvidencedValue<double?>("spawn-probability", null, unknown, provenance),
            new EvidencedValue<string?>("respawn-behavior", null, unknown, provenance),
            datasetVersion,
            "fixture-transform-v1",
            current,
            provenance);
        var coverage = new LootSpawnCoverage(1, 1, 1, 0);
        var snapshot = new LootSpawnSnapshot(
            "customs-fixture",
            datasetVersion,
            "customs",
            "fixture-transform-v1",
            importedUtc,
            current,
            coverage,
            provenance,
            [record]);
        var identity = new LootSpawnSourceIdentity(
            1,
            datasetVersion,
            Hash(generation),
            generatedUtc,
            dataThroughUtc,
            importedUtc,
            EvidenceSourceClass.PublicStructuredData,
            "json.tarkov.dev/regular/maps",
            "https://json.tarkov.dev/regular/maps",
            "fixture terms",
            confidence,
            producer,
            [
                new("maps-source", "json.tarkov.dev/regular/maps", Hash($"maps-{generation}")),
                new("items-source", "json.tarkov.dev/regular/items", Hash($"items-{generation}")),
                new("map-catalog", "https://example.test/maps.json", Hash($"catalog-{generation}")),
            ]);
        return new(
            identity,
            [snapshot],
            [new LootSpawnMapSourceCoverage("customs", 1, 1, 1, 1, 0)],
            []);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class MemoryStore(LootSpawnSourceBundle? head) : ILootSpawnSourcePublicationStore
    {
        public ValueTask<LootSpawnSourceBundle?> ReadLastKnownGoodAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(head);
        }

        public ValueTask PublishAsync(LootSpawnSourceBundle bundle, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask QuarantineAsync(
            LootSpawnSourceDiagnostic diagnostic,
            DateTimeOffset detectedUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubRefresh : ILootSpawnSourceRefreshService
    {
        public LootSpawnSourceImportResult Result { get; init; } = new(
            LootSpawnSourceImportDisposition.QuarantinedRetainedLastKnownGood,
            null,
            null,
            []);

        public bool LastForce { get; private set; }

        public ValueTask<LootSpawnSourceImportResult> RefreshAsync(
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastForce = force;
            return ValueTask.FromResult(Result);
        }
    }
}
