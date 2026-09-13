using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The second screen, which is one embedded page.
/// </summary>
/// <remarks>
/// Embedded rather than copied beside the binary, so the failure this guards against is a
/// deployment that serves an empty page because a file moved. It fails at startup instead, and
/// these say so before a deployment does.
/// </remarks>
public sealed class TabletPageTests
{
    [Fact]
    public void TheEmbeddedPageIsActuallyEmbedded()
    {
        Assert.NotNull(Tablet.Page);
        Assert.Contains("<!doctype html>", Tablet.Page, StringComparison.OrdinalIgnoreCase);
        Assert.True(Tablet.Page.Length > 4000, "the page should be the real one, not a stub");
    }

    [Fact]
    public void ItReadsTheGroupWithoutJoiningIt()
    {
        // A second screen has nothing to contribute -- it is not in the raid -- so it reads
        // rather than publishes. Posting to /state would put a phantom marker in the group and
        // a phantom name in everybody's panel.
        Assert.Contains("\"/state\"", Tablet.Page, StringComparison.Ordinal);
        Assert.DoesNotContain("method: \"POST\", body: JSON.stringify({ name", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void ItSendsTheKeyAsTheHeaderTheServerReads()
    {
        Assert.Contains("X-Group-Key", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void ItDoesNotPretendToBeTheMap()
    {
        // No artwork and no projection, so it says outright that it is a schematic. A plot of
        // world coordinates presented as the map would send somebody to the wrong place.
        Assert.Contains("A schematic, not the map", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void ItReachesNothingOutsideTheServerThatServedIt()
    {
        // Same origin, no CDN, no framework, no font host. A page on a tablet in a house with
        // no internet still has to work, because the group server is on the same network.
        Assert.DoesNotContain("https://", Tablet.Page, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", Tablet.Page, StringComparison.Ordinal);
    }
}
