using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Situations;

public sealed class SituationPartsTests
{
    [Theory]
    [InlineData("2026-09-23 21:48:26.506|1.1.5.1.47510|Debug|application|Matching with group id: 1", RaidPhaseMarkerKind.MatchingStarted)]
    [InlineData("2026-09-23 21:48:41.537|1.1.5.1.47510|Info|application|MatchingCompleted:11.26 real:15.03 diff:3.76", RaidPhaseMarkerKind.MatchingCompleted)]
    [InlineData("2026-09-23 01:46:57.767|1.1.5.1.47510|Info|application|MatchingCompleted:0 real:0 diff:0", RaidPhaseMarkerKind.MatchingCompleted)]
    [InlineData("2026-09-23 21:49:00.000|1.1.5.1.47510|Info|application|LocationLoaded:10.97 real:18.09 diff:7.12", RaidPhaseMarkerKind.LocationLoaded)]
    [InlineData("2026-09-23 21:50:00.000|1.1.5.1.47510|Info|application|GameStarted:31.37(0) real:53.4(0) diff:22.03", RaidPhaseMarkerKind.GameStarted)]
    [InlineData("2026-09-23 21:48:28.414|1.1.5.1.47510|Debug|application|TRACE-NetworkGameMatching G", RaidPhaseMarkerKind.MatchingStep)]
    [InlineData("2026-09-23 21:49:35.393|1.1.5.1.47510|Info|application|GameSpawn:58.67(0.09) real:68.88(0.08) diff:10.2", RaidPhaseMarkerKind.Spawning)]
    [InlineData("2026-09-23 21:49:38.185|1.1.5.1.47510|Info|application|GameSpawned:60.69(1.43) real:71.67(2.2) diff:10.98", RaidPhaseMarkerKind.Spawned)]
    public void TheFourLoadingMarkersAreRead(string line, RaidPhaseMarkerKind kind)
    {
        Assert.Equal(kind, RaidPhaseMarkerParser.ParseLine(line, DateTimeOffset.UnixEpoch)?.Kind);
    }

    [Theory]
    [InlineData("2026-09-23 21:48:28.414|1.1.5.1.47510|Info|application|GamePooled:55.95(35.32) real:61.82(35.86) diff:5.86")]
    [InlineData("2026-09-23 21:48:28.414|1.1.5.1.47510|Info|backend|GameStarted:31.37 quoted by some other file")]
    [InlineData(null)]
    public void OtherLinesAreNotMarkers(string? line)
    {
        Assert.Null(RaidPhaseMarkerParser.ParseLine(line, DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(0, "N")]
    [InlineData(44, "NE")]
    [InlineData(-90, "W")]
    [InlineData(359, "N")]
    [InlineData(double.NaN, null)]
    public void AHeadingReadsAsACompassPoint(double heading, string? expected)
    {
        Assert.Equal(expected, SituationFolder.Facing(heading));
    }

    /// <summary>The floor stood on names the building; a footprint on another floor does not.</summary>
    [Fact]
    public void ThePlaceIsTheNamedRectangleOnTheFloorWhoseHeightHoldsThePosition()
    {
        var dorms = new MapCatalogBounds(new(-20, 20), new(0, 40)) { Description = "dorms" };
        var ground = new MapFloorDefinition("ground", "Ground floor", null, null, true, [new MapLayerExtent(null, 2.7, [dorms])]);
        var second = new MapFloorDefinition("second", "2nd floor", null, null, false, [new MapLayerExtent(2.7, 6.5, [dorms])]);
        var variant = new MapVariant(
            "customs", "interactive", MapProjectionKind.Interactive, "Interactive", null, null, null, null, 256, null, null,
            null, null, null, null, null, null, null, null, [], [ground, second], []);
        var location = new MapLocation("customs", null, "Customs", null, null, [variant]);

        Assert.Equal(new SituationPlace("dorms", "2nd floor"), SituationPlaceLookup.Describe(location, new WorldPosition(-10, 4, 30)));
        Assert.Equal(new SituationPlace("dorms", "Ground floor"), SituationPlaceLookup.Describe(location, new WorldPosition(-10, 1, 30)));
        Assert.Equal(new SituationPlace(null, null), SituationPlaceLookup.Describe(location, new WorldPosition(50, 1, 30)));
        Assert.Equal(new SituationPlace(null, null), SituationPlaceLookup.Describe(null, new WorldPosition(0, 0, 0)));
    }
}
