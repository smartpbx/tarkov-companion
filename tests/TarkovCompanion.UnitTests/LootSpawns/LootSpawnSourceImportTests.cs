using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Infrastructure.GameData.LootSpawns;

namespace TarkovCompanion.UnitTests.LootSpawns;

public sealed class LootSpawnSourceImportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Checked_in_fixture_manifest_identifies_the_exact_content_bytes()
    {
        var fixture = FixtureDirectory();
        var manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture, "manifest.json")))!.AsObject();
        var content = File.ReadAllBytes(Path.Combine(fixture, "content.json"));

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            manifest["contentSha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task Fixture_accepts_unknown_fields_and_preserves_authorities_pool_semantics_and_partial_coverage()
    {
        var bundle = await ReadAsync(Documents());

        var snapshot = Assert.Single(bundle.Snapshots);
        Assert.Equal("customs", snapshot.MapId);
        Assert.Equal(
            ["customs-curated-map-only", "customs-curated-point"],
            snapshot.Records.Select(record => record.SpawnId));
        Assert.Equal(ResultCompleteness.Partial, snapshot.Status.Completeness);
        Assert.Equal(new LootSpawnCoverage(2, 1, 1, 1), snapshot.Coverage);
        Assert.Equal(EvidenceSourceClass.CuratedData, snapshot.Provenance.SourceClass);
        Assert.Equal(2d / 3d, snapshot.Provenance.Coverage!.Fraction);

        var unresolved = snapshot.Records[0];
        Assert.Equal(LootSpawnPrecision.MapOnly, unresolved.Location.Precision);
        Assert.Null(unresolved.Location.Geometry);
        Assert.Equal(LootSpawnPoolKind.UnweightedCandidates, unresolved.PoolKind);
        Assert.Equal(["fixture-gpu", "fixture-ledx"], unresolved.Candidates.Select(candidate => candidate.ItemId));
        Assert.All(unresolved.Candidates, candidate =>
            Assert.Equal(EvidenceSourceClass.PublicStructuredData, candidate.FleaGrossRoubles.Provenance.SourceClass));
        Assert.Equal(ResultCompleteness.Unknown, unresolved.SpawnProbability.Status.Completeness);

        Assert.Equal(
            ["coverage.map-partial", "coverage.map-missing", "coverage.map-missing"],
            bundle.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.Equal(["customs", "factory", "woods"], bundle.Diagnostics.Select(diagnostic => diagnostic.MapId));
    }

    [Fact]
    public async Task Missing_required_field_is_refused()
    {
        var documents = Documents(manifest => manifest.Remove("datasetVersion"));

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("source.required-field-missing", exception.Code);
    }

    [Fact]
    public async Task Oversized_document_is_refused_before_hashing_or_parsing()
    {
        var documents = new LootSpawnSourceDocuments(
            Documents().Manifest,
            new byte[JsonLootSpawnSourceBundleReader.MaximumContentBytes + 1]);

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("source.size-invalid", exception.Code);
    }

    [Fact]
    public async Task Oversized_collection_is_refused()
    {
        var documents = Documents(content =>
        {
            var pool = content["snapshots"]![0]!["records"]![0]!["pool"]!.AsObject();
            pool["kind"] = "unweightedCandidates";
            pool["itemIds"] = new JsonArray(
                Enumerable.Range(0, LootSpawnRecord.MaximumCandidates + 1)
                    .Select(index => JsonValue.Create($"item-{index}"))
                    .ToArray());
        });

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("source.collection-oversized", exception.Code);
    }

    [Fact]
    public async Task Oversized_string_is_refused()
    {
        var documents = Documents(content =>
            content["snapshots"]![0]!["records"]![0]!["label"] = new string('x', 257));

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("source.string-oversized", exception.Code);
    }

    [Fact]
    public async Task Excessive_unknown_nesting_is_still_refused_by_the_global_parser_bound()
    {
        var documents = Documents(content =>
        {
            JsonNode nested = JsonValue.Create(true)!;
            for (var index = 0; index < JsonLootSpawnSourceBundleReader.MaximumJsonDepth + 1; index++)
            {
                nested = new JsonObject { ["next"] = nested };
            }

            content["futureDeepField"] = nested;
        });

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("source.json-invalid", exception.Code);
    }

    [Fact]
    public async Task Duplicate_spawn_ids_are_refused_before_publication()
    {
        var documents = Documents(
            manifest =>
            {
                var coverage = manifest["coverage"]![0]!;
                coverage["knownRecords"] = 3;
                coverage["publishedRecords"] = 3;
                coverage["positionedRecords"] = 2;
                coverage["floorResolvedRecords"] = 2;
                coverage["unresolvedRecords"] = 1;
            },
            content =>
            {
                var records = content["snapshots"]![0]!["records"]!.AsArray();
                records.Add(records[0]!.DeepClone());
            });

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("record.id-duplicate", exception.Code);
    }

    [Theory]
    [InlineData("coordinate", "location.out-of-bounds")]
    [InlineData("floor", "location.floor-unknown")]
    public async Task Invalid_coordinates_and_floors_are_refused(string mutation, string expectedCode)
    {
        var documents = Documents(content =>
        {
            var location = content["snapshots"]![0]!["records"]![0]!["location"]!;
            if (mutation == "coordinate")
            {
                location["points"]![0]!["x"] = 101;
            }
            else
            {
                location["floorIds"]![0] = "invented-floor";
            }
        });

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task Unknown_item_is_not_fabricated_from_a_label()
    {
        var documents = Documents(content =>
            content["snapshots"]![0]!["records"]![0]!["pool"]!["itemIds"]![0] = "unknown-item");

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("pool.item-unknown", exception.Code);
    }

    [Theory]
    [InlineData("2025-01-01T00:00:00.0000000+00:00", "manifest.stale")]
    [InlineData("2026-09-16T18:06:00.0000000+00:00", "manifest.time-future")]
    public async Task Stale_and_future_sources_are_refused(string dataThrough, string expectedCode)
    {
        var documents = Documents(manifest =>
        {
            manifest["dataThroughUtc"] = dataThrough;
            if (expectedCode == "manifest.time-future")
            {
                manifest["generatedUtc"] = dataThrough;
            }
        });

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task Content_hash_mismatch_is_refused()
    {
        var documents = Documents(
            content: content => content["futureContentField"] = "changed-after-manifest",
            rehash: false);

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("content.hash-mismatch", exception.Code);
    }

    [Fact]
    public async Task Incompatible_schema_is_refused()
    {
        var documents = Documents(manifest => manifest["schemaVersion"] = 2);

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("schema.incompatible", exception.Code);
    }

    [Fact]
    public async Task Incompatible_map_transform_is_refused()
    {
        var documents = Documents(content =>
            content["snapshots"]![0]!["transformVersion"] = "unreviewed-transform");

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("snapshot.transform-incompatible", exception.Code);
    }

    [Fact]
    public async Task Public_structured_location_bundle_must_name_the_primary_json_tarkov_dev_source()
    {
        var documents = Documents(manifest =>
        {
            manifest["source"]!["class"] = "publicStructuredData";
            manifest["source"]!["identifier"] = "unreviewed.example/maps";
        });

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("manifest.source-incompatible", exception.Code);
    }

    [Fact]
    public async Task Json_tarkov_dev_location_bundle_preserves_public_structured_source_class()
    {
        var documents = Documents(manifest =>
        {
            manifest["source"]!["class"] = "publicStructuredData";
            manifest["source"]!["identifier"] = "json.tarkov.dev/regular/maps";
            manifest["source"]!["reference"] = "https://json.tarkov.dev/regular/maps";
        });

        var bundle = await ReadAsync(documents);

        Assert.Equal(EvidenceSourceClass.PublicStructuredData, Assert.Single(bundle.Snapshots).Provenance.SourceClass);
    }

    [Theory]
    [InlineData("hash", "content.hash-mismatch")]
    [InlineData("stale", "manifest.stale")]
    [InlineData("schema", "schema.incompatible")]
    public async Task Refused_replacement_is_quarantined_and_last_known_good_is_retained(
        string refusal,
        string expectedCode)
    {
        using var store = new AtomicLootSpawnPublicationStore();
        var service = new LootSpawnSourceImportService(new JsonLootSpawnSourceBundleReader(), store);
        var first = await service.ImportAsync(Documents(), Context());
        var replacement = refusal switch
        {
            "hash" => Documents(content: content => content["futureContentField"] = "tampered", rehash: false),
            "stale" => Documents(manifest =>
                manifest["dataThroughUtc"] = "2025-01-01T00:00:00.0000000+00:00"),
            "schema" => Documents(manifest => manifest["schemaVersion"] = 2),
            _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
        };

        var refused = await service.ImportAsync(replacement, Context());

        Assert.Equal(LootSpawnSourceImportDisposition.PublishedPartial, first.Disposition);
        Assert.Equal(LootSpawnSourceImportDisposition.QuarantinedRetainedLastKnownGood, refused.Disposition);
        Assert.Null(refused.Published);
        Assert.Same(first.Published, refused.LastKnownGood);
        Assert.Equal(expectedCode, Assert.Single(refused.Diagnostics).Code);
        Assert.Equal(expectedCode, Assert.Single(await store.ReadQuarantineAsync()).Diagnostic.Code);
    }

    [Fact]
    public async Task Older_valid_publication_rolls_back_to_the_newer_last_known_good_head()
    {
        using var store = new AtomicLootSpawnPublicationStore();
        var service = new LootSpawnSourceImportService(new JsonLootSpawnSourceBundleReader(), store);
        var newer = await service.ImportAsync(Documents(), Context());
        var olderDocuments = Documents(manifest =>
        {
            manifest["generatedUtc"] = "2026-09-15T13:00:00.0000000+00:00";
            manifest["dataThroughUtc"] = "2026-09-15T12:00:00.0000000+00:00";
            manifest["datasetVersion"] = "fixture-older";
        }, content => content["datasetVersion"] = "fixture-older");

        var refused = await service.ImportAsync(olderDocuments, Context());

        Assert.Equal(LootSpawnSourceImportDisposition.QuarantinedRetainedLastKnownGood, refused.Disposition);
        Assert.Equal("publication.superseded", Assert.Single(refused.Diagnostics).Code);
        Assert.Same(newer.Published, refused.LastKnownGood);
    }

    [Fact]
    public async Task Cancellation_never_publishes_or_quarantines()
    {
        using var store = new AtomicLootSpawnPublicationStore();
        var service = new LootSpawnSourceImportService(new JsonLootSpawnSourceBundleReader(), store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.ImportAsync(Documents(), Context(), cancellation.Token));

        Assert.Null(await store.ReadLastKnownGoodAsync(default));
        Assert.Empty(await store.ReadQuarantineAsync());
    }

    [Fact]
    public async Task Source_order_does_not_change_snapshot_candidate_or_coverage_order()
    {
        var reordered = Documents(
            manifest =>
            {
                var coverage = manifest["coverage"]!.AsArray();
                coverage.Insert(0, new JsonObject
                {
                    ["mapId"] = "woods",
                    ["knownRecords"] = 0,
                    ["publishedRecords"] = 0,
                    ["positionedRecords"] = 0,
                    ["floorResolvedRecords"] = 0,
                    ["unresolvedRecords"] = 0,
                });
            },
            content =>
            {
                var snapshots = content["snapshots"]!.AsArray();
                var woods = new JsonObject
                {
                    ["snapshotId"] = "fixture-woods-empty",
                    ["mapId"] = "woods",
                    ["transformVersion"] = "fixture-transform-v1",
                    ["records"] = new JsonArray(),
                };
                snapshots.Insert(0, woods);
                var records = snapshots[1]!["records"]!.AsArray();
                var first = records[0];
                records.RemoveAt(0);
                records.Add(first);
                var items = records[0]!["pool"]!["itemIds"]!.AsArray();
                var item = items[0];
                items.RemoveAt(0);
                items.Add(item);
            });

        var bundle = await ReadAsync(reordered);

        Assert.Equal(["customs", "woods"], bundle.Snapshots.Select(snapshot => snapshot.MapId));
        Assert.Equal(["customs", "woods"], bundle.Coverage.Select(coverage => coverage.MapId));
        Assert.Equal(
            ["customs-curated-map-only", "customs-curated-point"],
            bundle.Snapshots[0].Records.Select(record => record.SpawnId));
        Assert.Equal(
            ["fixture-gpu", "fixture-ledx"],
            bundle.Snapshots[0].Records[0].Candidates.Select(candidate => candidate.ItemId));
    }

    private static async Task<LootSpawnSourceBundle> ReadAsync(LootSpawnSourceDocuments documents) =>
        await new JsonLootSpawnSourceBundleReader().ReadAsync(documents, Context(), default);

    private static LootSpawnSourceImportContext Context()
    {
        var itemProvenance = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "json.tarkov.dev/regular/items",
            Now,
            EvidenceConfidence.Certain,
            new ProducerIdentity("json.tarkov.dev fixture adapter", "1"),
            dataThroughUtc: Now.AddHours(-1),
            reference: "https://json.tarkov.dev/regular/items");
        var items = new Dictionary<string, LootSpawnItemCatalogEntry>(StringComparer.Ordinal)
        {
            ["fixture-gpu"] = new(
                "fixture-gpu", "Synthetic graphics card", "Electronics", 900_000, 820_000, 125_000, 2, itemProvenance),
            ["fixture-ledx"] = new(
                "fixture-ledx", "Synthetic medical item", "Medical", 1_100_000, 990_000, 300_000, 1, itemProvenance),
        };
        var floors = new HashSet<string>(["ground", "first"], StringComparer.Ordinal);
        var maps = new Dictionary<string, LootSpawnMapSourceDefinition>(StringComparer.Ordinal)
        {
            ["customs"] = new("customs", "fixture-transform-v1", new MapSceneBounds(0, 0, 100, 100), floors),
            ["factory"] = new("factory", "fixture-transform-v1", new MapSceneBounds(0, 0, 100, 100), floors),
            ["woods"] = new("woods", "fixture-transform-v1", new MapSceneBounds(0, 0, 100, 100), floors),
        };
        return new(
            Now,
            TimeSpan.FromDays(30),
            items,
            maps,
            new HashSet<string>(["woods", "customs", "factory"], StringComparer.Ordinal));
    }

    private static LootSpawnSourceDocuments Documents(
        Action<JsonObject>? manifest = null,
        Action<JsonObject>? content = null,
        bool rehash = true)
    {
        var fixture = FixtureDirectory();
        var manifestNode = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture, "manifest.json")))!.AsObject();
        var contentNode = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture, "content.json")))!.AsObject();
        manifest?.Invoke(manifestNode);
        content?.Invoke(contentNode);
        var contentBytes = JsonSerializer.SerializeToUtf8Bytes(contentNode);
        if (rehash)
        {
            manifestNode["contentSha256"] = Convert.ToHexString(SHA256.HashData(contentBytes)).ToLowerInvariant();
        }

        return new(JsonSerializer.SerializeToUtf8Bytes(manifestNode), contentBytes);
    }

    private static string FixtureDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "loot-spawns", "source-v1");
}
