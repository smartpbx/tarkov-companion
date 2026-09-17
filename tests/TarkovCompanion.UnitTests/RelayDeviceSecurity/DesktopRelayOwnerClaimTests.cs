using System.Security.Cryptography;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

public sealed class DesktopRelayOwnerClaimTests
{
    [Fact]
    public async Task SelfBuiltClaimRecoversOwnershipAndIsSingleUse()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        using var signer = new TestIdentitySigner();
        using var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x11, 32).ToArray(), clock);
        var registry = await RelayDeviceRegistry.OpenAsync(clock, recovery);
        var desktopDeviceId = new CompanionDeviceId(Guid.NewGuid());

        var material = DesktopRelayOwnerClaim.Build(signer, desktopDeviceId, clock.UtcNow);
        var grant = recovery.CreateGrant(desktopDeviceId, material.Request.DeviceKey.KeyId);
        var attempt = material.ToCompletedAttempt();

        var recovered = await registry.RecoverOwnerAsync(grant, attempt, CompanionSurfaceKind.Desktop);
        var replay = await registry.RecoverOwnerAsync(
            recovery.CreateGrant(desktopDeviceId, material.Request.DeviceKey.KeyId),
            attempt,
            CompanionSurfaceKind.Desktop);

        Assert.True(recovered.Succeeded);
        Assert.True(registry.CanAuthenticate);
        Assert.Equal(material.DeviceKeyIdThumbprint, material.Request.DeviceKey.KeyId.Value);
        // A second owner cannot be recovered while the first is still live, independent of the
        // fresh grant's own single-use nonce — this is what stops a stolen admin key from quietly
        // displacing the real owner behind their back.
        Assert.False(replay.Succeeded);
    }

    [Fact]
    public void BuildToleratesSubMillisecondNowFromARealClock()
    {
        // TimeProvider.System.GetUtcNow() (what production actually passes) is sub-millisecond
        // precision; every protocol timestamp Build produces requires exact millisecond precision.
        // A `now` that lands exactly on a millisecond boundary, like every other test's fixed
        // clock, would never have caught a regression here.
        using var signer = new TestIdentitySigner();
        var now = TimeProvider.System.GetUtcNow();
        Assert.NotEqual(0, now.Ticks % TimeSpan.TicksPerMillisecond);

        var material = DesktopRelayOwnerClaim.Build(signer, new CompanionDeviceId(Guid.NewGuid()), now);

        Assert.Equal(0, material.Establishment.EstablishedUtc.Ticks % TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public void BuildIsDeterministicallyDistinctPerCall()
    {
        using var signer = new TestIdentitySigner();
        var now = RelaySecurityTestFactory.Now;

        var first = DesktopRelayOwnerClaim.Build(signer, new CompanionDeviceId(Guid.NewGuid()), now);
        var second = DesktopRelayOwnerClaim.Build(signer, new CompanionDeviceId(Guid.NewGuid()), now);

        // Same identity key both times, so the same device is being claimed...
        Assert.Equal(first.DeviceKeyIdThumbprint, second.DeviceKeyIdThumbprint);
        // ...but every other nonce, ephemeral key, and attempt id is fresh.
        Assert.NotEqual(first.Offer.AttemptId, second.Offer.AttemptId);
        Assert.NotEqual(first.DesktopNonceBase64Url, second.DesktopNonceBase64Url);
        Assert.NotEqual(first.Establishment.Assignment.SessionId, second.Establishment.Assignment.SessionId);
    }

    private sealed class TestIdentitySigner : IDesktopIdentitySigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public TestIdentitySigner()
        {
            var spki = _key.ExportSubjectPublicKeyInfo();
            PublicKey = new DesktopIdentityKey(
                new DeviceKeyId(Base64Url(SHA256.HashData(spki))),
                DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
                Base64Url(spki));
        }

        public DesktopIdentityKey PublicKey { get; }

        public byte[] Sign(ReadOnlySpan<byte> signatureInput) =>
            _key.SignData(signatureInput.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        public void Dispose() => _key.Dispose();

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
