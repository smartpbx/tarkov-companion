using Avalonia;
using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Whether there is anywhere to drag the map to.
/// </summary>
/// <remarks>
/// Reported as "i cant move the map around or anything". Panning moves the scroll offset, and
/// a scroll offset has no range when the content is no larger than the panel it sits in. The
/// map opens fitted — the whole of it on screen by definition — so the first thing anybody
/// does with a map did nothing, and did nothing silently.
///
/// Nothing was broken. What was missing is anything saying so, which is the difference between
/// a map that is fitted and a map that is dead.
/// </remarks>
public sealed class MapPanTests
{
    [Fact]
    public void A_map_larger_than_the_panel_can_be_dragged()
    {
        Assert.True(MapViewModel.CanPan(new Size(2000, 2000), new Size(800, 600)));
    }

    [Fact]
    public void A_fitted_map_has_nowhere_to_go()
    {
        // What a fit produces: the whole map on screen, and smaller than the panel in the
        // axis that was not the limiting one.
        Assert.False(MapViewModel.CanPan(new Size(800, 450), new Size(800, 600)));
    }

    [Fact]
    public void One_axis_of_room_is_enough_to_drag()
    {
        // A wide map fitted by height still runs off both sides, and dragging it sideways is
        // exactly what somebody wants.
        Assert.True(MapViewModel.CanPan(new Size(2000, 600), new Size(800, 600)));
    }

    [Fact]
    public void A_map_exactly_the_size_of_the_panel_has_nowhere_to_go()
    {
        // The limiting axis of a fit lands here, and floating point lands either side of it,
        // which is why the comparison carries a tolerance rather than being exact.
        Assert.False(MapViewModel.CanPan(new Size(800, 600), new Size(800, 600)));
        Assert.False(MapViewModel.CanPan(new Size(800.2, 600.2), new Size(800, 600)));
    }
}
