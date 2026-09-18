using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.V2Plan;

public sealed class KeepListWorkspaceViewModelTests
{
    [Fact]
    public async Task An_item_a_tracked_quest_needs_sorts_first_and_names_the_quest()
    {
        var requirements = new FakeRequirementCatalog
        {
            QuestRequirements = [new("gunsmith-4", "obj-1", "item-spring", 2, false)],
        };
        var quests = new FakeQuestReadService([Quest("gunsmith-4", "Gunsmith Part 4", RecordedTaskState.Active)]);
        var viewModel = new KeepListWorkspaceViewModel(
            requirements,
            new FakePlayerProfileService(TestProfile()),
            new FakeItemRepository().WithName("item-spring", "Spring"),
            new FakeItemFactCatalog(),
            quests);

        await viewModel.RefreshAsync();

        var group = Assert.Single(viewModel.Groups);
        Assert.Equal("Quests you're on (1)", group.Heading);
        var row = Assert.Single(group.Items);
        Assert.Equal("Spring", row.Name);
        Assert.Contains("2 for Gunsmith Part 4", row.Reasons);
        Assert.Equal("1 to keep", viewModel.Status);
    }

    [Fact]
    public async Task An_untracked_outstanding_quest_sorts_after_tracked_ones()
    {
        var requirements = new FakeRequirementCatalog
        {
            QuestRequirements = [new("prapors-choice", "obj-1", "item-bolts", 1, false)],
        };
        var quests = new FakeQuestReadService([Quest("prapors-choice", "Prapor's Choice", RecordedTaskState.NotStarted)]);
        var viewModel = new KeepListWorkspaceViewModel(
            requirements,
            new FakePlayerProfileService(TestProfile()),
            new FakeItemRepository().WithName("item-bolts", "Bolts"),
            new FakeItemFactCatalog(),
            quests);

        await viewModel.RefreshAsync();

        var group = Assert.Single(viewModel.Groups);
        Assert.Equal("Quests ahead of you (1)", group.Heading);
    }

    [Fact]
    public async Task Progress_recorded_against_an_objective_reduces_what_is_still_needed()
    {
        var requirements = new FakeRequirementCatalog
        {
            QuestRequirements = [new("gunsmith-4", "obj-1", "item-spring", 5, false)],
        };
        var quests = new FakeQuestReadService([Quest("gunsmith-4", "Gunsmith Part 4", RecordedTaskState.Active)]);
        var profile = TestProfile(objectiveProgress: new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["obj-1"] = 3,
        });
        var viewModel = new KeepListWorkspaceViewModel(
            requirements,
            new FakePlayerProfileService(profile),
            new FakeItemRepository().WithName("item-spring", "Spring"),
            new FakeItemFactCatalog(),
            quests);

        await viewModel.RefreshAsync();

        var row = Assert.Single(Assert.Single(viewModel.Groups).Items);
        Assert.Contains("2 for Gunsmith Part 4", row.Reasons);
    }

    [Fact]
    public async Task A_quest_fully_progressed_or_completed_drops_off_the_list()
    {
        var requirements = new FakeRequirementCatalog
        {
            QuestRequirements = [new("gunsmith-4", "obj-1", "item-spring", 2, false)],
        };
        var quests = new FakeQuestReadService([Quest("gunsmith-4", "Gunsmith Part 4", RecordedTaskState.Completed)]);
        var profile = TestProfile(completedTaskIds: new HashSet<string>(StringComparer.Ordinal) { "gunsmith-4" });
        var viewModel = new KeepListWorkspaceViewModel(
            requirements,
            new FakePlayerProfileService(profile),
            new FakeItemRepository().WithName("item-spring", "Spring"),
            new FakeItemFactCatalog(),
            quests);

        await viewModel.RefreshAsync();

        Assert.False(viewModel.HasGroups);
        Assert.Equal("Nothing to keep right now — quests, hideout, and keys are all clear.", viewModel.Status);
    }

    [Fact]
    public async Task Hideout_need_sums_across_stations_and_subtracts_what_is_already_owned()
    {
        var requirements = new FakeRequirementCatalog
        {
            HideoutRequirements =
            [
                new("bitcoin-farm", 2, "item-battery", 2),
                new("workbench", 2, "item-battery", 1),
            ],
        };
        var profile = TestProfile(
            hideoutLevels: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["bitcoin-farm"] = 1,
                ["workbench"] = 1,
            },
            owned: new Dictionary<string, int>(StringComparer.Ordinal) { ["item-battery"] = 1 });
        requirements.Stations =
        [
            new("bitcoin-farm", "Bitcoin Farm", [1, 2]),
            new("workbench", "Workbench", [1, 2]),
        ];
        var viewModel = new KeepListWorkspaceViewModel(
            requirements,
            new FakePlayerProfileService(profile),
            new FakeItemRepository().WithName("item-battery", "Battery"),
            new FakeItemFactCatalog(),
            new FakeQuestReadService([]));

        await viewModel.RefreshAsync();

        var group = Assert.Single(viewModel.Groups);
        Assert.Equal("Hideout upgrades (1)", group.Heading);
        var row = Assert.Single(group.Items);
        // 2 required for Bitcoin Farm + 1 for Workbench = 3 total, minus 1 owned = 2 remaining.
        Assert.Contains("2 for Bitcoin Farm", row.Reasons);
        Assert.Contains("1 for Workbench", row.Reasons);
    }

    [Fact]
    public async Task A_hideout_item_already_fully_owned_is_not_listed()
    {
        var requirements = new FakeRequirementCatalog
        {
            HideoutRequirements = [new("bitcoin-farm", 2, "item-battery", 2)],
            Stations = [new("bitcoin-farm", "Bitcoin Farm", [1, 2])],
        };
        var profile = TestProfile(
            hideoutLevels: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["bitcoin-farm"] = 1 },
            owned: new Dictionary<string, int>(StringComparer.Ordinal) { ["item-battery"] = 2 });
        var viewModel = new KeepListWorkspaceViewModel(
            requirements,
            new FakePlayerProfileService(profile),
            new FakeItemRepository(),
            new FakeItemFactCatalog(),
            new FakeQuestReadService([]));

        await viewModel.RefreshAsync();

        Assert.False(viewModel.HasGroups);
    }

    [Fact]
    public async Task A_key_a_tracked_quest_needs_is_kept_with_that_reason()
    {
        var requirements = new FakeRequirementCatalog
        {
            QuestRequirements = [new("laundry-1", "obj-1", "item-key-office", 1, false)],
        };
        var quests = new FakeQuestReadService([Quest("laundry-1", "Laundry Part 1", RecordedTaskState.Active)]);
        var factCatalog = new FakeItemFactCatalog
        {
            KeyFacts = [new("item-key-office", "customs", null, [], [], 5_000, 0, 0, false, 0, Provenance())],
        };
        var viewModel = new KeepListWorkspaceViewModel(
            requirements,
            new FakePlayerProfileService(TestProfile()),
            new FakeItemRepository().WithName("item-key-office", "Office key"),
            factCatalog,
            quests);

        await viewModel.RefreshAsync();

        var row = Assert.Single(Assert.Single(viewModel.Groups).Items);
        Assert.Equal("Office key", row.Name);
        Assert.Contains(row.Reasons, reason => reason.Contains("Laundry Part 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_key_nothing_needs_and_the_market_has_not_priced_is_left_off_the_list()
    {
        var factCatalog = new FakeItemFactCatalog
        {
            KeyFacts = [new("item-key-nowhere", null, null, [], [], null, 0, 0, false, 0, Provenance())],
        };
        var viewModel = new KeepListWorkspaceViewModel(
            new FakeRequirementCatalog(),
            new FakePlayerProfileService(TestProfile()),
            new FakeItemRepository(),
            factCatalog,
            new FakeQuestReadService([]));

        await viewModel.RefreshAsync();

        Assert.False(viewModel.HasGroups);
    }

    [Fact]
    public async Task A_key_the_market_prices_far_above_the_others_is_kept_as_high_value()
    {
        var cheap = Enumerable.Range(0, 7)
            .Select(i => new KeyFacts($"item-key-cheap-{i}", null, null, [], [], 1_000, 0, 0, false, 0, Provenance()));
        var dear = new KeyFacts("item-key-dear", null, null, [], [], 500_000, 0, 0, false, 0, Provenance());
        var factCatalog = new FakeItemFactCatalog { KeyFacts = [.. cheap, dear] };
        var viewModel = new KeepListWorkspaceViewModel(
            new FakeRequirementCatalog(),
            new FakePlayerProfileService(TestProfile()),
            new FakeItemRepository().WithName("item-key-dear", "Marked room key"),
            factCatalog,
            new FakeQuestReadService([]));

        await viewModel.RefreshAsync();

        var group = Assert.Single(viewModel.Groups, group => group.Items.Any(row => row.ItemId == "item-key-dear"));
        Assert.Equal("Keys worth keeping (1)", group.Heading);
    }

    [Fact]
    public async Task A_high_value_item_already_needed_gets_an_extra_high_value_reason_without_its_own_group()
    {
        var requirements = new FakeRequirementCatalog
        {
            QuestRequirements = [new("gunsmith-4", "obj-1", "item-rare-part", 1, false)],
        };
        var quests = new FakeQuestReadService([Quest("gunsmith-4", "Gunsmith Part 4", RecordedTaskState.Active)]);
        var itemRepository = new FakeItemRepository()
            .WithName("item-rare-part", "Rare part")
            .WithPrice("item-rare-part", 150_000);
        var viewModel = new KeepListWorkspaceViewModel(
            requirements,
            new FakePlayerProfileService(TestProfile()),
            itemRepository,
            new FakeItemFactCatalog(),
            quests);

        await viewModel.RefreshAsync();

        var group = Assert.Single(viewModel.Groups);
        Assert.Equal("Quests you're on (1)", group.Heading);
        var row = Assert.Single(group.Items);
        Assert.True(row.IsHighValue);
        Assert.Equal("S", row.Tier);
        Assert.Contains("high value", row.Reasons);
    }

    [Fact]
    public async Task No_synced_requirements_or_keys_reports_an_honest_pre_sync_empty_state()
    {
        var viewModel = new KeepListWorkspaceViewModel(
            new FakeRequirementCatalog(),
            new FakePlayerProfileService(TestProfile()),
            new FakeItemRepository(),
            new FakeItemFactCatalog(),
            new FakeQuestReadService([]));

        await viewModel.RefreshAsync();

        Assert.False(viewModel.HasGroups);
        Assert.Equal("No keep-list data cached yet.", viewModel.Status);
    }

    private static QuestSummaryReadModel Quest(string taskId, string name, RecordedTaskState state, bool isPinned = false) => new(
        taskId,
        name,
        null,
        null,
        state,
        "Manual",
        null,
        new QuestEligibility(QuestEligibilityState.Available, []),
        RecordedObjectivesSatisfaction.Indeterminate,
        isPinned,
        null,
        false,
        [],
        [],
        []);

    private static DataProvenance Provenance() => new("fixture", DateTimeOffset.UtcNow);

    private static PlayerProfile TestProfile(
        IReadOnlyDictionary<string, int>? hideoutLevels = null,
        IReadOnlyDictionary<string, int>? owned = null,
        IReadOnlyDictionary<string, int>? objectiveProgress = null,
        IReadOnlySet<string>? completedTaskIds = null) => new(
        Guid.NewGuid(),
        "Tester",
        GameMode.Regular,
        Level: 10,
        Faction.Usec,
        Edition: null,
        TraderLevels: new Dictionary<string, int>(StringComparer.Ordinal),
        CompletedTaskIds: completedTaskIds ?? new HashSet<string>(StringComparer.Ordinal),
        ObjectiveProgress: objectiveProgress ?? new Dictionary<string, int>(StringComparer.Ordinal),
        HideoutStationLevels: hideoutLevels ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        WishlistItemIds: new HashSet<string>(StringComparer.Ordinal),
        OwnedItemCounts: owned ?? new Dictionary<string, int>(StringComparer.Ordinal),
        EventItemStates: new Dictionary<string, EventItemState>(StringComparer.Ordinal),
        ItemOverrides: new Dictionary<string, string>(StringComparer.Ordinal),
        UpdatedUtc: DateTimeOffset.UtcNow);

    private sealed class FakeRequirementCatalog : IRequirementCatalog
    {
        public IReadOnlyList<HideoutStationSummary> Stations { get; set; } = [];

        public IReadOnlyList<HideoutItemRequirement> HideoutRequirements { get; set; } = [];

        public IReadOnlyList<QuestItemRequirement> QuestRequirements { get; set; } = [];

        public Task<IReadOnlyList<HideoutItemRequirement>> GetHideoutRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(HideoutRequirements);

        public Task<IReadOnlyList<QuestItemRequirement>> GetQuestRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(QuestRequirements);

        public Task<IReadOnlyList<HideoutStationSummary>> GetStationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Stations);

        public void Invalidate()
        {
        }
    }

    private sealed class FakeItemFactCatalog : IItemFactCatalog
    {
        public IReadOnlyList<KeyFacts> KeyFacts { get; set; } = [];

        public Task<IReadOnlyList<AmmoStats>> GetAmmoAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AmmoStats>>([]);

        public Task<IReadOnlyList<AmmoPackContents>> GetAmmoPacksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AmmoPackContents>>([]);

        public Task<IReadOnlyList<LoadoutItemFacts>> GetLoadoutFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LoadoutItemFacts>>([]);

        public Task<IReadOnlyList<KeyFacts>> GetKeyFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(KeyFacts);

        public void Invalidate()
        {
        }
    }

    private sealed class FakeQuestReadService(IReadOnlyList<QuestSummaryReadModel> tasks) : IQuestReadService
    {
        public Task<QuestBoardReadModel> GetQuestBoardAsync(QuestProfileScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(new QuestBoardReadModel(scope, 1, null, tasks, []));

        public Task<QuestItemNeedsReadModel> GetItemNeedsAsync(
            QuestProfileScope scope,
            string itemId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<QuestMapObjectivesReadModel> GetActiveMapObjectivesAsync(
            QuestProfileScope scope,
            IReadOnlyCollection<string> mapIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePlayerProfileService(PlayerProfile profile) : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(profile);

        public Task SaveAsync(PlayerProfile updated, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => Task.FromResult(profile);
    }

    private sealed class FakeItemRepository : IItemRepository
    {
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _prices = new(StringComparer.Ordinal);

        public FakeItemRepository WithName(string itemId, string name)
        {
            _names[itemId] = name;
            return this;
        }

        public FakeItemRepository WithPrice(string itemId, long fleaRoubles)
        {
            _prices[itemId] = fleaRoubles;
            return this;
        }

        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(new(
                itemId,
                _names.GetValueOrDefault(itemId, itemId),
                itemId,
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
                Provenance: Provenance()));

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(_prices.TryGetValue(itemId, out var price)
                ? new(price, [], null, null, null, Provenance())
                : null);
    }
}
