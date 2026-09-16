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
        Assert.Equal(Now, snapshot.GeneratedUtc);
        Assert.Equal(Now, snapshot.Provenance.ObservedUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero), snapshot.Provenance.GeneratedUtc);
        Assert.Equal(Now, bundle.Identity.ImportedUtc);

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
        var documents = Documents(content: content =>
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
        var documents = Documents(content: content =>
            content["snapshots"]![0]!["records"]![0]!["label"] = new string('x', 257));

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("source.string-oversized", exception.Code);
    }

    [Fact]
    public async Task Excessive_unknown_nesting_is_still_refused_by_the_global_parser_bound()
    {
        var documents = Documents(content: content =>
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
        var documents = Documents(content: content =>
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
    public async Task Duplicate_floor_claims_are_refused_instead_of_silently_collapsed()
    {
        var documents = Documents(content: content =>
            content["snapshots"]![0]!["records"]![0]!["location"]!["floorIds"] =
                new JsonArray(JsonValue.Create("ground"), JsonValue.Create("ground")));

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("location.floor-duplicate", exception.Code);
    }

    [Fact]
    public async Task Unknown_item_is_not_fabricated_from_a_label()
    {
        var documents = Documents(content: content =>
            content["snapshots"]![0]!["records"]![0]!["pool"]!["itemIds"]![0] = "unknown-item");

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("pool.item-unknown", exception.Code);
    }

    [Fact]
    public async Task Item_field_status_and_provenance_are_preserved_instead_of_manufactured_current()
    {
        var bundle = await new JsonLootSpawnSourceBundleReader().ReadAsync(
            Documents(),
            Context(FreshnessState.Stale),
            default);

        var candidate = Assert.Single(Assert.Single(bundle.Snapshots).Records[1].Candidates);
        Assert.Equal(FreshnessState.Stale, candidate.FleaGrossRoubles.Status.Freshness);
        Assert.Equal("flea-gross", candidate.FleaGrossRoubles.FieldId);
        Assert.Equal("json.tarkov.dev/regular/items", candidate.FleaGrossRoubles.Provenance.SourceIdentifier);
    }

    [Fact]
    public async Task Future_item_evidence_is_refused_instead_of_entering_the_snapshot()
    {
        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(async () =>
            await new JsonLootSpawnSourceBundleReader().ReadAsync(
                Documents(),
                Context(itemObservedUtc: Now.AddMinutes(1)),
                default));

        Assert.Equal("item.evidence-invalid", exception.Code);
    }

    [Fact]
    public async Task Item_fields_cannot_launder_a_noncanonical_reference_as_json_tarkov_dev()
    {
        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(async () =>
            await new JsonLootSpawnSourceBundleReader().ReadAsync(
                Documents(),
                Context(itemReference: "https://unreviewed.example/items"),
                default));

        Assert.Equal("item.source-incompatible", exception.Code);
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
    public async Task Content_hash_identity_must_use_canonical_lowercase_hex()
    {
        var documents = Documents(
            manifest => manifest["contentSha256"] =
                manifest["contentSha256"]!.GetValue<string>().ToUpperInvariant(),
            rehash: false);

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("manifest.hash-invalid", exception.Code);
    }

    [Fact]
    public async Task Identity_strings_with_hidden_surrounding_whitespace_are_refused()
    {
        var documents = Documents(content: content => content["datasetVersion"] = " fixture-2026-09-16");

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("source.string-noncanonical", exception.Code);
    }

    [Fact]
    public async Task Duplicate_json_members_are_refused_before_claims_are_read()
    {
        var original = Documents();
        var manifest = System.Text.Encoding.UTF8.GetString(original.Manifest.Span)
            .Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        var documents = new LootSpawnSourceDocuments(System.Text.Encoding.UTF8.GetBytes(manifest), original.Content);

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("source.duplicate-property", exception.Code);
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
        var documents = Documents(content: content =>
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

    [Fact]
    public async Task Public_maps_and_item_metadata_cannot_cross_game_modes()
    {
        var documents = Documents(manifest =>
        {
            manifest["source"]!["class"] = "publicStructuredData";
            manifest["source"]!["identifier"] = "json.tarkov.dev/regular/maps";
            manifest["source"]!["reference"] = "https://json.tarkov.dev/regular/maps";
        });

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(async () =>
            await new JsonLootSpawnSourceBundleReader().ReadAsync(
                documents,
                Context(
                    itemIdentifier: "json.tarkov.dev/pve/items",
                    itemReference: "https://json.tarkov.dev/pve/items",
                    itemAuthorityIdentifier: "json.tarkov.dev/pve/items",
                    itemAuthorityReference: "https://json.tarkov.dev/pve/items"),
                default));

        Assert.Equal("manifest.source-mode-mismatch", exception.Code);
    }

    [Fact]
    public async Task Self_declared_curated_authority_is_not_trusted()
    {
        var documents = Documents(manifest =>
        {
            manifest["source"]!["identifier"] = "unreviewed:curated-spawns";
            manifest["source"]!["reference"] = "https://unreviewed.example/spawns";
        });

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("manifest.source-unreviewed", exception.Code);
    }

    [Fact]
    public async Task Public_structured_source_requires_the_canonical_endpoint_reference()
    {
        var documents = Documents(manifest =>
        {
            manifest["source"]!["class"] = "publicStructuredData";
            manifest["source"]!["identifier"] = "json.tarkov.dev/regular/maps";
            manifest["source"]!["reference"] = "https://unreviewed.example/maps";
        });

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(() => ReadAsync(documents));

        Assert.Equal("manifest.source-reference-incompatible", exception.Code);
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
    public async Task Same_generation_cannot_rewrite_reviewed_manifest_metadata()
    {
        using var store = new AtomicLootSpawnPublicationStore();
        var service = new LootSpawnSourceImportService(new JsonLootSpawnSourceBundleReader(), store);
        var first = await service.ImportAsync(Documents(), Context());
        var changed = Documents(manifest => manifest["source"]!["license"] = "Different reviewed fixture terms");

        var refused = await service.ImportAsync(
            changed,
            Context(locationLicense: "Different reviewed fixture terms"));

        Assert.Equal("publication.identity-conflict", Assert.Single(refused.Diagnostics).Code);
        Assert.Same(first.Published, refused.LastKnownGood);
    }

    [Fact]
    public async Task Later_generation_cannot_regress_the_data_through_head()
    {
        using var store = new AtomicLootSpawnPublicationStore();
        var service = new LootSpawnSourceImportService(new JsonLootSpawnSourceBundleReader(), store);
        var first = await service.ImportAsync(Documents(), Context());
        var regressed = Documents(manifest =>
        {
            manifest["generatedUtc"] = "2026-09-16T13:00:00.0000000+00:00";
            manifest["dataThroughUtc"] = "2026-09-15T11:00:00.0000000+00:00";
        });

        var refused = await service.ImportAsync(regressed, Context());

        Assert.Equal("publication.evidence-regression", Assert.Single(refused.Diagnostics).Code);
        Assert.Same(first.Published, refused.LastKnownGood);
    }

    [Fact]
    public async Task Later_generation_cannot_silently_shrink_last_known_good_coverage()
    {
        using var store = new AtomicLootSpawnPublicationStore();
        var service = new LootSpawnSourceImportService(new JsonLootSpawnSourceBundleReader(), store);
        var first = await service.ImportAsync(Documents(), Context());
        var shrunken = Documents(
            manifest =>
            {
                manifest["generatedUtc"] = "2026-09-16T13:00:00.0000000+00:00";
                manifest["coverage"] = new JsonArray();
            },
            content => content["snapshots"] = new JsonArray());

        var refused = await service.ImportAsync(shrunken, Context());

        Assert.Equal("publication.coverage-regression", Assert.Single(refused.Diagnostics).Code);
        Assert.Same(first.Published, refused.LastKnownGood);
    }

    [Fact]
    public async Task Later_import_cannot_replace_newer_item_evidence_with_older_catalog_evidence()
    {
        using var store = new AtomicLootSpawnPublicationStore();
        var service = new LootSpawnSourceImportService(new JsonLootSpawnSourceBundleReader(), store);
        var first = await service.ImportAsync(Documents(), Context());

        var refused = await service.ImportAsync(
            Documents(),
            Context(importedUtc: Now.AddHours(1), itemObservedUtc: Now.AddHours(-1)));

        Assert.Equal("publication.item-evidence-regression", Assert.Single(refused.Diagnostics).Code);
        Assert.Same(first.Published, refused.LastKnownGood);
    }

    [Fact]
    public async Task Same_import_observation_cannot_publish_conflicting_item_values()
    {
        using var store = new AtomicLootSpawnPublicationStore();
        var service = new LootSpawnSourceImportService(new JsonLootSpawnSourceBundleReader(), store);
        var first = await service.ImportAsync(Documents(), Context());

        var refused = await service.ImportAsync(Documents(), Context(gpuFleaGrossRoubles: 910_000));

        Assert.Equal("publication.item-evidence-conflict", Assert.Single(refused.Diagnostics).Code);
        Assert.Same(first.Published, refused.LastKnownGood);
    }

    [Fact]
    public async Task One_store_cannot_switch_from_primary_to_a_different_source_authority()
    {
        using var store = new AtomicLootSpawnPublicationStore();
        var service = new LootSpawnSourceImportService(new JsonLootSpawnSourceBundleReader(), store);
        var first = await service.ImportAsync(Documents(), Context());
        var different = Documents(manifest =>
        {
            manifest["generatedUtc"] = "2026-09-16T13:00:00.0000000+00:00";
            manifest["source"]!["identifier"] = "fixture:reviewed-supplement";
            manifest["source"]!["reference"] = "fixtures/loot-spawns/reviewed-supplement.md";
        });

        var refused = await service.ImportAsync(
            different,
            Context(
                locationIdentifier: "fixture:reviewed-supplement",
                locationReference: "fixtures/loot-spawns/reviewed-supplement.md"));

        Assert.Equal("publication.source-conflict", Assert.Single(refused.Diagnostics).Code);
        Assert.Same(first.Published, refused.LastKnownGood);
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

    private static LootSpawnSourceImportContext Context(
        FreshnessState itemFreshness = FreshnessState.Current,
        string locationIdentifier = "fixture:loot-spawns/source-v1",
        string locationReference = "fixtures/loot-spawns/README.md",
        string locationLicense = "CC0-1.0 synthetic test fixture",
        DateTimeOffset? itemObservedUtc = null,
        string itemIdentifier = "json.tarkov.dev/regular/items",
        string itemReference = "https://json.tarkov.dev/regular/items",
        string itemAuthorityIdentifier = "json.tarkov.dev/regular/items",
        string itemAuthorityReference = "https://json.tarkov.dev/regular/items",
        DateTimeOffset? importedUtc = null,
        long gpuFleaGrossRoubles = 900_000)
    {
        var importInstant = importedUtc ?? Now;
        var observedUtc = itemObservedUtc ?? importInstant;
        var itemProvenance = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            itemIdentifier,
            observedUtc,
            EvidenceConfidence.Certain,
            new ProducerIdentity("json.tarkov.dev fixture adapter", "1"),
            dataThroughUtc: observedUtc.AddHours(-1),
            reference: itemReference);
        var items = new Dictionary<string, LootSpawnItemCatalogEntry>(StringComparer.Ordinal)
        {
            ["fixture-gpu"] = new(
                "fixture-gpu",
                "Synthetic graphics card",
                "Electronics",
                ItemValue<long>("flea-gross", gpuFleaGrossRoubles, itemProvenance, itemFreshness),
                ItemValue<long>("flea-net", 820_000L, itemProvenance, itemFreshness),
                ItemValue<long>("best-trader", 125_000L, itemProvenance, itemFreshness),
                ItemValue<int>("occupied-squares", 2, itemProvenance, itemFreshness)),
            ["fixture-ledx"] = new(
                "fixture-ledx",
                "Synthetic medical item",
                "Medical",
                ItemValue<long>("flea-gross", 1_100_000L, itemProvenance, itemFreshness),
                ItemValue<long>("flea-net", 990_000L, itemProvenance, itemFreshness),
                ItemValue<long>("best-trader", 300_000L, itemProvenance, itemFreshness),
                ItemValue<int>("occupied-squares", 1, itemProvenance, itemFreshness)),
        };
        var floors = new HashSet<string>(["ground", "first"], StringComparer.Ordinal);
        var maps = new Dictionary<string, LootSpawnMapSourceDefinition>(StringComparer.Ordinal)
        {
            ["customs"] = new("customs", "fixture-transform-v1", new MapSceneBounds(0, 0, 100, 100), floors),
            ["factory"] = new("factory", "fixture-transform-v1", new MapSceneBounds(0, 0, 100, 100), floors),
            ["woods"] = new("woods", "fixture-transform-v1", new MapSceneBounds(0, 0, 100, 100), floors),
        };
        return new(
            importInstant,
            TimeSpan.FromDays(30),
            items,
            maps,
            new HashSet<string>(["woods", "customs", "factory"], StringComparer.Ordinal),
            new LootSpawnItemCatalogAuthority(itemAuthorityIdentifier, itemAuthorityReference),
            [
                new(
                    EvidenceSourceClass.CuratedData,
                    locationIdentifier,
                    locationReference,
                    locationLicense,
                    new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.75)),
                new(
                    EvidenceSourceClass.PublicStructuredData,
                    "json.tarkov.dev/regular/maps",
                    "https://json.tarkov.dev/regular/maps",
                    "CC0-1.0 synthetic test fixture",
                    new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.75)),
            ]);
    }

    private static EvidencedValue<T?> ItemValue<T>(
        string fieldId,
        T? value,
        EvidenceProvenance provenance,
        FreshnessState freshness)
        where T : struct => new(
        fieldId,
        value,
        new ResultStatus(
            value is null ? ResultCompleteness.Unknown : ResultCompleteness.Complete,
            freshness,
            value is null ? "item.value-unavailable" : "item.value-current"),
        provenance);

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
