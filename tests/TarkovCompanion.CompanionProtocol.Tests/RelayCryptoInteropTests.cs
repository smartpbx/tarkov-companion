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
}
