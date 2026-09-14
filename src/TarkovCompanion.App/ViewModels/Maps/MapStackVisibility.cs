namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>
/// Which flat artwork is drawn while the floors are stacked.
/// </summary>
/// <remarks>
/// A seam rather than a property, because the rule is the whole of a defect that survived two
/// fixes and two readings of the code, and it can be checked without standing up a map.
/// </remarks>
public static class MapStackVisibility
{
    /// <summary>
    /// Whether the tile grid is drawn.
    /// </summary>
    /// <param name="hasTiles">Whether this map is drawn from tiles at all.</param>
    /// <param name="hasFloorStack">Whether the stack is on <b>and</b> loaded at least one floor.</param>
    /// <remarks>
    /// Against the loaded stack rather than the toggle, so a map whose floors have no artwork
    /// keeps drawing what it had instead of going blank. Hiding the tiles on the toggle alone
    /// would trade one silently empty map for another.
    /// </remarks>
    public static bool ShowsTiles(bool hasTiles, bool hasFloorStack) => hasTiles && !hasFloorStack;
}
