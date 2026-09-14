using System.Text;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The places on a map worth recognising, cut down to what a schematic can draw.
/// </summary>
/// <remarks>
/// The tablet plots world coordinates and says on the page that it is not the map, which is
/// honest and leaves it very hard to tell where anybody is. Landmarks are what turn a plot of
/// dots into a place.
///
/// The plan said the page should fetch the map catalog directly. That was written before
/// anybody measured it: the catalog is 8,542,745 bytes, 780,279 gzipped, and what a schematic
/// can draw out of it is 19,829 bytes, 4,588 gzipped — for every map at once.
/// </remarks>
public sealed class LandmarksTests
{
    [Fact]
    public void AnExtractKeepsTheNameTheGroupUses()
    {
        var built = Landmarks.Build(Bytes(Catalog));

        var extract = Assert.Single(built["customs"], mark => mark.Kind == "e");
        Assert.Equal("Old Azs Gate", extract.Name);
        Assert.Equal("scav", extract.Faction);
        Assert.Equal(300.5, extract.X);
        Assert.Equal(-198.5, extract.Z);
    }

    [Fact]
    public void ATransitIsNamedForWhereItGoes()
    {
        // Its own description is a translation token — "CUS_TRANSIT_9_DESC" — so the catalog's
        // one useful statement about a transit is the map on the other side of it.
        var built = Landmarks.Build(Bytes(Catalog));

        var transit = Assert.Single(built["customs"], mark => mark.Kind == "t");
        Assert.Equal("Transit to shoreline", transit.Name);
    }

    [Fact]
    public void ALockIsDrawnAndNotLabelled()
    {
        // The catalog calls all thirty-six of Customs' locks "door". Writing that beside each
        // one is thirty-six words that say nothing.
        var built = Landmarks.Build(Bytes(Catalog));

        var locked = Assert.Single(built["customs"], mark => mark.Kind == "l");
        Assert.Null(locked.Name);
    }

    [Fact]
    public void SpawnsAreLeftOut()
    {
        // 278 nameless points on Customs alone is not a landmark, it is a texture.
        var built = Landmarks.Build(Bytes(Catalog));

        Assert.DoesNotContain(built["customs"], mark => mark.Kind == "s");
        Assert.Equal(3, built["customs"].Count);
    }

    [Fact]
    public void SomethingWithNoPositionIsNotPlacedAtZero()
    {
        // Zero is a real place on every one of these maps, so a missing coordinate that became
        // one would put a landmark somewhere specific and wrong.
        var built = Landmarks.Build(Bytes("""
        {"data":{"maps":{"a":{"id":"a","normalizedName":"customs","extracts":[
          {"name":"Nowhere"},
          {"name":"Somewhere","position":{"x":1,"y":2,"z":3}}]}}}}
        """));

        var kept = Assert.Single(built["customs"]);
        Assert.Equal("Somewhere", kept.Name);
    }

    [Fact]
    public void AMapWithNothingToDrawIsNotListed()
    {
        var built = Landmarks.Build(Bytes("""
        {"data":{"maps":{"a":{"id":"a","normalizedName":"factory"}}}}
        """));

        Assert.Empty(built);
    }

    [Fact]
    public void AShapeItDoesNotRecogniseCostsTheLandmarksAndNothingElse()
    {
        // The page draws what it gets. An upstream that changes shape should cost the map its
        // labels, not stop the second screen loading.
        Assert.Empty(Landmarks.Build(Bytes("""{"data":{"maps":[]}}""")));
        Assert.Empty(Landmarks.Build(Bytes("""{"nothing":true}""")));
    }

    [Fact]
    public void CoordinatesAreRoundedBecauseTenCentimetresIsNotADistanceAnybodyTaps()
    {
        var built = Landmarks.Build(Bytes("""
        {"data":{"maps":{"a":{"id":"a","normalizedName":"customs","extracts":[
          {"name":"Precise","position":{"x":352.230316,"y":2.6,"z":-40.8052826}}]}}}}
        """));

        var mark = Assert.Single(built["customs"]);
        Assert.Equal(352.2, mark.X);
        Assert.Equal(-40.8, mark.Z);
    }

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>The three shapes that matter, cut from the real payload.</summary>
    private const string Catalog = """
    {"data":{"maps":{
      "5704e5fad2720bc05b8b4567":{"id":"5704e5fad2720bc05b8b4567","normalizedName":"shoreline"},
      "56f40101d2720b2a4d8b45d6":{"id":"56f40101d2720b2a4d8b45d6","normalizedName":"customs",
        "extracts":[{"id":"d201","name":"Old Azs Gate","faction":"scav",
          "position":{"x":300.51,"y":3.47,"z":-198.52}}],
        "transits":[{"id":"9","description":"CUS_TRANSIT_9_DESC","map":"5704e5fad2720bc05b8b4567",
          "position":{"x":650.64,"y":1.59,"z":124.94}}],
        "locks":[{"id":"9ef1","lockType":"door","key":"5913","needsPower":false,
          "position":{"x":577.73,"y":0.79,"z":4.09}}],
        "spawns":[{"zoneName":"z","position":{"x":1,"y":2,"z":3}}]}
    }}}
    """;
}
