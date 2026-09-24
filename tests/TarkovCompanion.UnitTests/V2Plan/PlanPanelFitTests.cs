using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Plan;

namespace TarkovCompanion.UnitTests.V2Plan;

/// <summary>
/// [#828] Plan › Quests at 150-200% interface scale: the objectives keep a full row's width.
/// </summary>
/// <remarks>
/// Widths are the map-and-panel width (the shell less the rail and the 340 map list) and heights
/// the body under the section tabs, read from 1920x1080 renders at each scale.
/// </remarks>
public sealed class PlanPanelFitTests
{
    private const double PanelWidthBeside = 440;

    [Fact]
    public void At_100_percent_the_side_column_keeps_its_full_width()
    {
        Assert.Equal(PlanPanelArrangement.BelowSideBySide, PlanPanelFit.Arrange(MapPanelPlacement.Below, 1412, 935, PanelWidthBeside));
        Assert.Equal(PlanPanelFit.SidePieceFullWidth, PlanPanelFit.SidePieceWidth(1412));
    }

    [Fact]
    public void At_150_percent_the_side_column_narrows_so_an_objective_row_fits()
    {
        const double panel = 737;

        var side = PlanPanelFit.SidePieceWidth(panel);

        Assert.Equal(PlanPanelArrangement.BelowSideBySide, PlanPanelFit.Arrange(MapPanelPlacement.Below, panel, 610, PanelWidthBeside));
        Assert.InRange(side, PlanPanelFit.SidePieceMinimumWidth, PlanPanelFit.SidePieceFullWidth - 1);
        Assert.True(panel - side - PlanPanelFit.SidePieceMargins >= PlanPanelFit.ObjectivesMinimumWidth);
    }

    [Fact]
    public void At_200_percent_on_1080p_the_map_preview_gives_way_to_the_panel()
    {
        Assert.Equal(PlanPanelArrangement.PanelOnly, PlanPanelFit.Arrange(MapPanelPlacement.Below, 530, 433, PanelWidthBeside));
    }

    [Fact]
    public void A_narrow_but_tall_window_keeps_the_map_and_stacks_the_panel_under_it()
    {
        Assert.Equal(PlanPanelArrangement.BelowStacked, PlanPanelFit.Arrange(MapPanelPlacement.Below, 600, 1200, PanelWidthBeside));
    }

    [Fact]
    public void Beside_is_kept_only_while_it_leaves_a_map_worth_drawing()
    {
        Assert.Equal(PlanPanelArrangement.Beside, PlanPanelFit.Arrange(MapPanelPlacement.Beside, 3300, 935, PanelWidthBeside));
        Assert.Equal(PlanPanelArrangement.PanelOnly, PlanPanelFit.Arrange(MapPanelPlacement.Beside, 400, 433, PanelWidthBeside));
    }

    [Fact]
    public void An_unmeasured_size_changes_nothing()
    {
        Assert.Equal(PlanPanelArrangement.BelowSideBySide, PlanPanelFit.Arrange(MapPanelPlacement.Below, double.NaN, double.NaN, PanelWidthBeside));
        Assert.Equal(PlanPanelFit.SidePieceFullWidth, PlanPanelFit.SidePieceWidth(double.NaN));
    }
}
