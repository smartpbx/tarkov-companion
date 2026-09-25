using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.Application.Services.Intel;

namespace TarkovCompanion.UnitTests.V2Intel;

/// <summary>[#902 P10] "I can do this now" emptied by unrecorded levels says so and links to where they are set.</summary>
public sealed class CraftsUnknownLevelsEmptyStateTests
{
    [Fact]
    public async Task Unknown_levels_name_the_missing_input_and_link_to_each_page_that_sets_it()
    {
        var navigated = new List<V2RouteId>();
        var crafts = new CraftsBartersWorkspaceViewModel(
            new Trades(Row("a", IntelTradeKind.Craft), Row("b", IntelTradeKind.Craft), Row("c", IntelTradeKind.Barter)),
            _ => { });
        crafts.AttachNavigation(navigated.Add);
        await crafts.LoadTask;

        Assert.False(crafts.ShowsSetTraderLevels);
        crafts.ReadyNowOnly = true;

        Assert.True(crafts.ShowsEmpty);
        Assert.Equal("3 need your trader or hideout levels before they can be checked.", crafts.EmptyLabel);
        Assert.True(crafts.ShowsSetTraderLevels);
        Assert.True(crafts.ShowsSetHideoutLevels);
        Assert.True(crafts.ShowsFilterReset);

        crafts.SetTraderLevelsCommand.Execute(null);
        crafts.SetHideoutLevelsCommand.Execute(null);
        Assert.Equal([V2Routes.Plan, V2Routes.Hideout], navigated);

        crafts.SearchText = "a";
        Assert.False(crafts.ShowsSetTraderLevels);
        Assert.True(crafts.ShowsSetHideoutLevels);
    }

    private static IntelTradeRow Row(string name, IntelTradeKind kind) => new(
        name,
        kind,
        [],
        new IntelTradeIngredient(name, name, 1),
        "Source",
        string.Empty,
        null,
        null,
        null,
        null,
        IntelTradeReadiness.Unknown);

    private sealed class Trades(params IntelTradeRow[] rows) : IIntelTradeCatalogService
    {
        public Task<IReadOnlyList<IntelTradeRow>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IntelTradeRow>>(rows);
    }
}
