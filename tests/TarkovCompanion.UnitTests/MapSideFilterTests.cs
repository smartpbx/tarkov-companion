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
    /// A scav is shown no spawn at all, whatever side it belongs to.
    /// </summary>
    /// <remarks>
    /// This is the second reversal on the same question, and the second time the answer came
    /// from Clayton rather than from reasoning here.
    ///
    /// It first asserted that a scav should see PMC spawns, on the argument that "a scav wants
    /// to know where the PMCs started". Reported as wrong: a scav joins twenty minutes in, so a
    /// PMC spawn describes where somebody was at a time the scav was not in the raid. Filtering
    /// those out left the scav ones, and #257 reports that as wrong too — "still showing when in
    /// scav runs, which is pointless". A scav arrives wherever the game puts them; where the
    /// other scavs may arrive is not a question they are asking either, and on Customs it was
    /// 151 markers on top of the exits.
    ///
    /// So the layer is empty for a scav. The exits are untouched, because they are what the map
    /// is read for once the raid is running.
    /// </remarks>
    [Theory]
    [InlineData(MapFeatureFaction.Pmc)]
    [InlineData(MapFeatureFaction.Scav)]
    [InlineData(MapFeatureFaction.Shared)]
    [InlineData(MapFeatureFaction.Unknown)]
    public void AScavIsShownNoSpawns(MapFeatureFaction faction) =>
        Assert.False(MapViewModel.CanBeTakenForTest(Spawn(faction), MapFeatureFaction.Scav));

    /// <summary>At the start of a PMC raid, where the other PMCs began is the whole point.</summary>
    [Fact]
    public void APmcIsShownPmcSpawns() =>
        Assert.True(MapViewModel.CanBeTakenForTest(Spawn(MapFeatureFaction.Pmc), MapFeatureFaction.Pmc));

    /// <summary>
    /// A PMC still sees a spawn the feed says nothing about, and so does a raid with no side.
    /// </summary>
    /// <remarks>
    /// Same rule the exits follow and the same reason: guessing a side away is worse than
    /// leaving it drawn. Only a scav, where the whole layer is the wrong question, is emptied.
    /// </remarks>
    [Theory]
    [InlineData(MapFeatureFaction.Unknown)]
    [InlineData(MapFeatureFaction.Shared)]
    public void ASpawnWithNoStatedSideStaysForAPmc(MapFeatureFaction faction) =>
        Assert.True(MapViewModel.CanBeTakenForTest(Spawn(faction), MapFeatureFaction.Pmc));

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
