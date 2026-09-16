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
    public async Task RecoveryGrantIsDeviceKeyBoundAndSingleUse()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        using var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)7, 32).ToArray(), clock);
        var registry = await RelayDeviceRegistry.OpenAsync(clock, recovery);
        var key = RelaySecurityTestFactory.DeviceKey("recovered-owner");
        var ownerId = new CompanionDeviceId(Guid.NewGuid());
        var grant = recovery.CreateGrant(ownerId, key.KeyId);

        var recovered = await registry.RecoverOwnerAsync(
            grant,
            "Recovered owner",
            key,
            new RelayChannelId(Guid.NewGuid()),
            CompanionSurfaceKind.Desktop);
        var replay = await registry.RecoverOwnerAsync(
            grant,
            "Replay owner",
            key,
            new RelayChannelId(Guid.NewGuid()),
            CompanionSurfaceKind.Desktop);

        Assert.True(recovered.Succeeded);
        Assert.True(registry.CanAuthenticate);
        Assert.False(replay.Succeeded);
        Assert.Equal("recovery-rejected", replay.Code);
    }

    [Fact]
    public async Task SessionRotationInvalidatesAStolenCredentialIndependently()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();

        var rotated = await context.Registry.RotateSessionAsync(owner);
        var replacement = Assert.IsType<RelaySessionCredential>(rotated.Value);
        var stolenOld = await context.Registry.AuthenticateAsync(
            context.OwnerCredential.SessionId,
            context.OwnerCredential.Secret);
        var activeNew = await context.Registry.AuthenticateAsync(replacement.SessionId, replacement.Secret);

        Assert.True(rotated.Succeeded);
        Assert.False(stolenOld.Authenticated);
        Assert.True(activeNew.Authenticated);
        Assert.NotEqual(context.OwnerCredential.Secret, replacement.Secret);
        Assert.NotEqual(context.OwnerCredential.SessionId, replacement.SessionId);
    }

    [Fact]
    public async Task RevokingOneDeviceTerminatesItsSessionsButNotTheOwner()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var completed = await RelaySecurityTestFactory.CompletedPairingAsync("member", context.Clock.UtcNow);
        var paired = await context.Registry.AddPairedDeviceAsync(
            owner,
            completed,
            DeviceAuthorizationRole.Member,
            CompanionSurfaceKind.TabletLandscape);
        var credential = Assert.IsType<RelaySessionCredential>(paired.Value);
        var member = Assert.IsType<RelayPrincipal>((await context.Registry.AuthenticateAsync(
            credential.SessionId,
            credential.Secret)).Principal);

        var revoked = await context.Registry.RevokeDeviceAsync(owner, member.DeviceId, "lost-device");

        Assert.True(revoked.Succeeded);
        Assert.False((await context.Registry.AuthenticateAsync(credential.SessionId, credential.Secret)).Authenticated);
        Assert.True((await context.Registry.AuthenticateAsync(
            context.OwnerCredential.SessionId,
            context.OwnerCredential.Secret)).Authenticated);
        Assert.Contains(
            Assert.IsAssignableFrom<IReadOnlyList<RelayAuditEvent>>(context.Registry.ReadAudit(owner).Value),
            entry => entry.Action == RelayAuditAction.DeviceRevoked && entry.OutcomeCode == "lost-device");
    }

    [Fact]
    public async Task BrowserCsrfTokenRotatesAndCannotReplay()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();

        var first = await context.Registry.ValidateAndRotateCsrfAsync(owner, context.OwnerCredential.CsrfToken);
        var replay = await context.Registry.ValidateAndRotateCsrfAsync(owner, context.OwnerCredential.CsrfToken);
        var second = await context.Registry.ValidateAndRotateCsrfAsync(
            owner,
            Assert.IsType<string>(first.Value));

        Assert.True(first.Succeeded);
        Assert.False(replay.Succeeded);
        Assert.Equal("csrf-rejected", replay.Code);
        Assert.True(second.Succeeded);
    }

    [Fact]
    public async Task CryptographicIdentityNotDisplayNameControlsAuthorization()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var completed = await RelaySecurityTestFactory.CompletedPairingAsync("same-name", context.Clock.UtcNow);
        var paired = await context.Registry.AddPairedDeviceAsync(
            owner,
            completed,
            DeviceAuthorizationRole.Observer,
            CompanionSurfaceKind.NarrowPhone);
        var observerCredential = Assert.IsType<RelaySessionCredential>(paired.Value);
        var observer = Assert.IsType<RelayPrincipal>((await context.Registry.AuthenticateAsync(
            observerCredential.SessionId,
            observerCredential.Secret)).Principal);

        Assert.True(RelayAuthorization.Decide(observer, RelayPermission.ReceiveOpaqueFrames).Allowed);
        Assert.False(RelayAuthorization.Decide(observer, RelayPermission.PublishOpaqueFrames).Allowed);
        Assert.False(RelayAuthorization.Decide(observer, RelayPermission.RevokeDevice, owner.DeviceId).Allowed);
        Assert.True(RelayAuthorization.Decide(owner, RelayPermission.RevokeDevice, observer.DeviceId).Allowed);
        Assert.DoesNotContain(
            typeof(RelayPrincipal).GetProperties(),
            property => property.Name.Contains("Name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SessionAndDeviceExpiryUseServerTimeAndFailClosed()
    {
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        context.Clock.Advance(ProtocolBounds.DeviceInactivityExpiry);

        var expired = await context.Registry.AuthenticateAsync(
            context.OwnerCredential.SessionId,
            context.OwnerCredential.Secret);

        Assert.False(expired.Authenticated);
        Assert.Equal("expired", expired.Code);
    }
}

public sealed class RelayAuthorizationTests
{
    [Theory]
    [InlineData(RelayPermission.ReceiveOpaqueFrames, true, true, true)]
    [InlineData(RelayPermission.PublishOpaqueFrames, true, true, false)]
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
            role,
            RelayAuthorization.CapabilitiesFor(role),
            CompanionSurfaceKind.DesktopBrowser,
            RelaySecurityTestFactory.Now,
            RelaySecurityTestFactory.Now.AddHours(1));
        var target = permission is RelayPermission.CloseOwnSession or RelayPermission.RotateOwnSession
            ? deviceId
            : new CompanionDeviceId(Guid.NewGuid());
        return RelayAuthorization.Decide(principal, permission, target).Allowed;
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
            _ = await context.AuthenticateOwnerAsync();
            Assert.True(File.Exists(path + ".backup"));
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
    public async Task CorruptPrimaryAndBackupNeverEnableOpenMode()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "devices.json");
        File.WriteAllText(path, "not-json");
        File.WriteAllText(path + ".backup", "also-not-json");
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        using var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x5a, 32).ToArray(), clock);

        var registry = await RelayDeviceRegistry.OpenAsync(
            clock,
            recovery,
            new VerifiedRelayRegistryStore(path));

        Assert.Equal(RelayRegistryLoadStatus.Corrupt, registry.LoadStatus);
        Assert.False(registry.CanAuthenticate);
        Assert.False((await registry.AuthenticateAsync(
            new DeviceSessionId(Guid.NewGuid()),
            RelaySecurityTestFactory.Base64Url(new string('x', 32)))).Authenticated);
    }

    [Fact]
    public async Task VerifiedBackupCannotResurrectARevokedSession()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "devices.json");
        RelaySessionCredential revokedCredential;
        using (var context = await RelaySecurityTestFactory.BootstrapAsync(path))
        {
            var owner = await context.AuthenticateOwnerAsync();
            var completed = await RelaySecurityTestFactory.CompletedPairingAsync("lost", context.Clock.UtcNow);
            var paired = await context.Registry.AddPairedDeviceAsync(
                owner,
                completed,
                DeviceAuthorizationRole.Member,
                CompanionSurfaceKind.TabletLandscape);
            revokedCredential = Assert.IsType<RelaySessionCredential>(paired.Value);
            Assert.True((await context.Registry.RevokeDeviceAsync(
                owner,
                revokedCredential.DeviceId,
                "lost-device")).Succeeded);
            File.WriteAllText(path, "{ corrupt");
        }

        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        using var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x5a, 32).ToArray(), clock);
        var reopened = await RelayDeviceRegistry.OpenAsync(clock, recovery, new VerifiedRelayRegistryStore(path));

        Assert.Equal(RelayRegistryLoadStatus.BackupRestored, reopened.LoadStatus);
        Assert.False((await reopened.AuthenticateAsync(
            revokedCredential.SessionId,
            revokedCredential.Secret)).Authenticated);
    }

    [Fact]
    public async Task RegistryPersistsNoRawSessionOrCsrfCredential()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "devices.json");
        using var context = await RelaySecurityTestFactory.BootstrapAsync(path);

        var stored = await File.ReadAllTextAsync(path);

        Assert.DoesNotContain(context.OwnerCredential.Secret, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(context.OwnerCredential.CsrfToken, stored, StringComparison.Ordinal);
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
