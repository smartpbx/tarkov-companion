using System.Net;
using System.Text.Json.Nodes;
using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

/// <summary>
/// Holds the shared transport binding that the desktop gateway, relay, and tablet implement
/// separately: framed-only session traffic, the pairing code, the QR payload, and the source hash.
/// </summary>
public sealed class TransportBindingTests
{
    [Fact]
    public void EverySessionRootTravelsOnlyInsideAnAuthenticatedFrameOnEveryTransport()
    {
        var plaintext = PairedTransportBinding.PlaintextRoots.ToHashSet();
        var framed = PairedTransportBinding.FramedRoots.Keys.ToHashSet();

        Assert.Empty(plaintext.Intersect(framed));
        Assert.True(plaintext.Union(framed).ToHashSet().SetEquals(CompanionProtocolJson.RootTypes));
        Assert.Equal(
            Enum.GetValues<RelayPayloadKind>().Order().ToArray(),
            PairedTransportBinding.FramedRoots.Values.Order().ToArray());
        Assert.Equal(
            new[] { typeof(ClientCommandEnvelope), typeof(ClientDeliveryAcknowledgement), typeof(ReconnectRequest) }.ToHashSet(),
            framed.Where(root => RelayPayload.DirectionOf(PairedTransportBinding.KindOf(root)) == PairingTrafficDirection.TabletToDesktop).ToHashSet());
        Assert.Equal(
            new[] { typeof(ServerEnvelope), typeof(ReconnectPlan) }.ToHashSet(),
            framed.Where(root => RelayPayload.DirectionOf(PairedTransportBinding.KindOf(root)) == PairingTrafficDirection.DesktopToTablet).ToHashSet());
        Assert.Throws<ArgumentException>(() => PairedTransportBinding.KindOf(typeof(PairingRequest)));
        Assert.DoesNotContain(Enum.GetNames<CompanionTransportKind>(), name => name.Contains("Plaintext", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PairingCodesAreTenCrockfordSymbolsAndNormalizeTypedLookAlikes()
    {
        for (var index = 0; index < 64; index++)
        {
            var code = PairedTransportBinding.GeneratePairingCode();
            Assert.Equal(PairedTransportBinding.PairingCodeCharacters, code.Length);
            Assert.All(code, symbol => Assert.Contains(symbol, PairedTransportBinding.PairingCodeAlphabet));
            Assert.Equal(code, PairedTransportBinding.NormalizePairingCode(code));
        }

        Assert.Equal("7K2M9QXR4T", PairedTransportBinding.NormalizePairingCode("7k2m-9qxr 4t"));
        Assert.Equal("01101ABCDE", PairedTransportBinding.NormalizePairingCode("oIl0L abcde"));
        Assert.Throws<ArgumentException>(() => PairedTransportBinding.NormalizePairingCode("7K2M9QXR4U"));
        Assert.Throws<ArgumentException>(() => PairedTransportBinding.NormalizePairingCode("7K2M9QXR4"));
        Assert.Throws<ArgumentException>(() => PairedTransportBinding.NormalizePairingCode("7K2M9QXR4TT"));
        Assert.Throws<ArgumentException>(() => PairedTransportBinding.NormalizePairingCode(" "));
        Assert.Equal(32, PairedTransportBinding.PairingCodeAlphabet.Distinct().Count());
        Assert.Equal("Tarkov-Pairing-Code", PairedTransportBinding.PairingCodeHeaderName);
    }

    [Fact]
    public void TheQrPayloadAndSourceHashesMatchTheIndependentVectors()
    {
        var binding = CryptoVectors.Root["transportBinding"]!.AsObject();
        var identity = CryptoVectors.DesktopIdentity();
        var code = binding["testPairingCode"]!.GetValue<string>();
        var payload = binding["qrPayload"]!.GetValue<string>();

        Assert.Equal(payload, PairedTransportBinding.FormatQrPayload(code, identity.KeyId));
        Assert.True(PairedTransportBinding.TryParseQrPayload(payload, out var parsedCode, out var parsedKey));
        Assert.Equal(code, parsedCode);
        Assert.Equal(identity.KeyId, parsedKey);
        foreach (var hostile in new[]
                 {
                     payload.ToLowerInvariant(),
                     payload.Replace("/2.0/", "/2.1/", StringComparison.Ordinal),
                     payload + "/extra",
                     "https://companion.example/pair?code=" + code,
                     payload[..^1],
                 })
        {
            Assert.False(PairedTransportBinding.TryParseQrPayload(hostile, out _, out _), hostile);
        }

        var key = CryptoVectors.Hex("transportBinding", "sourceHashKeyHex");
        foreach (var vector in binding["sourceHashes"]!.AsArray().Select(item => item!.AsObject()))
        {
            Assert.Equal(
                vector["sourceHash"]!.GetValue<string>(),
                PairedTransportBinding.ComputeSourceHash(key, IPAddress.Parse(vector["address"]!.GetValue<string>())));
        }

        Assert.Throws<ArgumentException>(() => PairedTransportBinding.ComputeSourceHash(key.AsSpan(0, 31), IPAddress.Loopback));
        _ = PairingRateLimiter.TryConsume(PairingRateState.Empty, PairedTransportBinding.ComputeSourceHash(key, IPAddress.Loopback), Now);
    }
}
