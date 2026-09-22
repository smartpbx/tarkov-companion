using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.UnitTests.V2Shell;
using static TarkovCompanion.UnitTests.V2Intel.IntelWorkspaceFakes;

namespace TarkovCompanion.UnitTests.V2Intel;

public sealed class KeysWorkspaceViewModelTests
{
    [Fact]
    public void AVerdictChipKeepsOnlyThatVerdictAndAllKeepsEveryKey()
    {
        var keys = new[]
        {
            Row("keep-1", KeepOrSell.Keep),
            Row("later-1", KeepOrSell.KeepForLater),
            Row("sell-1", KeepOrSell.Sell),
            Row("sell-2", KeepOrSell.Sell),
            Row("nocall-1", KeepOrSell.NoCall),
        };

        Assert.Equal(["keep-1"], KeysWorkspaceViewModel.Narrow(keys, KeyVerdictFilter.Keep).Select(key => key.ItemId));
        Assert.Equal(["later-1"], KeysWorkspaceViewModel.Narrow(keys, KeyVerdictFilter.KeepForLater).Select(key => key.ItemId));
        Assert.Equal(["sell-1", "sell-2"], KeysWorkspaceViewModel.Narrow(keys, KeyVerdictFilter.Sell).Select(key => key.ItemId));
        Assert.Equal(5, KeysWorkspaceViewModel.Narrow(keys, KeyVerdictFilter.All).Count);
    }

    [Fact]
    public async Task TheFilterBoxFindsKeysByTheMapTheyBelongToAsWellAsByName()
    {
        var (page, workspace) = await LoadedAsync();

        workspace.SearchQuery = "customs";
        Assert.Equal(["Dorm room 214 key", "Marked key"], workspace.Keys.Select(key => key.Name).Order());

        workspace.SearchQuery = "shoreline";
        Assert.Equal(["Health resort key"], workspace.Keys.Select(key => key.Name));

        workspace.SearchQuery = "marked";
        Assert.Equal(["Marked key"], workspace.Keys.Select(key => key.Name));
        Assert.Equal("1 key", workspace.KeyCountLabel);
        Assert.Equal("marked", page.SearchQuery);
    }

    [Fact]
    public async Task LoadingOpensTheFirstKeyAndItsFactsAndLocksFollow()
    {
        var (_, workspace) = await LoadedAsync();

        await WaitUntilAsync(() => workspace.HasSelectedKey);

        Assert.False(workspace.ShowsNoSelectedKey);
        Assert.Single(workspace.Keys, key => key.IsSelected);
        Assert.NotEmpty(workspace.SelectedName);
        Assert.True(workspace.HasSelectedLockIds);
        Assert.Contains("lock", workspace.SelectedLocks);
    }

    [Fact]
    public async Task PickingAKeyMovesTheSelectionWithoutBuildingTheListAgain()
    {
        // [#453] Each pick used to build every row again, and the view drew them all again.
        var (_, workspace) = await LoadedAsync();
        await WaitUntilAsync(() => workspace.HasSelectedKey);
        var rows = workspace.Keys;
        var other = rows.First(row => !row.IsSelected);

        other.SelectCommand.Execute(null);

        Assert.Same(rows, workspace.Keys);
        Assert.True(other.IsSelected);
        Assert.Single(workspace.Keys, key => key.IsSelected);
    }

    [Fact]
    public async Task AChipThatHidesEveryKeySaysSoAndTheUnfilteredCountSurvives()
    {
        var (_, workspace) = await LoadedAsync();
        await WaitUntilAsync(() => workspace.HasKeys);

        // No quest progress is wired into this page, so no key here can be a "keep": the verdict
        // comes from price alone and the page never calls anything a keep without a need.
        workspace.VerdictFilters.Single(chip => chip.Filter == KeyVerdictFilter.Keep).SelectCommand.Execute(null);

        Assert.True(workspace.ShowsNoKeys);
        Assert.Equal("No key matches this filter.", workspace.NoKeysLabel);
        Assert.Equal("0 of 3 keys", workspace.KeyCountLabel);
        Assert.True(workspace.VerdictFilters.Single(chip => chip.Filter == KeyVerdictFilter.Keep).IsSelected);
    }

    [Fact]
    public async Task OpeningInIntelHandsOverTheChosenKeysItemId()
    {
        string? opened = null;
        var (page, workspace) = await LoadedAsync(id => opened = id);
        await WaitUntilAsync(() => workspace.HasSelectedKey);

        workspace.OpenInIntelCommand.Execute(null);

        Assert.Equal(page.Selected!.ItemId, opened);
    }

    private static async Task<(KeysPageViewModel Page, KeysWorkspaceViewModel Workspace)> LoadedAsync(Action<string>? openItem = null)
    {
        var facts = new[]
        {
            new KeyFacts("dorm", "map-customs", 10, ["Room 214", "Room 215"], [], 220_000, 0, 0, false, 0, Provenance),
            new KeyFacts("marked", "map-customs", 2, ["Marked room"], [], 900_000, 0, 0, false, 0, Provenance),
            new KeyFacts("resort", "map-shoreline", 8, ["Room 222"], [], 300_000, 0, 0, false, 0, Provenance),
        };
        var page = new KeysPageViewModel(
            new FakeFactCatalog { Keys = facts },
            new FakeItemRepository(
                Item("dorm", "Dorm room 214 key", category: ItemCategory.Key),
                Item("marked", "Marked key", category: ItemCategory.Key),
                Item("resort", "Health resort key", category: ItemCategory.Key)),
            questProgress: null,
            maps: new NamedMaps(("map-customs", "Customs"), ("map-shoreline", "Shoreline")));
        var workspace = new KeysWorkspaceViewModel(page, openItem);

        await page.LoadAsync();
        return (page, workspace);
    }

    private static KeyRowViewModel Row(string id, KeepOrSell call) => new(
        id,
        id,
        "map",
        "Customs",
        true,
        "Opens 1 lock",
        "1 use(s)",
        "1 ₽",
        "test",
        [])
    {
        Verdict = new(call, "because"),
    };

    private sealed class NamedMaps(params (string Id, string Name)[] maps) : IMapDataService
    {
        public Task<MapDefinition?> GetAsync(string mapId, CancellationToken cancellationToken)
        {
            var match = maps.FirstOrDefault(map => map.Id == mapId);
            return Task.FromResult<MapDefinition?>(match.Id is null
                ? null
                : new MapDefinition(match.Id, match.Name, null, null, [], [], null, Provenance));
        }
    }
}

public sealed class FleaWorkspaceViewModelTests
{
    [Fact]
    public async Task OneStoredObservationIsDescribedAsOnePriceNotASevenDayRange()
    {
        var gpu = Item("gpu", "Graphics card", "GPU");
        var repository = new FakeItemRepository(gpu)
        {
            Price = new ItemPriceSnapshot(322_222, [], null, null, null, Provenance),
        };
        var page = new FleaPageViewModel(
            new RepositorySearch(repository),
            repository,
            new FixedHistory(new PriceHistoryPoint(DateTimeOffset.UtcNow, 322_222, 100_000, "sync")));
        page.Apply(V2ShellTestData.Snapshot().WithData(DataAvailability.Current, 10, DateTimeOffset.UnixEpoch));
        page.SearchQuery = "graphics";

        await page.SearchAsync();

        Assert.Equal("1 price so far", Assert.Single(page.Results).SevenDayBand);
    }

    [Fact]
    public async Task ALookupFillsTheRowsAndTheOnlyHitOpensWithItsHistory()
    {
        var gpu = Item("gpu", "Graphics card", "GPU");
        var price = new ItemPriceSnapshot(
            322_222,
            [new TraderOffer("therapist", "Therapist", 100_980, Provenance)],
            337_352,
            270_000,
            388_888,
            Provenance);
        var repository = new FakeItemRepository(gpu) { Price = price };
        var page = new FleaPageViewModel(new RepositorySearch(repository), repository, new FixedHistory(
            new(DateTimeOffset.UtcNow.AddDays(-2), 300_000, 100_000, "sync"),
            new(DateTimeOffset.UtcNow.AddDays(-1), 360_000, 100_000, "sync")));
        string? opened = null;
        var workspace = new FleaWorkspaceViewModel(page, id => opened = id);
        page.Apply(V2ShellTestData.Snapshot().WithData(DataAvailability.Current, 10, DateTimeOffset.UnixEpoch));

        workspace.SearchQuery = "graphics";
        await page.SearchAsync();

        Assert.True(workspace.HasResults);
        Assert.False(workspace.ShowsNoResults);
        var row = Assert.Single(workspace.Results);
        Assert.Equal("Graphics card", row.Name);
        Assert.Contains("322,222", row.FleaPrice);
        Assert.Contains("per slot", row.ValuePerSlot);
        Assert.True(row.IsSelected);
        Assert.True(workspace.HasSelection);
        Assert.Equal("Graphics card", workspace.SelectedName);
        Assert.Contains("24h", workspace.SelectedBand);
        Assert.Equal("7 d low 300,000 ₽ · avg 330,000 ₽ · high 360,000 ₽", row.SevenDayBand);
        Assert.Equal(row.SevenDayBand, workspace.SelectedSevenDayBand);

        await WaitUntilAsync(() => workspace.HistoryStatus.StartsWith("2 observations", StringComparison.Ordinal));
        Assert.True(workspace.HasHistory);

        workspace.OpenInIntelCommand.Execute(null);
        Assert.Equal("gpu", opened);
    }

    [Fact]
    public async Task ALookupThatFindsNothingSaysSoAndSelectsNothing()
    {
        var repository = new FakeItemRepository(Item("gpu", "Graphics card"));
        var page = new FleaPageViewModel(new RepositorySearch(repository), repository, new EmptyHistory());
        var workspace = new FleaWorkspaceViewModel(page);
        page.Apply(V2ShellTestData.Snapshot().WithData(DataAvailability.Current, 10, DateTimeOffset.UnixEpoch));

        workspace.SearchQuery = "zzz";
        await page.SearchAsync();

        Assert.True(workspace.ShowsNoResults);
        Assert.False(workspace.HasSelection);
        Assert.Equal("No local item matched that query.", workspace.SearchStatus);
    }

    private sealed class RepositorySearch(IItemRepository repository) : IItemSearchService
    {
        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            repository.SearchAsync(query, limit, cancellationToken);
    }

    private sealed class EmptyHistory : IPriceHistoryService
    {
        public Task<IReadOnlyList<PriceHistoryPoint>> GetAsync(string itemId, TimeSpan window, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PriceHistoryPoint>>([]);
    }

    private sealed class FixedHistory(params PriceHistoryPoint[] points) : IPriceHistoryService
    {
        public Task<IReadOnlyList<PriceHistoryPoint>> GetAsync(string itemId, TimeSpan window, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PriceHistoryPoint>>(points);
    }
}
