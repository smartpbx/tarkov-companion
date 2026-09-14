using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What the flat map does while the floors are stacked.
/// </summary>
/// <remarks>
/// Reported as "the 3d view doesnt seem to work at all for me... there is nothing 3d on it",
/// and it took the gallery photographing the stacked view to settle: the stacked Customs was
/// pixel-identical to the flat one across 51,496 sampled points, with the toggle lit.
///
/// A map is drawn either from one rasterised background image or from a grid of tiles, and
/// only the first was hidden when the stack came on. The tile grid is declared after the stack
/// in the same panel, so on a tile-drawn map — which Customs is, at 160 of 170 tiles — it was
/// painted straight over it. Three of Customs' four floors carry an upstream SVG layer, so the
/// stack had artwork all along and was underneath the whole time.
/// </remarks>
public sealed class MapFloorStackVisibilityTests
{
    [Fact]
    public void The_tiles_are_hidden_while_the_floors_are_stacked()
    {
        // The whole bug, as one line of arithmetic.
        Assert.False(Shows(hasTiles: true, stacked: true));
    }

    [Fact]
    public void The_tiles_are_drawn_when_they_are_the_map()
    {
        Assert.True(Shows(hasTiles: true, stacked: false));
    }

    [Fact]
    public void A_map_with_no_tiles_shows_none_either_way()
    {
        Assert.False(Shows(hasTiles: false, stacked: false));
        Assert.False(Shows(hasTiles: false, stacked: true));
    }

    /// <summary>
    /// A stack that loaded nothing leaves the flat map alone.
    /// </summary>
    /// <remarks>
    /// <c>HasFloorStack</c> is the toggle <b>and</b> at least one loaded floor, so a map whose
    /// floors have no artwork keeps drawing what it had rather than going blank. Hiding the
    /// tiles on the toggle alone would trade one silent empty map for another.
    /// </remarks>
    [Fact]
    public void A_stack_that_loaded_nothing_does_not_blank_the_map()
    {
        Assert.True(MapStackVisibility.ShowsTiles(hasTiles: true, hasFloorStack: false));
    }

    private static bool Shows(bool hasTiles, bool stacked) =>
        MapStackVisibility.ShowsTiles(hasTiles, hasFloorStack: stacked);
}
