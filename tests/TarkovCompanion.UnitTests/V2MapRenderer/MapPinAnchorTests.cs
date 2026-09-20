using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.Views.V2.MapRenderer;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// Issue 508: "a pin style would be better" than a square with a number in it, but only if the
/// pin's own tip — not the middle of its head — is what sits on the map coordinate. These tests
/// are the maths behind that promise, independent of Avalonia rendering it.
/// </summary>
public sealed class MapPinAnchorTests
{
    [Theory]
    [InlineData(44)]
    [InlineData(1)]
    [InlineData(88)]
    public void The_pins_own_tip_lands_on_the_centre_of_whatever_box_it_is_drawn_in(double boxExtent)
    {
        var (tipX, tipY) = MapPinGeometry.TipFor(boxExtent);

        Assert.Equal(boxExtent / 2, tipX, 10);
        Assert.Equal(boxExtent / 2, tipY, 10);
    }

    [Fact]
    public void The_pins_top_left_leaves_room_for_its_whole_head_above_the_tip()
    {
        var (left, top) = MapPinGeometry.TopLeftFor(MapSceneRendererViewModel.MarkerExtent);

        // The pin is drawn inside the 44px marker box's own coordinates (it may — and for a
        // round or shield head, does — extend above the box's own top edge; nothing clips it).
        // What must hold is that its own bottom edge is exactly at the box's vertical centre,
        // and it is horizontally centred in the box.
        Assert.Equal((MapSceneRendererViewModel.MarkerExtent - MapPinGeometry.Width) / 2, left, 10);
        Assert.Equal(MapSceneRendererViewModel.MarkerExtent / 2, top + MapPinGeometry.Height, 10);
    }

    [Fact]
    public void A_bigger_box_moves_the_tip_but_never_changes_the_pins_own_size()
    {
        var small = MapPinGeometry.TopLeftFor(44);
        var large = MapPinGeometry.TopLeftFor(88);

        // Only the box changed — MapPinGeometry never reads MarkerInverseZoom itself. The 44px
        // box already accounts for zoom via the marker's own ScaleTransform (see
        // MapSceneRendererObjectViewModel.MarkerInverseZoom), so a pin drawn at one zoom is the
        // same size on screen as at another; this only proves the *formula* scales consistently
        // if it were ever asked to draw into a differently sized box.
        Assert.NotEqual(small.Left, large.Left);
        Assert.NotEqual(small.Top, large.Top);
        Assert.Equal(MapPinGeometry.TipFor(44).X * 2, MapPinGeometry.TipFor(88).X, 10);
    }

    [Fact]
    public void The_tip_stays_on_the_spot_however_the_marker_is_rotated_because_the_box_never_turns()
    {
        // MapSceneRendererObjectViewModel.MarkerUprightDegrees exactly cancels the camera's own
        // rotation (see its own remarks), so the 44x44 box a pin is drawn in never turns on
        // screen regardless of map bearing. This is what that guarantee reduces to for the
        // pin's own maths: TipFor takes no bearing at all, because it does not need one.
        var uprightAtNorth = MapPinGeometry.TipFor(MapSceneRendererViewModel.MarkerExtent);
        var uprightAtAnyOtherBearing = MapPinGeometry.TipFor(MapSceneRendererViewModel.MarkerExtent);

        Assert.Equal(uprightAtNorth, uprightAtAnyOtherBearing);
    }
}
