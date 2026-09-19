using System.Security.Cryptography;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

public sealed class OwnerClaimDiagnosticTests
{
    [Fact]
    public async Task ARealDesktopClaimIsAccepted()
    {
        var clock = new RelayTestClock(new DateTimeOffset(2026, 9, 18, 23, 3, 0, TimeSpan.Zero));
        using var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x7c, 32).ToArray(), clock);
        var registry = await RelayDeviceRegistry.OpenAsync(clock, recovery);
        using var signer = new DiagnosticSigner();
        var deviceId = new CompanionDeviceId(Guid.NewGuid());

        var material = DesktopRelayOwnerClaim.Build(signer, deviceId, clock.UtcNow);
        var body = material.ToJsonBody();

        // Step 1: does the relay's own parser read back what the desktop wrote?
        var claim = RelayCompanionRoutes.TryParseClaim(body);
        Assert.NotNull(claim);

        // Step 2: does the parsed attempt satisfy the registry?
        var attempt = claim!.ToCompletedAttempt();
        var grant = recovery.CreateGrant(
            attempt.Establishment!.Assignment.DeviceId,
            attempt.Request!.DeviceKey.KeyId);
        var recovered = await registry.RecoverOwnerAsync(grant, attempt, CompanionSurfaceKind.Desktop);

        Assert.True(recovered.Succeeded, $"claim refused: {recovered.Code}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    [InlineData(1000)]
    [InlineData(30_000)]
    public async Task ADesktopClockAheadOfTheRelayStillClaims(int desktopAheadMilliseconds)
    {
        var relayNow = new DateTimeOffset(2026, 9, 18, 23, 3, 0, TimeSpan.Zero);
        var clock = new RelayTestClock(relayNow);
        using var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x7c, 32).ToArray(), clock);
        var registry = await RelayDeviceRegistry.OpenAsync(clock, recovery);
        using var signer = new DiagnosticSigner();

        var material = DesktopRelayOwnerClaim.Build(
            signer,
            new CompanionDeviceId(Guid.NewGuid()),
            relayNow.AddMilliseconds(desktopAheadMilliseconds));
        var claim = RelayCompanionRoutes.TryParseClaim(material.ToJsonBody());
        var attempt = claim!.ToCompletedAttempt();
        var grant = recovery.CreateGrant(
            attempt.Establishment!.Assignment.DeviceId,
            attempt.Request!.DeviceKey.KeyId);

        var recovered = await registry.RecoverOwnerAsync(grant, attempt, CompanionSurfaceKind.Desktop);

        Assert.True(
            recovered.Succeeded,
            $"a desktop {desktopAheadMilliseconds} ms ahead of the relay was refused: {recovered.Code}");
    }

    private sealed class DiagnosticSigner : IDesktopIdentitySigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public DiagnosticSigner()
        {
            var spki = _key.ExportSubjectPublicKeyInfo();
            PublicKey = new DesktopIdentityKey(
                new DeviceKeyId(Convert.ToBase64String(SHA256.HashData(spki)).TrimEnd('=').Replace('+', '-').Replace('/', '_')),
                DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
                Convert.ToBase64String(spki).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        }

        public DesktopIdentityKey PublicKey { get; }

        public byte[] Sign(ReadOnlySpan<byte> signatureInput) => _key.SignData(
            signatureInput,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        public void Dispose() => _key.Dispose();
    }
}
