using System.Security.Cryptography;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.StateSync;
using TarkovCompanion.GroupServer.Storage;
using TarkovCompanion.GroupServer.Tenancy;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

/// <summary>
/// [#553] One relay, several desktops. Each desktop owns its own tablets and nothing of anybody
/// else's; a relay claimed before tenancy existed comes up as its owner's tenant, tablets and all.
/// </summary>
public sealed class RelayTenantDirectoryTests : IDisposable
{
    private static readonly string Room = GroupKey.RoomFor("friends-of-the-relay");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "relay-tenancy-" + Guid.NewGuid().ToString("N"));
    private readonly RelayTestClock _clock = new(RelaySecurityTestFactory.Now);
    private readonly OwnerRecoveryProtector _recovery;

    public RelayTenantDirectoryTests()
    {
        _recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x33, 32).ToArray(), _clock);
    }

    public void Dispose()
    {
        _recovery.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task EachDesktopOwnsItsOwnTabletsAndNothingOfAnotherDesktops()
    {
        var directory = await OpenAsync(persisted: false);
        using var alice = new Desktop();
        using var bob = new Desktop();
        var aliceOwner = await RegisterAsync(directory, alice);
        var bobOwner = await RegisterAsync(directory, bob);
        var aliceTablet = await PairTabletAsync(directory, alice, aliceOwner, "alice-tablet");
        var bobTablet = await PairTabletAsync(directory, bob, bobOwner, "bob-tablet");

        var aliceTenant = directory.FindBySession(aliceTablet.SessionId)!;
        var bobTenant = directory.FindBySession(bobTablet.SessionId)!;
        Assert.NotSame(aliceTenant, bobTenant);
        Assert.Same(aliceTenant, directory.FindBySession(aliceOwner.SessionId));
        Assert.Equal(alice.KeyId, aliceTenant.DesktopKeyId);
        Assert.False(aliceTenant.IsLegacy);

        // Bob's tablet has no session in Alice's registry, so nothing of Alice's answers to it.
        Assert.False((await aliceTenant.Registry.AuthenticateAsync(bobTablet.SessionId, bobTablet.Secret)).Authenticated);

        // Alice publishes her map; her tablet reads it and Bob's tenant holds nothing.
        var alicePrincipal = (await aliceTenant.Registry.AuthenticateAsync(aliceOwner.SessionId, aliceOwner.Secret)).Principal!;
        Assert.True(aliceTenant.Maps!.Publish(alicePrincipal, "{\"map\":\"customs\"}"u8).Accepted);
        var aliceTabletPrincipal = (await aliceTenant.Registry.AuthenticateAsync(aliceTablet.SessionId, aliceTablet.Secret)).Principal!;
        var bobTabletPrincipal = (await bobTenant.Registry.AuthenticateAsync(bobTablet.SessionId, bobTablet.Secret)).Principal!;
        Assert.NotNull(aliceTenant.Maps.Read(aliceTabletPrincipal));
        Assert.Null(bobTenant.Maps!.Read(bobTabletPrincipal));
        // Even handed Alice's store directly, Bob's tablet is not a session Alice's registry knows.
        Assert.Null(aliceTenant.Maps.Read(bobTabletPrincipal));

        // Alice cannot revoke Bob's tablet: it is not a device of hers. Revoking her own leaves his.
        var bobTabletDevice = bobTenant.Registry.FindPairedDeviceByKey(RelaySecurityTestFactory.DeviceKey("bob-tablet").KeyId)!;
        var reached = await aliceTenant.Registry.RevokeDeviceAsync(alicePrincipal, bobTabletDevice.DeviceId, "not-hers");
        Assert.False(reached.Succeeded);
        var aliceTabletDevice = aliceTenant.Registry.FindPairedDeviceByKey(RelaySecurityTestFactory.DeviceKey("alice-tablet").KeyId)!;
        Assert.True((await aliceTenant.Registry.RevokeDeviceAsync(alicePrincipal, aliceTabletDevice.DeviceId, "revoked-by-desktop")).Succeeded);
        Assert.False((await aliceTenant.Registry.AuthenticateAsync(aliceTablet.SessionId, aliceTablet.Secret)).Authenticated);
        Assert.True((await bobTenant.Registry.AuthenticateAsync(bobTablet.SessionId, bobTablet.Secret)).Authenticated);
    }

    [Fact]
    public async Task RegisteringAgainIsTheSameDesktopWithItsTabletsIntact()
    {
        var directory = await OpenAsync(persisted: false);
        using var alice = new Desktop();
        var first = await RegisterAsync(directory, alice);
        var tablet = await PairTabletAsync(directory, alice, first, "kept-tablet");

        _clock.Advance(TimeSpan.FromHours(20));
        var second = await RegisterAsync(directory, alice);

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(2, directory.Tenants.Length);
        var tenant = directory.FindBySession(second.SessionId)!;
        Assert.NotNull(tenant.Registry.FindPairedDeviceByKey(RelaySecurityTestFactory.DeviceKey("kept-tablet").KeyId));
        Assert.Same(tenant, directory.FindBySession(tablet.SessionId));
    }

    [Fact]
    public async Task ADesktopCannotNameAnotherDesktopsSessionOrRegisterAPairingItDidNotSign()
    {
        var directory = await OpenAsync(persisted: false);
        using var alice = new Desktop();
        using var mallory = new Desktop();
        var aliceOwner = await RegisterAsync(directory, alice);
        var malloryOwner = await RegisterAsync(directory, mallory);
        var aliceTablet = await PairTabletAsync(directory, alice, aliceOwner, "victim-tablet");
        var malloryTenant = directory.FindBySession(malloryOwner.SessionId)!;
        var malloryPrincipal = (await malloryTenant.Registry.AuthenticateAsync(malloryOwner.SessionId, malloryOwner.Secret)).Principal!;

        // Her own pairing, naming Alice's tablet's session id.
        var colliding = await RelaySecurityTestFactory.CompletedPairingAsync(
            "mallory-tablet",
            _clock.UtcNow,
            sessionId: aliceTablet.SessionId,
            desktop: mallory.Signer);
        var collided = await directory.AddPairedDeviceAsync(
            malloryTenant, malloryPrincipal, colliding, DeviceAuthorizationRole.Member, CompanionSurfaceKind.TabletLandscape);
        Assert.Equal("pairing-collision", collided.Code);

        // A pairing Alice's desktop signed (one a tablet would pin Alice's key for).
        var alices = await RelaySecurityTestFactory.CompletedPairingAsync("stolen-tablet", _clock.UtcNow, desktop: alice.Signer);
        var stolen = await directory.AddPairedDeviceAsync(
            malloryTenant, malloryPrincipal, alices, DeviceAuthorizationRole.Member, CompanionSurfaceKind.TabletLandscape);
        Assert.Equal("pairing-not-this-desktop", stolen.Code);

        Assert.Same(directory.FindBySession(aliceOwner.SessionId), directory.FindBySession(aliceTablet.SessionId));
    }

    [Fact]
    public async Task ARoomHoldsABoundedNumberOfDesktops()
    {
        var directory = await OpenAsync(persisted: false);
        var desktops = new List<Desktop>();
        try
        {
            for (var index = 0; index < RelayTenantDirectory.MaximumDesktopsPerRoom; index++)
            {
                var desktop = new Desktop();
                desktops.Add(desktop);
                await RegisterAsync(directory, desktop);
            }

            var oneTooMany = new Desktop();
            desktops.Add(oneTooMany);
            var refused = await directory.RegisterDesktopAsync(Room, oneTooMany.Claim(_clock.UtcNow));
            Assert.Equal("room-full", refused.Code);

            // Another room on the same relay is not affected, and nobody already in is pushed out.
            var elsewhere = await directory.RegisterDesktopAsync(GroupKey.RoomFor("another-group"), oneTooMany.Claim(_clock.UtcNow));
            Assert.True(elsewhere.Succeeded);
            Assert.True((await directory.RegisterDesktopAsync(Room, desktops[0].Claim(_clock.UtcNow))).Succeeded);
        }
        finally
        {
            desktops.ForEach(desktop => desktop.Dispose());
        }
    }

    [Fact]
    public async Task RegisteredDesktopsAndTheirTabletsComeBackAfterARelayRestart()
    {
        using var alice = new Desktop();
        using var bob = new Desktop();
        RelaySessionCredential aliceOwner, aliceTablet, bobTablet;
        {
            var directory = await OpenAsync(persisted: true);
            aliceOwner = await RegisterAsync(directory, alice);
            var bobOwner = await RegisterAsync(directory, bob);
            aliceTablet = await PairTabletAsync(directory, alice, aliceOwner, "alice-tablet");
            bobTablet = await PairTabletAsync(directory, bob, bobOwner, "bob-tablet");
        }

        var restarted = await OpenAsync(persisted: true);

        Assert.Equal(3, restarted.Tenants.Length);
        var aliceTenant = restarted.FindBySession(aliceTablet.SessionId)!;
        var bobTenant = restarted.FindBySession(bobTablet.SessionId)!;
        Assert.Equal(alice.KeyId, aliceTenant.DesktopKeyId);
        Assert.Equal(bob.KeyId, bobTenant.DesktopKeyId);
        Assert.Equal(Room, aliceTenant.Room);
        Assert.True((await aliceTenant.Registry.AuthenticateAsync(aliceOwner.SessionId, aliceOwner.Secret)).Authenticated);
        Assert.True((await aliceTenant.Registry.AuthenticateAsync(aliceTablet.SessionId, aliceTablet.Secret)).Authenticated);
        Assert.True((await bobTenant.Registry.AuthenticateAsync(bobTablet.SessionId, bobTablet.Secret)).Authenticated);
    }

    /// <summary>
    /// What production holds today: relay-devices.json with one owner, claimed with the admin key,
    /// and one paired tablet. No room is written anywhere in it.
    /// </summary>
    [Fact]
    public async Task ALegacySingleOwnerFileLoadsAsThatDesktopsTenantAndLearnsItsRoomWhenItRegisters()
    {
        Directory.CreateDirectory(_folder);
        var legacyPath = Path.Combine(_folder, "relay-devices.json");
        using var owner = new Desktop();
        RelaySessionCredential tablet;
        {
            // Written by the very calls the claim ceremony made, through the same store.
            var registry = await RelayDeviceRegistry.OpenAsync(_clock, _recovery, new VerifiedRelayRegistryStore(legacyPath));
            var claim = owner.Claim(_clock.UtcNow);
            var grant = _recovery.CreateGrant(claim.Establishment!.Assignment.DeviceId, owner.KeyId);
            var claimed = await registry.RecoverOwnerAsync(grant, claim, CompanionSurfaceKind.Desktop);
            var principal = (await registry.AuthenticateAsync(claimed.Value!.SessionId, claimed.Value.Secret)).Principal!;
            var pairing = await RelaySecurityTestFactory.CompletedPairingAsync("production-tablet", _clock.UtcNow, desktop: owner.Signer);
            tablet = (await registry.AddPairedDeviceAsync(
                principal, pairing, DeviceAuthorizationRole.Member, CompanionSurfaceKind.TabletLandscape)).Value!;
        }

        var legacyBytes = await File.ReadAllBytesAsync(legacyPath);
        _clock.Advance(TimeSpan.FromHours(14));
        var directory = await OpenAsync(persisted: true);

        // Loaded as one tenant, that desktop's, with its tablet, before anybody has done anything.
        Assert.Single(directory.Tenants);
        Assert.Equal(owner.KeyId, directory.Legacy.DesktopKeyId);
        Assert.Null(directory.Legacy.Room);
        Assert.Same(directory.Legacy, directory.FindBySession(tablet.SessionId));
        Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(legacyPath));

        // Its first registration arrives with its group key: same tenant, room learned, no new one.
        var registered = await directory.RegisterDesktopAsync(Room, owner.Claim(_clock.UtcNow));
        Assert.True(registered.Succeeded, registered.Code);
        Assert.Single(directory.Tenants);
        Assert.Equal(Room, directory.Legacy.Room);
        Assert.Same(directory.Legacy, directory.FindBySession(registered.Value!.SessionId));
        var kept = directory.Legacy.Registry.FindPairedDeviceByKey(RelaySecurityTestFactory.DeviceKey("production-tablet").KeyId);
        Assert.Equal(DeviceLifecycleStatus.Active, kept!.Status);
        // The tablet is found for its key-possession resume under the desktop it pinned, which it
        // names by the identity key id it saw in the offer (not the relay's id for the same key),
        // and under no other desktop.
        Assert.NotEqual(owner.KeyId, owner.Signer.PublicKey.KeyId);
        Assert.Single(directory.FindPairedDevices(kept.DeviceKey.KeyId, owner.Signer.PublicKey.KeyId));
        using var somebodyElse = new Desktop();
        Assert.Empty(directory.FindPairedDevices(kept.DeviceKey.KeyId, somebodyElse.Signer.PublicKey.KeyId));

        // And a squadmate registers beside it without touching it.
        using var squadmate = new Desktop();
        await RegisterAsync(directory, squadmate);
        Assert.Equal(2, directory.Tenants.Length);

        // The room survives a restart too.
        var restarted = await OpenAsync(persisted: true);
        Assert.Equal(Room, restarted.Legacy.Room);
        Assert.Equal(owner.KeyId, restarted.Legacy.DesktopKeyId);
        Assert.Equal(2, restarted.Tenants.Length);
    }

    private async Task<RelayTenantDirectory> OpenAsync(bool persisted)
    {
        var legacy = await RelayDeviceRegistry.OpenAsync(
            _clock,
            _recovery,
            persisted ? new VerifiedRelayRegistryStore(Path.Combine(_folder, "relay-devices.json")) : null);
        return await RelayTenantDirectory.OpenAsync(
            _clock,
            _recovery,
            legacy,
            new OpaqueRelayFrameHub(legacy, _clock),
            new RelayMapSurfaceStore(legacy, _clock),
            persisted ? Path.Combine(_folder, "relay-desktops") : null);
    }

    private async Task<RelaySessionCredential> RegisterAsync(RelayTenantDirectory directory, Desktop desktop)
    {
        var registered = await directory.RegisterDesktopAsync(Room, desktop.Claim(_clock.UtcNow));
        Assert.True(registered.Succeeded, registered.Code);
        return registered.Value!;
    }

    private async Task<RelaySessionCredential> PairTabletAsync(
        RelayTenantDirectory directory,
        Desktop desktop,
        RelaySessionCredential owner,
        string tabletName)
    {
        var tenant = directory.FindBySession(owner.SessionId)!;
        var principal = (await tenant.Registry.AuthenticateAsync(owner.SessionId, owner.Secret)).Principal!;
        var pairing = await RelaySecurityTestFactory.CompletedPairingAsync(tabletName, _clock.UtcNow, desktop: desktop.Signer);
        var paired = await directory.AddPairedDeviceAsync(
            tenant, principal, pairing, DeviceAuthorizationRole.Member, CompanionSurfaceKind.TabletLandscape);
        Assert.True(paired.Succeeded, paired.Code);
        return paired.Value!;
    }

    /// <summary>One desktop: an identity key and the device id its authority file gives it.</summary>
    private sealed class Desktop : IDisposable
    {
        private readonly CompanionDeviceId _deviceId = new(Guid.NewGuid());

        public IdentitySigner Signer { get; } = new();

        public DeviceKeyId KeyId => Claim(RelaySecurityTestFactory.Now).Request!.DeviceKey.KeyId;

        /// <summary>The self-pairing the desktop builds for a claim, a resume, and now a registration.</summary>
        public PairingAttempt Claim(DateTimeOffset now) =>
            DesktopRelayOwnerClaim.Build(Signer, _deviceId, now).ToCompletedAttempt();

        public void Dispose() => Signer.Dispose();
    }

    private sealed class IdentitySigner : IDesktopIdentitySigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public IdentitySigner()
        {
            var spki = _key.ExportSubjectPublicKeyInfo();
            PublicKey = new DesktopIdentityKey(
                new DeviceKeyId(RelaySecurityTestFactory.Base64Url(SHA256.HashData(spki))),
                DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
                RelaySecurityTestFactory.Base64Url(spki));
        }

        public DesktopIdentityKey PublicKey { get; }

        public byte[] Sign(ReadOnlySpan<byte> signatureInput) =>
            _key.SignData(signatureInput.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        public void Dispose() => _key.Dispose();
    }
}
