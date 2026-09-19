using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

public sealed class RelayDeviceRegistryTests
{
    [Fact]
    public async Task EmptyStorageFailsClosedAndRoomKeyIsNotACredential()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        using var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)4, 32).ToArray(), clock);
        var registry = await RelayDeviceRegistry.OpenAsync(clock, recovery);

        var result = await registry.AuthenticateAsync(
            new DeviceSessionId(Guid.NewGuid()),
            "a-known-room-key");

        Assert.False(registry.CanAuthenticate);
        Assert.Equal(RelayRegistryLoadStatus.Uninitialized, registry.LoadStatus);
        Assert.False(result.Authenticated);
    }

    [Fact]
    public async Task RecoveryRequiresCurrentVersionDeviceKeyProofAndIsSingleUse()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        using var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)7, 32).ToArray(), clock);
        var registry = await RelayDeviceRegistry.OpenAsync(clock, recovery);
        var ownerId = new CompanionDeviceId(Guid.NewGuid());
        var pairing = await RelaySecurityTestFactory.CompletedPairingAsync(
            "recovered-owner",
            clock.UtcNow,
            ownerId);
        var grant = recovery.CreateGrant(ownerId, pairing.Request!.DeviceKey.KeyId);

        var wrongVersion = await registry.RecoverOwnerAsync(
            grant with { Version = OwnerRecoveryProtector.CurrentGrantVersion + 1 },
            pairing,
            CompanionSurfaceKind.Desktop);
        var recovered = await registry.RecoverOwnerAsync(grant, pairing, CompanionSurfaceKind.Desktop);
        var replay = await registry.RecoverOwnerAsync(grant, pairing, CompanionSurfaceKind.Desktop);

        Assert.False(wrongVersion.Succeeded);
        Assert.True(recovered.Succeeded);
        Assert.True(registry.CanAuthenticate);
        Assert.False(replay.Succeeded);
        // Named since package 48: a consumed grant is not the same refusal as a malformed claim,
        // a mismatched grant, or a relay that already has a live owner.
        Assert.Equal("owner-already-live", replay.Code);
    }

    [Fact]
    public async Task SignedResumeRotatesSessionAndInvalidatesStolenCredential()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var idOnly = await context.Registry.AuthenticateAsync(
            context.OwnerCredential.SessionId,
            RelaySecurityTestFactory.Base64Url(Enumerable.Repeat((byte)0x99, 32).ToArray()));
        var resume = await RelaySecurityTestFactory.CompletedResumeAsync(
            owner,
            context.OwnerKey,
            context.OwnerPairing.Establishment!.EstablishedUtc,
            context.Clock.UtcNow);

        var rotated = await context.Registry.RotateSessionAsync(
            owner,
            resume,
            CompanionSurfaceKind.Desktop);
        var replacement = Assert.IsType<RelaySessionCredential>(rotated.Value);
        var stolenOld = await context.Registry.AuthenticateAsync(
            context.OwnerCredential.SessionId,
            context.OwnerCredential.Secret);
        var activeNew = await context.Registry.AuthenticateAsync(replacement.SessionId, replacement.Secret);

        Assert.False(idOnly.Authenticated);
        Assert.True(rotated.Succeeded);
        Assert.False(stolenOld.Authenticated);
        Assert.True(activeNew.Authenticated);
        Assert.False(context.Registry.IsCurrent(owner));
        Assert.NotEqual(context.OwnerCredential.Secret, replacement.Secret);
        Assert.NotEqual(context.OwnerCredential.SessionId, replacement.SessionId);
    }

    [Fact]
    public async Task EqualEpochOrPairingReplayCannotRotateASession()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var resume = await RelaySecurityTestFactory.CompletedResumeAsync(
            owner,
            context.OwnerKey,
            context.OwnerPairing.Establishment!.EstablishedUtc,
            context.Clock.UtcNow);
        var first = await context.Registry.RotateSessionAsync(
            owner,
            resume,
            CompanionSurfaceKind.Desktop);
        var credential = Assert.IsType<RelaySessionCredential>(first.Value);
        var current = Assert.IsType<RelayPrincipal>((await context.Registry.AuthenticateAsync(
            credential.SessionId,
            credential.Secret)).Principal);

        var rejected = await context.Registry.RotateSessionAsync(
            current,
            resume,
            CompanionSurfaceKind.Desktop);

        Assert.False(rejected.Succeeded);
    }

    [Fact]
    public async Task RevocationAndReplacementAreIndependentPerDevice()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var pairing = await RelaySecurityTestFactory.CompletedPairingAsync("member", context.Clock.UtcNow);
        var paired = await context.Registry.AddPairedDeviceAsync(
            owner,
            pairing,
            DeviceAuthorizationRole.Member,
            CompanionSurfaceKind.TabletLandscape);
        var credential = Assert.IsType<RelaySessionCredential>(paired.Value);
        var member = Assert.IsType<RelayPrincipal>((await context.Registry.AuthenticateAsync(
            credential.SessionId,
            credential.Secret)).Principal);
        var replacementPairing = await RelaySecurityTestFactory.CompletedPairingAsync(
            "replacement",
            context.Clock.UtcNow);

        var replaced = await context.Registry.ReplaceDeviceAsync(
            owner,
            member.DeviceId,
            replacementPairing,
            CompanionSurfaceKind.TabletLandscape);
        var replacementCredential = Assert.IsType<RelaySessionCredential>(replaced.Value);

        Assert.True(replaced.Succeeded);
        Assert.False((await context.Registry.AuthenticateAsync(credential.SessionId, credential.Secret)).Authenticated);
        Assert.True((await context.Registry.AuthenticateAsync(
            replacementCredential.SessionId,
            replacementCredential.Secret)).Authenticated);
        Assert.True((await context.Registry.AuthenticateAsync(
            context.OwnerCredential.SessionId,
            context.OwnerCredential.Secret)).Authenticated);
        Assert.Contains(
            Assert.IsAssignableFrom<IReadOnlyList<RelayAuditEvent>>(context.Registry.ReadAudit(owner).Value),
            entry => entry.Action == RelayAuditAction.DeviceReplaced);
    }

    [Fact]
    public async Task BrowserCsrfIsSessionBoundCanonicalRotatingAndNullSafe()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var pairing = await RelaySecurityTestFactory.CompletedPairingAsync("csrf-member", context.Clock.UtcNow);
        var paired = await context.Registry.AddPairedDeviceAsync(
            owner,
            pairing,
            DeviceAuthorizationRole.Member,
            CompanionSurfaceKind.TabletPortrait);
        var memberCredential = Assert.IsType<RelaySessionCredential>(paired.Value);

        var missing = await context.Registry.ValidateAndRotateCsrfAsync(owner, null);
        var crossSession = await context.Registry.ValidateAndRotateCsrfAsync(owner, memberCredential.CsrfToken);
        var first = await context.Registry.ValidateAndRotateCsrfAsync(owner, context.OwnerCredential.CsrfToken);
        var replay = await context.Registry.ValidateAndRotateCsrfAsync(owner, context.OwnerCredential.CsrfToken);
        var nonCanonical = await context.Registry.ValidateAndRotateCsrfAsync(owner, context.OwnerCredential.CsrfToken + "=");

        Assert.False(missing.Succeeded);
        Assert.False(crossSession.Succeeded);
        Assert.True(first.Succeeded);
        Assert.False(replay.Succeeded);
        Assert.False(nonCanonical.Succeeded);
    }

    [Fact]
    public async Task ForgedRoleAndDisplayNameCannotBecomeAuthorizationIdentity()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var pairing = await RelaySecurityTestFactory.CompletedPairingAsync("observer", context.Clock.UtcNow);
        var paired = await context.Registry.AddPairedDeviceAsync(
            owner,
            pairing,
            DeviceAuthorizationRole.Observer,
            CompanionSurfaceKind.NarrowPhone);
        var credential = Assert.IsType<RelaySessionCredential>(paired.Value);
        var observer = Assert.IsType<RelayPrincipal>((await context.Registry.AuthenticateAsync(
            credential.SessionId,
            credential.Secret)).Principal);
        var forgedOwner = new RelayPrincipal(
            observer.DeviceId,
            observer.DeviceKeyId,
            observer.SessionId,
            observer.ChannelId,
            observer.ProtocolVersion,
            observer.KeyEpoch,
            DeviceAuthorizationRole.Owner,
            RelayAuthorization.CapabilitiesFor(DeviceAuthorizationRole.Owner),
            observer.Surface,
            observer.AuthenticatedUtc,
            observer.ExpiresUtc);

        var attempted = await context.Registry.RevokeDeviceAsync(
            forgedOwner,
            owner.DeviceId,
            "forged-owner");

        Assert.False(attempted.Succeeded);
        Assert.True(RelayAuthorization.Decide(
            observer,
            RelayPermission.PublishOpaqueFrames,
            context.Clock.UtcNow).Allowed);
        Assert.False(RelayAuthorization.Decide(
            observer,
            RelayPermission.RevokeDevice,
            context.Clock.UtcNow,
            owner.DeviceId).Allowed);
        Assert.DoesNotContain(
            typeof(RelayPrincipal).GetProperties(),
            property => property.Name.Contains("Name", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(RelayDeviceRecord).GetProperties(),
            property => property.Name.Contains("Name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SessionAndDeviceExpiryUseServerTimeAndFailClosed()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        context.Clock.Advance(ProtocolBounds.DeviceInactivityExpiry);

        var expired = await context.Registry.AuthenticateAsync(
            context.OwnerCredential.SessionId,
            context.OwnerCredential.Secret);

        Assert.False(expired.Authenticated);
        Assert.Equal("expired", expired.Code);
        Assert.False(context.Registry.IsCurrent(owner));
    }
}

public sealed class RelayAuthorizationTests
{
    [Theory]
    [InlineData(RelayPermission.ReceiveOpaqueFrames, true, true, true)]
    [InlineData(RelayPermission.PublishOpaqueFrames, true, true, true)]
    [InlineData(RelayPermission.CreatePairingInvitation, true, false, false)]
    [InlineData(RelayPermission.ApprovePairingInvitation, true, false, false)]
    [InlineData(RelayPermission.CloseOwnSession, true, true, true)]
    [InlineData(RelayPermission.RotateOwnSession, true, true, true)]
    [InlineData(RelayPermission.RevokeDevice, true, false, false)]
    [InlineData(RelayPermission.ReplaceDevice, true, false, false)]
    [InlineData(RelayPermission.ReadSecurityAudit, true, false, false)]
    public void EveryMutationHasAnExplicitLeastPrivilegeRoleMatrix(
        RelayPermission permission,
        bool ownerAllowed,
        bool memberAllowed,
        bool observerAllowed)
    {
        Assert.Equal(ownerAllowed, Decide(DeviceAuthorizationRole.Owner, permission));
        Assert.Equal(memberAllowed, Decide(DeviceAuthorizationRole.Member, permission));
        Assert.Equal(observerAllowed, Decide(DeviceAuthorizationRole.Observer, permission));
    }

    private static bool Decide(DeviceAuthorizationRole role, RelayPermission permission)
    {
        var deviceId = new CompanionDeviceId(Guid.NewGuid());
        var principal = new RelayPrincipal(
            deviceId,
            RelaySecurityTestFactory.DeviceKey(role.ToString()).KeyId,
            new DeviceSessionId(Guid.NewGuid()),
            new RelayChannelId(Guid.NewGuid()),
            CompanionProtocolVersion.Current,
            1,
            role,
            RelayAuthorization.CapabilitiesFor(role),
            CompanionSurfaceKind.DesktopBrowser,
            RelaySecurityTestFactory.Now,
            RelaySecurityTestFactory.Now.AddHours(1));
        var target = permission is RelayPermission.CloseOwnSession or RelayPermission.RotateOwnSession
            ? deviceId
            : new CompanionDeviceId(Guid.NewGuid());
        return RelayAuthorization.Decide(
            principal,
            permission,
            RelaySecurityTestFactory.Now,
            target).Allowed;
    }
}

public sealed class VerifiedRelayRegistryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"tarkov-relay-security-{Guid.NewGuid():N}");

    [Fact]
    public async Task CorruptPrimaryRestoresOnlyAVerifiedBackup()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "devices.json");
        RelaySessionCredential credential;
        using (var context = await RelaySecurityTestFactory.BootstrapAsync(path))
        {
            credential = context.OwnerCredential;
            File.WriteAllText(path, "{ corrupt");
        }

        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        using var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x5a, 32).ToArray(), clock);
        var reopened = await RelayDeviceRegistry.OpenAsync(
            clock,
            recovery,
            new VerifiedRelayRegistryStore(path));

        Assert.Equal(RelayRegistryLoadStatus.BackupRestored, reopened.LoadStatus);
        Assert.True((await reopened.AuthenticateAsync(credential.SessionId, credential.Secret)).Authenticated);
    }

    [Fact]
    public async Task CorruptOrDivergentCopiesNeverEnableOpenMode()
    {
        Directory.CreateDirectory(_directory);
        var pathA = Path.Combine(_directory, "a.json");
        var pathB = Path.Combine(_directory, "b.json");
        using var first = await RelaySecurityTestFactory.BootstrapAsync(pathA);
        using var second = await RelaySecurityTestFactory.BootstrapAsync(pathB);
        File.Copy(pathB, pathA + ".backup", overwrite: true);

        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        using var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x5a, 32).ToArray(), clock);
        var registry = await RelayDeviceRegistry.OpenAsync(
            clock,
            recovery,
            new VerifiedRelayRegistryStore(pathA));

        Assert.Equal(RelayRegistryLoadStatus.Corrupt, registry.LoadStatus);
        Assert.False(registry.CanAuthenticate);
        Assert.False((await registry.AuthenticateAsync(
            first.OwnerCredential.SessionId,
            first.OwnerCredential.Secret)).Authenticated);
    }

    [Fact]
    public async Task NewerPrimaryRepairsStaleBackupBeforeItCanResurrectState()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "rollback.json");
        RelaySessionCredential revokedCredential;
        RelaySessionCredential activeCredential;
        byte[] staleBackup;
        using (var context = await RelaySecurityTestFactory.BootstrapAsync(path))
        {
            revokedCredential = context.OwnerCredential;
            staleBackup = await File.ReadAllBytesAsync(path + ".backup");
            var owner = await context.AuthenticateOwnerAsync();
            var resume = await RelaySecurityTestFactory.CompletedResumeAsync(
                owner,
                context.OwnerKey,
                context.OwnerPairing.Establishment!.EstablishedUtc,
                context.Clock.UtcNow);
            activeCredential = Assert.IsType<RelaySessionCredential>((await context.Registry.RotateSessionAsync(
                owner,
                resume,
                CompanionSurfaceKind.Desktop)).Value);
        }

        await File.WriteAllBytesAsync(path + ".backup", staleBackup);
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        using var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x5a, 32).ToArray(), clock);
        _ = await RelayDeviceRegistry.OpenAsync(clock, recovery, new VerifiedRelayRegistryStore(path));
        File.WriteAllText(path, "{ corrupt");

        using var fallbackRecovery = new OwnerRecoveryProtector(
            Enumerable.Repeat((byte)0x5a, 32).ToArray(),
            clock);
        var restored = await RelayDeviceRegistry.OpenAsync(
            clock,
            fallbackRecovery,
            new VerifiedRelayRegistryStore(path));

        Assert.Equal(RelayRegistryLoadStatus.BackupRestored, restored.LoadStatus);
        Assert.False((await restored.AuthenticateAsync(
            revokedCredential.SessionId,
            revokedCredential.Secret)).Authenticated);
        Assert.True((await restored.AuthenticateAsync(
            activeCredential.SessionId,
            activeCredential.Secret)).Authenticated);
    }

    [Fact]
    public async Task RegistryDisappearanceAfterVerificationClosesInMemoryAuthority()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "disappeared.json");
        using var context = await RelaySecurityTestFactory.BootstrapAsync(path);
        File.Delete(path);
        File.Delete(path + ".backup");

        await Assert.ThrowsAsync<RelayRegistryUnavailableException>(() => context.Registry.AuthenticateAsync(
            context.OwnerCredential.SessionId,
            context.OwnerCredential.Secret).AsTask());

        Assert.False(context.Registry.CanAuthenticate);
        Assert.Equal(RelayRegistryLoadStatus.Corrupt, context.Registry.LoadStatus);
    }

    [Fact]
    public async Task RegistryPersistsNoRawCredentialOrDeviceName()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "devices.json");
        using var context = await RelaySecurityTestFactory.BootstrapAsync(path);

        var stored = await File.ReadAllTextAsync(path);

        Assert.DoesNotContain(context.OwnerCredential.Secret, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(context.OwnerCredential.CsrfToken, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("Device owner", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("a-known-room-key", stored, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
