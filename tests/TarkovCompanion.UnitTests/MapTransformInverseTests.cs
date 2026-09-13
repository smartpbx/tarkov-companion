using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Going from a point on the map back to a place in the world.
/// </summary>
/// <remarks>
/// Needed as soon as somebody can point at the map and mean a location. If this is even
/// slightly wrong a dropped waypoint lands near where it was clicked rather than on it, which
/// is the kind of error that looks like a rendering problem and is not.
/// </remarks>
public sealed class MapTransformInverseTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(-115.5)]
    public void APositionSurvivesTheRoundTrip(double rotation)
    {
        var transform = new MapCatalogTransform(1.7, 120, 2.3, -45, rotation);
        var original = new WorldPosition(123.5, 4.25, -67.75);

        Assert.True(transform.TryProject(original, out var point));
        Assert.True(transform.TryUnproject(point, original.Y, out var returned));

        Assert.Equal(original.X, returned.X, 6);
        Assert.Equal(original.Z, returned.Z, 6);
        Assert.Equal(original.Y, returned.Y, 6);
    }

    [Fact]
    public void HeightComesFromTheCallerBecauseTheProjectionDiscardsIt()
    {
        // Two places one above the other land on the same point, so the map cannot say which
        // was meant. The caller supplies it, and the sensible answer is whoever is pointing.
        var transform = new MapCatalogTransform(1, 0, 1, 0, 0);

        Assert.True(transform.TryProject(new(10, 2, 20), out var low));
        Assert.True(transform.TryProject(new(10, 90, 20), out var high));
        Assert.Equal(low, high);

        Assert.True(transform.TryUnproject(low, 90, out var recovered));
        Assert.Equal(90, recovered.Y, 6);
    }

    [Fact]
    public void AnInvalidTransformRefusesRatherThanGuessing()
    {
        Assert.False(new MapCatalogTransform(0, 0, 1, 0, 0).TryUnproject(new(1, 1), 0, out _));
        Assert.False(new MapCatalogTransform(1, 0, 1, 0, 0).TryUnproject(new(double.NaN, 1), 0, out _));
    }
}
