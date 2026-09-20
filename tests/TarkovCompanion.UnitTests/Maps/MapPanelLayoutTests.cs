using TarkovCompanion.App.ViewModels.V2.MapRenderer;

namespace TarkovCompanion.UnitTests.Maps;

/// <summary>
/// V2 rough package 32: the arithmetic that decides where a map workspace's panel goes.
/// </summary>
/// <remarks>
/// The numbers are the ones the acceptance sweep measured in the real window: the V2 rail is
/// 168px, Plan's map list is 340 and its objectives panel 440, and the body is the window minus
/// the top bar and the section tabs. Customs draws at about 1.8:1.
/// </remarks>
public sealed class MapPanelLayoutTests
{
    // Measured from a real render, not from the map's nominal bounds: what the arithmetic has to
    // agree with is the shape the artwork is drawn at, which for Customs is almost exactly 2:1.
    private const double CustomsAspect = 2.0;

    // Window height 1080, less the 60px top bar and the section tab strip.
    private const double BodyHeight1080 = 935;

    [Fact]
    public void Plan_at_1920x1080_draws_more_map_with_its_objectives_underneath()
    {
        // 1920 less the rail and the map list leaves 1412 for the map and the panel together.
        var placement = MapPanelLayout.Choose(1412, BodyHeight1080, panelWidth: 440, panelHeight: 320, CustomsAspect);

        Assert.Equal(MapPanelPlacement.Below, placement);
    }

    [Fact]
    public void Plan_on_an_ultrawide_keeps_the_concepts_arrangement()
    {
        var placement = MapPanelLayout.Choose(3332, BodyHeight1080, panelWidth: 440, panelHeight: 320, CustomsAspect);

        Assert.Equal(MapPanelPlacement.Beside, placement);
    }

    [Fact]
    public void A_cockpit_with_no_list_beside_it_keeps_its_panel_beside_at_1920()
    {
        // Raid and Team: the window less the rail, and a panel that is already proportional.
        var placement = MapPanelLayout.Choose(1752, BodyHeight1080, panelWidth: 487, panelHeight: 320, CustomsAspect);

        Assert.Equal(MapPanelPlacement.Beside, placement);
    }

    [Fact]
    public void A_near_tie_keeps_the_panel_where_a_player_expects_it()
    {
        // Below wins outright at the height the Plan workspace actually gives it.
        Assert.Equal(
            MapPanelPlacement.Below,
            MapPanelLayout.Choose(1412, BodyHeight1080, panelWidth: 440, panelHeight: 320, CustomsAspect));

        // A little more width for the map and the panel together, and below still wins — but by
        // about six per cent, which is not worth moving a player's panel for.
        const double NearlyTiedWidth = 1620;
        var beside = MapPanelLayout.DrawnArea(NearlyTiedWidth - 440, BodyHeight1080, CustomsAspect);
        var below = MapPanelLayout.DrawnArea(NearlyTiedWidth, BodyHeight1080 - 320, CustomsAspect);
        Assert.True(below > beside, "this case is only interesting when below would otherwise win");
        Assert.True(below < beside * MapPanelLayout.BelowMargin, "and only when it wins by less than the margin");

        Assert.Equal(
            MapPanelPlacement.Beside,
            MapPanelLayout.Choose(NearlyTiedWidth, BodyHeight1080, panelWidth: 440, panelHeight: 320, CustomsAspect));
    }

    [Theory]
    [InlineData(0, 900, 2.0)]
    [InlineData(900, 0, 2.0)]
    [InlineData(900, 900, 0)]
    [InlineData(900, 900, double.NaN)]
    [InlineData(double.NaN, 900, 2.0)]
    public void A_box_or_a_shape_that_cannot_be_drawn_into_is_worth_nothing(double width, double height, double aspect)
    {
        Assert.Equal(0, MapPanelLayout.DrawnArea(width, height, aspect));
    }

    [Fact]
    public void An_unknown_plan_shape_leaves_the_panel_where_it_is()
    {
        var placement = MapPanelLayout.Choose(1412, BodyHeight1080, 440, 320, double.NaN);

        Assert.Equal(MapPanelPlacement.Beside, placement);
    }

    [Fact]
    public void The_drawn_area_is_the_contain_fit_the_projection_will_apply()
    {
        // Height-limited: a 2000x500 box cannot draw a 2:1 plan wider than 500 * 2.
        Assert.Equal(1000 * (1000 / 2.0), MapPanelLayout.DrawnArea(2000, 500, 2.0), 3);

        // Width-limited: a 400x900 box draws it 400 wide.
        Assert.Equal(400 * (400 / 2.0), MapPanelLayout.DrawnArea(400, 900, 2.0), 3);
    }
}
