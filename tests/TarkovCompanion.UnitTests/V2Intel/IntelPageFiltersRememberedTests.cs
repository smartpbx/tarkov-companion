using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.UnitTests.V2Shell;
using static TarkovCompanion.UnitTests.V2Intel.IntelWorkspaceFakes;

namespace TarkovCompanion.UnitTests.V2Intel;

/// <summary>[#902 P8] Ammo and Crafts keep their chips and sorts across a restart.</summary>
public sealed class IntelPageFiltersRememberedTests : IDisposable
{
    private readonly LayoutFile _file = new();

    public void Dispose() => _file.Dispose();

    [Fact]
    public void Ammo_armor_class_and_sort_survive_a_restart()
    {
        var before = Ammo(new LearnModeSetting(_file.Restart()));
        before.ArmorFilters.Single(chip => chip.Key == "class-4").SelectCommand.Execute(null);
        before.Sorts.Single(chip => chip.Key == "sort-damage").SelectCommand.Execute(null);

        var after = Ammo(new LearnModeSetting(_file.Restart()));

        Assert.Equal(4, after.ArmorClass);
        Assert.Equal(AmmoSort.Damage, after.Sort);
        Assert.True(after.ArmorFilters.Single(chip => chip.Key == "class-4").IsSelected);
        Assert.True(after.Sorts.Single(chip => chip.Key == "sort-damage").IsSelected);
    }

    [Fact]
    public async Task Crafts_ready_now_and_sort_survive_a_restart()
    {
        var before = new CraftsBartersWorkspaceViewModel(new NoTrades(), _ => { }, new LearnModeSetting(_file.Restart()));
        await before.LoadTask;
        before.ReadyNowOnly = true;
        before.Sorts.Single(sort => sort.Sort == IntelTradeSort.Name).SelectCommand.Execute(null);

        var after = new CraftsBartersWorkspaceViewModel(new NoTrades(), _ => { }, new LearnModeSetting(_file.Restart()));
        await after.LoadTask;

        Assert.True(after.ReadyNowOnly);
        Assert.True(after.Sorts.Single(sort => sort.Sort == IntelTradeSort.Name).IsSelected);
        Assert.False(after.Sorts.Single(sort => sort.Sort == IntelTradeSort.Profit).IsSelected);
    }

    [Fact]
    public void Learn_mode_is_one_switch_shared_by_every_page_that_reads_it()
    {
        var learn = new LearnModeSetting(_file.Restart());
        var ammo = Ammo(learn);
        var crafts = new CraftsBartersWorkspaceViewModel(new NoTrades(), _ => { }, learn);

        ammo.LearnMode.IsEnabled = true;

        Assert.True(crafts.LearnMode.IsEnabled);
        Assert.True(new LearnModeSetting(_file.Restart()).IsEnabled);
    }

    private static AmmoWorkspaceViewModel Ammo(LearnModeSetting learn) =>
        new(new AmmoPageViewModel(new FakeFactCatalog(), new FakeItemRepository()), learnMode: learn);

    private sealed class NoTrades : IIntelTradeCatalogService
    {
        public Task<IReadOnlyList<IntelTradeRow>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IntelTradeRow>>([]);
    }
}
