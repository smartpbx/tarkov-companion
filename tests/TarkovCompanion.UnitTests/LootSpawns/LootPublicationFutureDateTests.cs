using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Infrastructure.GameData.LootSpawns;

namespace TarkovCompanion.UnitTests.LootSpawns;

/// <summary>
/// [#799] Clayton's Windows clock ran 4 h fast while publication.cache was rewritten. Once it was
/// corrected, every map read "0 spawns, Unavailable" although the cache verified intact.
/// </summary>
public sealed class LootPublicationFutureDateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 20, 19, 0, TimeSpan.Zero);
    private static readonly TimeSpan Skew = TimeSpan.FromHours(4);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "tarkov-loot-future-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task A_publication_written_while_the_clock_was_four_hours_fast_is_loaded_and_drawn()
    {
        var path = Path.Combine(_directory, "publication.cache");
        Directory.CreateDirectory(_directory);
        using (var fastStore = new DurableLootSpawnPublicationStore(path, new FixedClock(Now + Skew)))
        {
            await fastStore.PublishAsync(Bundle(Now + Skew - TimeSpan.FromMinutes(5), "one"), CancellationToken.None);
            await fastStore.PublishAsync(Bundle(Now + Skew, "two"), CancellationToken.None);
        }

        File.SetLastWriteTimeUtc(path, (Now + Skew).UtcDateTime);
        File.SetLastWriteTimeUtc(path + ".previous", (Now + Skew).UtcDateTime);

        // Restart with the clock corrected.
        var clock = new FixedClock(Now);
        using var store = new DurableLootSpawnPublicationStore(path, clock);
        var source = new HighValueLootRuntimeSource(store, new NoRefresh(), new HighValueLootLayerService(), clock);
        await source.InitializeAsync(CancellationToken.None);

        var result = source.Build(Request(Now.AddSeconds(49)));

        Assert.NotNull(source.LastKnownGood);
        Assert.Equal(Now + Skew, source.LastKnownGood.Identity.ImportedUtc);
        Assert.NotEqual(ResultCompleteness.Unavailable, result.Status.Completeness);
        Assert.Single(result.Entries);
        Assert.Single(result.Objects);
        Assert.Equal(850_000, Assert.Single(result.Entries).MaximumValue);
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(4)]
    public async Task A_wall_clock_jump_after_loading_still_draws_the_spawns(int hours)
    {
        var bundle = Bundle(Now, "one");
        var source = new HighValueLootRuntimeSource(
            new MemoryStore(bundle),
            new NoRefresh(),
            new HighValueLootLayerService(),
            new FixedClock(Now));
        await source.InitializeAsync(CancellationToken.None);
        Assert.Single(source.Build(Request(Now.AddMinutes(1))).Entries);

        var jumped = source.Build(Request(Now.AddMinutes(1).AddHours(hours)));

        Assert.NotEqual(ResultCompleteness.Unavailable, jumped.Status.Completeness);
        Assert.Single(jumped.Entries);
        Assert.Single(jumped.Objects);
    }

    [Fact]
    public async Task A_head_stamped_ahead_of_the_clock_is_due_a_refresh()
    {
        var source = new HighValueLootRuntimeSource(
            new MemoryStore(Bundle(Now + Skew, "one")),
            new NoRefresh(),
            new HighValueLootLayerService());
        await source.InitializeAsync(CancellationToken.None);

        Assert.True(source.NeedsRefresh(Now, TimeSpan.FromHours(1)));
        Assert.False(source.NeedsRefresh(Now + Skew + TimeSpan.FromMinutes(1), TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task A_correctly_dated_refresh_replaces_a_head_stamped_ahead_of_the_clock()
    {
        var path = Path.Combine(_directory, "publication.cache");
        Directory.CreateDirectory(_directory);
        using (var fastStore = new DurableLootSpawnPublicationStore(path, new FixedClock(Now + Skew)))
        {
            await fastStore.PublishAsync(Bundle(Now + Skew, "fast"), CancellationToken.None);
        }

        using var store = new DurableLootSpawnPublicationStore(path, new FixedClock(Now));
        var corrected = Bundle(Now, "corrected");

        await store.PublishAsync(corrected, CancellationToken.None);

        var head = await store.ReadLastKnownGoodAsync(CancellationToken.None);
        Assert.Equal(corrected.Identity.ContentSha256, head!.Identity.ContentSha256);
    }

    [Fact]
    public async Task A_head_behind_the_clock_still_refuses_an_older_import()
    {
        // The monotonic guard itself is unchanged for a head the clock agrees with.
        var path = Path.Combine(_directory, "publication.cache");
        Directory.CreateDirectory(_directory);
        using var store = new DurableLootSpawnPublicationStore(path, new FixedClock(Now + Skew));
        await store.PublishAsync(Bundle(Now, "newer"), CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<LootSpawnSourceImportException>(async () =>
            await store.PublishAsync(Bundle(Now.AddMinutes(-30), "older"), CancellationToken.None));

        Assert.Equal("publication.import-regression", refusal.Code);
    }

    private static HighValueLootRuntimeLayerRequest Request(DateTimeOffset evaluatedUtc) => new(
        "customs",
        "fixture-transform-v1",
        new MapSceneBounds(0, 0, 100, 100),
        evaluatedUtc,
        HighValueLootFilter.Default,
        ["ground"]);

    /// <summary>Stamped the way the tarkov.dev normalizer stamps a bundle: everything from the import clock.</summary>
    private static LootSpawnSourceBundle Bundle(DateTimeOffset importedUtc, string generation)
    {
        var datasetVersion = $"fixture-{generation}";
        var generatedUtc = importedUtc;
        var dataThroughUtc = importedUtc.AddMinutes(-3);
        var confidence = new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9);
        var producer = new ProducerIdentity("future-date fixture", "1");
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
        var snapshot = new LootSpawnSnapshot(
            "customs-fixture-" + generation,
            datasetVersion,
            "customs",
            "fixture-transform-v1",
            importedUtc,
            current,
            new LootSpawnCoverage(1, 1, 1, 0),
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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MemoryStore(LootSpawnSourceBundle? head) : ILootSpawnSourcePublicationStore
    {
        public ValueTask<LootSpawnSourceBundle?> ReadLastKnownGoodAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(head);

        public ValueTask PublishAsync(LootSpawnSourceBundle bundle, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask QuarantineAsync(
            LootSpawnSourceDiagnostic diagnostic,
            DateTimeOffset detectedUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoRefresh : ILootSpawnSourceRefreshService
    {
        public ValueTask<LootSpawnSourceImportResult> RefreshAsync(
            bool force = false,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LootSpawnSourceImportResult(
                LootSpawnSourceImportDisposition.QuarantinedRetainedLastKnownGood,
                null,
                null,
                []));
    }
}
