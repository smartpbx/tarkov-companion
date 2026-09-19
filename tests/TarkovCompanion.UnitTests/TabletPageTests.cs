using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The second screen, which is one embedded page — and, since #407, one paired surface.
/// </summary>
/// <remarks>
/// Embedded rather than copied beside the binary, so the failure this guards against is a
/// deployment that serves an empty page because a file moved. It fails at startup instead, and
/// these say so before a deployment does.
///
/// The rest of these are about what the page can reach. A tablet is only ever a companion paired
/// to one desktop: the group-key half — its own name, a shared key, `/state` polling, posting
/// `/waypoints` and `/pings`, and the relay's `/search` — is gone, and these fail if any of it
/// comes back, whether by a revert or by somebody reaching for the shortest way to add a feature.
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
    public void ThereIsNoGroupKeyPathLeftToReach()
    {
        // The standalone mode is what #407 removed: a tablet with a group key showed a second
        // copy of the same information, in a different shape, with no map — and the desktop has
        // to be running anyway to read the game's logs and screenshots.
        Assert.DoesNotContain("X-Group-Key", Tablet.Page, StringComparison.Ordinal);
        Assert.DoesNotContain("\"/state\"", Tablet.Page, StringComparison.Ordinal);
        Assert.DoesNotContain("/waypoints", Tablet.Page, StringComparison.Ordinal);
        Assert.DoesNotContain("/pings", Tablet.Page, StringComparison.Ordinal);
        Assert.DoesNotContain("/landmarks", Tablet.Page, StringComparison.Ordinal);
        Assert.DoesNotContain("Group key", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void ALootSpawnIsDrawnAsAPotentialAndAWaypointOnItSnapsToIt()
    {
        // Issue 318: the tablet already received every scene object, but drew a loot spawn as a
        // grey filled dot, the same as anything it did not know, and a waypoint tapped near one
        // landed wherever the finger did.
        Assert.Contains("LootSpawn: \"#e3b341\"", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("item.truth === \"PotentialSpawn\"", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("addMark(spawn.points[0], spawn.points[1], spawn.label)", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("markKind === \"Waypoint\" ? objectAt(project, px, py)", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemLookupsGoToThePairedDesktop()
    {
        // The relay's /search answers anybody holding a group key and knows nothing about the
        // person's quests or hideout. The desktop has the catalogue and that context, so a search
        // typed here drives the desktop's own search over the paired command path.
        Assert.DoesNotContain("/search?q=", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("type: \"search\"", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void EverythingItReadsIsBoundToThePairedSession()
    {
        // Both map resources are read with this session's own relay credential, never anonymously:
        // a revoked device's credential stops authenticating, so it stops seeing the map too.
        Assert.Contains("relayCall(`/v2/companion/relay/map${query}`)", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("relayCall(\"/v2/companion/relay/map/artwork\")", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("X-Relay-Credential", Tablet.Page, StringComparison.Ordinal);
        // Every fetch on this page goes through relayCall, which is the only thing that attaches
        // the session credential. A bare fetch to a relay route would be an unauthenticated read.
        Assert.DoesNotContain("fetch(\"/v2/companion/relay/", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void ItDrawsTheRealMapRatherThanASchematic()
    {
        // The schematic and the tap pad are both gone; the map is the surface. Objects are drawn
        // against the plan rectangle the desktop sent, so a mark is in the same place on both.
        Assert.DoesNotContain("A schematic, not the map", Tablet.Page, StringComparison.Ordinal);
        Assert.DoesNotContain("tapPad", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("context.drawImage(artwork", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("This map has no reviewed 2D plan yet.", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void AMarkFromTheTabletIsPlacedInThePlansOwnUnits()
    {
        // The tap pad sent 0-1 of a blank square, which put every mark from a tablet in the
        // corner of the real map. A mark now carries plan coordinates and the desktop's own
        // projection version, which is the same space its local marks are already stored in.
        Assert.Contains("coordinateSpace: \"World\"", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("projectionVersion: live.surface.transformVersion", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void AControlFrameDoesNotBounceBackAsAFollowUpdate()
    {
        // A workspace change this tablet caused is broadcast back to it like any other. Applying
        // it would fight the pan the person is still making, so it is dropped by its origin.
        Assert.Contains("update.origin?.deviceId?.value === live.deviceId", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlIsAskedForRatherThanTaken()
    {
        // Entering Control is a request the desktop answers; only Follow and Independent are set
        // directly. That is the reducer's own rule (SetInteractionModeCommand refuses Control).
        Assert.Contains("\"requestControl\"", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("\"setInteractionMode\"", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void ADesktopThatIsNotThereIsSaidSoPlainly()
    {
        Assert.Contains("The desktop is offline.", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReviewedArtworksAttributionIsVisibleOnTheTablet()
    {
        // ADR 0015: source, licence and content hash travel with the artwork and are shown where
        // it is drawn, not only on the desktop that fetched it.
        Assert.Contains("item.sourceUri", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("item.licenseUri", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("item.contentSha256", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMapReadIsHeldRatherThanPolledOnceTheRelayHasNamedARevision()
    {
        // [V2 rough package 34] The second screen was two waits behind the desk: the desktop
        // published on a one-second throttle and this page read on its own 1.5 s timer. The read
        // now waits on the relay, so a change arrives when it happens.
        Assert.Contains("since=${live.mapRevision}&wait=${HOLD_SECONDS}", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("X-Relay-Map-Revision", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelayThatCannotHoldIsStillReadable()
    {
        // The compatibility rule from the page's side: a relay that has not been redeployed sends
        // no revision header, and this falls back to the timer it always used rather than
        // spinning on a read that answers instantly.
        Assert.Contains("if (revision === null)", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("if (!held) await new Promise", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void TappingAThingOnTheMapSelectsItOnTheDesktop()
    {
        Assert.Contains("function objectAt(", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("type: \"select\"", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnswersToALookupComeBackToTheTablet()
    {
        // #417 sent the query to the desktop and left the answers there, which is half a feature:
        // the person is holding the tablet.
        Assert.Contains("surface.search?.results", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("The desktop looks it up and the answers come back here.", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void LayersCanBeSwitchedFromTheTablet()
    {
        Assert.Contains("function toggleLayer(", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("type: \"filter\"", Tablet.Page, StringComparison.Ordinal);
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
