using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>A client cannot assert its device identity, server time, or delivery sequence.</summary>
public sealed record ClientCommandEnvelope(
    CompanionProtocolVersion ProtocolVersion,
    DeviceSessionId SessionId,
    DateTimeOffset ClientSentUtc,
    CompanionCommand Command)
{
    public CompanionProtocolVersion ProtocolVersion { get; } = ProtocolVersion.IsDefined
        ? ProtocolVersion
        : throw new ArgumentException("A protocol version is required.", nameof(ProtocolVersion));

    public DeviceSessionId SessionId { get; } = SessionId.Value == Guid.Empty
        ? throw new ArgumentException("A session id is required.", nameof(SessionId))
        : SessionId;

    public DateTimeOffset ClientSentUtc { get; } = ProtocolGuard.Utc(ClientSentUtc, nameof(ClientSentUtc));

    public CompanionCommand Command { get; } = ProtocolGuard.NotNull(Command, nameof(Command));
}

public sealed record CommandAcknowledgement
{
    public CommandAcknowledgement(
        CommandId commandId,
        CanonicalAggregateKind aggregate,
        AggregateRevision requestedRevision,
        AggregateRevision appliedRevision,
        CommandId? appliedChangeId,
        GlobalRevision globalRevision,
        AuthorityEpoch authorityEpoch,
        CommandDisposition disposition,
        string code,
        CanonicalCompanionState? canonicalState)
    {
        CommandId = commandId;
        Aggregate = ProtocolGuard.Defined(aggregate, nameof(aggregate));
        RequestedRevision = requestedRevision;
        AppliedRevision = appliedRevision;
        AppliedChangeId = appliedChangeId;
        GlobalRevision = globalRevision;
        AuthorityEpoch = authorityEpoch;
        Disposition = ProtocolGuard.Defined(disposition, nameof(disposition));
        Code = ProtocolGuard.Required(code, nameof(code), ProtocolBounds.MaxShortStringBytes);
        CanonicalState = canonicalState;

        var requiresCanonical = disposition is CommandDisposition.RejectedStale or
            CommandDisposition.RejectedConflict or CommandDisposition.RequiresPreview;
        if (requiresCanonical != (canonicalState is not null))
        {
            throw new ArgumentException("Stale, conflict, and preview responses include canonical state.", nameof(canonicalState));
        }

        if ((appliedRevision.Value == 0) != (appliedChangeId is null))
        {
            throw new ArgumentException("Revision zero has no applied change; a positive revision names it.", nameof(appliedChangeId));
        }

        var thisCommandApplied = appliedChangeId == commandId;
        var occupiesRequestedRevision = disposition is CommandDisposition.Applied or
            CommandDisposition.Duplicate or CommandDisposition.PendingDesktopApproval;
        if (occupiesRequestedRevision && (!thisCommandApplied || appliedRevision != requestedRevision))
        {
            throw new ArgumentException("An accepted or duplicate command occupies its requested revision.", nameof(appliedChangeId));
        }
    }

    public CommandId CommandId { get; }

    public CanonicalAggregateKind Aggregate { get; }

    public AggregateRevision RequestedRevision { get; }

    public AggregateRevision AppliedRevision { get; }

    public CommandId? AppliedChangeId { get; }

    public GlobalRevision GlobalRevision { get; }

    public AuthorityEpoch AuthorityEpoch { get; }

    public CommandDisposition Disposition { get; }

    public string Code { get; }

    public CanonicalCompanionState? CanonicalState { get; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CommandAcknowledgementMessage), "commandAcknowledgement")]
[JsonDerivedType(typeof(CanonicalSnapshotMessage), "canonicalSnapshot")]
[JsonDerivedType(typeof(CanonicalUpdateMessage), "canonicalUpdate")]
[JsonDerivedType(typeof(DeprecationMessage), "deprecation")]
public abstract record ServerMessage
{
    private protected ServerMessage()
    {
    }
}

public sealed record CommandAcknowledgementMessage(CommandAcknowledgement Acknowledgement) : ServerMessage
{
    public CommandAcknowledgement Acknowledgement { get; } =
        ProtocolGuard.NotNull(Acknowledgement, nameof(Acknowledgement));
}

public sealed record CanonicalSnapshotMessage(CanonicalCompanionState State) : ServerMessage
{
    public CanonicalCompanionState State { get; } = ProtocolGuard.NotNull(State, nameof(State));
}

public sealed record CanonicalUpdateMessage(CanonicalUpdate Update) : ServerMessage
{
    public CanonicalUpdate Update { get; } = ProtocolGuard.NotNull(Update, nameof(Update));
}

public sealed record DeprecationMessage(ProtocolDeprecationNotice Notice) : ServerMessage
{
    public ProtocolDeprecationNotice Notice { get; } = ProtocolGuard.NotNull(Notice, nameof(Notice));
}

/// <summary>Device identity, server UTC, and delivery order are added only by the trusted server boundary.</summary>
public sealed record ServerEnvelope
{
    public ServerEnvelope(
        CompanionProtocolVersion protocolVersion,
        DeviceSessionId sessionId,
        CompanionDeviceId authenticatedOriginDeviceId,
        DateTimeOffset serverUtc,
        DeliverySequence deliverySequence,
        ServerMessage message)
    {
        ProtocolVersion = protocolVersion.IsDefined
            ? protocolVersion
            : throw new ArgumentException("A protocol version is required.", nameof(protocolVersion));
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("A session id is required.", nameof(sessionId))
            : sessionId;
        AuthenticatedOriginDeviceId = authenticatedOriginDeviceId.Value == Guid.Empty
            ? throw new ArgumentException("An authenticated origin device is required.", nameof(authenticatedOriginDeviceId))
            : authenticatedOriginDeviceId;
        ServerUtc = ProtocolGuard.Utc(serverUtc, nameof(serverUtc));
        DeliverySequence = deliverySequence.Value > 0
            ? deliverySequence
            : throw new ArgumentOutOfRangeException(nameof(deliverySequence));
        Message = ProtocolGuard.NotNull(message, nameof(message));
    }

    public CompanionProtocolVersion ProtocolVersion { get; }

    public DeviceSessionId SessionId { get; }

    public CompanionDeviceId AuthenticatedOriginDeviceId { get; }

    public DateTimeOffset ServerUtc { get; }

    public DeliverySequence DeliverySequence { get; }

    public ServerMessage Message { get; }
}

public enum RelayCipherSuite
{
    P256HkdfSha256Aes256Gcm = 1,
}

/// <summary>
/// The hosted relay can route and expire this frame but cannot inspect paired workspace,
/// selection, capture, or coordinate state inside its authenticated ciphertext.
/// </summary>
public sealed record OpaqueRelayFrame
{
    public OpaqueRelayFrame(
        CompanionProtocolVersion protocolVersion,
        RelayChannelId channelId,
        DeviceSessionId sessionId,
        long keyEpoch,
        long senderSequence,
        RelayCipherSuite cipherSuite,
        string nonceBase64Url,
        IReadOnlyList<string> ciphertextChunksBase64Url,
        string authenticationTagBase64Url,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc)
    {
        ProtocolVersion = protocolVersion.IsDefined
            ? protocolVersion
            : throw new ArgumentException("A protocol version is required.", nameof(protocolVersion));
        ChannelId = channelId.Value == Guid.Empty
            ? throw new ArgumentException("A relay channel is required.", nameof(channelId))
            : channelId;
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("A session id is required.", nameof(sessionId))
            : sessionId;
        KeyEpoch = ProtocolGuard.Positive(keyEpoch, nameof(keyEpoch));
        SenderSequence = ProtocolGuard.Positive(senderSequence, nameof(senderSequence));
        CipherSuite = ProtocolGuard.Defined(cipherSuite, nameof(cipherSuite));
        NonceBase64Url = ProtocolGuard.Base64Url(
            nonceBase64Url,
            nameof(nonceBase64Url),
            64,
            exactDecodedBytes: 12);
        CiphertextChunksBase64Url = ProtocolGuard.List(
            ProtocolGuard.List(ciphertextChunksBase64Url, nameof(ciphertextChunksBase64Url))
                .Select(chunk => ProtocolGuard.Base64Url(chunk, nameof(ciphertextChunksBase64Url))),
            nameof(ciphertextChunksBase64Url));
        if (CiphertextChunksBase64Url.Count == 0)
        {
            throw new ArgumentException("An encrypted relay frame carries ciphertext.", nameof(ciphertextChunksBase64Url));
        }
        AuthenticationTagBase64Url = ProtocolGuard.Base64Url(
            authenticationTagBase64Url,
            nameof(authenticationTagBase64Url),
            64,
            exactDecodedBytes: 16);
        IssuedUtc = ProtocolGuard.Utc(issuedUtc, nameof(issuedUtc));
        ExpiresUtc = ProtocolGuard.Utc(expiresUtc, nameof(expiresUtc));
        if (ExpiresUtc <= IssuedUtc || ExpiresUtc - IssuedUtc > ProtocolBounds.CommandLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "A relay frame expires within five minutes.");
        }
    }

    public CompanionProtocolVersion ProtocolVersion { get; }

    public RelayChannelId ChannelId { get; }

    public DeviceSessionId SessionId { get; }

    public long KeyEpoch { get; }

    public long SenderSequence { get; }

    public RelayCipherSuite CipherSuite { get; }

    public string NonceBase64Url { get; }

    public IReadOnlyList<string> CiphertextChunksBase64Url { get; }

    public string AuthenticationTagBase64Url { get; }

    public DateTimeOffset IssuedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }
}
