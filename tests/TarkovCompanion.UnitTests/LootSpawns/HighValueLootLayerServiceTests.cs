using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.LootSpawns;

public sealed class HighValueLootLayerServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Unweighted_pool_shows_a_ceiling_and_counts_without_inventing_expected_value()
    {
        var spawn = Spawn(
            "customs-marked-room",
            [Candidate("gpu", "Graphics card", 900_000), Candidate("cable", "Military cable", 80_000)]);
        var filter = Filter(new LootSpawnValueThresholds(100_000, 150_000, 500_000, 800_000));

        var result = Build(Snapshot([spawn]), filter);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(80_000, entry.MinimumValue);
        Assert.Equal(900_000, entry.MaximumValue);
        Assert.Equal(1, entry.HighValueCandidateCount);
        Assert.Equal(LootSpawnValueTier.Exceptional, entry.Tier);
        Assert.Null(entry.ExpectedValueRoubles);
        Assert.Contains("Potential up to", entry.Summary, StringComparison.Ordinal);
        Assert.Contains("2 candidates", entry.Summary, StringComparison.Ordinal);
        var marker = Assert.Single(result.Objects);
        Assert.Equal(MapSceneTruthKind.PotentialSpawn, marker.Truth);
        Assert.Equal(MapSceneObjectKind.LootSpawn, marker.Kind);
        Assert.Equal(HighValueLootLayerService.LayerId, marker.LayerId);
    }

    [Fact]
    public void Map_only_knowledge_stays_in_the_list_without_a_fabricated_marker()
    {
        var spawn = Spawn(
            "woods-map-only",
            [Candidate("ledx", "LEDX", 1_100_000)],
            new LootSpawnLocation(LootSpawnPrecision.MapOnly, null));

        var result = Build(Snapshot([spawn]));

        var entry = Assert.Single(result.Entries);
        Assert.Null(entry.SceneObjectId);
        Assert.Empty(result.Objects);
        Assert.Equal(LootSpawnPrecision.MapOnly, entry.Spawn.Location.Precision);
    }

    [Fact]
    public void Out_of_bounds_geometry_is_quarantined_instead_of_clamped_to_an_edge()
    {
        var spawn = Spawn(
            "customs-outside",
            [Candidate("gpu", "Graphics card", 900_000)],
            new LootSpawnLocation(LootSpawnPrecision.ExactPoint, MapSceneGeometry.At(new(250, 20))));

        var result = Build(Snapshot([spawn]));

        Assert.Empty(result.Objects);
        Assert.Empty(result.Entries);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Kind == HighValueLootDiagnosticKind.InvalidGeometry);
        Assert.Equal(spawn.SpawnId, diagnostic.SpawnId);
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
    }

    [Fact]
    public void Profile_need_can_elevate_a_spawn_but_stale_price_stays_unknown()
    {
        var need = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.CurrentQuest,
            "quest.current",
            "Needed for the active quest.",
            CompleteStatus,
            Provenance("need"));
        var stalePrice = Provenance("stale-price", Now.AddHours(-2));
        var candidate = Candidate("gpu", "Graphics card", 900_000, [need], stalePrice);
        var spawn = Spawn("customs-quest", [candidate]);

        var result = Build(Snapshot([spawn]), Filter(maximumPriceAge: TimeSpan.FromMinutes(30)));

        var entry = Assert.Single(result.Entries);
        Assert.Equal(LootSpawnValueTier.ProfileRelevant, entry.Tier);
        Assert.Null(entry.MinimumValue);
        Assert.Null(entry.MaximumValue);
        Assert.Contains(entry.MissingFacts, fact => fact.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("quest.current", Assert.Single(entry.ProfileNeeds).Code);
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
    }

    [Fact]
    public void Specific_floor_filter_does_not_treat_unknown_floor_as_every_floor()
    {
        var spawn = Spawn("reserve-unknown-floor", [Candidate("gpu", "Graphics card", 900_000)]);
        var filter = new HighValueLootFilter(
            LootSpawnValueBasis.BestNet,
            LootSpawnValueThresholds.Default,
            TimeSpan.FromHours(1),
            TimeSpan.FromDays(90),
            0.5,
            floorId: "bunker");

        var result = Build(Snapshot([spawn]), filter);

        Assert.Empty(result.Entries);
        Assert.Empty(result.Objects);
        Assert.Contains(result.Diagnostics, item => item.Kind == HighValueLootDiagnosticKind.FloorUnknown);
    }

    [Fact]
    public void Stale_last_known_snapshot_remains_renderable_and_says_that_it_is_stale()
    {
        var snapshot = Snapshot(
            [Spawn("customs-stale", [Candidate("gpu", "Graphics card", 900_000)])],
            freshness: FreshnessState.Stale);

        var result = Build(snapshot);

        Assert.Single(result.Objects);
        Assert.Equal(FreshnessState.Stale, result.Status.Freshness);
        Assert.Contains("Stale", result.CompactLegend, StringComparison.Ordinal);
    }

    [Fact]
    public void High_value_only_preset_keeps_orientation_and_user_required_context()
    {
        var layers = new[]
        {
            new MapSceneLayer(new("labels"), "Labels", 1, true),
            new MapSceneLayer(new("extracts"), "Extracts", 2, true),
            new MapSceneLayer(new("hazards"), "Hazards", 3, true),
            HighValueLootLayerService.Layer,
        };

        var states = HighValueLootLayerPreset.Create(layers, [new("hazards")]);

        Assert.False(states.Single(state => state.LayerId == new MapSceneLayerId("labels")).IsVisible);
        Assert.True(states.Single(state => state.LayerId == new MapSceneLayerId("extracts")).IsVisible);
        Assert.True(states.Single(state => state.LayerId == new MapSceneLayerId("hazards")).IsVisible);
        Assert.True(states.Single(state => state.LayerId == HighValueLootLayerService.LayerId).IsVisible);
    }

    [Fact]
    public void Contracts_reject_fake_precision_and_mislabelled_single_item_pools()
    {
        Assert.Throws<ArgumentException>(() => new LootSpawnLocation(
            LootSpawnPrecision.MapOnly,
            MapSceneGeometry.At(new(20, 20))));

        Assert.Throws<ArgumentException>(() => new LootSpawnRecord(
            "bad-pool",
            "customs",
            "Bad pool",
            Point(),
            LootSpawnPoolKind.SingleKnownItem,
            [Candidate("a", "A", 100_000), Candidate("b", "B", 110_000)],
            Unknown<double?>("probability"),
            Unknown<string?>("respawn"),
            "dataset-1",
            "transform-1",
            CompleteStatus,
            Provenance("spawn")));
    }

    [Fact]
    public void Snapshot_copies_records_before_publication()
    {
        var records = new List<LootSpawnRecord>
        {
            Spawn("customs-one", [Candidate("gpu", "Graphics card", 900_000)]),
        };
        var snapshot = Snapshot(records);

        records.Clear();

        Assert.Single(snapshot.Records);
        Assert.Equal(1, snapshot.Coverage.Published);
    }

    private static HighValueLootLayerResult Build(
        LootSpawnSnapshot snapshot,
        HighValueLootFilter? filter = null) => new HighValueLootLayerService().Build(new(
        "customs",
        "transform-1",
        new MapSceneBounds(0, 0, 100, 100),
        Now,
        filter ?? Filter(),
        snapshot));

    private static LootSpawnSnapshot Snapshot(
        IReadOnlyList<LootSpawnRecord> records,
        FreshnessState freshness = FreshnessState.Current)
    {
        var positioned = records.Count(record => record.Location.Geometry is not null);
        var floors = records.Count(record => record.Location.Geometry is not null && record.Location.FloorIds.Count > 0);
        return new(
            "snapshot-1",
            "dataset-1",
            "customs",
            "transform-1",
            Now.AddMinutes(-10),
            new ResultStatus(ResultCompleteness.Complete, freshness),
            new LootSpawnCoverage(records.Count, positioned, floors, records.Count - positioned),
            Provenance("snapshot"),
            records);
    }

    private static LootSpawnRecord Spawn(
        string id,
        IReadOnlyList<LootSpawnCandidate> candidates,
        LootSpawnLocation? location = null) => new(
        id,
        "customs",
        $"Spawn {id}",
        location ?? Point(),
        candidates.Count == 1 ? LootSpawnPoolKind.SingleKnownItem : LootSpawnPoolKind.UnweightedCandidates,
        candidates,
        Unknown<double?>("probability"),
        Unknown<string?>("respawn"),
        "dataset-1",
        "transform-1",
        CompleteStatus,
        Provenance($"spawn-{id}"));

    private static LootSpawnLocation Point() => new(
        LootSpawnPrecision.ExactPoint,
        MapSceneGeometry.At(new(20, 30)));

    private static LootSpawnCandidate Candidate(
        string id,
        string name,
        long value,
        IReadOnlyList<LootSpawnProfileNeed>? needs = null,
        EvidenceProvenance? priceProvenance = null) => new(
        id,
        name,
        "electronics",
        Complete<long?>("flea-gross", value + 25_000, priceProvenance),
        Complete<long?>("flea-net", value, priceProvenance),
        Complete<long?>("trader", value / 2, priceProvenance),
        Complete<int?>("squares", 2, priceProvenance),
        needs);

    private static HighValueLootFilter Filter(
        LootSpawnValueThresholds? thresholds = null,
        TimeSpan? maximumPriceAge = null) => new(
        LootSpawnValueBasis.BestNet,
        thresholds ?? LootSpawnValueThresholds.Default,
        maximumPriceAge ?? TimeSpan.FromHours(1),
        TimeSpan.FromDays(90),
        0.5);

    private static EvidencedValue<T> Complete<T>(
        string id,
        T value,
        EvidenceProvenance? provenance = null) => new(
        id,
        value,
        CompleteStatus,
        provenance ?? Provenance(id));

    private static EvidencedValue<T> Unknown<T>(string id) => new(
        id,
        default,
        new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current),
        Provenance(id));

    private static EvidenceProvenance Provenance(string id, DateTimeOffset? observedUtc = null) => new(
        EvidenceSourceClass.PublicStructuredData,
        $"fixture://{id}",
        observedUtc ?? Now.AddMinutes(-10),
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.95),
        new ProducerIdentity("loot-spawn-fixture", "1"));

    private static ResultStatus CompleteStatus { get; } = new(
        ResultCompleteness.Complete,
        FreshnessState.Current);
}
