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

public sealed partial class KeysWorkspaceViewModelTests
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
    public void A_key_rows_learn_line_is_the_key_engines_verdict_reason()
    {
        var key = Row("keep-1", KeepOrSell.Keep);
        var row = new KeyListRowViewModel(key, false, null!);

        Assert.Equal(key.VerdictReason, row.LearnReason);
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

    /// <summary>#283: a key a scan found is marked on its row, the You own chip keeps only those, and the panel says so.</summary>
    [Fact]
    public async Task KeysAScanFoundAreMarkedAndFilterable()
    {
        var profile = new OwnedCountsProfile(new Dictionary<string, int>(StringComparer.Ordinal) { ["marked"] = 1, ["dorm"] = 0 });
        var (page, workspace) = await LoadedAsync(profiles: profile);
        await WaitUntilAsync(() => workspace.HasKeys);

        Assert.Equal("Owned", Assert.Single(workspace.Keys, row => row.Key.ItemId == "marked").Owned);
        Assert.False(Assert.Single(workspace.Keys, row => row.Key.ItemId == "dorm").HasOwned);

        workspace.VerdictFilters.Single(chip => chip.Filter == KeyVerdictFilter.Owned).SelectCommand.Execute(null);
        Assert.Equal(["marked"], workspace.Keys.Select(row => row.Key.ItemId));

        page.Selected = page.Keys.Single(key => key.ItemId == "marked");
        Assert.Equal("You own it.", workspace.SelectedOwned);
        page.Selected = page.Keys.Single(key => key.ItemId == "dorm");
        Assert.Equal("You don't own it.", workspace.SelectedOwned);
        page.Selected = page.Keys.Single(key => key.ItemId == "resort");
        Assert.Equal("Owned: not scanned. Stash › Key cases.", workspace.SelectedOwned);
    }

    private static async Task<(KeysPageViewModel Page, KeysWorkspaceViewModel Workspace)> LoadedAsync(
        Action<string>? openItem = null,
        TarkovCompanion.Core.Abstractions.IPlayerProfileService? profiles = null)
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
            maps: new NamedMaps(("map-customs", "Customs"), ("map-shoreline", "Shoreline")),
            profiles: profiles);
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
        Verdict = new(call, new(TarkovCompanion.Core.Domain.Planning.KeyVerdictReason.Middle)),
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

    /// <summary>
    /// #874: searching "Salewa" read "Best 34,330 ₽ · Flea (54cb57776803fa99248b456e)" and
    /// "1 results": the flea's best named the best trader, by the id an item refresh left as its name.
    /// </summary>
    [Theory]
    [InlineData(34_330L, 7_695L, "54cb57776803fa99248b456e", "Best 34,330 ₽ on the flea")]
    [InlineData(5_000L, 7_695L, "Therapist", "Best 7,695 ₽ at Therapist")]
    [InlineData(5_000L, 7_695L, "54cb57776803fa99248b456e", "Best 7,695 ₽ at a trader")]
    public async Task TheBestSaleNamesTheFleaOrATraderNeverAnId(long flea, long trader, string traderName, string expected)
    {
        var salewa = Item("544fb45d4bdc2dee738b4568", "Salewa first aid kit", "Salewa");
        var repository = new FakeItemRepository(salewa)
        {
            Price = new ItemPriceSnapshot(
                flea,
                [new TraderOffer("54cb57776803fa99248b456e", traderName, trader, Provenance)],
                null,
                null,
                null,
                Provenance),
        };
        var page = new FleaPageViewModel(new RepositorySearch(repository), repository, new EmptyHistory());
        var workspace = new FleaWorkspaceViewModel(page);
        page.Apply(V2ShellTestData.Snapshot().WithData(DataAvailability.Current, 10, DateTimeOffset.UnixEpoch));

        workspace.SearchQuery = "salewa";
        await page.SearchAsync();

        var row = Assert.Single(workspace.Results);
        Assert.Equal(expected, row.BestSale);
        Assert.Equal(expected, workspace.SelectedBestSale);
        Assert.Equal("1 result from the local cache", workspace.SearchStatus);
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

    /// <summary>
    /// A failed search printed "Item search failed: " and the exception's message. It now says so
    /// in words, offers Retry where the rows were, and Retry that succeeds takes the notice away.
    /// </summary>
    [Fact]
    public async Task AFailedSearchSaysSoInWordsAndRetryRecovers()
    {
        var repository = new FakeItemRepository(Item("gpu", "Graphics card"));
        var search = new FailingOnceSearch(new RepositorySearch(repository));
        var page = new FleaPageViewModel(search, repository, new EmptyHistory());
        var workspace = new FleaWorkspaceViewModel(page);
        page.Apply(V2ShellTestData.Snapshot().WithData(DataAvailability.Current, 10, DateTimeOffset.UnixEpoch));

        workspace.SearchQuery = "graphics";
        await page.SearchAsync();

        Assert.Equal("Search failed", workspace.SearchStatus);
        Assert.DoesNotContain(FailingOnceSearch.Message, workspace.SearchStatus, StringComparison.Ordinal);
        Assert.True(workspace.SearchFault.IsVisible);
        Assert.Equal("Couldn't search the item cache", workspace.SearchFault.Title);
        Assert.DoesNotContain(FailingOnceSearch.Message, workspace.SearchFault.Detail, StringComparison.Ordinal);
        Assert.False(workspace.ShowsNoResults);

        await workspace.SearchFault.RetryAsync();

        Assert.False(workspace.SearchFault.IsVisible);
        Assert.True(workspace.HasResults);
        Assert.Equal("1 result from the local cache", workspace.SearchStatus);
    }

    private sealed class FailingOnceSearch(IItemSearchService inner) : IItemSearchService
    {
        public const string Message = "SQLite Error 1: 'no such table: items'";
        private bool _failed;

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        {
            if (!_failed)
            {
                _failed = true;
                throw new InvalidOperationException(Message);
            }

            return inner.SearchAsync(query, limit, cancellationToken);
        }
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
