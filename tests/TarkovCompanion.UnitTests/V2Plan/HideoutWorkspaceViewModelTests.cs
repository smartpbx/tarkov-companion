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
    public async Task A_load_that_fails_says_so_in_the_pane_and_Retry_brings_the_stations_back()
    {
        // #453: a failed load used to leave an empty pane and one grey sentence. The notice names
        // what failed, never shows the exception, and its Retry is the same load run again.
        var requirements = new FakeRequirementCatalog
        {
            Fails = true,
            Stations = [new("lavatory", "Lavatory", [1, 2])],
            Requirements = [new("lavatory", 1, "item-bolts", 5)],
        };
        var viewModel = new HideoutWorkspaceViewModel(
            requirements,
            new FakePlayerProfileService(TestProfile(
                hideoutLevels: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                owned: new Dictionary<string, int>(StringComparer.Ordinal))),
            new FakeItemRepository());

        await viewModel.LoadAsync();

        Assert.True(viewModel.LoadFault.IsVisible);
        Assert.Equal("The hideout did not load", viewModel.LoadFault.Title);
        Assert.DoesNotContain("no such table", viewModel.LoadFault.Title + viewModel.LoadFault.Detail, StringComparison.Ordinal);
        Assert.Empty(viewModel.Stations);

        requirements.Fails = false;
        await viewModel.LoadFault.RetryAsync();

        Assert.False(viewModel.LoadFault.IsVisible);
        Assert.False(viewModel.LoadFault.IsRetrying);
        Assert.Single(viewModel.Stations);
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

    [Fact]
    public async Task Raising_the_built_level_saves_it_and_moves_the_requirements_to_the_level_after()
    {
        var requirements = new FakeRequirementCatalog
        {
            Stations = [new("lavatory", "Lavatory", [1, 2, 3])],
            Requirements =
            [
                new("lavatory", 2, "item-bolts", 5),
                new("lavatory", 3, "item-nails", 7),
            ],
        };
        var profiles = new FakePlayerProfileService(TestProfile(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["lavatory"] = 1 },
            new Dictionary<string, int>(StringComparer.Ordinal)));
        var items = new FakeItemRepository();
        items.Names["item-bolts"] = "Bolts";
        items.Names["item-nails"] = "Nails";
        var viewModel = new HideoutWorkspaceViewModel(requirements, profiles, items);
        await viewModel.RefreshAsync();
        Assert.Equal("Bolts", Assert.Single(viewModel.Items).ItemName);
        Assert.True(viewModel.CanRaiseLevel);
        Assert.True(viewModel.CanLowerLevel);

        viewModel.RaiseLevelCommand.Execute(null);
        // Until the requirements have followed, not only the station row: the rows are read on
        // the pool now and arrive a moment after the list does.
        await WaitUntilAsync(() =>
            viewModel.Stations.Single().BuiltLevel == 2 && viewModel.Items is [{ ItemName: "Nails" }]);

        Assert.Equal(2, profiles.Current.HideoutStationLevels["lavatory"]);
        Assert.Equal("Level 2 of 3", viewModel.SelectedLevelLabel);
        Assert.Equal("Nails", Assert.Single(viewModel.Items).ItemName);
    }

    [Fact]
    public async Task The_built_level_stays_within_zero_and_the_stations_highest()
    {
        var requirements = new FakeRequirementCatalog { Stations = [new("lavatory", "Lavatory", [1, 2, 3])] };
        var profiles = new FakePlayerProfileService(TestProfile(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["lavatory"] = 2 },
            new Dictionary<string, int>(StringComparer.Ordinal)));
        var viewModel = new HideoutWorkspaceViewModel(requirements, profiles, new FakeItemRepository());
        await viewModel.RefreshAsync();

        await viewModel.SetBuiltLevelAsync("lavatory", 9);
        Assert.Equal(3, profiles.Current.HideoutStationLevels["lavatory"]);
        Assert.False(viewModel.CanRaiseLevel);

        await viewModel.SetBuiltLevelAsync("lavatory", -4);
        Assert.Equal(0, profiles.Current.HideoutStationLevels["lavatory"]);
        Assert.False(viewModel.CanLowerLevel);
        Assert.True(viewModel.CanRaiseLevel);

        await viewModel.SetBuiltLevelAsync("not-a-station", 1);
        Assert.False(profiles.Current.HideoutStationLevels.ContainsKey("not-a-station"));
    }

    [Fact]
    public async Task The_rollup_adds_an_items_needs_across_stations_before_taking_the_holding_off()
    {
        var requirements = new FakeRequirementCatalog
        {
            Stations = [new("lavatory", "Lavatory", [1, 2]), new("workbench", "Workbench", [1, 2])],
            Requirements =
            [
                new("lavatory", 1, "item-bolts", 5),
                new("workbench", 1, "item-bolts", 4),
                new("workbench", 1, "item-nails", 2),
                new("lavatory", 2, "item-gears", 9),
            ],
        };
        var profiles = new FakePlayerProfileService(TestProfile(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.Ordinal) { ["item-bolts"] = 3, ["item-nails"] = 2 }));
        var items = new FakeItemRepository();
        items.Names["item-bolts"] = "Bolts";
        var viewModel = new HideoutWorkspaceViewModel(requirements, profiles, items);

        await viewModel.RefreshAsync();

        // One pile of three bolts serves whichever station is built first: 9 asked for, 3 held.
        var bolts = Assert.Single(viewModel.Rollup);
        Assert.Equal("Bolts", bolts.ItemName);
        Assert.Equal(9, bolts.Need);
        Assert.Equal(3, bolts.Have);
        Assert.Equal(6, bolts.Remaining);
        Assert.Equal("3 / 9", bolts.ProgressLabel);
        Assert.Equal("1 item still needed", viewModel.RollupHeading);
        Assert.True(viewModel.HasRollup);
    }

    [Fact]
    public async Task A_barter_route_shows_only_where_the_traders_loyalty_allows_it_and_it_beats_the_flea()
    {
        var requirements = new FakeRequirementCatalog
        {
            Stations = [new("lavatory", "Lavatory", [1, 2])],
            Requirements = [new("lavatory", 1, "item-bolts", 5)],
        };
        var items = new FakeItemRepository();
        items.Names["item-bolts"] = "Bolts";
        items.FleaPrices["item-bolts"] = 5_000;
        items.FleaPrices["item-scrap"] = 1_000;
        var barters = new FakeBarters(new BarterOffer(
            "b1", "prapor", 2, null, new BarterItem("item-bolts", 1), [new BarterItem("item-scrap", 1)]));

        var unlocked = new HideoutWorkspaceViewModel(
            requirements,
            new FakePlayerProfileService(TestProfile(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal) { ["prapor"] = 2 })),
            items,
            barters);
        var locked = new HideoutWorkspaceViewModel(
            requirements,
            new FakePlayerProfileService(TestProfile(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal) { ["prapor"] = 1 })),
            items,
            barters);

        await unlocked.RefreshAsync();
        await locked.RefreshAsync();

        var row = Assert.Single(unlocked.Items);
        Assert.True(row.HasCheapestRoute);
        Assert.Contains("prapor", row.CheapestRoute);
        Assert.Contains("1,000", row.CheapestRoute);
        Assert.False(Assert.Single(locked.Items).HasCheapestRoute);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static PlayerProfile TestProfile(
        IReadOnlyDictionary<string, int> hideoutLevels,
        IReadOnlyDictionary<string, int> owned,
        IReadOnlyDictionary<string, int>? traderLevels = null) => new(
        Guid.NewGuid(),
        "Tester",
        GameMode.Regular,
        Level: 10,
        Faction.Usec,
        Edition: null,
        TraderLevels: traderLevels ?? new Dictionary<string, int>(StringComparer.Ordinal),
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

        public bool Fails { get; set; }

        public Task<IReadOnlyList<HideoutStationSummary>> GetStationsAsync(CancellationToken cancellationToken) =>
            Fails
                ? Task.FromException<IReadOnlyList<HideoutStationSummary>>(new InvalidOperationException("no such table: hideout_stations"))
                : Task.FromResult(Stations);

        public void Invalidate()
        {
        }
    }

    private sealed class FakePlayerProfileService : IPlayerProfileService
    {
        public FakePlayerProfileService(PlayerProfile profile) => Current = profile;

        public PlayerProfile Current { get; private set; }

        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        public Task SaveAsync(PlayerProfile updated, CancellationToken cancellationToken)
        {
            Current = updated;
            return Task.CompletedTask;
        }

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => Task.FromResult(Current);
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

        public Dictionary<string, long> FleaPrices { get; } = new(StringComparer.Ordinal);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(FleaPrices.TryGetValue(itemId, out var flea)
                ? new ItemPriceSnapshot(flea, [], null, null, null, new DataProvenance("fixture", DateTimeOffset.UtcNow))
                : null);
    }

    private sealed class FakeBarters(params BarterOffer[] offers) : IBarterCatalog
    {
        public Task<IReadOnlyList<BarterOffer>> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BarterOffer>>(offers);
    }
}
