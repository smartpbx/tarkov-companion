using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Infrastructure.GameData.LootSpawns;

namespace TarkovCompanion.UnitTests.LootSpawns;

public sealed class DurableLootSpawnPublicationStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 21, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "tarkov-loot-publication-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Restart_preserves_exact_artifacts_timestamps_provenance_and_coverage()
    {
        var path = PublicationPath();
        var expected = Bundle(Now, "generation-one", "item-a", "item-b");
        using (var writer = Store(path))
        {
            await writer.PublishAsync(expected, default);
        }

        using var restarted = Store(path);
        var actual = Assert.IsType<LootSpawnSourceBundle>(
            await restarted.ReadLastKnownGoodAsync(default));

        Assert.Equal(expected.Identity.ContentSha256, actual.Identity.ContentSha256);
        Assert.Equal(expected.Identity.GeneratedUtc, actual.Identity.GeneratedUtc);
        Assert.Equal(expected.Identity.DataThroughUtc, actual.Identity.DataThroughUtc);
        Assert.Equal(expected.Identity.ImportedUtc, actual.Identity.ImportedUtc);
        Assert.Equal(
            expected.Identity.Artifacts.Select(value => (value.Role, value.SourceIdentifier, value.ContentSha256)),
            actual.Identity.Artifacts.Select(value => (value.Role, value.SourceIdentifier, value.ContentSha256)));
        var snapshot = Assert.Single(actual.Snapshots);
        Assert.Equal(expected.Snapshots[0].Provenance, snapshot.Provenance);
        Assert.Equal(expected.Snapshots[0].Coverage, snapshot.Coverage);
        Assert.Equal(expected.Coverage[0], Assert.Single(actual.Coverage));
        Assert.Equal(["item-a", "item-b"], Assert.Single(snapshot.Records).Candidates.Select(value => value.ItemId));
    }

    [Fact]
    public async Task Corrupt_head_recovers_previous_generation_and_retains_bounded_evidence()
    {
        var path = PublicationPath();
        var first = Bundle(Now, "generation-one", "item-a");
        var second = Bundle(Now.AddMinutes(10), "generation-two", "item-a");
        using (var writer = Store(path))
        {
            await writer.PublishAsync(first, default);
            await writer.PublishAsync(second, default);
        }

        var corrupted = await File.ReadAllBytesAsync(path, default);
        corrupted[^1] ^= 0x01;
        await File.WriteAllBytesAsync(path, corrupted, default);
        using var restarted = Store(path);

        var recovered = Assert.IsType<LootSpawnSourceBundle>(
            await restarted.ReadLastKnownGoodAsync(default));

        Assert.Equal(first.Identity.ContentSha256, recovered.Identity.ContentSha256);
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.Equal(
            "publication.cache-recovered",
            Assert.Single(await restarted.ReadQuarantineAsync()).Diagnostic.Code);
        Assert.Equal(first.Identity.ContentSha256,
            (await restarted.ReadLastKnownGoodAsync(default))!.Identity.ContentSha256);
    }

    [Fact]
    public async Task Corrupt_head_and_fallback_are_set_aside_without_becoming_a_publication()
    {
        var path = PublicationPath();
        using (var writer = Store(path))
        {
            await writer.PublishAsync(Bundle(Now, "generation-one", "item-a"), default);
        }

        await File.WriteAllTextAsync(path, "corrupt primary", default);
        await File.WriteAllTextAsync(path + ".previous", "corrupt fallback", default);
        using var restarted = Store(path);

        Assert.Null(await restarted.ReadLastKnownGoodAsync(default));
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.True(File.Exists(path + ".previous.corrupt"));
        Assert.Equal(
            "publication.cache-corrupt",
            Assert.Single(await restarted.ReadQuarantineAsync()).Diagnostic.Code);
    }

    [Fact]
    public async Task Concurrent_store_instances_cannot_replace_a_newer_head_with_an_older_one()
    {
        var path = PublicationPath();
        var older = Bundle(Now, "generation-one", "item-a");
        var newer = Bundle(Now.AddMinutes(10), "generation-two", "item-a");
        using var first = Store(path);
        using var second = Store(path);

        var outcomes = await Task.WhenAll(
            CaptureAsync(() => first.PublishAsync(older, default)),
            CaptureAsync(() => second.PublishAsync(newer, default)));

        Assert.All(outcomes.Where(value => value is not null), value =>
            Assert.Contains(
                Assert.IsType<LootSpawnSourceImportException>(value).Code,
                ["publication.import-regression", "publication.superseded"]));
        using var reader = Store(path);
        Assert.Equal(
            newer.Identity.ContentSha256,
            (await reader.ReadLastKnownGoodAsync(default))!.Identity.ContentSha256);
    }

    [Fact]
    public async Task Removing_a_prior_candidate_is_an_item_evidence_regression_even_when_record_coverage_is_unchanged()
    {
        using var store = new AtomicLootSpawnPublicationStore();
        await store.PublishAsync(Bundle(Now, "generation-one", "item-a", "item-b"), default);

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(async () =>
            await store.PublishAsync(Bundle(Now.AddMinutes(10), "generation-two", "item-a"), default));

        Assert.Equal("publication.item-evidence-regression", exception.Code);
        Assert.Equal(
            ["item-a", "item-b"],
            Assert.Single((await store.ReadLastKnownGoodAsync(default))!.Snapshots)
                .Records.Single().Candidates.Select(value => value.ItemId));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private DurableLootSpawnPublicationStore Store(string path) =>
        new(path, new FixedTimeProvider(Now.AddHours(1)));

    private string PublicationPath() => Path.Combine(_directory, "loot-publication.cache");

    private static async Task<Exception?> CaptureAsync(Func<ValueTask> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static LootSpawnSourceBundle Bundle(
        DateTimeOffset importedUtc,
        string generation,
        params string[] itemIds)
    {
        var generatedUtc = importedUtc.AddMinutes(-2);
        var dataThroughUtc = importedUtc.AddMinutes(-3);
        var datasetVersion = $"fixture-{generation}";
        var confidence = new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9);
        var producer = new ProducerIdentity("durable publication fixture", "1");
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
        var candidates = itemIds.Select(itemId => new LootSpawnCandidate(
                itemId,
                $"Fixture {itemId}",
                "Fixture",
                new EvidencedValue<long?>("flea-gross", 100_000, current, provenance),
                new EvidencedValue<long?>("flea-net", 90_000, current, provenance),
                new EvidencedValue<long?>("best-trader", 50_000, current, provenance),
                new EvidencedValue<int?>("occupied-squares", 2, current, provenance)))
            .ToArray();
        var record = new LootSpawnRecord(
            "fixture-spawn",
            "customs",
            "Fixture spawn",
            new LootSpawnLocation(LootSpawnPrecision.ExactPoint, [new MapScenePoint(10, 20)], ["ground"]),
            candidates.Length == 1
                ? LootSpawnPoolKind.SingleKnownItem
                : LootSpawnPoolKind.UnweightedCandidates,
            candidates,
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
        var contentHash = Hash(generation);
        var identity = new LootSpawnSourceIdentity(
            1,
            datasetVersion,
            contentHash,
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
            [new LootSpawnSourceDiagnostic("fixture.measured", "Fixture publication", "customs")]);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
