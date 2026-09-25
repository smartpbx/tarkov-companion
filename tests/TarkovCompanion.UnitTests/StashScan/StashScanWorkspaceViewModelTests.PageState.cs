using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels.V2.StashScan;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.UnitTests.V2Contracts;
using TarkovCompanion.UnitTests.V2Shell;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.UnitTests.StashScan;

/// <summary>[#902 P8] The sort-plan tiles filter the item list; the choice and the view are remembered.</summary>
public sealed partial class StashScanWorkspaceViewModelTests
{
    [Fact]
    public async Task A_plan_tile_narrows_the_list_and_the_choice_survives_a_restart()
    {
        using var file = new LayoutFile();
        var before = await SortedAsync(file.Restart());
        Assert.True(before.IsGridView);

        before.PlanTiles.Single(tile => tile.IsKeep).SelectCommand!.Execute(null);

        // Nothing is Keep on this snapshot: the list says which group it shows and offers the rest.
        Assert.True(before.IsListView);
        Assert.Empty(before.Items);
        Assert.True(before.HasGroupFilter);
        Assert.Equal("Only Keep · 0 items", before.GroupFilterLabel);
        Assert.True(before.PlanTiles.Single(tile => tile.IsKeep).IsSelected);

        before.PlanTiles.Single(tile => tile.IsSell).SelectCommand!.Execute(null);
        Assert.Equal("Gas analyzer", Assert.Single(before.Items).DisplayName);

        var after = await SortedAsync(file.Restart());

        Assert.True(after.IsListView);
        Assert.True(after.HasGroupFilter);
        Assert.True(after.PlanTiles.Single(tile => tile.IsSell).IsSelected);
        Assert.Single(after.Items);

        after.ShowAllGroupsCommand.Execute(null);

        Assert.False(after.HasGroupFilter);
        Assert.False((await SortedAsync(file.Restart())).HasGroupFilter);
    }

    [Fact]
    public async Task A_tile_the_plan_has_not_sorted_does_nothing_and_says_so()
    {
        var store = new FakeSnapshotStore();
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(V2Shell.V2ShellTestData.Snapshot() with { Profile = null }));

        await viewModel.LoadAsync();

        Assert.All(viewModel.PlanTiles.Where(tile => !tile.IsWired), tile => Assert.Null(tile.SelectCommand));
    }

    private static async Task<StashScanWorkspaceViewModel> SortedAsync(IWorkspaceLayoutStore layout)
    {
        var store = new FakeSnapshotStore();
        store.Seed(Record(scope: new(ProfileId, "wipe-fixture", "Pvp")));
        var provenance = new DataProvenance("fixture", DateTimeOffset.UnixEpoch);
        var catalog = new FakeItemFactCatalog(
            [new AmmoStats("ammo-9x19", "9x19mm", 10, 20, null, null, 1, null, null, null, false, false, provenance)],
            [new KeyFacts("key-101", "customs", 20, [], ["quest-1", "quest-2"], null, 0, 0, false, 0, provenance)]);
        var reviewCommands = new InMemoryStashReviewCommandSink();
        var now = V2Capture.LootScanFactFixtures.Now;
        var profiles = new ProfileContextService(new Profiles.MemoryProfileStore(), new Profiles.ProfileClock(now));
        var profile = Profiles.ProfileV2Fixtures.Profile(
            Profiles.ProfileV2Fixtures.Context(ProfileId, "wipe-fixture", Core.Domain.Profiles.ProfileGameMode.Pvp),
            "unrelated");
        await profiles.CreateAsync(Profiles.ProfileV2Fixtures.Request(profile), CancellationToken.None);
        var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);
        var facts = new V2Capture.LootScanFactFixtures.Catalog();
        var viewModel = new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            catalog,
            new FakeRuntimeStateStore(RuntimeSnapshot()),
            clock: new Runtime.ManualTimeProvider(now),
            profileContext: runtime,
            planSource: new StashPlanSource(new LootScanRecommendationSource(
                facts,
                facts,
                new LootScanNeedSource(
                    new V2Capture.LootScanFactFixtures.Profiles(),
                    new ProfileNeedAggregationService([], []),
                    new V2Capture.LootScanFactFixtures.Quests([]),
                    new V2Capture.LootScanFactFixtures.Requirements()))),
            layout: layout);

        await viewModel.LoadAsync();
        return viewModel;
    }
}
