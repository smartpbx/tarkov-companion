using System.Security.Cryptography;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>
/// A client cannot assert its device identity, server time, or delivery sequence. It does name the
/// desktop authority lifetime its revision was computed against, so a command from before a
/// desktop restart cannot apply to coincidentally equal revisions afterwards.
/// </summary>
public sealed record ClientCommandEnvelope
{
    public ClientCommandEnvelope(
        CompanionProtocolVersion protocolVersion,
        DeviceSessionId sessionId,
        AuthorityEpoch authorityEpoch,
        DateTimeOffset clientSentUtc,
        CompanionCommand command)
    {
        ProtocolVersion = ProtocolGuard.Version(protocolVersion, nameof(protocolVersion));
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("A session id is required.", nameof(sessionId))
            : sessionId;
        AuthorityEpoch = authorityEpoch.Value == Guid.Empty
            ? throw new ArgumentException("An authority epoch is required.", nameof(authorityEpoch))
            : authorityEpoch;
        ClientSentUtc = ProtocolGuard.Utc(clientSentUtc, nameof(clientSentUtc));
        Command = ProtocolGuard.NotNull(command, nameof(command));
    }

    public CompanionProtocolVersion ProtocolVersion { get; }

    public DeviceSessionId SessionId { get; }

    public AuthorityEpoch AuthorityEpoch { get; }

    /// <summary>Diagnostic only; the desktop orders and expires commands by its own receive time.</summary>
    public DateTimeOffset ClientSentUtc { get; }

    public CompanionCommand Command { get; }
}

/// <summary>
/// The desktop's answer to one command. <see cref="AppliedRevision"/> is the aggregate's revision
/// after handling and <see cref="AppliedChangeId"/> is whichever change occupies it, so comparing
/// it with <see cref="CommandId"/> is what distinguishes the command that landed from a divergent
/// one, exactly as for the v2 <c>StateAcknowledgement</c>.
/// </summary>
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
        CommandId = commandId.Value == Guid.Empty
            ? throw new ArgumentException("A command id is required.", nameof(commandId))
            : commandId;
        Aggregate = ProtocolGuard.Defined(aggregate, nameof(aggregate));
        RequestedRevision = requestedRevision.Value > 0
            ? requestedRevision
            : throw new ArgumentOutOfRangeException(nameof(requestedRevision), "A command requests a positive revision.");
        AppliedRevision = appliedRevision;
        AppliedChangeId = appliedChangeId is { } change && change.Value == Guid.Empty
            ? throw new ArgumentException("An applied change id is required when present.", nameof(appliedChangeId))
            : appliedChangeId;
        GlobalRevision = globalRevision;
        AuthorityEpoch = authorityEpoch.Value == Guid.Empty
            ? throw new ArgumentException("An authority epoch is required.", nameof(authorityEpoch))
            : authorityEpoch;
        Disposition = ProtocolGuard.Defined(disposition, nameof(disposition));
        Code = ProtocolGuard.Required(code, nameof(code), ProtocolBounds.MaxShortStringBytes);
        CanonicalState = canonicalState;

        if ((appliedRevision.Value == 0) != (appliedChangeId is null))
        {
            throw new ArgumentException("Revision zero has no applied change; a positive revision names it.", nameof(appliedChangeId));
        }

        if (RequiresCanonicalState(disposition) != (canonicalState is not null))
        {
            throw new ArgumentException(
                "Stale, conflict, preview, snapshot, and identifier-reuse responses include canonical state; others do not.",
                nameof(canonicalState));
        }

        var thisCommandApplied = appliedChangeId == commandId;
        var consistent = disposition switch
        {
            CommandDisposition.Applied => thisCommandApplied && appliedRevision == requestedRevision,
            CommandDisposition.RejectedStale => !thisCommandApplied && appliedRevision.Value > requestedRevision.Value,
            CommandDisposition.RejectedConflict => !thisCommandApplied && appliedRevision == requestedRevision,

            // The retained id already names a different accepted payload; the applied change may be
            // that original command, and naming it never means this payload applied.
            CommandDisposition.RejectedCommandIdReuse => true,
            _ => !thisCommandApplied,
        };
        if (!consistent)
        {
            throw new ArgumentException(
                $"{disposition} is inconsistent with revisions {requestedRevision.Value}/{appliedRevision.Value} " +
                $"and change ids {commandId.Value}/{appliedChangeId?.Value}.",
                nameof(disposition));
        }

        if (canonicalState is not null)
        {
            var cursor = canonicalState.Cursor(aggregate);
            if (canonicalState.AuthorityEpoch != authorityEpoch ||
                canonicalState.GlobalRevision != globalRevision ||
                cursor.Revision != appliedRevision ||
                cursor.LastChangeId != appliedChangeId)
            {
                throw new ArgumentException("Included canonical state is the state this acknowledgement describes.", nameof(canonicalState));
            }
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

    /// <summary>
    /// The v2 contract disposition with the same revision and change-id meaning, or null for a
    /// narrower paired-protocol rejection that the v2 stream vocabulary does not name.
    /// </summary>
    [JsonIgnore]
    public AcknowledgementDisposition? CoreDisposition => Disposition switch
    {
        CommandDisposition.Applied => AcknowledgementDisposition.Applied,
        CommandDisposition.RejectedStale => AcknowledgementDisposition.RejectedStale,
        CommandDisposition.RejectedConflict => AcknowledgementDisposition.RejectedConflict,
        CommandDisposition.UnsupportedVersion => AcknowledgementDisposition.UnsupportedVersion,
        _ => null,
    };

    internal static bool RequiresCanonicalState(CommandDisposition disposition) => disposition is
        CommandDisposition.RejectedStale or
        CommandDisposition.RejectedConflict or
        CommandDisposition.RequiresPreview or
        CommandDisposition.RequiresSnapshot or
        CommandDisposition.RejectedCommandIdReuse;
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

/// <summary>
/// Device identity, server UTC, and delivery order are added only by the trusted server boundary.
/// Every envelope to one device consumes exactly one sequence from that device's stream.
/// </summary>
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
        ProtocolVersion = ProtocolGuard.Version(protocolVersion, nameof(protocolVersion));
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

    /// <summary>The authenticated device whose command caused the message, or the desktop for its own messages.</summary>
    public CompanionDeviceId AuthenticatedOriginDeviceId { get; }

    public DateTimeOffset ServerUtc { get; }

    public DeliverySequence DeliverySequence { get; }

    public ServerMessage Message { get; }
}

/// <summary>
/// A tablet's live acknowledgement of its delivery stream. It acknowledges every envelope through
/// one device sequence and reports, per aggregate, the revision and change it now holds. The
/// timestamp is diagnostic; it never overrides desktop ordering.
/// </summary>
public sealed record ClientDeliveryAcknowledgement
{
    public ClientDeliveryAcknowledgement(
        CompanionProtocolVersion protocolVersion,
        DeviceSessionId sessionId,
        AuthorityEpoch authorityEpoch,
        DeliverySequence throughDeliverySequence,
        GlobalRevision globalRevision,
        IReadOnlyList<AggregateAcknowledgement> aggregateAcknowledgements)
    {
        ProtocolVersion = ProtocolGuard.Version(protocolVersion, nameof(protocolVersion));
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("A session id is required.", nameof(sessionId))
            : sessionId;
        AuthorityEpoch = authorityEpoch.Value == Guid.Empty
            ? throw new ArgumentException("An authority epoch is required.", nameof(authorityEpoch))
            : authorityEpoch;
        ThroughDeliverySequence = throughDeliverySequence.Value > 0
            ? throughDeliverySequence
            : throw new ArgumentOutOfRangeException(nameof(throughDeliverySequence));
        GlobalRevision = globalRevision;
        AggregateAcknowledgements = AggregateAcknowledgement.RequireDistinct(aggregateAcknowledgements, nameof(aggregateAcknowledgements));
    }

    public CompanionProtocolVersion ProtocolVersion { get; }

    public DeviceSessionId SessionId { get; }

    public AuthorityEpoch AuthorityEpoch { get; }

    public DeliverySequence ThroughDeliverySequence { get; }

    public GlobalRevision GlobalRevision { get; }

    public IReadOnlyList<AggregateAcknowledgement> AggregateAcknowledgements { get; }
}

public enum RelayCipherSuite
{
    P256HkdfSha256Aes256Gcm = 1,
}

/// <summary>
/// The hosted relay can route and expire this frame but cannot inspect paired workspace,
/// selection, capture, name, or coordinate state inside its authenticated ciphertext.
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
        int ciphertextLength,
        IReadOnlyList<string> ciphertextChunksBase64Url,
        string authenticationTagBase64Url,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc)
    {
        ProtocolVersion = ProtocolGuard.Version(protocolVersion, nameof(protocolVersion));
        ChannelId = channelId.Value == Guid.Empty
            ? throw new ArgumentException("A relay channel is required.", nameof(channelId))
            : channelId;
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("A session id is required.", nameof(sessionId))
            : sessionId;
        KeyEpoch = ProtocolGuard.KeyEpoch(keyEpoch, nameof(keyEpoch));
        SenderSequence = senderSequence is > 0 and <= ProtocolBounds.MaxSenderSequence
            ? senderSequence
            : throw new ArgumentOutOfRangeException(nameof(senderSequence), "A sender sequence is 1 through 2^32-1.");
        CipherSuite = ProtocolGuard.Defined(cipherSuite, nameof(cipherSuite));
        var suppliedNonce = ProtocolGuard.DecodeBase64Url(
            nonceBase64Url,
            nameof(nonceBase64Url),
            exactDecodedBytes: ProtocolBounds.RelayNonceBytes);
        if (!CryptographicOperations.FixedTimeEquals(PairingCryptography.EncodeRelayNonce(KeyEpoch, SenderSequence), suppliedNonce))
        {
            throw new ArgumentException("The relay nonce must encode this key epoch and sender sequence.", nameof(nonceBase64Url));
        }

        NonceBase64Url = nonceBase64Url;
        CiphertextLength = ciphertextLength is > 0 and <= ProtocolBounds.MaxPayloadBytes
            ? ciphertextLength
            : throw new ArgumentOutOfRangeException(nameof(ciphertextLength));
        CiphertextChunksBase64Url = ProtocolGuard.List(ciphertextChunksBase64Url, nameof(ciphertextChunksBase64Url));

        // Canonical chunking: base64url of the whole ciphertext cut into 1024-character pieces,
        // so each ciphertext has one frame spelling and a relay cannot re-chunk it unnoticed.
        var expectedCharacters = (int)((((long)CiphertextLength * 4) + 2) / 3);
        var expectedChunks = (expectedCharacters + ProtocolBounds.RelayCiphertextChunkCharacters - 1) /
                             ProtocolBounds.RelayCiphertextChunkCharacters;
        if (CiphertextChunksBase64Url.Count != expectedChunks)
        {
            throw new ArgumentException("Ciphertext is split into canonical 1024-character chunks.", nameof(ciphertextChunksBase64Url));
        }

        var decodedLength = 0;
        for (var index = 0; index < CiphertextChunksBase64Url.Count; index++)
        {
            var chunk = CiphertextChunksBase64Url[index];
            var last = index == CiphertextChunksBase64Url.Count - 1;
            var expectedLength = last
                ? expectedCharacters - (index * ProtocolBounds.RelayCiphertextChunkCharacters)
                : ProtocolBounds.RelayCiphertextChunkCharacters;
            if (chunk is null || chunk.Length != expectedLength)
            {
                throw new ArgumentException("Ciphertext is split into canonical 1024-character chunks.", nameof(ciphertextChunksBase64Url));
            }

            decodedLength = checked(decodedLength + ProtocolGuard.DecodeBase64Url(chunk, nameof(ciphertextChunksBase64Url)).Length);
        }

        if (decodedLength != CiphertextLength)
        {
            throw new ArgumentException("The declared ciphertext length must equal the decoded chunks.", nameof(ciphertextLength));
        }

        AuthenticationTagBase64Url = ProtocolGuard.Base64Url(
            authenticationTagBase64Url,
            nameof(authenticationTagBase64Url),
            exactDecodedBytes: ProtocolBounds.RelayAuthenticationTagBytes);
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

    public int CiphertextLength { get; }

    public IReadOnlyList<string> CiphertextChunksBase64Url { get; }

    public string AuthenticationTagBase64Url { get; }

    public DateTimeOffset IssuedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }

    /// <summary>The AES-GCM additional data for this frame as read by a receiver expecting <paramref name="direction"/>.</summary>
    public byte[] EncodeAdditionalAuthenticatedData(PairingTrafficDirection direction) =>
        PairingCryptography.EncodeRelayAdditionalAuthenticatedData(
            direction,
            ProtocolVersion,
            ChannelId,
            SessionId,
            KeyEpoch,
            SenderSequence,
            CipherSuite,
            CiphertextLength,
            IssuedUtc,
            ExpiresUtc);

    internal byte[] DecodeCiphertext() =>
        ProtocolGuard.DecodeBase64Url(
            string.Concat(CiphertextChunksBase64Url),
            nameof(CiphertextChunksBase64Url),
            maximumCharacters: int.MaxValue);
}

public enum RelayFrameDisposition
{
    Accepted = 1,
    WrongSession,
    Expired,
    Replayed,
}

/// <summary>
/// One receiving direction of one session. A relay can drop frames, which the paired delivery
/// stream detects, but it cannot replay or reorder an accepted frame: sender sequences are accepted
/// only while strictly increasing and unexpired by the receiver's clock. Call this before
/// <see cref="PairingCryptography.OpenRelayFrame"/> and commit the returned receiver only after the
/// frame authenticates.
/// </summary>
public sealed record RelayFrameReceiver
{
    public RelayFrameReceiver(
        DeviceSessionId sessionId,
        RelayChannelId channelId,
        long keyEpoch,
        PairingTrafficDirection direction,
        long lastAcceptedSequence)
    {
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("A session id is required.", nameof(sessionId))
            : sessionId;
        ChannelId = channelId.Value == Guid.Empty
            ? throw new ArgumentException("A relay channel is required.", nameof(channelId))
            : channelId;
        KeyEpoch = ProtocolGuard.KeyEpoch(keyEpoch, nameof(keyEpoch));
        Direction = ProtocolGuard.Defined(direction, nameof(direction));
        LastAcceptedSequence = lastAcceptedSequence is >= 0 and <= ProtocolBounds.MaxSenderSequence
            ? lastAcceptedSequence
            : throw new ArgumentOutOfRangeException(nameof(lastAcceptedSequence));
    }

    public DeviceSessionId SessionId { get; }

    public RelayChannelId ChannelId { get; }

    public long KeyEpoch { get; }

    public PairingTrafficDirection Direction { get; }

    public long LastAcceptedSequence { get; }

    public static RelayFrameReceiver For(DeviceSession session, PairingTrafficDirection receivingDirection)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new RelayFrameReceiver(session.SessionId, session.RelayChannelId, session.KeyEpoch, receivingDirection, 0);
    }

    public (RelayFrameReceiver Receiver, RelayFrameDisposition Disposition) Accept(OpaqueRelayFrame frame, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        if (frame.SessionId != SessionId || frame.ChannelId != ChannelId || frame.KeyEpoch != KeyEpoch)
        {
            return (this, RelayFrameDisposition.WrongSession);
        }

        if (now >= frame.ExpiresUtc || frame.IssuedUtc - now > ProtocolBounds.MaxClientClockSkew)
        {
            return (this, RelayFrameDisposition.Expired);
        }

        if (frame.SenderSequence <= LastAcceptedSequence)
        {
            return (this, RelayFrameDisposition.Replayed);
        }

        return (new RelayFrameReceiver(SessionId, ChannelId, KeyEpoch, Direction, frame.SenderSequence), RelayFrameDisposition.Accepted);
    }
}
