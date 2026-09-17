using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.UnitTests.V2Plan;

public sealed class HideoutWorkspaceViewModelTests
{
    [Fact]
    public async Task Refreshing_marks_a_station_buildable_only_when_every_next_level_item_is_owned()
    {
        var requirements = new FakeRequirementCatalog();
        requirements.Stations =
        [
            new("lavatory", "Lavatory", [1, 2, 3]),
            new("workbench", "Workbench", [1, 2]),
        ];
        requirements.Requirements =
        [
            new("lavatory", 2, "item-bolts", 5),
            new("lavatory", 2, "item-nails", 3),
            new("workbench", 2, "item-toolkit", 1),
        ];
        var profile = TestProfile(hideoutLevels: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["lavatory"] = 1,
            ["workbench"] = 1,
        }, owned: new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["item-bolts"] = 5,
            ["item-nails"] = 3,
            ["item-toolkit"] = 0,
        });
        var viewModel = new HideoutWorkspaceViewModel(
            requirements,
            new FakePlayerProfileService(profile),
            new FakeItemRepository());

        await viewModel.RefreshAsync();

        var lavatory = Assert.Single(viewModel.Stations, station => station.StationId == "lavatory");
        Assert.True(lavatory.CanBuildNow);
        Assert.Equal(0, lavatory.MissingItemCount);

        var workbench = Assert.Single(viewModel.Stations, station => station.StationId == "workbench");
        Assert.False(workbench.CanBuildNow);
        Assert.Equal(1, workbench.MissingItemCount);

        Assert.Contains("1 ready to build now", viewModel.Status);
    }

    [Fact]
    public async Task Selecting_a_station_lists_what_its_next_level_still_needs()
    {
        var requirements = new FakeRequirementCatalog();
        requirements.Stations = [new("lavatory", "Lavatory", [1, 2, 3])];
        requirements.Requirements =
        [
            new("lavatory", 2, "item-bolts", 5),
            new("lavatory", 2, "item-nails", 3),
        ];
        var profile = TestProfile(
            hideoutLevels: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["lavatory"] = 1 },
            owned: new Dictionary<string, int>(StringComparer.Ordinal) { ["item-bolts"] = 2, ["item-nails"] = 3 });
        var itemRepository = new FakeItemRepository();
        itemRepository.Names["item-bolts"] = "Bolts";
        itemRepository.Names["item-nails"] = "Nails";
        var viewModel = new HideoutWorkspaceViewModel(requirements, new FakePlayerProfileService(profile), itemRepository);

        await viewModel.RefreshAsync();
        var station = Assert.Single(viewModel.Stations);
        await viewModel.SelectAsync(station, CancellationToken.None);

        var bolts = Assert.Single(viewModel.Items, row => row.ItemName == "Bolts");
        Assert.False(bolts.IsSatisfied);
        Assert.Equal("3", bolts.Remaining);
        var nails = Assert.Single(viewModel.Items, row => row.ItemName == "Nails");
        Assert.True(nails.IsSatisfied);
        Assert.Equal("Complete", nails.Remaining);
    }

    [Fact]
    public async Task Refresh_selects_the_first_station_so_the_detail_pane_is_never_blank()
    {
        var requirements = new FakeRequirementCatalog();
        requirements.Stations = [new("lavatory", "Lavatory", [1, 2, 3])];
        requirements.Requirements = [new("lavatory", 1, "item-bolts", 5)];
        var profile = TestProfile(
            hideoutLevels: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            owned: new Dictionary<string, int>(StringComparer.Ordinal) { ["item-bolts"] = 2 });
        var itemRepository = new FakeItemRepository();
        itemRepository.Names["item-bolts"] = "Bolts";
        var viewModel = new HideoutWorkspaceViewModel(requirements, new FakePlayerProfileService(profile), itemRepository);

        await viewModel.RefreshAsync();

        var station = Assert.Single(viewModel.Stations);
        Assert.True(viewModel.HasSelection);
        Assert.True(station.IsSelected);
        Assert.Equal("1 missing", station.StateLabel);
        Assert.Equal("2 / 5", Assert.Single(viewModel.Items).ProgressLabel);
        Assert.Equal("1 station · 0 ready to build now", viewModel.Status);
    }

    [Fact]
    public async Task A_station_already_at_its_highest_level_has_no_outstanding_items()
    {
        var requirements = new FakeRequirementCatalog();
        requirements.Stations = [new("lavatory", "Lavatory", [1, 2, 3])];
        requirements.Requirements = [];
        var profile = TestProfile(
            hideoutLevels: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["lavatory"] = 3 },
            owned: new Dictionary<string, int>(StringComparer.Ordinal));
        var viewModel = new HideoutWorkspaceViewModel(requirements, new FakePlayerProfileService(profile), new FakeItemRepository());

        await viewModel.RefreshAsync();

        var station = Assert.Single(viewModel.Stations);
        Assert.False(station.HasNextLevel);
        Assert.Equal("Fully built.", station.NextLevelSummary);
    }

    private static PlayerProfile TestProfile(
        IReadOnlyDictionary<string, int> hideoutLevels,
        IReadOnlyDictionary<string, int> owned) => new(
        Guid.NewGuid(),
        "Tester",
        GameMode.Regular,
        Level: 10,
        Faction.Usec,
        Edition: null,
        TraderLevels: new Dictionary<string, int>(StringComparer.Ordinal),
        CompletedTaskIds: new HashSet<string>(StringComparer.Ordinal),
        ObjectiveProgress: new Dictionary<string, int>(StringComparer.Ordinal),
        HideoutStationLevels: hideoutLevels,
        WishlistItemIds: new HashSet<string>(StringComparer.Ordinal),
        OwnedItemCounts: owned,
        EventItemStates: new Dictionary<string, EventItemState>(StringComparer.Ordinal),
        ItemOverrides: new Dictionary<string, string>(StringComparer.Ordinal),
        UpdatedUtc: DateTimeOffset.UtcNow);

    private sealed class FakeRequirementCatalog : IRequirementCatalog
    {
        public IReadOnlyList<HideoutStationSummary> Stations { get; set; } = [];

        public IReadOnlyList<HideoutItemRequirement> Requirements { get; set; } = [];

        public Task<IReadOnlyList<HideoutItemRequirement>> GetHideoutRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Requirements);

        public Task<IReadOnlyList<QuestItemRequirement>> GetQuestRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuestItemRequirement>>([]);

        public Task<IReadOnlyList<HideoutStationSummary>> GetStationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Stations);

        public void Invalidate()
        {
        }
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
        public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(new(
                itemId,
                Names.GetValueOrDefault(itemId, itemId),
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
                Provenance: new DataProvenance("fixture", DateTimeOffset.UtcNow)));

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }
}
