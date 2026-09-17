using System.Text.Json;
using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

/// <summary>
/// Cross-language proof for v2r-tablet-marks-sync: <c>Interop/tablet-sealed-upsert-mark-frame.json</c>
/// is a real <see cref="OpaqueRelayFrame"/> sealed by
/// <c>src/TarkovCompanion.GroupServer/Tablet/relay-crypto.js</c> (the tablet's own AES-256-GCM
/// sealing, generated once and committed the same way the independently computed vectors under
/// Golden/crypto are — see <c>CryptographyVectorTests</c>). Not under Golden/ itself:
/// <c>GoldenAndHostileJsonTests</c> treats every file there as one of the closed set of canonical
/// wire-root examples, which this fixture is not. This desktop-side C# code opens it with the fixed
/// traffic key the JS generator used and must recover the exact <see cref="ClientCommandEnvelope"/>
/// the tablet sent, proving <see cref="PairingCryptography.OpenRelayFrame"/> and the JS
/// <c>sealRelayFrame</c> agree on the wire, not just on the nonce/AAD encoding
/// <c>scripts/test-relay-crypto.mjs</c> checks against the independent golden vector.
/// </summary>
public sealed class RelayCryptoInteropTests
{
    // The same 32 raw bytes as the fixture generator's trafficKeyBase64Url
    // ("MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY").
    private static readonly byte[] TrafficKey = FromBase64Url("MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY");

    private static OpaqueRelayFrame TabletSealedFrame() =>
        CompanionProtocolJson.Deserialize<OpaqueRelayFrame>(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Interop", "tablet-sealed-upsert-mark-frame.json")));

    [Fact]
    public void DesktopOpensATabletSealedUpsertMarkFrame()
    {
        var frame = TabletSealedFrame();

        var payload = PairingCryptography.OpenRelayFrame(TrafficKey, PairingTrafficDirection.TabletToDesktop, frame);

        Assert.Equal(RelayPayloadKind.ClientCommandEnvelope, payload.Kind);
        var envelope = CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(payload.Json.Span);
        Assert.Equal(new DeviceSessionId(Guid.Parse("20000000-0000-4000-8000-000000000002")), envelope.SessionId);
        var command = Assert.IsType<UpsertMarkCommand>(envelope.Command);
        Assert.Equal(new MarkId(Guid.Parse("50000000-0000-4000-8000-000000000099")), command.MarkId);
        Assert.Equal(0, command.ExpectedMarkRevision);
        Assert.Equal(MapMarkKind.Ping, command.Mark.Kind);
        Assert.Equal(MapMarkScope.PairedDevice, command.Mark.Scope);
        Assert.Equal(CoordinateSpaceKind.Normalized, command.Mark.CoordinateSpace);
        Assert.Equal("customs", command.Mark.State.MapId);
        Assert.Equal(0.4, command.Mark.State.X);
        Assert.Equal(0.6, command.Mark.State.Y);
        Assert.Equal("From the tablet", command.Mark.State.Label);
    }

    [Fact]
    public void OpeningTheTabletSealedFrameWithTheWrongDirectionFailsClosed() =>
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            PairingCryptography.OpenRelayFrame(TrafficKey, PairingTrafficDirection.DesktopToTablet, TabletSealedFrame()));

    [Fact]
    public void OpeningTheTabletSealedFrameWithTheWrongKeyFailsClosed()
    {
        var wrongKey = (byte[])TrafficKey.Clone();
        wrongKey[0] ^= 0xFF;

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            PairingCryptography.OpenRelayFrame(wrongKey, PairingTrafficDirection.TabletToDesktop, TabletSealedFrame()));
    }

    // The attempt id the JS fixture generator sealed this credential for
    // ("5a1d3c2e-7b10-4c00-8a00-000000000010" — the same value pairing-offer.json/pairing-challenge.json
    // already use for an attempt id elsewhere in Golden/handshake).
    private static readonly Guid CredentialAttemptId = Guid.Parse("5a1d3c2e-7b10-4c00-8a00-000000000010");

    private static SealedRelayCredential TabletSealedCredential() =>
        JsonSerializer.Deserialize<SealedRelayCredential>(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Interop", "tablet-sealed-relay-credential.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void DesktopOpensATabletSealedRelayCredential()
    {
        var sealedCredential = TabletSealedCredential();

        var credential = RelayCredentialCryptography.Open(TrafficKey, CredentialAttemptId, sealedCredential);

        Assert.Equal("relay-bearer-secret-for-the-tablet", credential);
    }

    [Fact]
    public void OpeningASealedCredentialForTheWrongAttemptFailsClosed() =>
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            RelayCredentialCryptography.Open(TrafficKey, Guid.NewGuid(), TabletSealedCredential()));

    [Fact]
    public void OpeningASealedCredentialWithTheWrongKeyFailsClosed()
    {
        var wrongKey = (byte[])TrafficKey.Clone();
        wrongKey[0] ^= 0xFF;

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            RelayCredentialCryptography.Open(wrongKey, CredentialAttemptId, TabletSealedCredential()));
    }

    [Fact]
    public void DesktopSealedCredentialRoundTripsBackThroughItself()
    {
        var attemptId = Guid.NewGuid();
        var expiresUtc = DateTimeOffset.UtcNow.AddMinutes(2);

        var sealedCredential = RelayCredentialCryptography.Seal(TrafficKey, attemptId, "a-fresh-bearer-secret", expiresUtc);
        var recovered = RelayCredentialCryptography.Open(TrafficKey, attemptId, sealedCredential);

        Assert.Equal("a-fresh-bearer-secret", recovered);
        // A ciphertext sealed for this purpose is structurally unrelated to an OpaqueRelayFrame —
        // it carries no channel/session/keyEpoch/senderSequence at all — so it cannot be replayed
        // into RelayDeviceRegistry.AdvanceFrameSequenceAsync the way a captured frame could be.
        Assert.NotNull(sealedCredential.CiphertextBase64Url);
        Assert.NotNull(sealedCredential.AuthenticationTagBase64Url);
    }

    [Fact]
    public void TwoSealsUnderTheSameKeyNeverShareANonce()
    {
        // A fixed nonce reused across two seals under the same traffic key (e.g. a retried
        // registration sealing a second, different secret) would be AES-GCM nonce reuse: it leaks
        // the two plaintexts' XOR and the authenticator's GHASH key, enabling forgeries.
        var attemptId = Guid.NewGuid();
        var expiresUtc = DateTimeOffset.UtcNow.AddMinutes(2);

        var first = RelayCredentialCryptography.Seal(TrafficKey, attemptId, "first-secret", expiresUtc);
        var second = RelayCredentialCryptography.Seal(TrafficKey, attemptId, "second-secret", expiresUtc);

        Assert.NotEqual(first.NonceBase64Url, second.NonceBase64Url);
        Assert.Equal("first-secret", RelayCredentialCryptography.Open(TrafficKey, attemptId, first));
        Assert.Equal("second-secret", RelayCredentialCryptography.Open(TrafficKey, attemptId, second));
    }
}
