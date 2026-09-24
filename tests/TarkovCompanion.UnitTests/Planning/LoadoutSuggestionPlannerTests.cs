using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.Planning;

/// <summary>
/// The objectives below are the seed catalog's own (json.tarkov.dev, synced 2026-09-14): ids,
/// descriptions, map links, key targets and subtype JSON copied from it, lists shortened where
/// a test only needs the shape.
/// </summary>
public sealed class LoadoutSuggestionPlannerTests
{
    private const string Customs = "56f40101d2720b2a4d8b45d6";
    private const string Woods = "5704e3c2d2720bac5b8b4567";
    private const string Dorm114Key = "59387a4986f77401cc236e62";
    private const string Aks74U = "57dc2fa62459775949412633";
    private const string Aks74Un = "583990e32459771419544dd2";
    private const string Mosin = "5bfd297f0db834001a669119";
    private const string Sv98 = "55801eed4bdc2d89578b4588";
    private const string BramitSuppressor = "5b86a0e586f7745b600ccb23";
    private const string HybridSuppressor = "59bffbb386f77435b379b9c2";
    private const string Ms2000 = "5991b51486f77447b112d44f";
    private const string ScavVest = "572b7adb24597762ae139821";
    private const string Ushanka = "59e7708286f7742cbd762753";

    private static readonly Dictionary<string, LoadoutSuggestionItem> Items = new(StringComparer.Ordinal)
    {
        [Dorm114Key] = new(Dorm114Key, "Dorm room 114 key", false),
        [Aks74U] = new(Aks74U, "Kalashnikov AKS-74U 5.45x39 assault rifle", false, "AKS-74U"),
        [Aks74Un] = new(Aks74Un, "Kalashnikov AKS-74UN 5.45x39 assault rifle", false, "AKS-74UN"),
        [Mosin] = new(Mosin, "Mosin 7.62x54R bolt-action rifle (Infantry)", false),
        [Sv98] = new(Sv98, "SV-98 7.62x54R bolt-action sniper rifle", false),
        [BramitSuppressor] = new(BramitSuppressor, "Mosin Rifle Bramit 7.62x54R sound suppressor", true),
        [HybridSuppressor] = new(HybridSuppressor, "SilencerCo Hybrid 46 multi-caliber sound suppressor", true),
        [Ms2000] = new(Ms2000, "MS2000 Marker", false),
    };

    private static LoadoutSuggestionObjective Pharmacist() => new(
        "5969f9e986f7741dde183a50",
        "Pharmacist",
        "5969fa4886f7741ddb481544",
        "Locate and obtain the suitcase with the device on Customs",
        QuestObjectiveKind.FindQuestItem,
        [Customs],
        [
            new("5910922b86f7747d96753483", "questItem", 0, 0, 1, null),
            new(Dorm114Key, "requiredKeys", 0, 0, null, null),
        ],
        null,
        1,
        "{}");

    private static LoadoutSuggestionObjective PunisherPart1() => new(
        "59c512ad86f7741f0d09de9b",
        "The Punisher - Part 1",
        "59674d5186f77446b852d5f7",
        "Eliminate Scavs with AKS-74U on Customs",
        QuestObjectiveKind.Shoot,
        [Customs],
        [],
        null,
        25,
        $$"""{"shotType":"kill","bodyParts":[],"usingWeapon":["{{Aks74Un}}","{{Aks74U}}"],"usingWeaponMods":[],"distance":{"value":0,"compareMethod":">="},"wearing":[],"notWearing":[],"targetNames":["assaultGroup","Savage","Marksman"]}""");

    private static LoadoutSuggestionObjective TarkovShooterPart6() => new(
        "5bc4856986f77454c317bea7",
        "The Tarkov Shooter - Part 6",
        "5bc485b586f774726473a858",
        "Eliminate PMC operatives with a suppressed bolt-action rifle",
        QuestObjectiveKind.Shoot,
        [],
        [],
        null,
        3,
        $$"""{"shotType":"kill","usingWeapon":["{{Sv98}}","{{Mosin}}"],"usingWeaponMods":[["{{BramitSuppressor}}"],["{{HybridSuppressor}}"]],"distance":{"value":0,"compareMethod":">="},"wearing":[],"notWearing":[],"targetNames":["AnyPmc"]}""");

    private static LoadoutSuggestionObjective TarkovShooterPart1() => new(
        "5bc4776586f774512d07cf05",
        "The Tarkov Shooter - Part 1",
        "5bc850d186f7747213700892",
        "Eliminate Scavs from over 40 meters away with a bolt-action rifle with iron sights",
        QuestObjectiveKind.Shoot,
        [Woods, Customs],
        [],
        null,
        5,
        $$"""{"shotType":"kill","usingWeapon":["{{Mosin}}","{{Sv98}}"],"usingWeaponMods":[],"distance":{"value":40,"compareMethod":">="},"wearing":[],"notWearing":[]}""");

    private static LoadoutSuggestionObjective TarkovShooterPart3() => new(
        "5bc47dbf86f7741ee74e93b9",
        "The Tarkov Shooter - Part 3",
        "5bc47e3e86f7741e6b2f3332",
        "Eliminate PMC operatives from less than 25 meters away with a bolt-action rifle",
        QuestObjectiveKind.Shoot,
        [],
        [],
        null,
        3,
        $$"""{"shotType":"kill","usingWeapon":["{{Mosin}}","{{Sv98}}"],"usingWeaponMods":[],"distance":{"value":25,"compareMethod":"<="},"wearing":[],"notWearing":[]}""");

    private static LoadoutSuggestionObjective Setup() => new(
        "5c1234c286f77406fa13baeb",
        "Setup",
        "5c1fa9c986f7740de474cb3d",
        "Eliminate PMC operatives with common Scav weapons while wearing the specified gear on Customs",
        QuestObjectiveKind.Shoot,
        [Customs],
        [],
        null,
        15,
        $$"""{"usingWeapon":["54491c4f4bdc2db1078b4568"],"wearing":[[{"id":"{{Ushanka}}","name":"Ushanka ear flap hat","shortName":"Ushanka"},{"id":"{{ScavVest}}","name":"Scav Vest","shortName":"Scav Vest"}]],"notWearing":[],"distance":{"value":0,"compareMethod":">="} }""");

    private static LoadoutSuggestionObjective EagleOwl() => new(
        "5d25e29d86f7740a22516326",
        "The Survivalist Path - Eagle-Owl",
        "5d25fd8386f77443fe457cae",
        "Eliminate Scavs during 21:00-04:00 without using any NVGs or thermal sights (Excluding Factory)",
        QuestObjectiveKind.Shoot,
        [Customs, Woods],
        [],
        null,
        6,
        """{"usingWeapon":[],"wearing":[],"notWearing":[{"id":"5c0696830db834001d23f5da","name":"PNV-10T night vision goggles"},{"id":"5c066e3a0db834001b7353f0","name":"Armasight N-15 night vision goggles"}],"distance":{"value":0,"compareMethod":">="}}""");

    private static LoadoutSuggestionObjective OilRun(string objectiveId) => new(
        "59c124d686f774189b3c843f",
        "Oil Run",
        objectiveId,
        "Locate and mark any of the fuel tank trucks with an MS2000 Marker on Customs",
        QuestObjectiveKind.Mark,
        [Customs],
        [new(Ms2000, "markerItem", 0, 0, 1, null)],
        null,
        1,
        "{}");

    [Fact]
    public void A_locked_room_asks_for_its_key_and_names_the_quest()
    {
        var suggestions = LoadoutSuggestionPlanner.Suggest(Customs, [Pharmacist()], Items);

        var key = Assert.Single(suggestions, suggestion => suggestion.Kind == LoadoutSuggestionKind.Key);
        Assert.Equal("Bring Dorm room 114 key", key.Title);
        Assert.Equal(["Pharmacist"], key.Quests);
        Assert.Equal("Locate and obtain the suitcase with the device on Customs", key.Reason);
        Assert.Equal([Dorm114Key], key.ItemIds);
        Assert.Equal(PlannerVersions.LoadoutSuggestions, key.PlannerVersion);
        // The suitcase itself is picked up, so the bag needs room for it. The catalog does not
        // list quest items, so the id is not shown as a name.
        var room = Assert.Single(suggestions, suggestion => suggestion.Kind == LoadoutSuggestionKind.Room);
        Assert.Equal("Room for the quest item", room.Title);
    }

    [Fact]
    public void A_weapon_condition_names_the_weapons_that_count()
    {
        var weapon = Assert.Single(LoadoutSuggestionPlanner.Suggest(Customs, [PunisherPart1()], Items));

        Assert.Equal(LoadoutSuggestionKind.Weapon, weapon.Kind);
        Assert.Equal("Bring one of 2 weapons: AKS-74UN, AKS-74U", weapon.Title);
        Assert.Equal(["The Punisher - Part 1"], weapon.Quests);
        Assert.False(weapon.AnyMap);
    }

    [Fact]
    public void Suppressor_sets_read_as_a_suppressor_and_a_mapless_kill_is_offered_on_any_map()
    {
        var suggestions = LoadoutSuggestionPlanner.Suggest(Customs, [TarkovShooterPart6()], Items);

        var suppressor = Assert.Single(suggestions, suggestion => suggestion.Kind == LoadoutSuggestionKind.WeaponMod);
        Assert.Equal("Fit a suppressor to it", suppressor.Title);
        Assert.Equal([BramitSuppressor, HybridSuppressor], suppressor.ItemIds);
        Assert.All(suggestions, suggestion => Assert.True(suggestion.AnyMap));
    }

    [Fact]
    public void Mods_that_are_not_suppressors_are_named()
    {
        var objective = TarkovShooterPart6() with
        {
            SubtypeJson = $$"""{"usingWeaponMods":[["{{Ms2000}}"]]}""",
        };

        var mod = Assert.Single(LoadoutSuggestionPlanner.Suggest(Customs, [objective], Items));
        Assert.Equal("Fit MS2000 Marker", mod.Title);
    }

    [Fact]
    public void Range_is_read_in_both_directions()
    {
        var suggestions = LoadoutSuggestionPlanner.Suggest(Customs, [TarkovShooterPart1(), TarkovShooterPart3()], Items);

        Assert.Contains(suggestions, suggestion => suggestion.Kind == LoadoutSuggestionKind.Range && suggestion.Title == "Long range: 40 m or more");
        Assert.Contains(suggestions, suggestion => suggestion.Kind == LoadoutSuggestionKind.Range && suggestion.Title == "Close range: within 25 m");
        // Both parts ask for the same bolt-action rifles: one suggestion serving both quests.
        var weapon = Assert.Single(suggestions, suggestion => suggestion.Kind == LoadoutSuggestionKind.Weapon);
        Assert.Equal(["The Tarkov Shooter - Part 1", "The Tarkov Shooter - Part 3"], weapon.Quests);
        Assert.EndsWith("(+1 more)", weapon.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Worn_gear_uses_the_names_the_catalog_gives_it_and_forbidden_gear_is_left_behind()
    {
        var suggestions = LoadoutSuggestionPlanner.Suggest(Customs, [Setup(), EagleOwl()], Items);

        var wear = Assert.Single(suggestions, suggestion => suggestion.Kind == LoadoutSuggestionKind.Wear);
        Assert.Equal("Wear Ushanka ear flap hat + Scav Vest", wear.Title);
        var leave = Assert.Single(suggestions, suggestion => suggestion.Kind == LoadoutSuggestionKind.LeaveBehind);
        Assert.Equal("Leave behind: PNV-10T night vision goggles, Armasight N-15 night vision goggles", leave.Title);
        Assert.Empty(leave.ItemIds);
    }

    [Fact]
    public void Markers_are_counted_per_objective_because_each_is_left_behind()
    {
        var suggestions = LoadoutSuggestionPlanner.Suggest(Customs, [OilRun("59c128b986f77415037680df"), OilRun("59c128b986f77415037680e0")], Items);

        var marker = Assert.Single(suggestions);
        Assert.Equal(LoadoutSuggestionKind.Carry, marker.Kind);
        Assert.Equal("Bring MS2000 Marker ×2", marker.Title);
        Assert.Equal(2, marker.Count);
    }

    [Fact]
    public void Objectives_on_another_map_suggest_nothing()
    {
        Assert.Empty(LoadoutSuggestionPlanner.Suggest(Woods, [Pharmacist(), PunisherPart1(), Setup()], Items));
    }

    [Fact]
    public void Map_bound_suggestions_come_before_any_map_ones_and_keys_lead()
    {
        var suggestions = LoadoutSuggestionPlanner.Suggest(Customs, [TarkovShooterPart6(), PunisherPart1(), Pharmacist()], Items);

        Assert.Equal(LoadoutSuggestionKind.Key, suggestions[0].Kind);
        Assert.False(suggestions[0].AnyMap);
        Assert.True(suggestions[^1].AnyMap);
    }

    [Fact]
    public void Unreadable_conditions_suggest_nothing_rather_than_throw()
    {
        var objective = PunisherPart1() with { SubtypeJson = "{not json" };

        Assert.Empty(LoadoutSuggestionPlanner.Suggest(Customs, [objective], Items));
        Assert.Same(ShootConditions.None, ShootConditions.Read("[1,2]"));
    }

    [Fact]
    public async Task The_service_says_what_is_owned_what_is_not_scanned_and_who_sells_the_rest()
    {
        var profile = Profile(new Dictionary<string, int>(StringComparer.Ordinal) { [Aks74U] = 1, [Ms2000] = 0 });
        var tasks = new[]
        {
            ActiveTask("Pharmacist", Customs, Pharmacist()),
            ActiveTask("The Punisher - Part 1", Customs, PunisherPart1()),
            ActiveTask("Oil Run", Customs, OilRun("59c128b986f77415037680df")),
        };
        var service = new LoadoutSuggestionService(
            new Profiles(profile),
            new Board(tasks),
            new Repository(),
            acquisitions: new Acquisitions(new ItemAcquisitionOffer(Ms2000, ItemAcquisitionKind.Cash, "therapist", "Therapist", 1, null, null, 25000, [])));

        var plan = await service.PlanAsync(null, CancellationToken.None);

        Assert.Equal(Customs, plan.MapId);
        Assert.Equal(PlannerVersions.LoadoutSuggestions, plan.PlannerVersion);
        var key = Assert.Single(plan.Rows, row => row.Suggestion.Kind == LoadoutSuggestionKind.Key);
        Assert.Equal("Owned: not scanned", key.Have);
        Assert.Equal("No trader sells it", key.Source);
        var weapon = Assert.Single(plan.Rows, row => row.Suggestion.Kind == LoadoutSuggestionKind.Weapon);
        Assert.True(weapon.IsOwned);
        Assert.Equal("You own Kalashnikov AKS-74U 5.45x39 assault rifle", weapon.Have);
        var room = Assert.Single(plan.Rows, row => row.Suggestion.Kind == LoadoutSuggestionKind.Room);
        Assert.Equal(string.Empty, room.Have + room.Source);
        var marker = Assert.Single(plan.Rows, row => row.Suggestion.Kind == LoadoutSuggestionKind.Carry);
        Assert.Equal("None owned", marker.Have);
        Assert.True(marker.IsObtainable);
        Assert.StartsWith("Therapist 25", marker.Source, StringComparison.Ordinal);
    }

    private static QuestSummaryReadModel ActiveTask(string name, string map, LoadoutSuggestionObjective objective) => new(
        objective.TaskId,
        name,
        null,
        map,
        RecordedTaskState.Active,
        "Manual",
        null,
        new(QuestEligibilityState.Available, []),
        RecordedObjectivesSatisfaction.NotSatisfied,
        false,
        null,
        false,
        [],
        [],
        [
            new QuestObjectiveReadModel(
                objective.ObjectiveId,
                objective.Description,
                objective.Kind,
                false,
                false,
                RecordedObjectiveState.InProgress,
                null,
                objective.TargetCount,
                objective.FoundInRaidRequired,
                "Manual",
                null,
                false,
                objective.MapIds,
                objective.ItemTargets)
            {
                SubtypeJson = objective.SubtypeJson,
            },
        ]);

    private static PlayerProfile Profile(IReadOnlyDictionary<string, int> owned) => new(
        Guid.NewGuid(),
        "Tester",
        GameMode.Regular,
        Level: 10,
        Faction.Usec,
        Edition: null,
        TraderLevels: new Dictionary<string, int>(StringComparer.Ordinal) { ["therapist"] = 1 },
        CompletedTaskIds: new HashSet<string>(StringComparer.Ordinal),
        ObjectiveProgress: new Dictionary<string, int>(StringComparer.Ordinal),
        HideoutStationLevels: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        WishlistItemIds: new HashSet<string>(StringComparer.Ordinal),
        OwnedItemCounts: owned,
        EventItemStates: new Dictionary<string, EventItemState>(StringComparer.Ordinal),
        ItemOverrides: new Dictionary<string, string>(StringComparer.Ordinal),
        UpdatedUtc: DateTimeOffset.UtcNow);

    private sealed class Profiles(PlayerProfile profile) : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(profile);

        public Task SaveAsync(PlayerProfile updated, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => Task.FromResult(profile);
    }

    private sealed class Board(IReadOnlyList<QuestSummaryReadModel> tasks) : IQuestReadService
    {
        public Task<QuestBoardReadModel> GetQuestBoardAsync(QuestProfileScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(new QuestBoardReadModel(scope, 1, null, tasks, []));

        public Task<QuestItemNeedsReadModel> GetItemNeedsAsync(QuestProfileScope scope, string itemId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuestMapObjectivesReadModel> GetActiveMapObjectivesAsync(
            QuestProfileScope scope,
            IReadOnlyCollection<string> mapIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Repository : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(Items.TryGetValue(itemId, out var item)
                ? new(
                    itemId,
                    item.Name,
                    item.Name,
                    string.Empty,
                    ItemCategory.Unknown,
                    new ItemDimensions(1, 1),
                    FleaEligible: true,
                    IconUri: null,
                    ImageUri: null,
                    WikiUri: null,
                    PropertiesType: null,
                    PropertiesJson: null,
                    CategoryIds: new HashSet<string>(StringComparer.Ordinal),
                    Provenance: new DataProvenance("fixture", DateTimeOffset.UtcNow))
                : null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }

    private sealed class Acquisitions(params ItemAcquisitionOffer[] offers) : IItemAcquisitionService
    {
        public Task<IReadOnlyList<ProfiledItemAcquisition>> GetAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProfiledItemAcquisition>>(
            [
                .. offers
                    .Where(offer => itemIds.Contains(offer.ItemId))
                    .Select(offer => new ProfiledItemAcquisition(offer, new(true, "Available now"))),
            ]);
    }
}
