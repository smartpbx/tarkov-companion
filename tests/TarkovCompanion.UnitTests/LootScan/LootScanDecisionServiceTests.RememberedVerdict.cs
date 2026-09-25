using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.UnitTests.LootScan;

/// <summary>[#902 P8] A new scan opens on the remembered verdict chip, and an emptied list offers All.</summary>
public sealed partial class LootScanDecisionServiceTests
{
    [Fact]
    public void A_scan_opened_on_a_remembered_chip_that_matches_nothing_says_so_and_offers_all()
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "rare-loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(anchor, "rare-loot", valueRoubles: 120_000)]);

        var viewModel = new LootScanViewModel(result, culture: CultureInfo.InvariantCulture, filter: LootScanVerdict.Swap);

        Assert.Equal(LootScanVerdict.Swap, viewModel.Filter);
        Assert.True(viewModel.Filters.Single(chip => chip.Verdict == LootScanVerdict.Swap).IsSelected);
        Assert.Empty(viewModel.VisibleDecisions);
        Assert.True(viewModel.ShowsFilterEmpty);
        Assert.Equal("No Swap calls on this scan", viewModel.FilterEmptyLabel);

        viewModel.ClearFilterCommand.Execute(null);

        Assert.Null(viewModel.Filter);
        Assert.Single(viewModel.VisibleDecisions);
        Assert.False(viewModel.ShowsFilterEmpty);
    }
}
