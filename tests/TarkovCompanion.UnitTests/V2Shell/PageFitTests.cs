using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// [#832] Intel and Plan pages at 150-200% interface scale on 1920x1080.
/// </summary>
/// <remarks>
/// Page widths are the shell less the rail: 1752 at 100%, 1112 at 150% and 900 at 200%.
/// </remarks>
public sealed class PageFitTests
{
    private const double At100 = 1752;
    private const double At150 = 1112;
    private const double At200 = 900;

    [Fact]
    public void At_100_percent_nothing_moves()
    {
        Assert.Equal(new AmmoPageFit(true, PageFit.AmmoCaliberListWidth, false), PageFit.Ammo(At100));
        Assert.False(PageFit.EventsStacked(At100));
        Assert.Equal(PageFit.HideoutListWidth, PageFit.HideoutList(At100));
        Assert.Equal(PageFit.HideoutSectionWidth, PageFit.HideoutSection(At100));
        Assert.Equal(520, PageFit.SidePanelWidth(At100, 520, 400, 480));
    }

    [Fact]
    public void Ammo_puts_the_round_panel_under_the_table_once_the_table_would_lose_its_row()
    {
        var fit = PageFit.Ammo(At150);

        Assert.False(fit.DetailBeside);
        Assert.Equal(PageFit.AmmoCaliberListWidth, fit.CaliberListMinimum);
        Assert.True(fit.ClassesOnOwnLine);
    }

    [Fact]
    public void Ammo_at_200_percent_narrows_the_caliber_list_and_drops_the_chips_under_each_round()
    {
        Assert.Equal(new AmmoPageFit(false, PageFit.AmmoCaliberListNarrowWidth, true), PageFit.Ammo(At200));
    }

    [Fact]
    public void Ammo_keeps_the_panel_beside_from_the_width_that_holds_a_full_table()
    {
        Assert.True(PageFit.Ammo(PageFit.AmmoDetailBesideMinimumWidth).DetailBeside);
        Assert.False(PageFit.Ammo(PageFit.AmmoDetailBesideMinimumWidth - 1).DetailBeside);
        Assert.False(PageFit.Ammo(PageFit.AmmoDetailBesideMinimumWidth).ClassesOnOwnLine);
    }

    [Fact]
    public void A_wide_page_under_the_threshold_keeps_the_chips_on_the_row()
    {
        // The panel has gone under, which gives the table the whole page back.
        var fit = PageFit.Ammo(1400);

        Assert.False(fit.DetailBeside);
        Assert.False(fit.ClassesOnOwnLine);
    }

    [Fact]
    public void Events_stack_at_200_percent_but_not_at_150()
    {
        Assert.False(PageFit.EventsStacked(At150));
        Assert.True(PageFit.EventsStacked(At200));
    }

    [Fact]
    public void Hideout_sections_fit_the_card_at_200_percent()
    {
        var section = PageFit.HideoutSection(At200);

        Assert.Equal(PageFit.HideoutListNarrowWidth, PageFit.HideoutList(At200));
        Assert.InRange(section, PageFit.HideoutSectionMinimumWidth, PageFit.HideoutSectionWidth - 1);
        Assert.True(PageFit.HideoutList(At200) + section + 84 <= At200);
        Assert.Equal(PageFit.HideoutSectionWidth, PageFit.HideoutSection(At150));
    }

    [Fact]
    public void Loadout_suggestions_keep_one_line_at_100_percent_only()
    {
        Assert.False(PageFit.LoadoutSuggestionsStacked(At100));
        Assert.True(PageFit.LoadoutSuggestionsStacked(At150));
        Assert.True(PageFit.LoadoutSuggestionsStacked(At200));
    }

    [Fact]
    public void A_side_panel_gives_way_to_the_page_and_stops_at_its_minimum()
    {
        Assert.Equal(420, PageFit.SidePanelWidth(At200, 520, 400, 480));
        Assert.Equal(400, PageFit.SidePanelWidth(600, 520, 400, 480));
        Assert.Equal(520, PageFit.SidePanelWidth(double.NaN, 520, 400, 480));
    }

    [Fact]
    public void An_unmeasured_page_gets_the_designed_layout()
    {
        Assert.True(PageFit.Ammo(0).DetailBeside);
        Assert.False(PageFit.EventsStacked(double.PositiveInfinity));
        Assert.Equal(PageFit.HideoutSectionWidth, PageFit.HideoutSection(double.NaN));
    }
}
