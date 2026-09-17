using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.StateSync;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

public sealed class RelayCompanionRoutesTests
{
    [Fact]
    public async Task GetFramesResponseRoundTripsThroughTheDesktopBridgesParser()
    {
        // RelayFrameBatchResponse is written with the framework's own default request JSON
        // options (it is not itself a paired-device wire root); this proves that a frame nested
        // inside it still comes back byte-identical through RelayMarksBridge.ParseFrameBatch's
        // CompanionProtocolJson boundary, rather than losing every nested identifier's shape to
        // the outer envelope's default (int-enum, unwrapped-id) serialization.
        using var context = await RelaySecurityTestFactory.BootstrapAsync();
        var owner = await context.AuthenticateOwnerAsync();
        var frame = RelaySecurityTestFactory.Frame(owner, RelaySecurityTestFactory.Now, senderSequence: 1);
        var batch = new RelayFrameBatch(
            CompanionProtocolVersion.Current,
            [new RelayQueuedFrame(7, frame, RelaySecurityTestFactory.Now)],
            RequiresReconnect: false,
            RelaySecurityTestFactory.Now);

        var responseJson = JsonSerializer.Serialize(
            RelayFrameBatchResponse.From(batch),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var parsed = RelayMarksBridge.ParseFrameBatch(responseJson);

        var (deliveryId, roundTripped) = Assert.Single(parsed);
        Assert.Equal(7, deliveryId);
        Assert.NotNull(roundTripped);
        Assert.Equal(frame.SessionId, roundTripped!.SessionId);
        Assert.Equal(frame.ChannelId, roundTripped.ChannelId);
        Assert.Equal(frame.KeyEpoch, roundTripped.KeyEpoch);
        Assert.Equal(frame.SenderSequence, roundTripped.SenderSequence);
        Assert.Equal(frame.ProtocolVersion, roundTripped.ProtocolVersion);
        Assert.Equal(frame.NonceBase64Url, roundTripped.NonceBase64Url);
        Assert.Equal(frame.AuthenticationTagBase64Url, roundTripped.AuthenticationTagBase64Url);
    }

    [Fact]
    public async Task ClientJsonBodyRoundTripsIntoAWorkingClaim()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        using var signer = new DesktopRelayOwnerClaimTests_TestIdentitySigner();
        using var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x22, 32).ToArray(), clock);
        var registry = await RelayDeviceRegistry.OpenAsync(clock, recovery);
        var desktopDeviceId = new CompanionDeviceId(Guid.NewGuid());
        var material = DesktopRelayOwnerClaim.Build(signer, desktopDeviceId, clock.UtcNow);

        var wireBytes = material.ToJsonBody();
        var parsed = RelayCompanionRoutes.TryParseClaim(wireBytes);

        Assert.NotNull(parsed);
        var attempt = parsed!.ToCompletedAttempt();
        var grant = recovery.CreateGrant(desktopDeviceId, parsed.Request.DeviceKey.KeyId);
        var recovered = await registry.RecoverOwnerAsync(grant, attempt, CompanionSurfaceKind.Desktop);
        Assert.True(recovered.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"offer":{},"desktopNonceBase64Url":"x","codeConsumedUtc":"2026-01-01T00:00:00Z"}""")]
    public void MalformedOrIncompleteBodiesNeverParse(string body)
    {
        Assert.Null(RelayCompanionRoutes.TryParseClaim(System.Text.Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void PerSourceLimitTripsBeforeGlobalLimit()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        var gate = new RelayOwnerClaimGate(clock);
        var address = IPAddress.Parse("203.0.113.7");

        var admitted = Enumerable.Range(0, 5).Select(_ => gate.Admit(address).Allowed).ToArray();
        var sixth = gate.Admit(address);

        Assert.All(admitted, Assert.True);
        Assert.False(sixth.Allowed);
    }

    [Fact]
    public void GlobalLimitTripsAcrossDistinctSources()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        var gate = new RelayOwnerClaimGate(clock);

        // Four distinct sources, five admissions apiece (twenty), stays under both the per-source
        // cap and the shared relay-wide cap...
        var results = Enumerable.Range(0, 4)
            .SelectMany(sourceIndex => Enumerable.Range(0, 5)
                .Select(_ => gate.Admit(IPAddress.Parse($"203.0.113.{sourceIndex + 1}")).Allowed))
            .ToArray();
        // ...and a fifth distinct source is refused purely by the relay-wide budget, even though it
        // has made no request of its own yet (ABUSE-ADMIN-KEY-GUESS: a botnet spraying source
        // addresses cannot bypass the aggregate cap by rotating them).
        var fifthSource = gate.Admit(IPAddress.Parse("203.0.113.5"));

        Assert.All(results, Assert.True);
        Assert.False(fifthSource.Allowed);
    }

    private sealed class DesktopRelayOwnerClaimTests_TestIdentitySigner : IDesktopIdentitySigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public DesktopRelayOwnerClaimTests_TestIdentitySigner()
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
