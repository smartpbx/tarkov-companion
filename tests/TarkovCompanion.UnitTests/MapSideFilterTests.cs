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
    /// A scav does not see PMC spawn points, because they are not news by the time they arrive.
    /// </summary>
    /// <remarks>
    /// This used to assert the opposite, on the argument that "a scav wants to know where the
    /// PMCs started". Reported as wrong and it is: a scav joins twenty minutes in, so a PMC
    /// spawn point describes where somebody was at a time the scav was not in the raid. It was
    /// also inconsistent — SpawnProximity has always filtered the panel by side, so the list
    /// beside the map and the markers on it disagreed.
    /// </remarks>
    [Fact]
    public void AScavIsNotShownPmcSpawns() =>
        Assert.False(MapViewModel.CanBeTakenForTest(
            Spawn(MapFeatureFaction.Pmc),
            MapFeatureFaction.Scav));

    [Fact]
    public void AScavIsShownScavSpawns() =>
        Assert.True(MapViewModel.CanBeTakenForTest(Spawn(MapFeatureFaction.Scav), MapFeatureFaction.Scav));

    /// <summary>At the start of a PMC raid, where the other PMCs began is the whole point.</summary>
    [Fact]
    public void APmcIsShownPmcSpawns() =>
        Assert.True(MapViewModel.CanBeTakenForTest(Spawn(MapFeatureFaction.Pmc), MapFeatureFaction.Pmc));

    /// <summary>
    /// A spawn the feed says nothing about stays, and so does every spawn on a raid whose side
    /// was never established.
    /// </summary>
    /// <remarks>
    /// Same rule the exits follow and the same reason: an empty layer reads as a broken
    /// feature, and guessing a side away is worse than leaving it drawn.
    /// </remarks>
    [Theory]
    [InlineData(MapFeatureFaction.Unknown)]
    [InlineData(MapFeatureFaction.Shared)]
    public void ASpawnWithNoStatedSideStays(MapFeatureFaction faction) =>
        Assert.True(MapViewModel.CanBeTakenForTest(Spawn(faction), MapFeatureFaction.Scav));

    [Fact]
    public void EverySpawnStaysWhenTheSideIsNotKnown() =>
        Assert.True(MapViewModel.CanBeTakenForTest(Spawn(MapFeatureFaction.Pmc), MapFeatureFaction.Unknown));

    private static MapOverlayElement Spawn(MapFeatureFaction faction) =>
        new(MapOverlayKind.Spawns, new(0, 0), "Spawn") { Faction = faction };

    [Fact]
    public void LockedDoorsAreNotFiltered() =>
        Assert.True(MapViewModel.CanBeTakenForTest(
            new(MapOverlayKind.Keys, new(0, 0), "Door") { Faction = MapFeatureFaction.Pmc },
            MapFeatureFaction.Scav));

    private static MapOverlayElement Extract(MapFeatureFaction faction) =>
        new(MapOverlayKind.Extracts, new(10, 20), "Dorms V-Ex") { Faction = faction };
}
