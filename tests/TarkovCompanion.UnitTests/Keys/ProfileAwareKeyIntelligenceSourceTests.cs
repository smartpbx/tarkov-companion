using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Intelligence.Keys;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Keys;
using TarkovCompanion.Core.Domain.Loadouts;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.Keys;

public sealed class ProfileAwareKeyIntelligenceSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RuntimeSourcePreservesKnownProfileFactsAndUnknownModelInputsForEveryEntryPoint()
    {
        const string itemId = "dorm-206";
        var profile = Profile(itemId, owned: 3);
        var needs = new ProfileNeedAggregationService(
            [new("quest-active", "objective-key", itemId, 2, true)],
            []);
        var source = new ProfileAwareKeyIntelligenceSource(
            new FakeFacts(new KeyFacts(
                itemId,
                "customs",
                40,
                ["dorms-206-lock"],
                [],
                250_000,
                null,
                null,
                null,
                null,
                new DataProvenance("json.tarkov.dev/items", Now.AddHours(-1), Now.AddHours(-1), "fixture://key", new Confidence(0.95)))
            {
                LockFacts = [new("dorms-206-lock", "customs")],
            }),
            new FakeProfiles(profile),
            needs,
            new FakeQuests(profile, itemId),
            gameVersion: new FakeGameVersion("0.16.9"),
            timeProvider: new FixedTime(Now));

        var results = new List<ProfileAwareKeyIntelligenceResult>();
        foreach (var entryPoint in Enum.GetValues<KeyIntelligenceEntryPoint>())
        {
            results.Add(Assert.IsType<ProfileAwareKeyIntelligenceResult>(
                await source.GetAsync(itemId, entryPoint, CancellationToken.None)));
        }

        var result = results[0];
        Assert.All(results, candidate =>
        {
            Assert.Equal(result.Tier, candidate.Tier);
            Assert.Equal(result.Score, candidate.Score);
            Assert.Equal(result.Reasons.Select(reason => reason.Code), candidate.Reasons.Select(reason => reason.Code));
        });
        Assert.Equal(KeyIntelligenceTier.A, result.Tier);
        Assert.Null(result.Score);
        Assert.Equal(3, result.Inventory.TotalOwned.Value);
        Assert.Equal(1, result.Inventory.FoundInRaidOwned.Value);
        Assert.Equal(2, result.Inventory.DuplicateQuantity.Value);
        Assert.Equal(40, result.Inventory.MaximumUses.Value);
        Assert.Null(result.Inventory.RemainingUses.Value);
        var requirement = Assert.Single(result.Requirements);
        Assert.Equal(KeyRequirementTiming.Current, requirement.Timing);
        Assert.Equal(2, requirement.RemainingQuantity);
        Assert.True(requirement.RequiresFoundInRaid);
        var association = Assert.Single(result.Utility.Associations);
        Assert.Equal("customs", association.MapId.Value);
        Assert.Equal("dorms-206-lock", association.LockId.Value);
        Assert.Null(association.RoomId.Value);
        Assert.Null(result.Utility.ExpectedLootProxyRoubles.Value);
        Assert.Null(result.Utility.RouteUtility.Value);
        Assert.Null(result.Utility.RouteRisk.Value);
        Assert.Contains(result.MissingFacts, fact => fact.Contains("expected-loot", StringComparison.Ordinal));
        Assert.Equal("0.16.9", result.GameVersion);
    }

    private static PlayerProfile Profile(string itemId, int owned) => new(
        Guid.NewGuid(),
        "Tester",
        GameMode.Regular,
        20,
        Faction.Usec,
        null,
        new Dictionary<string, int>(),
        new HashSet<string>(),
        new Dictionary<string, int>(),
        new Dictionary<string, int>(),
        new HashSet<string>(),
        new Dictionary<string, int> { [itemId] = owned },
        new Dictionary<string, EventItemState>(),
        new Dictionary<string, string>(),
        Now,
        "wipe-1");

    private static QuestSummaryReadModel Quest(string taskId) => new(
        taskId,
        "Active quest",
        null,
        null,
        RecordedTaskState.Active,
        "Manual",
        Now,
        new QuestEligibility(QuestEligibilityState.Available, []),
        RecordedObjectivesSatisfaction.NotSatisfied,
        false,
        null,
        false,
        [],
        [],
        []);

    private sealed class FakeFacts(KeyFacts key) : IItemFactCatalog
    {
        public Task<IReadOnlyList<AmmoStats>> GetAmmoAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AmmoStats>>([]);

        public Task<IReadOnlyList<AmmoPackContents>> GetAmmoPacksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AmmoPackContents>>([]);

        public Task<IReadOnlyList<LoadoutItemFacts>> GetLoadoutFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LoadoutItemFacts>>([]);

        public Task<IReadOnlyList<KeyFacts>> GetKeyFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KeyFacts>>([key]);

        public void Invalidate()
        {
        }
    }

    private sealed class FakeProfiles(PlayerProfile profile) : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(profile);
        public Task SaveAsync(PlayerProfile updated, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);
        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => Task.FromResult(profile);
    }

    private sealed class FakeQuests(PlayerProfile profile, string itemId) : IQuestReadService
    {
        public Task<QuestBoardReadModel> GetQuestBoardAsync(QuestProfileScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(new QuestBoardReadModel(scope, 1, null, [Quest("quest-active")], []));

        public Task<QuestItemNeedsReadModel> GetItemNeedsAsync(
            QuestProfileScope scope,
            string requestedItemId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new QuestItemNeedsReadModel(
                scope,
                requestedItemId,
                requestedItemId == itemId ? 1 : null,
                requestedItemId == itemId ? profile.OwnedItemCounts[itemId] - 1 : null,
                [],
                []));

        public Task<QuestMapObjectivesReadModel> GetActiveMapObjectivesAsync(
            QuestProfileScope scope,
            IReadOnlyCollection<string> mapIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeGameVersion(string version) : IGameVersionSource
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(version);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
