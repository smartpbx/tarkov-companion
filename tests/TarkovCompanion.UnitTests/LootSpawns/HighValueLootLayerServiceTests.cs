using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.LootSpawns;

public sealed class HighValueLootLayerServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Minimum_value_filters_by_the_selected_item_or_slot_basis()
    {
        var snapshot = Snapshot([Spawn("bulky-value", [Candidate("bulky", "Bulky value", 300_000)])]);
        var perItem = Filter(
            valueBasis: LootSpawnValueBasis.BestNet,
            includeProfileRelevant: false,
            minimumValueRoubles: 250_000);
        var perSlot = Filter(
            valueBasis: LootSpawnValueBasis.ValuePerSquare,
            includeProfileRelevant: false,
            minimumValueRoubles: 250_000);

        Assert.Single(Build(snapshot, perItem).Entries);
        Assert.Empty(Build(snapshot, perSlot).Entries);
    }

    [Fact]
    public void Any_value_keeps_a_known_below_default_value_visible_to_the_default_tier_projection()
    {
        var snapshot = Snapshot([Spawn("wire", [Candidate("wire", "Wire", 10_000)])]);
        var any = Filter(includeProfileRelevant: false, minimumValueRoubles: 0);

        var entry = Assert.Single(Build(snapshot, any).Entries);

        Assert.Equal(LootSpawnValueTier.Qualifying, entry.Tier);
        Assert.Equal(10_000, entry.MaximumValue);
    }

    [Fact]
    public void Unweighted_pool_shows_a_ceiling_and_counts_without_inventing_expected_value()
    {
        var spawn = Spawn(
            "customs-marked-room",
            [Candidate("gpu", "Graphics card", 900_000), Candidate("cable", "Military cable", 80_000)]);
        var filter = Filter(new LootSpawnValueThresholds(100_000, 150_000, 500_000, 800_000));

        var result = Build(Snapshot([spawn]), filter);

        Assert.Equal("customs", result.MapId);
        Assert.Equal("transform-1", result.TransformVersion);
        Assert.Same(filter, result.AppliedFilter);
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
    public void Candidate_filter_keeps_the_full_unweighted_pool_denominator_visible()
    {
        var spawn = Spawn(
            "customs-filtered-pool",
            [
                Candidate("gpu", "Graphics card", 900_000),
                Candidate("cable", "Military cable", 80_000),
                Candidate("wire", "Wire", 10_000),
            ]);
        var filter = Filter(itemIds: ["gpu"]);

        var entry = Assert.Single(Build(Snapshot([spawn]), filter).Entries);

        Assert.Equal(1, entry.MatchedCandidateCount);
        Assert.Equal(3, entry.Spawn.Candidates.Count);
        Assert.Contains("1 of 3 candidates match filter", entry.Summary, StringComparison.Ordinal);
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
            new LootSpawnLocation(LootSpawnPrecision.ExactPoint, [new(250, 20)]));

        var result = Build(Snapshot([spawn]));

        Assert.Empty(result.Objects);
        Assert.Empty(result.Entries);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Kind == HighValueLootDiagnosticKind.InvalidGeometry);
        Assert.Equal(spawn.SpawnId, diagnostic.SpawnId);
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
    }

    [Fact]
    public void Floor_absent_from_the_selected_transform_is_quarantined_before_scene_composition()
    {
        var location = new LootSpawnLocation(
            LootSpawnPrecision.ExactPoint,
            [new(20, 30)],
            ["basement"]);
        var spawn = Spawn("customs-wrong-floor", [Candidate("gpu", "Graphics card", 900_000)], location);

        var result = Build(Snapshot([spawn]), floorIds: ["ground"]);

        Assert.Empty(result.Objects);
        Assert.Empty(result.Entries);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(HighValueLootDiagnosticKind.InvalidFloor, diagnostic.Kind);
        Assert.Equal("spawn.floor-not-in-map-transform", diagnostic.Code);
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
    }

    [Fact]
    public void Declared_floor_survives_projection_for_shared_scene_validation()
    {
        var location = new LootSpawnLocation(
            LootSpawnPrecision.ExactPoint,
            [new(20, 30)],
            ["basement"]);
        var spawn = Spawn("customs-valid-floor", [Candidate("gpu", "Graphics card", 900_000)], location);

        var result = Build(Snapshot([spawn]), floorIds: ["ground", "basement"]);

        Assert.Equal(["basement"], Assert.Single(result.Objects).FloorIds);
    }

    [Fact]
    public void Positioned_spawn_with_unknown_floor_stays_list_only_on_a_floor_aware_map()
    {
        var spawn = Spawn(
            "customs-unresolved-floor",
            [Candidate("gpu", "Graphics card", 900_000)]);

        var result = Build(Snapshot([spawn]), floorIds: ["ground", "upper"]);

        var entry = Assert.Single(result.Entries);
        Assert.Null(entry.SceneObjectId);
        Assert.Empty(result.Objects);
        Assert.Contains(entry.MissingFacts, fact => fact.StartsWith("Floor is unresolved", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, item =>
            item.Kind == HighValueLootDiagnosticKind.FloorUnknown &&
            item.SpawnId == spawn.SpawnId);
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
    }

    [Fact]
    public void Positioned_spawn_with_unknown_floor_can_render_when_the_map_has_only_one_floor()
    {
        var spawn = Spawn(
            "woods-single-floor",
            [Candidate("gpu", "Graphics card", 900_000)]);

        var result = Build(Snapshot([spawn]), floorIds: ["base"]);

        Assert.Single(result.Entries);
        Assert.Single(result.Objects);
        Assert.DoesNotContain(result.Diagnostics, item => item.Kind == HighValueLootDiagnosticKind.FloorUnknown);
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
        Assert.Contains(entry.MissingFacts, fact =>
            fact.Contains("No current trustworthy flea or trader value", StringComparison.Ordinal));
        Assert.Equal("quest.current", Assert.Single(entry.ProfileNeeds).Code);
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
    }

    [Fact]
    public void A_lone_trader_value_ranks_the_spawn_as_a_lower_bound_and_says_so()
    {
        // [Issue 563] Requiring both markets hid every item the flea market bans, and with the
        // real feed (no flea fee) every item at all.
        var need = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.CurrentQuest,
            "quest.current",
            "Needed for the active quest.",
            CompleteStatus,
            Provenance("need"));
        var candidate = new LootSpawnCandidate(
            "unknown-flea",
            "Unknown flea item",
            "electronics",
            Unknown<long?>("gross"),
            Unknown<long?>("net"),
            Complete<long?>("trader", 10_000),
            Complete<int?>("squares", 1),
            [need]);

        var entry = Assert.Single(Build(Snapshot([Spawn("asymmetric-best-net", [candidate])])).Entries);

        Assert.Equal(10_000, entry.MinimumValue);
        Assert.Equal(10_000, entry.MaximumValue);
        Assert.Equal(LootSpawnValueTier.ProfileRelevant, entry.Tier);
        Assert.Contains(entry.MissingFacts, fact => fact.Contains("lower bound", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_real_feed_shape_flea_price_without_a_fee_ranks_under_the_default_filter()
    {
        // [Issue 563] What json.tarkov.dev actually publishes: a flea price, a trader price, no
        // fee (so no flea net) and an unscored source a day old. The default filter used to refuse
        // it three times over (confidence, 30-minute price age, flea net required).
        var dayOld = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "fixture://items",
            Now.AddDays(-1),
            EvidenceConfidence.Unscored,
            new ProducerIdentity("loot-spawn-fixture", "1"));
        var candidate = new LootSpawnCandidate(
            "ledx",
            "LEDX",
            "Medical",
            Complete<long?>("flea-gross", 900_000, dayOld),
            Unknown<long?>("flea-net"),
            Complete<long?>("trader", 400_000, dayOld),
            Complete<int?>("squares", 1, dayOld),
            []);
        var spawn = Spawn("real-shape", [candidate], provenance: dayOld);

        var result = new HighValueLootLayerService().Build(new(
            "customs",
            "transform-1",
            new MapSceneBounds(0, 0, 100, 100),
            Now,
            HighValueLootFilter.Default,
            Snapshot([spawn], provenance: dayOld),
            ["ground"]));

        var entry = Assert.Single(result.Entries);
        Assert.Equal(900_000, entry.MaximumValue);
        Assert.Single(result.Objects);
        Assert.Contains(entry.MissingFacts, fact => fact.Contains("Flea fee is unknown", StringComparison.Ordinal));
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
    public void Stale_profile_need_cannot_elevate_a_below_threshold_candidate()
    {
        var staleNeed = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.CurrentQuest,
            "quest.stale",
            "Old quest context.",
            CompleteStatus,
            Provenance("stale-need", Now.AddDays(-120)));
        var spawn = Spawn(
            "customs-stale-need",
            [Candidate("wire", "Wire", 10_000, [staleNeed])]);

        var result = Build(Snapshot([spawn]));

        Assert.Empty(result.Entries);
        Assert.Empty(result.Objects);
        Assert.Contains(result.Diagnostics, item => item.Code == "spawn.below-threshold");
    }

    [Fact]
    public void Durable_user_pin_does_not_expire_under_the_source_age_filter()
    {
        var pin = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.UserPin,
            "pin.user",
            "Pinned by the user.",
            CompleteStatus,
            UserProvenance("old-pin", Now.AddDays(-120)));
        var spawn = Spawn(
            "customs-pinned",
            [Candidate("wire", "Wire", 10_000, [pin])]);

        var entry = Assert.Single(Build(Snapshot([spawn])).Entries);

        Assert.Equal(LootSpawnValueTier.ProfileRelevant, entry.Tier);
        Assert.Equal("pin.user", Assert.Single(entry.ProfileNeeds).Code);
    }

    [Fact]
    public void Non_authoritative_pin_claim_cannot_bypass_the_source_age_filter()
    {
        var pin = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.UserPin,
            "pin.external",
            "External pin claim.",
            CompleteStatus,
            Provenance("old-external-pin", Now.AddDays(-120)));
        var spawn = Spawn(
            "customs-external-pin",
            [Candidate("wire", "Wire", 10_000, [pin])]);

        var result = Build(Snapshot([spawn]));

        Assert.Empty(result.Entries);
        Assert.Contains(result.Diagnostics, item => item.Code == "spawn.below-threshold");
    }

    [Fact]
    public void Ambiguous_price_cannot_qualify_a_spawn_as_high_value()
    {
        var provenance = Provenance("ambiguous-price");
        var candidate = new LootSpawnCandidate(
            "gpu",
            "Graphics card",
            "electronics",
            Complete<long?>("gross", 925_000),
            new EvidencedValue<long?>(
                "net",
                900_000,
                CompleteStatus,
                provenance,
                candidates: [new EvidenceCandidate<long?>("low", "Possible low price", 10_000, provenance)]),
            Unknown<long?>("trader"),
            Complete<int?>("squares", 2));

        var result = Build(Snapshot([Spawn("customs-ambiguous", [candidate])]));

        Assert.Empty(result.Entries);
        Assert.Empty(result.Objects);
        Assert.Contains(result.Diagnostics, item => item.Code == "spawn.value-unavailable");
    }

    [Fact]
    public void Stale_nested_price_input_cannot_be_laundered_by_a_current_derived_claim()
    {
        var staleInput = Provenance("stale-input", Now.AddHours(-2));
        var derived = DerivedProvenance("derived-price", staleInput);
        var need = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.CurrentQuest,
            "quest.keep",
            "Keep for a quest.",
            CompleteStatus,
            Provenance("need"));
        var candidate = Candidate("gpu", "Graphics card", 900_000, [need], derived);

        var entry = Assert.Single(Build(
            Snapshot([Spawn("derived-stale-price", [candidate])]),
            Filter(maximumPriceAge: TimeSpan.FromHours(1))).Entries);

        Assert.Null(entry.MaximumValue);
        Assert.Equal(LootSpawnValueTier.ProfileRelevant, entry.Tier);
    }

    [Fact]
    public void Low_confidence_nested_price_input_cannot_be_laundered_by_a_confident_derived_claim()
    {
        var weakInput = Provenance("weak-input", confidence: 0.10);
        var derived = DerivedProvenance("derived-price", weakInput);
        var need = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.CurrentQuest,
            "quest.keep",
            "Keep for a quest.",
            CompleteStatus,
            Provenance("need"));
        var candidate = Candidate("gpu", "Graphics card", 900_000, [need], derived);

        var entry = Assert.Single(Build(Snapshot([Spawn("derived-weak-price", [candidate])])).Entries);

        Assert.Null(entry.MaximumValue);
        Assert.Equal(LootSpawnValueTier.ProfileRelevant, entry.Tier);
    }

    [Fact]
    public void Stale_nested_spawn_source_cannot_be_laundered_by_a_current_derived_claim()
    {
        var staleInput = Provenance("stale-spawn-input", Now.AddDays(-120));
        var derived = DerivedProvenance("derived-spawn", staleInput);
        var spawn = Spawn(
            "derived-stale-spawn",
            [Candidate("gpu", "Graphics card", 900_000)],
            provenance: derived);

        var result = Build(Snapshot([spawn]));

        Assert.Empty(result.Entries);
        Assert.Empty(result.Objects);
        Assert.Equal("spawn.source-age-filtered", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Future_correction_cannot_change_the_current_value_tier()
    {
        var provenance = Provenance("future-correction");
        var futureNet = new EvidencedValue<long?>(
            "net",
            900_000,
            CompleteStatus,
            provenance,
            corrections:
            [
                new EvidenceCorrection<long?>(
                    1,
                    10_000,
                    900_000,
                    Now.AddMinutes(5),
                    CorrectionOriginClass.User,
                    "fixture-user"),
            ]);
        var candidate = new LootSpawnCandidate(
            "future-price",
            "Future price",
            "electronics",
            Complete<long?>("gross", 925_000),
            futureNet,
            Complete<long?>("trader", 10_000),
            Complete<int?>("squares", 1));
        var filter = Filter(valueBasis: LootSpawnValueBasis.FleaNet);

        var result = Build(Snapshot([Spawn("future-correction", [candidate])]), filter);

        Assert.Empty(result.Entries);
        Assert.Contains(result.Diagnostics, item => item.Code == "spawn.value-unavailable");
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "spawn.below-threshold");
    }

    [Fact]
    public void Partially_valued_pool_never_presents_a_subset_maximum_as_the_pool_ceiling()
    {
        var unknown = new LootSpawnCandidate(
            "mystery",
            "Mystery item",
            "electronics",
            Unknown<long?>("mystery-gross"),
            Unknown<long?>("mystery-net"),
            Unknown<long?>("mystery-trader"),
            Complete<int?>("mystery-squares", 1));
        var spawn = Spawn(
            "customs-partial-values",
            [Candidate("gpu", "Graphics card", 900_000), unknown]);

        var result = Build(Snapshot([spawn]));

        var entry = Assert.Single(result.Entries);
        Assert.Equal(2, entry.MatchedCandidateCount);
        Assert.Equal(1, entry.ValuedCandidateCount);
        Assert.False(entry.IsValueRangeComplete);
        Assert.Equal(900_000, entry.MaximumValue);
        Assert.Contains("known current values up to", entry.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Potential up to", entry.Summary, StringComparison.Ordinal);
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
    }

    [Fact]
    public void Unknown_candidate_prevents_a_partially_valued_pool_from_being_declared_below_threshold()
    {
        var unknown = new LootSpawnCandidate(
            "mystery",
            "Mystery item",
            "electronics",
            Unknown<long?>("mystery-gross"),
            Unknown<long?>("mystery-net"),
            Unknown<long?>("mystery-trader"),
            Complete<int?>("mystery-squares", 1));
        var spawn = Spawn(
            "customs-indeterminate-values",
            [Candidate("wire", "Wire", 10_000), unknown]);

        var result = Build(Snapshot([spawn]));

        Assert.Empty(result.Entries);
        Assert.Contains(result.Diagnostics, item => item.Code == "spawn.value-incomplete");
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
    }

    [Fact]
    public void Reliable_duplicate_need_is_not_hidden_by_an_older_duplicate()
    {
        var stale = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.CurrentQuest,
            "quest.shared",
            "Old quest context.",
            CompleteStatus,
            Provenance("stale-need", Now.AddDays(-120)));
        var current = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.CurrentQuest,
            "quest.shared",
            "Current quest context.",
            CompleteStatus,
            Provenance("current-need"));
        var spawn = Spawn(
            "customs-duplicate-need",
            [Candidate("old", "Old candidate", 10_000, [stale]), Candidate("current", "Current candidate", 10_000, [current])]);

        var result = Build(Snapshot([spawn]));

        var entry = Assert.Single(result.Entries);
        Assert.Equal("Current quest context.", Assert.Single(entry.ProfileNeeds).Explanation);
        Assert.Equal(LootSpawnValueTier.ProfileRelevant, entry.Tier);
        Assert.Equal(["quest.shared"], entry.ProfileNeedConflictCodes);
        Assert.Contains(result.Diagnostics, item => item.Kind == HighValueLootDiagnosticKind.ConflictingEvidence);
    }

    [Fact]
    public void Conflicting_profile_claim_resolution_is_reviewable_and_permutation_invariant()
    {
        var older = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.CurrentQuest,
            "quest.shared",
            "Older interpretation.",
            CompleteStatus,
            Provenance("need-old", Now.AddMinutes(-20)));
        var newer = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.CurrentQuest,
            "quest.shared",
            "Newer interpretation.",
            CompleteStatus,
            Provenance("need-new", Now.AddMinutes(-5)));
        var firstCandidate = Candidate("a", "A", 900_000, [older]);
        var secondCandidate = Candidate("b", "B", 900_000, [newer]);

        var first = Build(Snapshot([Spawn("customs-conflict", [firstCandidate, secondCandidate])]));
        var permuted = Build(Snapshot([Spawn("customs-conflict", [secondCandidate, firstCandidate])]));

        var firstEntry = Assert.Single(first.Entries);
        var permutedEntry = Assert.Single(permuted.Entries);
        Assert.Equal("Newer interpretation.", Assert.Single(firstEntry.ProfileNeeds).Explanation);
        Assert.Equal(
            firstEntry.ProfileNeeds.Select(need => (need.Code, need.Explanation)),
            permutedEntry.ProfileNeeds.Select(need => (need.Code, need.Explanation)));
        Assert.Equal(firstEntry.ProfileNeedConflictCodes.ToArray(), permutedEntry.ProfileNeedConflictCodes.ToArray());
        Assert.Equal(firstEntry.MissingFacts.ToArray(), permutedEntry.MissingFacts.ToArray());
        Assert.Equal(
            first.Diagnostics.Select(item => (item.Kind, item.Code, item.SpawnId)),
            permuted.Diagnostics.Select(item => (item.Kind, item.Code, item.SpawnId)));
        Assert.Equal(ResultCompleteness.Partial, first.Status.Completeness);
    }

    [Fact]
    public void Corroborating_profile_claims_from_different_sources_are_not_reported_as_conflicts()
    {
        var firstNeed = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.CurrentQuest,
            "quest.shared",
            "Needed for the active quest.",
            CompleteStatus,
            Provenance("need-one", Now.AddMinutes(-20)));
        var secondNeed = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.CurrentQuest,
            "quest.shared",
            "Needed for the active quest.",
            CompleteStatus,
            Provenance("need-two", Now.AddMinutes(-5)));
        var spawn = Spawn(
            "customs-corroborated",
            [Candidate("a", "A", 10_000, [firstNeed]), Candidate("b", "B", 10_000, [secondNeed])]);

        var result = Build(Snapshot([spawn]));

        var entry = Assert.Single(result.Entries);
        Assert.Empty(entry.ProfileNeedConflictCodes);
        Assert.DoesNotContain(result.Diagnostics, item => item.Kind == HighValueLootDiagnosticKind.ConflictingEvidence);
    }

    [Fact]
    public void Large_valid_need_pool_is_bounded_instead_of_crashing_projection()
    {
        var candidates = Enumerable.Range(0, 17)
            .Select(candidateIndex => Candidate(
                $"item-{candidateIndex}",
                $"Item {candidateIndex}",
                10_000,
                Enumerable.Range(0, LootSpawnCandidate.MaximumProfileNeeds)
                    .Select(needIndex => new LootSpawnProfileNeed(
                        LootSpawnProfileNeedKind.FutureQuest,
                        $"need-{candidateIndex}-{needIndex}",
                        "Future quest requirement.",
                        CompleteStatus,
                        Provenance($"need-{candidateIndex}-{needIndex}")))
                    .ToArray()))
            .ToArray();

        var result = Build(Snapshot([Spawn("customs-many-needs", candidates)]));

        var entry = Assert.Single(result.Entries);
        Assert.Equal(HighValueLootEntry.MaximumProjectedProfileNeeds, entry.ProfileNeeds.Count);
        Assert.Contains(entry.MissingFacts, fact => fact.Contains("omitted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Partial_record_makes_the_layer_partial_even_when_its_visible_fields_are_usable()
    {
        var partial = new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current);
        var spawn = Spawn("customs-partial", [Candidate("gpu", "Graphics card", 900_000)], status: partial);

        var result = Build(Snapshot([spawn]));

        Assert.Single(result.Entries);
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
    }

    [Fact]
    public void Unknown_record_freshness_is_not_promoted_to_current_by_a_current_snapshot()
    {
        var unknownFreshness = new ResultStatus(ResultCompleteness.Complete, FreshnessState.Unknown);
        var spawn = Spawn(
            "customs-unknown-freshness",
            [Candidate("gpu", "Graphics card", 900_000)],
            status: unknownFreshness);

        var result = Build(Snapshot([spawn]));

        Assert.Equal(FreshnessState.Unknown, result.Status.Freshness);
        Assert.Contains("Freshness unknown", result.CompactLegend, StringComparison.Ordinal);
    }

    [Fact]
    public void Ambiguous_probability_and_respawn_are_reported_as_missing_facts()
    {
        var provenance = Provenance("ambiguous-spawn-facts");
        var probability = new EvidencedValue<double?>(
            "probability",
            0.5,
            CompleteStatus,
            provenance,
            candidates: [new EvidenceCandidate<double?>("other-probability", "Other probability", 0.2, provenance)]);
        var respawn = new EvidencedValue<string?>(
            "respawn",
            "Once",
            CompleteStatus,
            provenance,
            candidates: [new EvidenceCandidate<string?>("other-respawn", "Other behavior", "Unknown", provenance)]);
        var spawn = Spawn(
            "customs-ambiguous-facts",
            [Candidate("gpu", "Graphics card", 900_000)],
            spawnProbability: probability,
            respawnBehavior: respawn);

        var entry = Assert.Single(Build(Snapshot([spawn])).Entries);

        Assert.Null(entry.ProjectedSpawnProbability);
        Assert.Null(entry.ProjectedRespawnBehavior);
        Assert.Contains(entry.MissingFacts, fact => fact.StartsWith("Spawn probability", StringComparison.Ordinal));
        Assert.Contains(entry.MissingFacts, fact => fact.StartsWith("Respawn behavior", StringComparison.Ordinal));
    }

    [Fact]
    public void Current_unambiguous_probability_and_respawn_survive_the_typed_projection()
    {
        var provenance = Provenance("trusted-spawn-facts");
        var spawn = Spawn(
            "customs-trusted-facts",
            [Candidate("gpu", "Graphics card", 900_000)],
            spawnProbability: new("probability", 0.25, CompleteStatus, provenance),
            respawnBehavior: new("respawn", "Once per raid", CompleteStatus, provenance));

        var entry = Assert.Single(Build(Snapshot([spawn])).Entries);

        Assert.Equal(0.25, entry.ProjectedSpawnProbability);
        Assert.Equal("Once per raid", entry.ProjectedRespawnBehavior);
        Assert.DoesNotContain(entry.MissingFacts, fact => fact.StartsWith("Spawn probability", StringComparison.Ordinal));
        Assert.DoesNotContain(entry.MissingFacts, fact => fact.StartsWith("Respawn behavior", StringComparison.Ordinal));
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
    public void High_value_only_preset_keeps_orientation_labels_and_user_required_context()
    {
        var layers = new[]
        {
            new MapSceneLayer(new("labels"), "Labels", 1, true),
            new MapSceneLayer(new("extracts"), "Extracts", 2, true),
            new MapSceneLayer(new("hazards"), "Hazards", 3, true),
            new MapSceneLayer(new("spawns"), "Spawn areas", 4, true),
            HighValueLootLayerService.Layer,
        };

        var states = HighValueLootLayerPreset.Create(layers, [new("hazards")]);

        // [Issue 563] Place names stay on: "all the names of places disappear" was this preset
        // hiding "labels" along with every other marker layer.
        Assert.True(states.Single(state => state.LayerId == new MapSceneLayerId("labels")).IsVisible);
        Assert.True(states.Single(state => state.LayerId == new MapSceneLayerId("extracts")).IsVisible);
        Assert.True(states.Single(state => state.LayerId == new MapSceneLayerId("hazards")).IsVisible);
        Assert.True(states.Single(state => state.LayerId == HighValueLootLayerService.LayerId).IsVisible);
        // Other marker layers (spawn areas, keys, quests, ...) are exactly what "loot only" means.
        Assert.False(states.Single(state => state.LayerId == new MapSceneLayerId("spawns")).IsVisible);
    }

    [Fact]
    public void High_value_only_preset_rejects_duplicate_layer_ids()
    {
        Assert.Throws<ArgumentException>(() => HighValueLootLayerPreset.Create(
            [HighValueLootLayerService.Layer, HighValueLootLayerService.Layer]));
    }

    [Fact]
    public void Marker_identity_survives_a_dataset_version_refresh()
    {
        var first = Build(Snapshot(
            [Spawn("customs-stable", [Candidate("gpu", "Graphics card", 900_000)], datasetVersion: "dataset-1")],
            datasetVersion: "dataset-1"));
        var refreshed = Build(Snapshot(
            [Spawn("customs-stable", [Candidate("gpu", "Graphics card", 950_000)], datasetVersion: "dataset-2")],
            datasetVersion: "dataset-2"));

        Assert.Equal(Assert.Single(first.Objects).Id, Assert.Single(refreshed.Objects).Id);
    }

    [Fact]
    public void Value_tier_boundaries_never_label_an_included_value_below_threshold()
    {
        var thresholds = new LootSpawnValueThresholds(50, 75, 150, 500);

        Assert.Equal(LootSpawnValueTier.BelowThreshold, thresholds.Classify(49));
        Assert.Equal(LootSpawnValueTier.Qualifying, thresholds.Classify(50));
        Assert.Equal(LootSpawnValueTier.Qualifying, thresholds.Classify(74));
        Assert.Equal(LootSpawnValueTier.Moderate, thresholds.Classify(75));
        Assert.Equal(LootSpawnValueTier.High, thresholds.Classify(150));
        Assert.Equal(LootSpawnValueTier.Exceptional, thresholds.Classify(500));
    }

    [Fact]
    public void Projection_order_is_stable_across_record_permutations()
    {
        var alpha = Spawn("alpha", [Candidate("alpha-item", "Alpha", 900_000)]);
        var middle = Spawn("middle", [Candidate("middle-item", "Middle", 900_000)]);
        var zulu = Spawn("zulu", [Candidate("zulu-item", "Zulu", 900_000)]);

        var first = Build(Snapshot([zulu, alpha, middle]));
        var permuted = Build(Snapshot([middle, zulu, alpha]));

        Assert.Equal(["alpha", "middle", "zulu"], first.Entries.Select(entry => entry.Spawn.SpawnId));
        Assert.Equal(
            first.Entries.Select(entry => entry.Spawn.SpawnId),
            permuted.Entries.Select(entry => entry.Spawn.SpawnId));
        Assert.Equal(first.Objects.Select(item => item.Id), permuted.Objects.Select(item => item.Id));
    }

    [Fact]
    public void Diagnostic_order_is_stable_across_record_permutations()
    {
        static LootSpawnLocation Outside(double y) => new(
            LootSpawnPrecision.ExactPoint,
            [new(250, y)]);

        var alpha = Spawn("alpha", [Candidate("alpha-item", "Alpha", 900_000)], Outside(10));
        var middle = Spawn("middle", [Candidate("middle-item", "Middle", 900_000)], Outside(20));
        var zulu = Spawn("zulu", [Candidate("zulu-item", "Zulu", 900_000)], Outside(30));

        var result = Build(Snapshot([zulu, alpha, middle]));

        Assert.Equal(["alpha", "middle", "zulu"], result.Diagnostics.Select(item => item.SpawnId));
    }

    [Fact]
    public void Layer_result_rejects_an_object_without_its_accessible_entry()
    {
        var valid = Build(Snapshot([Spawn("customs-linked", [Candidate("gpu", "Graphics card", 900_000)])]));

        Assert.Throws<ArgumentException>(() => new HighValueLootLayerResult(
            valid.Layer,
            valid.MapId,
            valid.TransformVersion,
            valid.AppliedFilter,
            valid.Status,
            valid.CompactLegend,
            valid.DataThroughUtc,
            valid.Coverage,
            valid.Objects,
            [],
            valid.Diagnostics));
    }

    [Fact]
    public void Contracts_reject_fake_precision_and_mislabelled_single_item_pools()
    {
        Assert.Throws<ArgumentException>(() => new LootSpawnLocation(
            LootSpawnPrecision.MapOnly,
            [new(20, 20)]));

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

    [Fact]
    public void Snapshot_rejects_coverage_numbers_not_measured_from_its_records()
    {
        var record = Spawn("customs-measured", [Candidate("gpu", "Graphics card", 900_000)]);

        var action = () => new LootSpawnSnapshot(
            "snapshot-bad-coverage",
            "dataset-1",
            "customs",
            "transform-1",
            Now,
            CompleteStatus,
            new LootSpawnCoverage(1, 0, 0, 1),
            Provenance("snapshot"),
            [record]);

        var exception = Assert.Throws<ArgumentException>(action);
        Assert.Contains("measured", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_rejects_a_default_or_pre_provenance_generation_time()
    {
        var record = Spawn("customs-time", [Candidate("gpu", "Graphics card", 900_000)]);
        var coverage = new LootSpawnCoverage(1, 1, 0, 0);
        var provenance = Provenance("snapshot-time");

        Assert.Throws<ArgumentException>(() => new LootSpawnSnapshot(
            "snapshot-default-time",
            "dataset-1",
            "customs",
            "transform-1",
            default,
            CompleteStatus,
            coverage,
            provenance,
            [record]));
        Assert.Throws<ArgumentException>(() => new LootSpawnSnapshot(
            "snapshot-before-source",
            "dataset-1",
            "customs",
            "transform-1",
            Now.AddMinutes(-20),
            CompleteStatus,
            coverage,
            provenance,
            [record]));
    }

    [Fact]
    public void Future_generated_snapshot_is_unavailable_at_an_earlier_evaluation_time()
    {
        var snapshot = Snapshot(
            [Spawn("customs-future-snapshot", [Candidate("gpu", "Graphics card", 900_000)])],
            generatedUtc: Now.AddMinutes(5));

        var result = Build(snapshot);

        Assert.Empty(result.Entries);
        Assert.Empty(result.Objects);
        Assert.Equal(ResultCompleteness.Unavailable, result.Status.Completeness);
        Assert.Equal("snapshot.generated-in-future", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Snapshot_source_age_is_checked_before_any_spawn_is_drawn()
    {
        var snapshot = Snapshot(
            [Spawn("customs-old-snapshot", [Candidate("gpu", "Graphics card", 900_000)])],
            provenance: Provenance("old-snapshot", Now.AddDays(-120)));

        var result = Build(snapshot);

        Assert.Empty(result.Entries);
        Assert.Empty(result.Objects);
        Assert.Equal(ResultCompleteness.Unavailable, result.Status.Completeness);
        Assert.Equal("snapshot.source-age-filtered", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Geometry_complexity_is_rejected_at_the_loot_contract_boundary()
    {
        var pointCount = LootSpawnLocation.MaximumGeometryPoints + 1;
        var points = Enumerable.Range(0, pointCount)
            .Select(index =>
            {
                var angle = index * Math.Tau / pointCount;
                return new MapScenePoint(50 + (Math.Cos(angle) * 10), 50 + (Math.Sin(angle) * 10));
            })
            .ToArray();
        Assert.Throws<ArgumentException>(() => new LootSpawnLocation(
            LootSpawnPrecision.BoundedArea,
            points));
    }

    [Fact]
    public void Misreported_geometry_collection_is_stopped_before_scene_geometry_allocation()
    {
        var hostile = new MisreportedReadOnlyList<MapScenePoint>(
            new MapScenePoint(10, 10),
            actualCount: 100_000,
            reportedCount: 0);

        Assert.Throws<ArgumentException>(() => new LootSpawnLocation(
            LootSpawnPrecision.BoundedArea,
            hostile));
        Assert.Equal(LootSpawnLocation.MaximumGeometryPoints + 1, hostile.EnumeratedCount);
    }

    [Fact]
    public void Misreported_profile_need_collection_is_stopped_at_the_hard_bound()
    {
        var need = new LootSpawnProfileNeed(
            LootSpawnProfileNeedKind.FutureQuest,
            "future",
            "Future quest requirement.",
            CompleteStatus,
            Provenance("future"));
        var hostile = new MisreportedReadOnlyList<LootSpawnProfileNeed>(
            need,
            actualCount: 10_000,
            reportedCount: 0);

        Assert.Throws<ArgumentException>(() => new LootSpawnCandidate(
            "hostile",
            "Hostile",
            "test",
            Complete<long?>("gross", 100_000),
            Complete<long?>("net", 100_000),
            Complete<long?>("trader", 50_000),
            Complete<int?>("squares", 1),
            hostile));
        Assert.Equal(LootSpawnCandidate.MaximumProfileNeeds + 1, hostile.EnumeratedCount);
    }

    [Fact]
    public void Snapshot_rejects_aggregate_candidate_budget_even_when_each_record_is_valid()
    {
        var candidates = Enumerable.Range(0, LootSpawnRecord.MaximumCandidates)
            .Select(index => Candidate($"item-{index}", $"Item {index}", 100_000))
            .ToArray();
        var recordCount = (LootSpawnSnapshot.MaximumTotalCandidates / candidates.Length) + 1;
        var records = Enumerable.Range(0, recordCount)
            .Select(index => Spawn($"spawn-{index:D4}", candidates))
            .ToArray();

        var exception = Assert.Throws<ArgumentException>(() => Snapshot(records));

        Assert.Contains("aggregate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_rejects_aggregate_profile_need_budget()
    {
        var needs = Enumerable.Range(0, LootSpawnCandidate.MaximumProfileNeeds)
            .Select(index => new LootSpawnProfileNeed(
                LootSpawnProfileNeedKind.FutureQuest,
                $"need-{index}",
                "Future quest requirement.",
                CompleteStatus,
                Provenance($"need-{index}")))
            .ToArray();
        var candidate = Candidate("reused", "Reused", 100_000, needs);
        var recordCount = (LootSpawnSnapshot.MaximumTotalProfileNeeds / needs.Length) + 1;
        var records = Enumerable.Range(0, recordCount)
            .Select(index => Spawn($"spawn-{index:D4}", [candidate]))
            .ToArray();

        var exception = Assert.Throws<ArgumentException>(() => Snapshot(records));

        Assert.Contains("aggregate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_rejects_aggregate_geometry_budget()
    {
        var pointCount = LootSpawnLocation.MaximumGeometryPoints;
        var points = Enumerable.Range(0, pointCount)
            .Select(index =>
            {
                var angle = index * Math.Tau / pointCount;
                return new MapScenePoint(50 + (Math.Cos(angle) * 10), 50 + (Math.Sin(angle) * 10));
            })
            .ToArray();
        var location = new LootSpawnLocation(
            LootSpawnPrecision.BoundedArea,
            points);
        var recordCount = (LootSpawnSnapshot.MaximumTotalGeometryPoints / pointCount) + 1;
        var records = Enumerable.Range(0, recordCount)
            .Select(index => Spawn($"spawn-{index:D4}", [Candidate($"item-{index}", $"Item {index}", 100_000)], location))
            .ToArray();

        var exception = Assert.Throws<ArgumentException>(() => Snapshot(records));

        Assert.Contains("aggregate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancelled_projection_returns_no_partial_result()
    {
        var snapshot = Snapshot([Spawn("cancelled", [Candidate("gpu", "Graphics card", 900_000)])]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Build(snapshot, cancellationToken: cancellation.Token));
    }

    private static HighValueLootLayerResult Build(
        LootSpawnSnapshot snapshot,
        HighValueLootFilter? filter = null,
        IReadOnlyList<string>? floorIds = null,
        CancellationToken cancellationToken = default) => new HighValueLootLayerService().Build(new(
            "customs",
            "transform-1",
            new MapSceneBounds(0, 0, 100, 100),
            Now,
            filter ?? Filter(),
            snapshot,
            floorIds),
        cancellationToken);

    private static LootSpawnSnapshot Snapshot(
        IReadOnlyList<LootSpawnRecord> records,
        FreshnessState freshness = FreshnessState.Current,
        string datasetVersion = "dataset-1",
        DateTimeOffset? generatedUtc = null,
        EvidenceProvenance? provenance = null)
    {
        var positioned = records.Count(record => record.Location.Geometry is not null);
        var floors = records.Count(record => record.Location.Geometry is not null && record.Location.FloorIds.Count > 0);
        return new(
            "snapshot-1",
            datasetVersion,
            "customs",
            "transform-1",
            generatedUtc ?? Now.AddMinutes(-10),
            new ResultStatus(ResultCompleteness.Complete, freshness),
            new LootSpawnCoverage(records.Count, positioned, floors, records.Count - positioned),
            provenance ?? Provenance("snapshot"),
            records);
    }

    private static LootSpawnRecord Spawn(
        string id,
        IReadOnlyList<LootSpawnCandidate> candidates,
        LootSpawnLocation? location = null,
        string datasetVersion = "dataset-1",
        ResultStatus? status = null,
        EvidencedValue<double?>? spawnProbability = null,
        EvidencedValue<string?>? respawnBehavior = null,
        EvidenceProvenance? provenance = null) => new(
        id,
        "customs",
        $"Spawn {id}",
        location ?? Point(),
        candidates.Count == 1 ? LootSpawnPoolKind.SingleKnownItem : LootSpawnPoolKind.UnweightedCandidates,
        candidates,
        spawnProbability ?? Unknown<double?>("probability"),
        respawnBehavior ?? Unknown<string?>("respawn"),
        datasetVersion,
        "transform-1",
        status ?? CompleteStatus,
        provenance ?? Provenance($"spawn-{id}"));

    private static LootSpawnLocation Point() => new(
        LootSpawnPrecision.ExactPoint,
        [new(20, 30)]);

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
        TimeSpan? maximumPriceAge = null,
        LootSpawnValueBasis valueBasis = LootSpawnValueBasis.BestNet,
        IReadOnlyList<string>? itemIds = null,
        bool includeProfileRelevant = true,
        long? minimumValueRoubles = null) => new(
        valueBasis,
        thresholds ?? LootSpawnValueThresholds.Default,
        maximumPriceAge ?? TimeSpan.FromHours(1),
        TimeSpan.FromDays(90),
        0.5,
        includeProfileRelevant,
        itemIds: itemIds,
        minimumValueRoubles: minimumValueRoubles);

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

    private static EvidenceProvenance Provenance(
        string id,
        DateTimeOffset? observedUtc = null,
        double confidence = 0.95) => new(
        EvidenceSourceClass.PublicStructuredData,
        $"fixture://{id}",
        observedUtc ?? Now.AddMinutes(-10),
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, confidence),
        new ProducerIdentity("loot-spawn-fixture", "1"));

    private static EvidenceProvenance DerivedProvenance(
        string id,
        EvidenceProvenance input) => new(
        EvidenceSourceClass.DerivedCalculation,
        $"fixture://{id}",
        Now.AddMinutes(-10),
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.95),
        new ProducerIdentity("loot-spawn-fixture", "1"),
        generatedUtc: Now.AddMinutes(-10),
        inputs: [input]);

    private static EvidenceProvenance UserProvenance(
        string id,
        DateTimeOffset observedUtc) => new(
        EvidenceSourceClass.UserEntered,
        $"fixture://{id}",
        observedUtc,
        EvidenceConfidence.Unscored,
        new ProducerIdentity("loot-spawn-fixture", "1"));

    private static ResultStatus CompleteStatus { get; } = new(
        ResultCompleteness.Complete,
        FreshnessState.Current);

    private sealed class MisreportedReadOnlyList<T>(T value, int actualCount, int reportedCount) : IReadOnlyList<T>
    {
        public int EnumeratedCount { get; private set; }

        public int Count { get; } = reportedCount;

        public T this[int index] => index >= 0 && index < actualCount
            ? value
            : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < actualCount; index++)
            {
                EnumeratedCount++;
                yield return value;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
