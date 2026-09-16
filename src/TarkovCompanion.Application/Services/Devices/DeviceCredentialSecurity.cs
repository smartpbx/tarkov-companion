using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>
/// The last accepted WebAuthn signature counter for one bound device key.
/// </summary>
/// <remarks>
/// A challenge id makes persistence retries idempotent without allowing the same counter to prove
/// a different handshake. Authenticators that always report zero still rely on the protocol's
/// single-use challenge and increasing session key epoch for replay protection.
/// </remarks>
public sealed record DeviceSignatureCounter
{
    public DeviceSignatureCounter(
        DeviceKeyId deviceKeyId,
        HandshakeChallengeId challengeId,
        uint signatureCounter,
        DateTimeOffset challengeIssuedUtc)
    {
        DeviceKeyId = string.IsNullOrEmpty(deviceKeyId.Value)
            ? throw new ArgumentException("A device key id is required.", nameof(deviceKeyId))
            : deviceKeyId;
        ChallengeId = challengeId.Value == Guid.Empty
            ? throw new ArgumentException("A handshake challenge id is required.", nameof(challengeId))
            : challengeId;
        SignatureCounter = signatureCounter;
        ChallengeIssuedUtc = challengeIssuedUtc == default || challengeIssuedUtc.Offset != TimeSpan.Zero ||
                             challengeIssuedUtc.Ticks % TimeSpan.TicksPerMillisecond != 0
            ? throw new ArgumentException("A millisecond-precision UTC challenge time is required.", nameof(challengeIssuedUtc))
            : challengeIssuedUtc;
    }

    public DeviceKeyId DeviceKeyId { get; }

    public HandshakeChallengeId ChallengeId { get; }

    public uint SignatureCounter { get; }

    public DateTimeOffset ChallengeIssuedUtc { get; }
}

/// <summary>
/// Atomically accepts a WebAuthn counter only when it cannot replay a different challenge.
/// </summary>
public interface IDeviceSignatureCounterStore
{
    ValueTask<bool> TryAcceptAsync(
        DeviceSignatureCounter candidate,
        CancellationToken cancellationToken);
}
