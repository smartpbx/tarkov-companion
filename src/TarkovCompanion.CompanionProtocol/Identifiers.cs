using System.Text.Json.Serialization;

namespace TarkovCompanion.CompanionProtocol;

public readonly record struct PairingAttemptId
{
    [JsonConstructor]
    public PairingAttemptId(Guid value) => Value = ProtocolGuard.Id(value, nameof(value));

    public Guid Value { get; }
}

/// <summary>One desktop-issued pairing or session-resume challenge; it is bound into the transcript.</summary>
public readonly record struct HandshakeChallengeId
{
    [JsonConstructor]
    public HandshakeChallengeId(Guid value) => Value = ProtocolGuard.Id(value, nameof(value));

    public Guid Value { get; }
}

public readonly record struct DeviceSessionId
{
    [JsonConstructor]
    public DeviceSessionId(Guid value) => Value = ProtocolGuard.Id(value, nameof(value));

    public Guid Value { get; }
}

public readonly record struct CommandId
{
    [JsonConstructor]
    public CommandId(Guid value) => Value = ProtocolGuard.Id(value, nameof(value));

    public Guid Value { get; }
}

public readonly record struct AuthorityEpoch
{
    [JsonConstructor]
    public AuthorityEpoch(Guid value) => Value = ProtocolGuard.Id(value, nameof(value));

    public Guid Value { get; }
}

public readonly record struct MarkId
{
    [JsonConstructor]
    public MarkId(Guid value) => Value = ProtocolGuard.Id(value, nameof(value));

    public Guid Value { get; }
}

public readonly record struct ControlLeaseId
{
    [JsonConstructor]
    public ControlLeaseId(Guid value) => Value = ProtocolGuard.Id(value, nameof(value));

    public Guid Value { get; }
}

public readonly record struct CaptureIntentId
{
    [JsonConstructor]
    public CaptureIntentId(Guid value) => Value = ProtocolGuard.Id(value, nameof(value));

    public Guid Value { get; }
}

public readonly record struct RelayChannelId
{
    [JsonConstructor]
    public RelayChannelId(Guid value) => Value = ProtocolGuard.Id(value, nameof(value));

    public Guid Value { get; }
}

public readonly record struct AggregateRevision
{
    [JsonConstructor]
    public AggregateRevision(long value) => Value = ProtocolGuard.NonNegative(value, nameof(value));

    public long Value { get; }

    public AggregateRevision Next() => new(checked(Value + 1));
}

public readonly record struct GlobalRevision
{
    [JsonConstructor]
    public GlobalRevision(long value) => Value = ProtocolGuard.NonNegative(value, nameof(value));

    public long Value { get; }

    public GlobalRevision Next() => new(checked(Value + 1));
}

public readonly record struct DeliverySequence
{
    [JsonConstructor]
    public DeliverySequence(long value) => Value = ProtocolGuard.NonNegative(value, nameof(value));

    public long Value { get; }

    public DeliverySequence Next() => new(checked(Value + 1));
}

/// <summary>
/// A public-key thumbprint: base64url(SHA-256(exact public key bytes)). It identifies a device
/// or desktop identity key and is not private key material.
/// </summary>
public readonly record struct DeviceKeyId
{
    [JsonConstructor]
    public DeviceKeyId(string value) => Value = ProtocolGuard.Base64Url(
        value,
        nameof(value),
        ProtocolBounds.MaxShortStringBytes,
        exactDecodedBytes: 32);

    public string Value { get; }

    public override string ToString() => Value;
}
