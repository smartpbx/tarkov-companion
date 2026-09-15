using System.Security.Cryptography;
using System.Text;
using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

/// <summary>
/// Compares the C# encodings with vectors produced by an independent implementation of the
/// normative byte layouts, so a browser or gateway implementation has a fixed target.
/// </summary>
public sealed class CryptographyVectorTests
{
    [Fact]
    public void PairingContextNameSealCommitmentAndCodeMatchTheIndependentVectors()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var request = GoldenRoot<PairingRequest>("handshake/pairing-request.json");
        using var tabletEphemeral = CryptoVectors.AgreementKey("tabletEphemeralPairing");
        using var desktopEphemeral = CryptoVectors.AgreementKey("desktopEphemeralPairing");
        var tabletSecret = PairingCryptography.DeriveP256SharedSecret(tabletEphemeral, offer.DesktopEphemeralKey);
        var desktopSecret = PairingCryptography.DeriveP256SharedSecret(desktopEphemeral, request.EphemeralKey);
        var context = PairingCryptography.EncodePairingRequestContext(offer, request.NegotiatedVersion, request.DeviceKey, request.EphemeralKey, request.ClientNonceBase64Url);
        var contextHash = SHA256.HashData(context);
        var sealedName = PairingCryptography.SealDeviceName(tabletSecret, contextHash, CryptoVectors.Text("pairing", "deviceName"));

        Assert.Equal(CryptoVectors.Text("pairing", "sharedSecretHex"), Convert.ToHexStringLower(tabletSecret));
        Assert.Equal(tabletSecret, desktopSecret);
        Assert.Equal(CryptoVectors.Text("pairing", "requestContextHex"), Convert.ToHexStringLower(context));
        Assert.Equal(CryptoVectors.Text("pairing", "requestContextHashBase64Url"), Base64UrlOf(contextHash));
        Assert.Equal(request.RequestedDeviceName, sealedName);
        Assert.Equal(CryptoVectors.Text("pairing", "commitmentBase64Url"), Base64UrlOf(PairingCryptography.ComputePairingCommitment(offer, request)));
        Assert.Equal(CryptoVectors.Text("pairing", "verificationCode"), PairingCryptography.ComputeVerificationCode(offer, request));
        Assert.Equal(CryptoVectors.Text("pairing", "deviceName"), PairingCryptography.OpenDeviceName(desktopSecret, offer, request));
        Assert.Equal(CryptoVectors.Text("testOnlyKeys", "tabletDevice", "xHex"), Convert.ToHexStringLower(FromBase64Url(request.DeviceKey.CosePublicKeyBase64Url)[10..42]));
    }

    [Fact]
    public void TranscriptSignatureInputAndTrafficKeysMatchTheIndependentVectors()
    {
        var offer = GoldenRoot<PairingOffer>("handshake/pairing-offer.json");
        var request = GoldenRoot<PairingRequest>("handshake/pairing-request.json");
        var challenge = GoldenRoot<HandshakeChallenge>("handshake/pairing-challenge.json");
        var transcript = HandshakeTranscript.FromChallenge(challenge, offer, request);
        var hash = transcript.ComputeHash();
        var secret = CryptoVectors.Hex("pairing", "sharedSecretHex");

        Assert.Equal(CryptoVectors.Text("pairing", "transcriptHex"), Convert.ToHexStringLower(transcript.Encode()));
        Assert.Equal(CryptoVectors.Text("pairing", "transcriptHashBase64Url"), Base64UrlOf(hash));
        Assert.True(transcript.Matches(challenge));
        Assert.Equal(CryptoVectors.Text("pairing", "desktopSignatureInputHex"), Convert.ToHexStringLower(PairingCryptography.EncodeDesktopSignatureInput(hash)));
        Assert.True(PairingCryptography.VerifyDesktopSignature(challenge.DesktopIdentityKey, hash, FromBase64Url(challenge.DesktopSignatureBase64Url)));
        hash[0] ^= 1;
        Assert.False(PairingCryptography.VerifyDesktopSignature(challenge.DesktopIdentityKey, hash, FromBase64Url(challenge.DesktopSignatureBase64Url)));
        Assert.Equal(
            CryptoVectors.Text("pairing", "tabletToDesktopKeyHex"),
            Convert.ToHexStringLower(PairingCryptography.DeriveTrafficKey(secret, challenge.TranscriptHashBase64Url, PairingTrafficDirection.TabletToDesktop)));
        Assert.Equal(
            CryptoVectors.Text("pairing", "desktopToTabletKeyHex"),
            Convert.ToHexStringLower(PairingCryptography.DeriveTrafficKey(secret, challenge.TranscriptHashBase64Url, PairingTrafficDirection.DesktopToTablet)));

        var resumeChallenge = GoldenRoot<HandshakeChallenge>("handshake/session-resume-challenge.json");
        var resumeTranscript = HandshakeTranscript.FromChallenge(resumeChallenge, GoldenRoot<SessionResumeRequest>("handshake/session-resume-request.json"));
        using var tabletResume = CryptoVectors.AgreementKey("tabletEphemeralResume");

        Assert.Equal(CryptoVectors.Text("sessionResume", "transcriptHex"), Convert.ToHexStringLower(resumeTranscript.Encode()));
        Assert.True(resumeTranscript.Matches(resumeChallenge));
        Assert.Equal(
            CryptoVectors.Text("sessionResume", "sharedSecretHex"),
            Convert.ToHexStringLower(PairingCryptography.DeriveP256SharedSecret(tabletResume, resumeChallenge.DesktopEphemeralKey)));
    }

    [Fact]
    public void TheGoldenRelayFrameIsReproducedAndDecryptsToTheIndependentPlaintext()
    {
        var frame = GoldenRoot<OpaqueRelayFrame>("relay/opaque-relay-frame.json");
        var tabletToDesktop = CryptoVectors.Hex("pairing", "tabletToDesktopKeyHex");
        var desktopToTablet = CryptoVectors.Hex("pairing", "desktopToTabletKeyHex");
        var plaintext = Encoding.UTF8.GetBytes(CryptoVectors.Text("relayFrame", "plaintextUtf8"));

        Assert.Equal(CryptoVectors.Text("relayFrame", "nonceHex"), Convert.ToHexStringLower(PairingCryptography.EncodeRelayNonce(frame.KeyEpoch, frame.SenderSequence)));
        Assert.Equal(
            CryptoVectors.Text("relayFrame", "additionalAuthenticatedDataHex"),
            Convert.ToHexStringLower(frame.EncodeAdditionalAuthenticatedData(PairingTrafficDirection.TabletToDesktop)));
        Assert.Equal(plaintext, PairingCryptography.OpenRelayFrame(tabletToDesktop, PairingTrafficDirection.TabletToDesktop, frame));
        Assert.Equal(
            CompanionInteractionMode.Independent,
            Assert.IsType<SetInteractionModeCommand>(CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(plaintext).Command).Mode);

        var resealed = PairingCryptography.SealRelayFrame(
            tabletToDesktop,
            PairingTrafficDirection.TabletToDesktop,
            frame.ProtocolVersion,
            frame.ChannelId,
            frame.SessionId,
            frame.KeyEpoch,
            frame.SenderSequence,
            frame.IssuedUtc,
            frame.ExpiresUtc,
            plaintext);
        AssertJsonEqual(Golden("relay/opaque-relay-frame.json"), CompanionProtocolJson.Serialize(resealed));

        Assert.ThrowsAny<CryptographicException>(() => PairingCryptography.OpenRelayFrame(tabletToDesktop, PairingTrafficDirection.DesktopToTablet, frame));
        Assert.ThrowsAny<CryptographicException>(() => PairingCryptography.OpenRelayFrame(desktopToTablet, PairingTrafficDirection.TabletToDesktop, frame));
    }

    [Fact]
    public void EveryRoutingFieldAndCiphertextByteIsAuthenticated()
    {
        var key = CryptoVectors.Hex("pairing", "tabletToDesktopKeyHex");
        var frame = GoldenRoot<OpaqueRelayFrame>("relay/opaque-relay-frame.json");
        var chunk = frame.CiphertextChunksBase64Url[0];
        var flippedCiphertext = FromBase64Url(chunk);
        flippedCiphertext[3] ^= 0x40;
        var flippedTag = FromBase64Url(frame.AuthenticationTagBase64Url);
        flippedTag[0] ^= 0x01;

        var tampered = new[]
        {
            Rebuild(frame, channelId: new RelayChannelId(Guid.Parse("90000000-0000-4000-8000-0000000000ff"))),
            Rebuild(frame, sessionId: new DeviceSessionId(Guid.Parse("20000000-0000-4000-8000-0000000000ff"))),
            Rebuild(frame, keyEpoch: 2),
            Rebuild(frame, senderSequence: 2),
            Rebuild(frame, issuedUtc: frame.IssuedUtc.AddSeconds(1)),
            Rebuild(frame, expiresUtc: frame.ExpiresUtc.AddSeconds(1)),
            Rebuild(frame, protocolVersion: new CompanionProtocolVersion(2, 1)),
            Rebuild(frame, ciphertext: Base64UrlOf(flippedCiphertext)),
            Rebuild(frame, tag: Base64UrlOf(flippedTag)),
        };

        Assert.All(tampered, candidate => Assert.ThrowsAny<CryptographicException>(() =>
            PairingCryptography.OpenRelayFrame(key, PairingTrafficDirection.TabletToDesktop, candidate)));
    }

    [Fact]
    public void RelayFramesUseCanonicalChunksAndCarryAFullPlaintextPayload()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var session = new DeviceSessionId(Guid.Parse("20000000-0000-4000-8000-000000000002"));
        var channel = new RelayChannelId(Guid.Parse("90000000-0000-4000-8000-000000000001"));
        var plaintext = RandomNumberGenerator.GetBytes(ProtocolBounds.MaxPayloadBytes);

        var frame = PairingCryptography.SealRelayFrame(key, PairingTrafficDirection.DesktopToTablet, CompanionProtocolVersion.Current, channel, session, 7, ProtocolBounds.MaxSenderSequence, Now, Now.AddMinutes(1), plaintext);
        var payload = CompanionProtocolJson.Serialize(frame);
        var wire = CompanionProtocolJson.Deserialize<OpaqueRelayFrame>(payload);

        Assert.Equal(86, frame.CiphertextChunksBase64Url.Count);
        Assert.All(frame.CiphertextChunksBase64Url.SkipLast(1), item => Assert.Equal(ProtocolBounds.RelayCiphertextChunkCharacters, item.Length));
        Assert.InRange(payload.Length, ProtocolBounds.MaxPayloadBytes, ProtocolBounds.MaxRelayFrameBytes);
        Assert.Equal(plaintext, PairingCryptography.OpenRelayFrame(key, PairingTrafficDirection.DesktopToTablet, wire));

        var rechunked = frame.CiphertextChunksBase64Url.ToList();
        rechunked[1] = rechunked[0][^4..] + rechunked[1];
        rechunked[0] = rechunked[0][..^4];
        Assert.Throws<ArgumentException>(() => Rebuild(frame, chunks: rechunked));
        Assert.Throws<ArgumentOutOfRangeException>(() => PairingCryptography.SealRelayFrame(
            key, PairingTrafficDirection.DesktopToTablet, CompanionProtocolVersion.Current, channel, session, 7, 1, Now, Now.AddMinutes(1), new byte[ProtocolBounds.MaxPayloadBytes + 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PairingCryptography.SealRelayFrame(
            key, PairingTrafficDirection.DesktopToTablet, CompanionProtocolVersion.Current, channel, session, 7, ProtocolBounds.MaxSenderSequence + 1, Now, Now.AddMinutes(1), plaintext));
    }

    [Fact]
    public void AReceiverRejectsReplayedExpiredAndForeignFramesBeforeDecryption()
    {
        var frame = GoldenRoot<OpaqueRelayFrame>("relay/opaque-relay-frame.json");
        var receiver = new RelayFrameReceiver(frame.SessionId, frame.ChannelId, frame.KeyEpoch, PairingTrafficDirection.TabletToDesktop, 0);

        var accepted = receiver.Accept(frame, frame.IssuedUtc);
        var replayed = accepted.Receiver.Accept(frame, frame.IssuedUtc.AddSeconds(1));
        var expired = receiver.Accept(frame, frame.ExpiresUtc);
        var foreign = new RelayFrameReceiver(new DeviceSessionId(Guid.Parse("20000000-0000-4000-8000-0000000000ff")), frame.ChannelId, frame.KeyEpoch, PairingTrafficDirection.TabletToDesktop, 0)
            .Accept(frame, frame.IssuedUtc);
        var nextEpoch = new RelayFrameReceiver(frame.SessionId, frame.ChannelId, frame.KeyEpoch + 1, PairingTrafficDirection.TabletToDesktop, 0)
            .Accept(frame, frame.IssuedUtc);

        Assert.Equal(RelayFrameDisposition.Accepted, accepted.Disposition);
        Assert.Equal(frame.SenderSequence, accepted.Receiver.LastAcceptedSequence);
        Assert.Equal(RelayFrameDisposition.Replayed, replayed.Disposition);
        Assert.Same(accepted.Receiver, replayed.Receiver);
        Assert.Equal(RelayFrameDisposition.Expired, expired.Disposition);
        Assert.Equal(RelayFrameDisposition.WrongSession, foreign.Disposition);
        Assert.Equal(RelayFrameDisposition.WrongSession, nextEpoch.Disposition);
    }

    private static OpaqueRelayFrame Rebuild(
        OpaqueRelayFrame frame,
        CompanionProtocolVersion? protocolVersion = null,
        RelayChannelId? channelId = null,
        DeviceSessionId? sessionId = null,
        long? keyEpoch = null,
        long? senderSequence = null,
        DateTimeOffset? issuedUtc = null,
        DateTimeOffset? expiresUtc = null,
        string? ciphertext = null,
        IReadOnlyList<string>? chunks = null,
        string? tag = null)
    {
        var epoch = keyEpoch ?? frame.KeyEpoch;
        var sequence = senderSequence ?? frame.SenderSequence;
        return new OpaqueRelayFrame(
            protocolVersion ?? frame.ProtocolVersion,
            channelId ?? frame.ChannelId,
            sessionId ?? frame.SessionId,
            epoch,
            sequence,
            frame.CipherSuite,
            Base64UrlOf(PairingCryptography.EncodeRelayNonce(epoch, sequence)),
            frame.CiphertextLength,
            chunks ?? (ciphertext is null ? frame.CiphertextChunksBase64Url : new[] { ciphertext }),
            tag ?? frame.AuthenticationTagBase64Url,
            issuedUtc ?? frame.IssuedUtc,
            expiresUtc ?? frame.ExpiresUtc);
    }
}
