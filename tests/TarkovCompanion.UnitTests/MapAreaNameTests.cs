using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Which building's floor the player is standing on.
/// </summary>
/// <remarks>
/// Customs publishes eighteen named rectangles across its floors, each with its own height
/// band: the second floor of dorms is 2.7 to 6.5 metres and the second floor of big red starts
/// at 5.7. So "2nd Floor" means two different heights depending on the building, and the name
/// has been parsed out of the catalog and thrown away since the parser was written.
/// </remarks>
public sealed class MapAreaNameTests
{
    /// <summary>The two real Customs extents that share a floor and differ in height.</summary>
    private static readonly MapFloorDefinition SecondFloor = new(
        "second-floor",
        "2nd Floor",
        "Second_Floor",
        null,
        false,
        [
            new(2.7, 6.5, [Rectangle(0, 0, 100, 100, "dorms")]),
            new(5.7, 1000, [Rectangle(200, 200, 300, 300, "big red 2nd")]),
        ]);

    [Fact]
    public void TheBuildingIsNamed() =>
        Assert.Equal("dorms", MapAreaName.Describe(SecondFloor, new(50, 4, 50)));

    [Fact]
    public void ADifferentBuildingOnTheSameFloorIsNamedSeparately() =>
        Assert.Equal("big red 2nd", MapAreaName.Describe(SecondFloor, new(250, 20, 250)));

    /// <summary>
    /// The height band decides between two buildings whose footprints overlap the position.
    /// </summary>
    [Fact]
    public void HeightPicksBetweenTwoFootprints()
    {
        var overlapping = new MapFloorDefinition(
            "second-floor",
            "2nd Floor",
            null,
            null,
            false,
            [
                new(0, 5, [Rectangle(0, 0, 100, 100, "lower")]),
                new(5, 10, [Rectangle(0, 0, 100, 100, "upper")]),
            ]);

        Assert.Equal("lower", MapAreaName.Describe(overlapping, new(50, 2, 50)));
        Assert.Equal("upper", MapAreaName.Describe(overlapping, new(50, 7, 50)));
    }

    /// <summary>
    /// A footprint still names the building when the height does not match.
    /// </summary>
    /// <remarks>
    /// That is the right answer for somebody looking at a floor they are not standing on.
    /// </remarks>
    [Fact]
    public void AFootprintNamesTheBuildingEvenFromAnotherFloor() =>
        Assert.Equal("dorms", MapAreaName.Describe(SecondFloor, new(50, 900, 50)));

    [Fact]
    public void SomewhereWithNoNamedAreaSaysNothing() =>
        Assert.Null(MapAreaName.Describe(SecondFloor, new(-500, 4, -500)));

    /// <summary>
    /// A floor with no named rectangles says nothing rather than repeating its own name.
    /// </summary>
    [Fact]
    public void AnUnnamedFloorSaysNothing() =>
        Assert.Null(MapAreaName.Describe(
            new("base", "Ground", null, null, true, [new(null, null, [])]),
            new(50, 4, 50)));

    [Fact]
    public void NoFloorSaysNothing() => Assert.Null(MapAreaName.Describe(null, new(0, 0, 0)));

    private static MapCatalogBounds Rectangle(double x1, double z1, double x2, double z2, string description) =>
        new(new(x1, z1), new(x2, z2)) { Description = description };
}
