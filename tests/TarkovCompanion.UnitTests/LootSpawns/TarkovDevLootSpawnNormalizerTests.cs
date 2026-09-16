using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Infrastructure.GameData.LootSpawns;
using TarkovCompanion.Infrastructure.Maps;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.UnitTests.LootSpawns;

public sealed class TarkovDevLootSpawnNormalizerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 18, 0, 0, TimeSpan.Zero);
    private static readonly Uri CatalogUri = new(
        "https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Production_responses_fold_aliases_project_exact_points_and_measure_unpublished_containers()
    {
        var request = Request();
        var normalized = await NormalizeAsync(request);

        var snapshot = Assert.Single(normalized.Bundle.Snapshots);
        Assert.Equal("synthetic-port", snapshot.MapId);
        Assert.Equal(3, snapshot.Records.Count);
        Assert.Equal(new LootSpawnCoverage(3, 2, 2, 1), snapshot.Coverage);
        Assert.Equal(ResultCompleteness.Partial, snapshot.Status.Completeness);
        Assert.Equal(FreshnessState.Current, snapshot.Status.Freshness);
        Assert.Equal(EvidenceSourceClass.PublicStructuredData, snapshot.Provenance.SourceClass);
        Assert.Equal("json.tarkov.dev/regular/maps", snapshot.Provenance.SourceIdentifier);
        Assert.Equal(Now, snapshot.GeneratedUtc);
        Assert.Equal(Now.AddHours(-2), snapshot.Provenance.DataThroughUtc);
        Assert.Equal(
            Hash(request.Maps.RawSourceJson!),
            normalized.Bundle.Identity.Artifacts.Single(value => value.Role == "maps-source").ContentSha256);
        Assert.Equal(
            Hash(request.Items.RawSourceJson!),
            normalized.Bundle.Identity.Artifacts.Single(value => value.Role == "items-source").ContentSha256);
        Assert.Equal(
            request.MapCatalog.Provenance.ContentSha256,
            normalized.Bundle.Identity.Artifacts.Single(value => value.Role == "map-catalog").ContentSha256);

        var exact = Assert.Single(snapshot.Records, record => record.Candidates.Count == 2);
        Assert.Equal(LootSpawnPrecision.ExactPoint, exact.Location.Precision);
        var point = Assert.Single(exact.Location.GeometryPoints!);
        Assert.Equal(9, point.X, 10);
        Assert.Equal(19, point.Y, 10);
        Assert.Equal(["layer-0-upper-floor"], exact.Location.FloorIds);
        Assert.Equal(LootSpawnPoolKind.UnweightedCandidates, exact.PoolKind);
        Assert.Equal(ResultCompleteness.Unknown, exact.SpawnProbability.Status.Completeness);
        Assert.Equal(ResultCompleteness.Unknown, exact.RespawnBehavior.Status.Completeness);

        var salewa = Assert.Single(exact.Candidates, candidate => candidate.ItemId == "item-001");
        Assert.Equal("Salewa first aid kit", salewa.DisplayName);
        Assert.Equal("Medicine", salewa.Category);
        Assert.Equal(21_000, salewa.FleaGrossRoubles.Value);
        Assert.Null(salewa.FleaNetRoubles.Value);
        Assert.Equal(ResultCompleteness.Unknown, salewa.FleaNetRoubles.Status.Completeness);
        Assert.Equal(12_000, salewa.BestTraderRoubles.Value);
        Assert.Equal(2, salewa.OccupiedSquares.Value);
        Assert.Equal("json.tarkov.dev/regular/items", salewa.FleaGrossRoubles.Provenance.SourceIdentifier);

        var mapOnly = Assert.Single(snapshot.Records, record => record.Location.Precision == LootSpawnPrecision.MapOnly);
        Assert.Null(mapOnly.Location.Geometry);
        Assert.Empty(mapOnly.Location.FloorIds);
        Assert.Contains(snapshot.Records, record =>
            record.Location.FloorIds.Count == 1 && record.Location.FloorIds[0] == "base");

        var coverage = Assert.Single(normalized.Bundle.Coverage);
        Assert.Equal(4, coverage.KnownRecordCount);
        Assert.Equal(3, coverage.PublishedRecordCount);
        Assert.Equal(2, coverage.PositionedRecordCount);
        Assert.Equal(2, coverage.FloorResolvedRecordCount);
        Assert.Equal(1, coverage.UnresolvedRecordCount);
        Assert.Contains(normalized.Bundle.Diagnostics, value => value.Code == "coverage.map-partial");
        Assert.Contains(normalized.Bundle.Diagnostics, value => value.Code == "coverage.map-unresolved");

        var binding = Assert.Single(normalized.MapBindings);
        Assert.Equal(["synthetic-port", "synthetic-port-night"], binding.SourceMapIds);
        Assert.NotNull(binding.SceneBounds);
        Assert.Equal(snapshot.TransformVersion, binding.TransformVersion);
        Assert.Contains("base", binding.FloorIds);
    }

    [Fact]
    public async Task Missing_transform_keeps_candidate_membership_as_map_only_without_inventing_coordinates()
    {
        var request = Request();
        var source = JsonNode.Parse(request.MapCatalogJson)!.AsArray();
        source[0]!["maps"]![0]!.AsObject().Remove("transform");
        var catalogJson = source.ToJsonString();
        var catalog = TarkovDevMapCatalogParser.Parse(
            catalogJson,
            CatalogUri,
            Now.AddHours(-2));

        var normalized = await NormalizeAsync(request with
        {
            MapCatalogJson = catalogJson,
            MapCatalog = catalog,
        });

        var snapshot = Assert.Single(normalized.Bundle.Snapshots);
        Assert.All(snapshot.Records, record => Assert.Equal(LootSpawnPrecision.MapOnly, record.Location.Precision));
        Assert.Equal(0, snapshot.Coverage.Positioned);
        Assert.Equal(3, snapshot.Coverage.Unresolved);
        Assert.Null(Assert.Single(normalized.MapBindings).SceneBounds);
    }

    [Fact]
    public async Task Exact_catalog_document_wins_over_a_different_caller_supplied_object_graph()
    {
        var request = Request();
        var location = Assert.Single(request.MapCatalog.Locations);
        var variant = location.Variants[0] with { Transform = null };
        var altered = request.MapCatalog with
        {
            Locations = [location with { Variants = [variant] }],
        };

        var normalized = await NormalizeAsync(request with { MapCatalog = altered });

        Assert.Equal(2, Assert.Single(normalized.Bundle.Snapshots).Coverage.Positioned);
    }

    [Fact]
    public async Task Catalog_document_that_does_not_match_its_provenance_hash_is_refused()
    {
        var request = Request();

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(async () =>
            await NormalizeAsync(request with { MapCatalogJson = request.MapCatalogJson + "\n" }));

        Assert.Equal("map-catalog.content-mismatch", exception.Code);
    }

    [Fact]
    public async Task Gross_alias_mismatch_cannot_publish_an_empty_first_snapshot_set()
    {
        var request = Request();
        var source = JsonNode.Parse(request.MapCatalogJson)!.AsArray();
        source[0]!["normalizedName"] = "different-map";
        source[0]!["maps"]![0]!["altMaps"] = new JsonArray("different-map-night");
        var catalogJson = source.ToJsonString();
        var catalog = TarkovDevMapCatalogParser.Parse(
            catalogJson,
            CatalogUri,
            Now.AddHours(-2));

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(async () =>
            await NormalizeAsync(request with
            {
                MapCatalogJson = catalogJson,
                MapCatalog = catalog,
            }));

        Assert.Equal("source.map-coverage-insufficient", exception.Code);
    }

    [Fact]
    public async Task Exact_response_document_wins_over_a_different_caller_supplied_object_graph()
    {
        var request = Request();
        var emptyMaps = new TarkovDevMapsData
        {
            Maps = new Dictionary<string, TarkovDevMap>(StringComparer.Ordinal),
        };
        var maps = request.Maps with { Data = emptyMaps };

        var normalized = await NormalizeAsync(request with { Maps = maps });

        Assert.Equal(3, Assert.Single(normalized.Bundle.Snapshots).Records.Count);
    }

    [Fact]
    public async Task Cross_mode_or_unlabelled_responses_are_refused_before_candidate_join()
    {
        var request = Request();

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(async () =>
            await NormalizeAsync(request with
            {
                Items = request.Items with { SourceKey = "pve/items" },
            }));

        Assert.Equal("source.mode-mismatch", exception.Code);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("Data")]
    public async Task Duplicate_source_json_members_are_refused_instead_of_using_last_value_wins(
        string competingName)
    {
        var request = Request();
        var duplicate = request.Maps.Json.Replace(
            "\"data\":",
            $"\"{competingName}\":null,\"data\":",
            StringComparison.Ordinal);

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(async () =>
            await NormalizeAsync(request with { Maps = request.Maps with { Json = duplicate } }));

        Assert.Equal("source.duplicate-property", exception.Code);
    }

    [Fact]
    public async Task Candidate_absent_from_the_exact_items_response_drops_only_that_pool_and_reports_coverage()
    {
        var request = Request();
        var maps = Maps(firstDayPool: ["unknown-item", "item-002"]);
        var response = Response(maps, "regular/maps", Now.AddHours(-1));

        var normalized = await NormalizeAsync(request with { Maps = response });

        var snapshot = Assert.Single(normalized.Bundle.Snapshots);
        Assert.Equal(2, snapshot.Records.Count);
        Assert.Equal(4, Assert.Single(normalized.Bundle.Coverage).KnownRecordCount);
        var partial = Assert.Single(normalized.Bundle.Diagnostics, value => value.Code == "coverage.map-partial");
        Assert.Contains("1 loose pool", partial.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_endpoint_evidence_remains_stale_and_never_becomes_current_during_normalization()
    {
        var request = Request();
        var staleItems = request.Items with { IsStale = true };
        var staleMaps = request.Maps with { IsStale = true };

        var normalized = await NormalizeAsync(request with { Items = staleItems, Maps = staleMaps });

        var snapshot = Assert.Single(normalized.Bundle.Snapshots);
        Assert.Equal(FreshnessState.Stale, snapshot.Status.Freshness);
        Assert.All(snapshot.Records, record => Assert.Equal(FreshnessState.Stale, record.Status.Freshness));
        Assert.All(
            snapshot.Records.SelectMany(record => record.Candidates),
            candidate => Assert.Equal(FreshnessState.Stale, candidate.OccupiedSquares.Status.Freshness));
    }

    [Fact]
    public async Task Oversized_loose_pool_is_refused_before_domain_allocation()
    {
        var request = Request();
        var oversizedPool = Enumerable.Range(0, LootSpawnRecord.MaximumCandidates + 1)
            .Select(index => $"item-{index}")
            .ToArray();
        var maps = Maps(firstDayPool: oversizedPool);
        var response = Response(maps, "regular/maps", Now.AddHours(-1));

        var exception = await Assert.ThrowsAsync<LootSpawnSourceImportException>(async () =>
            await NormalizeAsync(request with { Maps = response }));

        Assert.Equal("source.collection-oversized", exception.Code);
    }

    [Fact]
    public async Task Spawn_identity_is_stable_when_source_record_order_changes()
    {
        var request = Request();
        var original = await NormalizeAsync(request);
        var maps = Maps(reverseDayLooseOrder: true);
        var reordered = await NormalizeAsync(request with
        {
            Maps = Response(maps, "regular/maps", Now.AddHours(-1)),
        });

        Assert.Equal(
            Assert.Single(original.Bundle.Snapshots).Records.Select(record => record.SpawnId),
            Assert.Single(reordered.Bundle.Snapshots).Records.Select(record => record.SpawnId));
    }

    private static async Task<TarkovDevLootSpawnNormalizationResult> NormalizeAsync(
        TarkovDevLootSpawnNormalizationRequest request) =>
        await new TarkovDevLootSpawnNormalizer().NormalizeAsync(request);

    private static TarkovDevLootSpawnNormalizationRequest Request()
    {
        var catalogJson = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "maps",
            "tarkov-dev-catalog.synthetic.json"));
        var catalog = TarkovDevMapCatalogParser.Parse(
            catalogJson,
            CatalogUri,
            Now.AddHours(-2));
        return new(
            GameMode.Regular,
            "en",
            Now,
            Response(Maps(), "regular/maps", Now.AddHours(-1), Now.AddMinutes(-90)),
            Response(Items(), "regular/items", Now.AddMinutes(-30), Now.AddMinutes(-45)),
            catalogJson,
            catalog);
    }

    private static TarkovDevMapsData Maps(
        IReadOnlyList<string>? firstDayPool = null,
        bool reverseDayLooseOrder = false)
    {
        var firstLoose = new TarkovDevMapLoot
        {
            Position = new TarkovDevMapPosition { X = 4, Y = 6, Z = 2 },
            Items = firstDayPool ?? ["item-001", "item-002"],
        };
        var secondLoose = new TarkovDevMapLoot
        {
            Position = new TarkovDevMapPosition { X = 101, Y = 1, Z = 2 },
            Items = ["item-001"],
        };
        IReadOnlyList<TarkovDevMapLoot> dayLoose = reverseDayLooseOrder
            ? [secondLoose, firstLoose]
            : [firstLoose, secondLoose];
        var day = new TarkovDevMap
        {
            Id = "map-day",
            Name = "Synthetic Port",
            NormalizedName = "synthetic-port",
            Extracts =
            [
                new TarkovDevMapExtract
                {
                    Id = "extract-one",
                    Name = "Fixture extract",
                    Position = new TarkovDevMapPosition { X = 0, Y = 0, Z = 0 },
                },
            ],
            LootContainers =
            [
                new TarkovDevMapLoot
                {
                    LootContainer = "container-type",
                    Position = new TarkovDevMapPosition { X = 1, Y = 1, Z = 1 },
                },
            ],
            LootLoose = dayLoose,
        };
        var night = new TarkovDevMap
        {
            Id = "map-night",
            Name = "Synthetic Port Night",
            NormalizedName = "synthetic-port-night",
            LootLoose =
            [
                new TarkovDevMapLoot
                {
                    Position = new TarkovDevMapPosition { X = 5, Y = 3, Z = 3 },
                    Items = ["item-002"],
                },
            ],
        };
        return new()
        {
            Maps = new Dictionary<string, TarkovDevMap>(StringComparer.Ordinal)
            {
                [day.Id] = day,
                [night.Id] = night,
            },
            LootContainers = new Dictionary<string, TarkovDevLootContainer>(StringComparer.Ordinal)
            {
                ["container-type"] = new()
                {
                    Id = "container-type",
                    NormalizedName = "synthetic-container",
                },
            },
        };
    }

    private static TarkovDevItemsData Items()
    {
        var first = new TarkovDevItem
        {
            Id = "item-001",
            Name = "item-001 Name",
            ShortName = "item-001 ShortName",
            NormalizedName = "salewa-first-aid-kit",
            Width = 1,
            Height = 2,
            Updated = Now.AddHours(-3),
            LastLowPrice = 21_000,
            Types = ["meds"],
            Categories = ["medical"],
            SellToTrader =
            [
                new TarkovDevTraderPrice
                {
                    Trader = "therapist",
                    Currency = "RUB",
                    Price = 12_000,
                    PriceRub = 12_000,
                },
            ],
        };
        var second = new TarkovDevItem
        {
            Id = "item-002",
            Name = "Synthetic barter item",
            ShortName = "Barter",
            Width = 2,
            Height = 1,
            Types = ["barter", "noFlea"],
            Categories = ["barter"],
        };
        return new()
        {
            Items = new Dictionary<string, TarkovDevItem>(StringComparer.Ordinal)
            {
                [first.Id] = first,
                [second.Id] = second,
            },
            ItemCategories = new Dictionary<string, TarkovDevItemCategory>(StringComparer.Ordinal)
            {
                ["medical"] = new() { Id = "medical", Name = "Medical" },
                ["barter"] = new() { Id = "barter", Name = "Barter" },
            },
        };
    }

    private static TarkovDevResponse<T> Response<T>(
        T data,
        string sourceKey,
        DateTimeOffset cachedUtc,
        DateTimeOffset? lastModified = null)
    {
        var json = JsonSerializer.Serialize(new TarkovDevEnvelope<T> { Data = data }, JsonOptions);
        return new(
            data,
            json,
            cachedUtc,
            IsFromCache: false,
            IsStale: false,
            ETag: "\"fixture\"",
            LastModified: lastModified,
            RawSourceJson: json,
            RefusalReason: null,
            SourceKey: sourceKey);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
