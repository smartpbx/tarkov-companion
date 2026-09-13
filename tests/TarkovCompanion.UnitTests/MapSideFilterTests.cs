using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Which exits stay on the map once the raid's own side is known.
/// </summary>
/// <remarks>
/// Reported as "if i am a scav, i should only see scav extracts". A PMC exit is not a worse
/// option for a scav, it is not an option, and drawing it is worse than drawing nothing: it
/// sends somebody to a door that will not open.
/// </remarks>
public sealed class MapSideFilterTests
{
    [Theory]
    [InlineData(MapFeatureFaction.Scav, true)]
    [InlineData(MapFeatureFaction.Shared, true)]
    [InlineData(MapFeatureFaction.Unknown, true)]
    [InlineData(MapFeatureFaction.Pmc, false)]
    public void AScavSeesEveryExitExceptThePmcOnes(MapFeatureFaction exit, bool shown) =>
        Assert.Equal(shown, MapViewModel.CanBeTakenForTest(Extract(exit), MapFeatureFaction.Scav));

    [Theory]
    [InlineData(MapFeatureFaction.Pmc, true)]
    [InlineData(MapFeatureFaction.Shared, true)]
    [InlineData(MapFeatureFaction.Unknown, true)]
    [InlineData(MapFeatureFaction.Scav, false)]
    public void APmcSeesEveryExitExceptTheScavOnes(MapFeatureFaction exit, bool shown) =>
        Assert.Equal(shown, MapViewModel.CanBeTakenForTest(Extract(exit), MapFeatureFaction.Pmc));

    /// <summary>Before the side is known, nothing is hidden on a guess.</summary>
    [Theory]
    [InlineData(MapFeatureFaction.Pmc)]
    [InlineData(MapFeatureFaction.Scav)]
    public void AnUnknownSideHidesNothing(MapFeatureFaction exit) =>
        Assert.True(MapViewModel.CanBeTakenForTest(Extract(exit), MapFeatureFaction.Unknown));

    /// <summary>
    /// A scav wants to know where the PMCs started, so spawns keep both sides.
    /// </summary>
    [Fact]
    public void SpawnsAreNotFiltered() =>
        Assert.True(MapViewModel.CanBeTakenForTest(
            new(MapOverlayKind.Spawns, new(0, 0), "Spawn") { Faction = MapFeatureFaction.Pmc },
            MapFeatureFaction.Scav));

    [Fact]
    public void LockedDoorsAreNotFiltered() =>
        Assert.True(MapViewModel.CanBeTakenForTest(
            new(MapOverlayKind.Keys, new(0, 0), "Door") { Faction = MapFeatureFaction.Pmc },
            MapFeatureFaction.Scav));

    private static MapOverlayElement Extract(MapFeatureFaction faction) =>
        new(MapOverlayKind.Extracts, new(10, 20), "Dorms V-Ex") { Faction = faction };
}
