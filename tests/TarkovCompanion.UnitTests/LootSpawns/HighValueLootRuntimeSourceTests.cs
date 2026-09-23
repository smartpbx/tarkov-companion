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
    public async Task A_quarantined_refresh_still_records_its_own_outcome_for_setup_to_read()
    {
        // [Issue 563] LastKnownGood alone cannot say a refresh was ever attempted, let alone why
        // it failed: a quarantine never touches it. Setup > Data reads LastRefreshOutcome instead.
        var refresh = new StubRefresh
        {
            Result = new(
                LootSpawnSourceImportDisposition.QuarantinedRetainedLastKnownGood,
                null,
                null,
                [new("source.refresh-failed", "Fixture refresh failed.")]),
        };
        var source = Source(new MemoryStore(null), refresh);
        Assert.Null(source.LastRefreshOutcome);

        await source.RefreshAsync(force: true, CancellationToken.None);

        Assert.NotNull(source.LastRefreshOutcome);
        Assert.Equal(LootSpawnSourceImportDisposition.QuarantinedRetainedLastKnownGood, source.LastRefreshOutcome.Disposition);
        Assert.Equal("source.refresh-failed", Assert.Single(source.LastRefreshOutcome.Diagnostics).Code);
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
    public async Task Transform_mismatch_with_a_loaded_head_is_still_drawn_and_labelled_maybe_stale()
    {
        // [Issue 563] Refusing the snapshot here is what left "High-value loot only" empty on a
        // PC with a complete publication. A same-map snapshot from another catalog revision is
        // drawn, bound to the scene's transform, and says its positions may be off.
        var source = Source(new MemoryStore(Bundle(Now, "generation-one")), new StubRefresh());
        await source.InitializeAsync(CancellationToken.None);

        var result = source.Build(Request(Now.AddMinutes(1)) with
        {
            TransformVersion = "different-transform",
        });

        var entry = Assert.Single(result.Entries);
        Assert.Equal("different-transform", entry.Spawn.TransformVersion);
        Assert.Equal("different-transform", result.TransformVersion);
        Assert.Single(result.Objects);
        Assert.Equal(FreshnessState.Stale, result.Status.Freshness);
        Assert.Contains("positions may be off", result.CompactLegend, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "snapshot.transform-stale");
    }

    [Fact]
    public async Task Another_maps_snapshot_is_never_rebound_onto_this_map()
    {
        var source = Source(new MemoryStore(Bundle(Now, "generation-one")), new StubRefresh());
        await source.InitializeAsync(CancellationToken.None);

        var result = source.Build(Request(Now.AddMinutes(1)) with { MapId = "woods" });

        Assert.Empty(result.Entries);
        Assert.Empty(result.Objects);
        Assert.Equal(ResultCompleteness.Unavailable, result.Status.Completeness);
    }

    [Fact]
    public async Task Map_identity_casing_drift_still_finds_and_rebinds_the_matching_snapshot()
    {
        // [#716] A durable publication uses canonical lower-case IDs, while a catalog or raid
        // handoff can preserve display casing. Exact lookup called that map Unavailable even
        // though its snapshot and records were present in publication.cache.
        var source = Source(new MemoryStore(Bundle(Now, "generation-one")), new StubRefresh());
        await source.InitializeAsync(CancellationToken.None);

        var result = source.Build(Request(Now.AddMinutes(1)) with { MapId = "Customs" });

        var entry = Assert.Single(result.Entries);
        Assert.Equal("Customs", result.MapId);
        Assert.Equal("Customs", entry.Spawn.MapId);
        Assert.Equal(ResultCompleteness.Complete, result.Status.Completeness);
    }

    [Fact]
    public async Task The_same_request_stays_stable_for_a_raid_and_anything_else_gets_a_new_layer()
    {
        // [#657] The Raid map asks again on every squad position; answering with the same result
        // is what lets the map keep its loot markers instead of rebuilding them.
        var second = Bundle(Now.AddMinutes(10), "generation-two");
        var refresh = new StubRefresh
        {
            Result = new(LootSpawnSourceImportDisposition.Published, second, second, []),
        };
        var source = Source(new MemoryStore(Bundle(Now, "generation-one")), refresh);
        await source.InitializeAsync(CancellationToken.None);
        var at = Now.AddMinutes(1);

        var built = source.Build(Request(at));

        Assert.Same(built, source.Build(Request(at.AddSeconds(59))));
        Assert.Same(built, source.Build(Request(at) with { FloorIds = ["ground"] }));
        var later = source.Build(Request(at.AddMinutes(59)));
        Assert.Same(built, later);
        later = source.Build(Request(at.AddHours(1)));
        Assert.NotSame(built, later);
        Assert.NotSame(later, source.Build(Request(at.AddHours(1)) with { MapBounds = new MapSceneBounds(0, 0, 50, 50) }));
        var floors = source.Build(Request(at.AddHours(1)) with { FloorIds = ["ground", "roof"] });
        var narrow = new HighValueLootFilter(LootSpawnValueBasis.BestNet, LootSpawnValueThresholds.Default, TimeSpan.FromDays(1), TimeSpan.FromDays(90), 0);
        var filtered = source.Build(Request(at.AddHours(1)) with { FloorIds = ["ground", "roof"], Filter = narrow });
        Assert.NotSame(floors, filtered);
        // The player's filter and the default one, asked for in turn, are both kept.
        Assert.Same(floors, source.Build(Request(at.AddHours(1).AddSeconds(1)) with { FloorIds = ["ground", "roof"] }));
        Assert.Same(filtered, source.Build(Request(at.AddHours(1).AddSeconds(1)) with { FloorIds = ["ground", "roof"], Filter = narrow }));
        var head = source.Build(Request(at.AddHours(1)));
        Assert.NotSame(head, source.Build(Request(at.AddSeconds(59))));

        var beforeRefresh = source.Build(Request(at.AddSeconds(59)));
        await source.RefreshAsync(force: false, CancellationToken.None);
        Assert.NotSame(beforeRefresh, source.Build(Request(at.AddSeconds(59))));
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
