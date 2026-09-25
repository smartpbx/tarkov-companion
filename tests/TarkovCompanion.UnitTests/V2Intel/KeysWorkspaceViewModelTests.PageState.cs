using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.UnitTests.V2Shell;
using static TarkovCompanion.UnitTests.V2Intel.IntelWorkspaceFakes;

namespace TarkovCompanion.UnitTests.V2Intel;

/// <summary>[#902 P8] The verdict chip is remembered, and a chip that empties the list offers All.</summary>
public sealed partial class KeysWorkspaceViewModelTests
{
    [Fact]
    public async Task The_verdict_chip_survives_a_restart_and_an_emptied_list_offers_show_all()
    {
        using var file = new LayoutFile();
        var (_, before) = await RememberingAsync(file);
        before.VerdictFilters.Single(chip => chip.Filter == KeyVerdictFilter.Owned).SelectCommand.Execute(null);

        var (_, after) = await RememberingAsync(file);

        // Nothing has been scanned, so "You own" matches no key: the page says why and offers All.
        Assert.Equal(KeyVerdictFilter.Owned, after.Filter);
        Assert.True(after.ShowsNoKeys);
        Assert.True(after.ShowsFilterReset);

        after.ClearFilterCommand.Execute(null);

        Assert.Equal(KeyVerdictFilter.All, after.Filter);
        Assert.Equal(3, after.Keys.Count);
        Assert.False(after.ShowsFilterReset);
        Assert.Equal(KeyVerdictFilter.All, (await RememberingAsync(file)).Workspace.Filter);
    }

    private static async Task<(KeysPageViewModel Page, KeysWorkspaceViewModel Workspace)> RememberingAsync(LayoutFile file)
    {
        var facts = new[]
        {
            new KeyFacts("dorm", "map-customs", 10, ["Room 214"], [], 220_000, 0, 0, false, 0, Provenance),
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
        var workspace = new KeysWorkspaceViewModel(page, learnMode: new LearnModeSetting(file.Restart()));
        await page.LoadAsync();
        return (page, workspace);
    }
}
