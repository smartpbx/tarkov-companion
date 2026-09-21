using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

/// <summary>
/// Who may publish the desktop's map to the relay, and who may read it (#407).
/// </summary>
/// <remarks>
/// The artwork is the one thing a tablet needs that cannot travel as a sealed frame, so it is the
/// one thing the relay holds in the clear. That makes "only a live paired session ever sees it"
/// the property worth pinning down: a reviewed asset served to anybody who asks would be a
/// redistribution the licence does not cover, and a revoked tablet still drawing the raid would
/// make revocation a label rather than a fact.
/// </remarks>
public sealed class RelayMapSurfaceTests
{
    /// <summary>
    /// The store works on a clock that has ticks below a millisecond, which is every real clock.
    /// </summary>
    /// <remarks>
    /// Relay authorization refuses such a timestamp, this store used to hand it the raw clock, and
    /// every other test here runs on a manual clock set to whole milliseconds. So the suite was
    /// green while the deployed relay answered 500 to every map publish and every map read, and
    /// a paired tablet said the desktop was offline.
    /// </remarks>
    [Fact]
    public async Task AClockWithTicksBelowAMillisecondDoesNotBreakTheStore()
    {
        var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var store = new RelayMapSurfaceStore(context.Registry, new OffTheMillisecond(context.Clock));

        var held = store.Describe(owner);

        // The owner, with nothing published yet: described, and holding nothing. It used to throw.
        Assert.NotNull(held);
        Assert.False(held.Held);
    }

    private sealed class OffTheMillisecond(TimeProvider inner) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow().AddTicks(1234);

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);
    }

    [Fact]
    public async Task OnlyTheOwnerPublishesAndOnlyAPairedSessionReads()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var tablet = await AddTabletAsync(context, owner);
        var store = new RelayMapSurfaceStore(context.Registry, context.Clock);
        var surface = Encoding.UTF8.GetBytes("""{"mapId":"customs"}""");

        var byTablet = store.Publish(tablet.Principal, surface);
        var byOwner = store.Publish(owner, surface);
        var read = store.Read(tablet.Principal);

        Assert.False(byTablet.Accepted);
        Assert.Equal("not-authorized", byTablet.Code);
        Assert.True(byOwner.Accepted);
        Assert.Equal(surface, read!.SurfaceJson);
    }

    [Fact]
    public async Task ArtworkIsRejectedUnlessItIsTheBytesTheSurfaceNames()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var tablet = await AddTabletAsync(context, owner);
        var store = new RelayMapSurfaceStore(context.Registry, context.Clock);
        store.Publish(owner, Encoding.UTF8.GetBytes("""{"mapId":"customs"}"""));
        var artwork = Encoding.UTF8.GetBytes("not really a png, but bytes with a hash");
        var sha = Convert.ToHexStringLower(SHA256.HashData(artwork));

        // A picture that is not the one the surface's content hash names would be drawn under
        // another asset's attribution, so the relay checks rather than trusts the declaration.
        var wrongHash = store.PublishArtwork(owner, "image/png", new string('0', 64), artwork);
        var wrongType = store.PublishArtwork(owner, "text/html", sha, artwork);
        var byTablet = store.PublishArtwork(tablet.Principal, "image/png", sha, artwork);
        var accepted = store.PublishArtwork(owner, "image/png", sha, artwork);

        Assert.Equal("artwork-hash-mismatch", wrongHash.Code);
        Assert.Equal("media-type-rejected", wrongType.Code);
        Assert.Equal("not-authorized", byTablet.Code);
        Assert.True(accepted.Accepted);
        Assert.Equal(artwork, store.Read(tablet.Principal)!.Artwork);
    }

    [Fact]
    public async Task ARevokedDeviceSeesNothing()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var tablet = await AddTabletAsync(context, owner);
        var store = new RelayMapSurfaceStore(context.Registry, context.Clock);
        store.Publish(owner, Encoding.UTF8.GetBytes("""{"mapId":"customs"}"""));
        Assert.NotNull(store.Read(tablet.Principal));

        var revoked = await context.Registry.RevokeDeviceAsync(owner, tablet.Principal.DeviceId, "test-revoked");

        Assert.True(revoked.Succeeded);
        // Two independent refusals, because either alone would be enough to lose: the principal
        // this device already holds stops being current, and its credential stops authenticating
        // at all, so it cannot obtain another one.
        Assert.Null(store.Read(tablet.Principal));
        Assert.Null((await context.Registry.AuthenticateAsync(tablet.Credential.SessionId, tablet.Credential.Secret)).Principal);
    }

    [Fact]
    public async Task AnOversizedSurfaceIsRefusedRatherThanHeld()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var store = new RelayMapSurfaceStore(context.Registry, context.Clock);

        var refused = store.Publish(owner, new byte[RelayMapSurfaceStore.MaximumSurfaceBytes + 1]);

        Assert.False(refused.Accepted);
        Assert.Equal("surface-rejected", refused.Code);
        Assert.Null(store.Read(owner));
    }

    private sealed record PairedTablet(RelayPrincipal Principal, RelaySessionCredential Credential);

    private static async ValueTask<PairedTablet> AddTabletAsync(RelayTestContext context, RelayPrincipal owner)
    {
        var completed = await RelaySecurityTestFactory.CompletedPairingAsync("tablet", context.Clock.UtcNow);
        var paired = await context.Registry.AddPairedDeviceAsync(
            owner,
            completed,
            DeviceAuthorizationRole.Member,
            CompanionSurfaceKind.TabletLandscape);
        var credential = Assert.IsType<RelaySessionCredential>(paired.Value);
        var principal = Assert.IsType<RelayPrincipal>(
            (await context.Registry.AuthenticateAsync(credential.SessionId, credential.Secret)).Principal);
        return new PairedTablet(principal, credential);
    }
}
