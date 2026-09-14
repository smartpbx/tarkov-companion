using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Keys;
using TarkovCompanion.Core.Domain.Loadouts;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.UnitTests;

public sealed class ProfileNeedAggregationTests
{
    [Fact]
    public async Task AggregatesProfileNeedsIntoRecommendationContext()
    {
        var profile = TestProfile.Create() with
        {
            ObjectiveProgress = new Dictionary<string, int> { ["objective"] = 1 },
            HideoutStationLevels = new Dictionary<string, int> { ["workbench"] = 1 },
            OwnedItemCounts = new Dictionary<string, int> { ["bolts"] = 2 },
            WishlistItemIds = new HashSet<string> { "bolts" },
            ItemOverrides = new Dictionary<string, string> { ["bolts"] = "Keep" },
        };
        var profiles = new StubProfileService(profile);
        var aggregator = new ProfileNeedAggregationService(
            [new("task", "objective", "bolts", 3, true)],
            [new("workbench", 2, "bolts", 5)]);
        using var events = new ProfileEventTrackerService(profiles, [TestEvent("event", "bolts")]);
        var service = new RecommendationContextService(profiles, aggregator, events);

        var context = await service.BuildAsync(
            "bolts",
            isFoundInRaid: true,
            "event",
            "Useful barter component.",
            Confidence.Certain,
            CancellationToken.None);

        Assert.Equal(2, context.OutstandingQuestCount);
        Assert.Equal(2, context.OutstandingFoundInRaidQuestCount);
        Assert.Equal(3, context.OutstandingHideoutCount);
        Assert.True(context.IsWishlisted);
        Assert.Equal(EventItemState.Untested, context.EventState);
        Assert.Equal("Keep", context.UserOverride);
    }

    [Fact]
    public void CompletedQuestAndBuiltStationRemoveNeeds()
    {
        var profile = TestProfile.Create() with
        {
            CompletedTaskIds = new HashSet<string> { "task" },
            HideoutStationLevels = new Dictionary<string, int> { ["workbench"] = 2 },
        };
        var aggregator = new ProfileNeedAggregationService(
            [new("task", "objective", "bolts", 3, true)],
            [new("workbench", 2, "bolts", 5)]);

        var result = aggregator.GetItemNeed(profile, "bolts");

        Assert.Equal(0, result.Summary.OutstandingItems);
        Assert.Equal(0, result.Summary.HideoutCount);
    }

    /// <summary>
    /// One quest wanting two of something is one quest that wants it.
    /// </summary>
    /// <remarks>
    /// The summary used to report only a sum of outstanding quantities, under a field called
    /// QuestCount, and the Keys page read it as a number of quests.
    /// </remarks>
    [Fact]
    public void QuestsAreCountedOncePerQuestAndQuantitiesSeparately()
    {
        var profile = TestProfile.Create();
        var aggregator = new ProfileNeedAggregationService(
            [
                new("task-a", "objective-1", "bolts", 2, false),
                new("task-a", "objective-2", "bolts", 3, false),
                new("task-b", "objective-3", "bolts", 1, false),
            ],
            []);

        var summary = aggregator.GetItemNeed(profile, "bolts").Summary;

        Assert.Equal(6, summary.OutstandingItems);
        Assert.Equal(2, summary.QuestsNeedingIt);
    }

    /// <summary>
    /// Only the quests the player is actually on count as tracked.
    /// </summary>
    /// <remarks>
    /// The filter was "not completed", which on a fresh profile is every quest in the game, and
    /// the reason it produced said "you are tracking".
    /// </remarks>
    [Fact]
    public void OnlyTheQuestsTheProfileIsOnCountAsTracked()
    {
        var profile = TestProfile.Create();
        var aggregator = new ProfileNeedAggregationService(
            [
                new("task-a", "objective-1", "bolts", 1, false),
                new("task-b", "objective-2", "bolts", 1, false),
            ],
            []);

        var summary = aggregator
            .GetItemNeed(profile, "bolts", new HashSet<string>(StringComparer.Ordinal) { "task-a" })
            .Summary;

        Assert.Equal(2, summary.QuestsNeedingIt);
        Assert.Equal(1, summary.TrackedQuestsNeedingIt);
    }

    [Fact]
    public void NothingIsTrackedWhenNobodyCouldSay()
    {
        // Null is "no board could be read", and reporting everything as tracked would be the
        // direction that misleads.
        var profile = TestProfile.Create();
        var aggregator = new ProfileNeedAggregationService(
            [new("task-a", "objective-1", "bolts", 1, false)],
            []);

        var summary = aggregator.GetItemNeed(profile, "bolts").Summary;

        Assert.Equal(1, summary.QuestsNeedingIt);
        Assert.Equal(0, summary.TrackedQuestsNeedingIt);
    }

    private static EventDefinition TestEvent(string eventId, params string[] itemIds) => new(
        eventId,
        "Allergy-style event",
        null,
        null,
        true,
        new HashSet<string>(itemIds),
        "{}",
        new("fixture", DateTimeOffset.UnixEpoch));
}

public sealed class ProfileEventTrackerTests
{
    [Fact]
    public async Task AllergicConsumptionTakesPrecedenceOverLaterSafeObservation()
    {
        var profiles = new StubProfileService(TestProfile.Create());
        using var tracker = new ProfileEventTrackerService(
            profiles,
            [new("event", "Allergy-style event", null, null, true, new HashSet<string> { "milk", "juice" }, "{}", new("fixture", DateTimeOffset.UnixEpoch))]);

        Assert.Equal(EventItemState.Untested, await tracker.GetItemStateAsync("event", "milk", CancellationToken.None));
        Assert.Equal(
            EventItemState.Allergic,
            await tracker.RecordConsumptionAsync("event", "milk", EventItemState.Allergic, CancellationToken.None));
        Assert.Equal(
            EventItemState.Allergic,
            await tracker.RecordConsumptionAsync("event", "milk", EventItemState.Safe, CancellationToken.None));

        var decision = await tracker.GetConsumptionDecisionAsync("event", "milk", CancellationToken.None);
        var progress = await tracker.GetProgressAsync("event", CancellationToken.None);
        var counts = await tracker.GetStateCountsAsync("event", CancellationToken.None);
        Assert.Equal(RecommendationAction.AvoidConsume, decision.Action);
        Assert.Equal(2, progress.Total);
        Assert.Equal(1, progress.Tested);
        Assert.Equal(1, progress.Allergic);
        Assert.Equal(0, progress.Safe);
        Assert.Equal(1, counts.Untested);
    }
}

public sealed class AmmoIntelligenceTests
{
    [Fact]
    public async Task ResolvesPacksRanksCaliberAndLabelsHeuristic()
    {
        var stats = AmmoFixtures();
        var service = new AmmoIntelligenceService(
            stats,
            [new("pack", "ammo-3", 120, stats[0].Provenance)],
            [new("ammo-1", 30, new Dictionary<string, int> { ["prapor"] = 4 }, null, new HashSet<GameMode> { GameMode.Regular })]);
        var lowLevelProfile = TestProfile.Create() with { Level = 10 };

        var pack = await service.GetAsync("pack", lowLevelProfile, CancellationToken.None);
        var available = await service.GetCaliberAsync("5.45x39", lowLevelProfile, CancellationToken.None);

        Assert.NotNull(pack);
        Assert.Equal("ammo-3", pack.Stats.ItemId);
        Assert.Equal("B", pack.Tier);
        Assert.Contains("Heuristic, not a live detection", pack.LearnModeExplanation, StringComparison.Ordinal);
        Assert.Equal(ArmorEffectiveness.Good, pack.ArmorClassRatings[3]);
        Assert.DoesNotContain(available, x => x.Stats.ItemId == "ammo-1");
        Assert.Equal(["ammo-2", "ammo-3", "ammo-4", "ammo-5"], available.Select(x => x.Stats.ItemId));
    }

    internal static AmmoStats[] AmmoFixtures()
    {
        var provenance = new DataProvenance("json.tarkov.dev fixture", DateTimeOffset.UnixEpoch, Confidence: new(0.95));
        return
        [
            new("ammo-1", "5.45x39", 50, 50, null, null, 1, null, null, null, false, false, provenance),
            new("ammo-2", "5.45x39", 52, 45, null, null, 1, null, null, null, false, false, provenance),
            new("ammo-3", "5.45x39", 55, 30, null, null, 1, null, null, null, false, false, provenance),
            new("ammo-4", "5.45x39", 60, 20, null, null, 1, null, null, null, false, false, provenance),
            new("ammo-5", "5.45x39", 70, 10, null, null, 1, null, null, null, false, false, provenance),
        ];
    }
}

public sealed class KeyIntelligenceTests
{
    [Fact]
    public async Task PersonalQuestCompletionChangesExplicitScore()
    {
        var service = new KeyIntelligenceService([KeyFactsFixture()]);
        var neededProfile = TestProfile.Create();
        var completedProfile = neededProfile with { CompletedTaskIds = new HashSet<string> { "task" } };

        var needed = await service.GetAsync("key", neededProfile, CancellationToken.None);
        var completed = await service.GetAsync("key", completedProfile, CancellationToken.None);

        Assert.NotNull(needed);
        Assert.NotNull(completed);
        Assert.Equal(100, needed.Score.Quest);
        Assert.Equal(0, completed.Score.Quest);
        Assert.True(needed.Score.Economy > 0);
        Assert.Equal(85, needed.Score.Uses);
        Assert.Equal(65, needed.Score.LockUtility);
        Assert.Equal(100, needed.Score.UniqueAccess);
        Assert.True(needed.Score.RiskAdjustedLoot > 0);
        Assert.True(needed.Score.WeightedTotal > completed.Score.WeightedTotal);
        Assert.Contains("40 maximum uses", needed.Explanation, StringComparison.Ordinal);
        Assert.Contains("route risk", needed.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourcedCuratedOverrideWinsGeneratedAdviceAndScore()
    {
        var provenance = new DataProvenance(
            "maintainer review",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            Reference: "internal://key-review/1",
            Confidence: new(0.9));
        var curatedScore = new KeyScoreComponents(100, 100, 100, 100, 100, 100);
        var service = new KeyIntelligenceService(
            [KeyFactsFixture()],
            [new("key", curatedScore, "S", "Curated advice.", "Curated explanation.", provenance)]);

        var result = await service.GetAsync("key", TestProfile.Create(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.IsCurated);
        Assert.Equal("S", result.Tier);
        Assert.Equal("Curated advice.", result.Advice);
        Assert.Equal(provenance, result.Provenance);
    }

    private static KeyFacts KeyFactsFixture() => new(
        "key",
        "customs",
        40,
        ["dorms-1", "dorms-2"],
        ["task"],
        100_000,
        250_000,
        80,
        true,
        0.25,
        new("json.tarkov.dev fixture", DateTimeOffset.UnixEpoch, Confidence: new(0.9)));
}

public sealed class LoadoutIntelligenceTests
{
    [Fact]
    public async Task CalculatesRepeatedItemCostWeightAndAmmoKitWarning()
    {
        var ammoService = new AmmoIntelligenceService(AmmoIntelligenceTests.AmmoFixtures());
        var service = new LoadoutIntelligenceService(Catalog(), ammoService);
        var selection = new LoadoutSelection(
            "weapon",
            "ammo-5",
            ["mag", "mag"],
            "armor",
            ["plate"],
            "helmet",
            null,
            null,
            null,
            ["med"]);

        var result = await service.EvaluateAsync(selection, TestProfile.Create(), CancellationToken.None);

        Assert.True(result.IsCompatible);
        Assert.Equal(227_000, result.ApproximateCostRoubles);
        Assert.Equal(13, result.ApproximateWeightKg);
        Assert.Equal("D", result.AmmoTier);
        Assert.Contains(result.Warnings, x => x.Contains("weak relative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportsWeaponAmmoAndPlateIncompatibility()
    {
        var ammoService = new AmmoIntelligenceService(AmmoIntelligenceTests.AmmoFixtures());
        var catalog = Catalog().Append(new(
            "bad-plate", "Bad plate", ItemCategory.Plate, 10_000, 2, null, new HashSet<string>(), new HashSet<string> { "different-armor" }));
        var service = new LoadoutIntelligenceService(catalog, ammoService);
        var selection = new LoadoutSelection(
            "other-weapon",
            "ammo-5",
            ["mag"],
            "armor",
            ["bad-plate"],
            null,
            null,
            null,
            null,
            []);

        var result = await service.EvaluateAsync(selection, null, CancellationToken.None);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.CompatibilityIssues, x => x.Contains("does not match", StringComparison.Ordinal));
        Assert.Contains(result.CompatibilityIssues, x => x.Contains("not compatible with the selected weapon", StringComparison.Ordinal));
        Assert.Contains(result.CompatibilityIssues, x => x.Contains("not compatible with the selected armor", StringComparison.Ordinal));
    }

    private static IEnumerable<LoadoutItemFacts> Catalog()
    {
        yield return new("weapon", "AK", ItemCategory.Weapon, 100_000, 4, "5.45x39", new HashSet<string>(), new HashSet<string>());
        yield return new("other-weapon", "M4", ItemCategory.Weapon, 100_000, 4, "5.56x45", new HashSet<string>(), new HashSet<string>());
        yield return new("ammo-5", "Ammo", ItemCategory.Ammunition, 5_000, 0.5, "5.45x39", new HashSet<string>(), new HashSet<string>());
        yield return new("mag", "Magazine", ItemCategory.Attachment, 10_000, 0.5, "5.45x39", new HashSet<string> { "weapon" }, new HashSet<string>());
        yield return new("armor", "Armor", ItemCategory.Armor, 70_000, 5, null, new HashSet<string>(), new HashSet<string>());
        yield return new("plate", "Plate", ItemCategory.Plate, 20_000, 1.5, null, new HashSet<string>(), new HashSet<string> { "armor" });
        yield return new("helmet", "Helmet", ItemCategory.Helmet, 10_000, 0.5, null, new HashSet<string>(), new HashSet<string>());
        yield return new("med", "Medical", ItemCategory.Medicine, 2_000, 0.5, null, new HashSet<string>(), new HashSet<string>());
    }
}

public sealed class RecommendationPriorityCoverageTests
{
    [Fact]
    public void PrioritiesAreDeterministicAcrossProfileSpecializedAndEconomyInputs()
    {
        Assert.Equal(RecommendationAction.DropFirst, Recommend(userOverride: "DropFirst", eventState: EventItemState.Allergic).Action);
        Assert.Equal(RecommendationAction.AvoidConsume, Recommend(eventState: EventItemState.Allergic, quest: 1).Action);
        Assert.Equal(RecommendationAction.EssentialKeep, Recommend(quest: 1, hideout: 1, wishlist: true).Action);
        Assert.Equal(RecommendationAction.Keep, Recommend(hideout: 1, wishlist: true).Action);
        Assert.Equal(RecommendationAction.Keep, Recommend(wishlist: true).Action);

        var specialized = Recommend(specializedAdvice: "S-tier ammunition.");
        Assert.Equal(
            [RecommendationReasonCode.AmmoQuality, RecommendationReasonCode.BestFleaValue],
            specialized.Reasons.Select(x => x.Code));
        Assert.Equal(RecommendationAction.SellFlea, specialized.Action);

        var trader = Recommend(flea: 10_000, trader: 30_000);
        Assert.Equal(RecommendationAction.SellTrader, trader.Action);
    }

    private static RecommendationResult Recommend(
        string? userOverride = null,
        EventItemState eventState = EventItemState.Unknown,
        int quest = 0,
        int hideout = 0,
        bool wishlist = false,
        string? specializedAdvice = null,
        long flea = 30_000,
        long trader = 20_000)
    {
        var provenance = new DataProvenance("fixture", DateTimeOffset.UnixEpoch);
        var item = new ItemDefinition(
            "ammo",
            "Ammo",
            "Ammo",
            string.Empty,
            ItemCategory.Ammunition,
            new(1, 1),
            true,
            null,
            null,
            null,
            null,
            null,
            new HashSet<string>(),
            provenance);
        var price = new ItemPriceSnapshot(
            flea,
            [new("trader", "Trader", trader, provenance)],
            null,
            null,
            null,
            provenance);
        var context = new RecommendationContext(
            true,
            quest,
            quest,
            hideout,
            wishlist,
            eventState,
            userOverride,
            specializedAdvice,
            Confidence.Certain);
        return new RecommendationEngine().Recommend(item, price, context, ValueTierThresholds.Default);
    }
}

internal static class TestProfile
{
    public static PlayerProfile Create() => new(
        Guid.Parse("58bfe8c7-e4ac-4e27-87b4-b7055a026e52"),
        "Test",
        GameMode.Regular,
        20,
        Faction.Usec,
        "Standard",
        new Dictionary<string, int>(),
        new HashSet<string>(),
        new Dictionary<string, int>(),
        new Dictionary<string, int>(),
        new HashSet<string>(),
        new Dictionary<string, int>(),
        new Dictionary<string, EventItemState>(),
        new Dictionary<string, string>(),
        DateTimeOffset.UnixEpoch);
}

internal sealed class StubProfileService(PlayerProfile profile) : IPlayerProfileService
{
    private PlayerProfile _profile = profile;

    public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_profile);
    }

    public Task SaveAsync(PlayerProfile profileToSave, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _profile = profileToSave;
        return Task.CompletedTask;
    }

    public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => throw new NotSupportedException();
}
