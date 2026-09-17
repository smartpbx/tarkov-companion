using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>What a just-applied paired command means for the desktop's own local mark store.</summary>
internal enum MarkReconciliationKind
{
    None = 1,
    Create,
    Update,
    Remove,
}

internal readonly record struct MarkReconciliationAction(
    MarkReconciliationKind Kind,
    Guid MarkId,
    RaidMarkKind LocalKind,
    string MapId,
    string? FloorId,
    double X,
    double Y,
    string? Label)
{
    public static MarkReconciliationAction None { get; } = new(MarkReconciliationKind.None, Guid.Empty, default, "", null, 0, 0, null);
}

/// <summary>
/// The first live paired-device payload (v2r-relay-owner, #290): carries a tablet's mark add/edit/
/// remove to the desktop's own <see cref="IRaidMarkStore"/> over the now-composed relay registry and
/// opaque-frame hub, and delivers each newly paired tablet its starting canonical snapshot.
/// </summary>
/// <remarks>
/// Deliberately one direction only for this rough pass: a mark placed on the tablet reaches the
/// desktop's map, but a mark the desktop already had, or places locally, does not yet reach the
/// tablet. Broadcasting desktop-local marks needs <see cref="CanonicalCompanionState.Marks"/> kept
/// in step with <see cref="IRaidMarkStore"/>, which has no existing "desktop as command origin"
/// reducer path to fold through; inventing one for this package risked untested canonical-state
/// surgery shared by every other paired-device flow. See the package PR's "Deferred to polish".
/// </remarks>
public sealed class RelayMarksBridge : IAsyncDisposable
{
    private const string SessionHeader = "X-Relay-Session";
    private const string CredentialHeader = "X-Relay-Credential";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FrameLifetime = TimeSpan.FromSeconds(30);

    private readonly DesktopCompanionAuthority _authority;
    private readonly IRaidMarkStore _marks;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, PairedSessionState> _sessionsById = new();
    private readonly Dictionary<Guid, Guid> _localMarkIdByCanonicalMarkId = new();
    private HttpClient? _relay;
    private OwnerCredential? _owner;
    private CancellationTokenSource? _loop;
    private long _afterDeliveryId;

    public RelayMarksBridge(DesktopCompanionAuthority authority, IRaidMarkStore marks, TimeProvider timeProvider)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _marks = marks ?? throw new ArgumentNullException(nameof(marks));
        _clock = timeProvider ?? TimeProvider.System;
    }

    public void Configure(Uri relayOrigin)
    {
        ArgumentNullException.ThrowIfNull(relayOrigin);
        lock (_gate)
        {
            _relay ??= new HttpClient { BaseAddress = new Uri(relayOrigin.AbsoluteUri.TrimEnd('/') + "/") };
        }
    }

    /// <summary>Called once "Claim this relay" succeeds, so the poll loop can start reading this desktop's own queue.</summary>
    public void SetOwnerCredential(Guid sessionId, string credential, DateTimeOffset expiresUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        lock (_gate)
        {
            _owner = new OwnerCredential(sessionId, credential, expiresUtc);
        }

        EnsureLoopStarted();
    }

    /// <summary>
    /// Registers a just-completed tablet pairing on the relay (so the hub can route its traffic),
    /// then immediately delivers the canonical snapshot <c>DesktopCompanionAuthority.RegisterPairingAsync</c>
    /// already queued for it locally.
    /// </summary>
    public async Task RegisterPairedDeviceAsync(
        PairingOffer offer,
        string desktopNonceBase64Url,
        DateTimeOffset codeConsumedUtc,
        PairingRequest request,
        HandshakeChallenge challenge,
        EstablishedDesktopSession session,
        DeviceAuthorizationRole role,
        CompanionSurfaceKind surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        HttpClient? relay;
        OwnerCredential? owner;
        lock (_gate)
        {
            relay = _relay;
            owner = _owner;
        }

        if (relay is null || owner is null)
        {
            // No relay configured, or this desktop has not claimed it yet. The pairing itself
            // still succeeded locally; only relay live-sync for this device is unavailable.
            return;
        }

        var pairingBody = RelayDeviceClaimWireFormat.Build(offer, desktopNonceBase64Url, codeConsumedUtc, request, challenge, session.Establishment);
        var body = new JsonObject
        {
            ["pairing"] = JsonNode.Parse(pairingBody),
            ["role"] = role.ToString(),
            ["surface"] = surface.ToString(),
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v2/companion/relay/devices")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString())),
        };
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        AddBearer(httpRequest, owner);
        using var response = await relay.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var assignment = session.Establishment.Assignment;
        var state = new PairedSessionState(
            assignment.DeviceId,
            assignment.SessionId,
            assignment.RelayChannelId,
            assignment.KeyEpoch,
            session.TabletToDesktopKey.ToArray(),
            session.DesktopToTabletKey.ToArray());
        lock (_gate)
        {
            _sessionsById[assignment.SessionId.Value] = state;
        }

        foreach (var delivery in session.Mutation.Deliveries)
        {
            await PublishDeliveryAsync(state, delivery, _authority.Snapshot.CanonicalState, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureLoopStarted()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _loop = new CancellationTokenSource();
        }

        _ = Task.Run(() => PollLoopAsync(_loop.Token));
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Transient; the next tick tries again.
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        HttpClient? relay;
        OwnerCredential? owner;
        lock (_gate)
        {
            relay = _relay;
            owner = _owner;
        }

        if (relay is null || owner is null)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"v2/companion/relay/frames?after={_afterDeliveryId}");
        AddBearer(request, owner);
        using var response = await relay.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var frames = ParseFrameBatch(json);

        foreach (var (deliveryId, frame) in frames)
        {
            if (frame is not null)
            {
                await HandleInboundFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            }

            _afterDeliveryId = Math.Max(_afterDeliveryId, deliveryId);
        }

        if (frames.Count > 0)
        {
            using var ack = new HttpRequestMessage(HttpMethod.Post, $"v2/companion/relay/frames/{_afterDeliveryId}/ack");
            AddBearer(ack, owner);
            using var ackResponse = await relay.SendAsync(ack, cancellationToken).ConfigureAwait(false);
            _ = ackResponse; // best-effort; an unacknowledged delivery is simply re-read next poll
        }
    }

    private async Task HandleInboundFrameAsync(OpaqueRelayFrame frame, CancellationToken cancellationToken)
    {
        PairedSessionState? state;
        lock (_gate)
        {
            _sessionsById.TryGetValue(frame.SessionId.Value, out state);
        }

        if (state is null)
        {
            return;
        }

        RelayPayload payload;
        try
        {
            payload = PairingCryptography.OpenRelayFrame(
                state.TabletToDesktopKey,
                PairingTrafficDirection.TabletToDesktop,
                frame);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return;
        }

        if (payload.Kind != RelayPayloadKind.ClientCommandEnvelope)
        {
            return;
        }

        var command = CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(payload.Json.Span);
        var authenticatedFrame = new AuthenticatedPairedFrame(
            frame.SessionId,
            frame.KeyEpoch,
            "relay-marks-bridge",
            Now());
        PairedCommandApplication application;
        try
        {
            application = await _authority.ApplyCommandAsync(authenticatedFrame, command, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        await ReconcileAsync(command.Command, application.Acknowledgement.Disposition, cancellationToken).ConfigureAwait(false);

        foreach (var delivery in application.Deliveries)
        {
            PairedSessionState? target;
            lock (_gate)
            {
                target = _sessionsById.Values.FirstOrDefault(candidate => candidate.DeviceId == delivery.DeviceId);
            }

            if (target is not null)
            {
                await PublishDeliveryAsync(target, delivery, application.State.CanonicalState, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task ReconcileAsync(CompanionCommand command, CommandDisposition disposition, CancellationToken cancellationToken)
    {
        Guid? localId;
        var action = PlanReconciliation(command, disposition, out var canonicalMarkId, out localId);
        switch (action.Kind)
        {
            case MarkReconciliationKind.Create:
            {
                var created = await _marks.AddAsync(action.LocalKind, action.MapId, action.FloorId, action.X, action.Y, action.Label, cancellationToken)
                    .ConfigureAwait(false);
                lock (_gate)
                {
                    _localMarkIdByCanonicalMarkId[canonicalMarkId] = created.Id;
                }

                break;
            }

            case MarkReconciliationKind.Update when localId is { } editId:
                await _marks.MoveAsync(editId, action.X, action.Y, cancellationToken).ConfigureAwait(false);
                await _marks.RenameAsync(editId, action.Label, cancellationToken).ConfigureAwait(false);
                break;

            case MarkReconciliationKind.Remove when localId is { } removeId:
                await _marks.RemoveAsync(removeId, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _localMarkIdByCanonicalMarkId.Remove(canonicalMarkId);
                }

                break;
        }
    }

    private MarkReconciliationAction PlanReconciliation(
        CompanionCommand command,
        CommandDisposition disposition,
        out Guid canonicalMarkId,
        out Guid? localId)
    {
        canonicalMarkId = Guid.Empty;
        localId = null;
        if (disposition != CommandDisposition.Applied)
        {
            return MarkReconciliationAction.None;
        }

        switch (command)
        {
            case UpsertMarkCommand upsert when upsert.Mark.Kind is MapMarkKind.Ping or MapMarkKind.Waypoint:
                canonicalMarkId = upsert.MarkId.Value;
                lock (_gate)
                {
                    _localMarkIdByCanonicalMarkId.TryGetValue(canonicalMarkId, out var existing);
                    localId = existing == Guid.Empty ? null : existing;
                }

                var localKind = upsert.Mark.Kind == MapMarkKind.Ping ? RaidMarkKind.Ping : RaidMarkKind.Waypoint;
                return new MarkReconciliationAction(
                    upsert.ExpectedMarkRevision == 0 && localId is null ? MarkReconciliationKind.Create : MarkReconciliationKind.Update,
                    canonicalMarkId,
                    localKind,
                    upsert.Mark.State.MapId,
                    upsert.Mark.State.FloorId,
                    upsert.Mark.State.X,
                    upsert.Mark.State.Y,
                    upsert.Mark.State.Label);

            case DeleteMarkCommand delete:
                canonicalMarkId = delete.MarkId.Value;
                lock (_gate)
                {
                    _localMarkIdByCanonicalMarkId.TryGetValue(canonicalMarkId, out var existing);
                    localId = existing == Guid.Empty ? null : existing;
                }

                return localId is null
                    ? MarkReconciliationAction.None
                    : new MarkReconciliationAction(MarkReconciliationKind.Remove, canonicalMarkId, default, "", null, 0, 0, null);

            default:
                return MarkReconciliationAction.None;
        }
    }

    private async Task PublishDeliveryAsync(
        PairedSessionState state,
        AuthorityDelivery delivery,
        CanonicalCompanionState current,
        CancellationToken cancellationToken)
    {
        HttpClient? relay;
        OwnerCredential? owner;
        lock (_gate)
        {
            relay = _relay;
            owner = _owner;
        }

        if (relay is null || owner is null)
        {
            return;
        }

        var now = Now();
        var message = delivery.Item.Resolve(current);
        var serverEnvelope = new ServerEnvelope(
            CompanionProtocolVersion.Current,
            state.SessionId,
            current.DesktopDeviceId,
            now,
            delivery.Item.Sequence,
            message);
        var json = CompanionProtocolJson.Serialize(serverEnvelope);
        var senderSequence = state.NextSenderSequence();
        var frame = PairingCryptography.SealRelayFrame(
            state.DesktopToTabletKey,
            PairingTrafficDirection.DesktopToTablet,
            RelayPayloadKind.ServerEnvelope,
            CompanionProtocolVersion.Current,
            state.ChannelId,
            state.SessionId,
            state.KeyEpoch,
            senderSequence,
            now,
            now.Add(FrameLifetime),
            json);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v2/companion/relay/frames")
        {
            Content = new ByteArrayContent(CompanionProtocolJson.Serialize(frame)),
        };
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        AddBearer(httpRequest, owner);
        using var response = await relay.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        _ = response; // best-effort; a lost desktop-to-tablet delivery surfaces as a stale tablet, not a crash
    }

    private static void AddBearer(HttpRequestMessage request, OwnerCredential owner)
    {
        request.Headers.Add(SessionHeader, owner.SessionId.ToString("D"));
        request.Headers.Add(CredentialHeader, owner.Credential);
    }

    // Every protocol timestamp (ServerEnvelope, SealRelayFrame, AuthenticatedPairedFrame) requires
    // exact millisecond precision; TimeProvider.System.GetUtcNow() is sub-millisecond and would
    // otherwise fail every one of these constructors' own validation.
    private DateTimeOffset Now()
    {
        var utc = _clock.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? loop;
        HttpClient? relay;
        lock (_gate)
        {
            loop = _loop;
            relay = _relay;
            _loop = null;
            _relay = null;
        }

        if (loop is not null)
        {
            await loop.CancelAsync().ConfigureAwait(false);
            loop.Dispose();
        }

        relay?.Dispose();
    }

    private sealed record OwnerCredential(Guid SessionId, string Credential, DateTimeOffset ExpiresUtc);

    private sealed class PairedSessionState(
        CompanionDeviceId deviceId,
        DeviceSessionId sessionId,
        RelayChannelId channelId,
        long keyEpoch,
        byte[] tabletToDesktopKey,
        byte[] desktopToTabletKey)
    {
        private long _senderSequence;

        public CompanionDeviceId DeviceId { get; } = deviceId;

        public DeviceSessionId SessionId { get; } = sessionId;

        public RelayChannelId ChannelId { get; } = channelId;

        public long KeyEpoch { get; } = keyEpoch;

        public byte[] TabletToDesktopKey { get; } = tabletToDesktopKey;

        public byte[] DesktopToTabletKey { get; } = desktopToTabletKey;

        public long NextSenderSequence() => Interlocked.Increment(ref _senderSequence);
    }

    /// <summary>
    /// Parses a <c>GET /v2/companion/relay/frames</c> response body (RelayCompanionRoutes'
    /// <c>RelayFrameBatchResponse</c>): ordinary System.Text.Json for the envelope, since it is not
    /// itself a paired-device wire root, but each nested frame is read back through
    /// <see cref="CompanionProtocolJson"/> — the exact boundary the relay wrote it with — rather than
    /// re-modeled with default request JSON options, which would not reproduce every nested
    /// identifier's wire shape. A frame that fails that check comes back as a null
    /// <see cref="OpaqueRelayFrame"/> paired with its still-valid delivery id, so the caller can
    /// still advance past it without losing the rest of the batch.
    /// </summary>
    public static IReadOnlyList<(long DeliveryId, OpaqueRelayFrame? Frame)> ParseFrameBatch(string json)
    {
        RelayFrameBatchWire? batch;
        try
        {
            batch = JsonSerializer.Deserialize<RelayFrameBatchWire>(json, WireJsonOptions);
        }
        catch (JsonException)
        {
            return [];
        }

        if (batch is null)
        {
            return [];
        }

        var results = new List<(long, OpaqueRelayFrame?)>(batch.Frames.Count);
        foreach (var envelope in batch.Frames)
        {
            OpaqueRelayFrame? frame;
            try
            {
                frame = CompanionProtocolJson.Deserialize<OpaqueRelayFrame>(Encoding.UTF8.GetBytes(envelope.Frame.GetRawText()));
            }
            catch (JsonException)
            {
                frame = null;
            }

            results.Add((envelope.DeliveryId, frame));
        }

        return results;
    }

    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record RelayFrameBatchWire(int ProtocolVersion, bool RequiresReconnect, DateTimeOffset ServerUtc, IReadOnlyList<RelayFrameEnvelopeWire> Frames);

    private sealed record RelayFrameEnvelopeWire(long DeliveryId, JsonElement Frame);
}

/// <summary>
/// The relay's self-claim/device-registration wire shape:
/// <c>{offer, desktopNonceBase64Url, codeConsumedUtc, request, challenge, establishment}</c>. Shared
/// by <see cref="DesktopRelayOwnerClaim"/>'s own claim and by <see cref="RelayMarksBridge"/> registering
/// an ordinary (non-owner) paired tablet.
/// </summary>
internal static class RelayDeviceClaimWireFormat
{
    public static byte[] Build(
        PairingOffer offer,
        string desktopNonceBase64Url,
        DateTimeOffset codeConsumedUtc,
        PairingRequest request,
        HandshakeChallenge challenge,
        SessionEstablished establishment)
    {
        var body = new JsonObject
        {
            ["offer"] = JsonNode.Parse(CompanionProtocolJson.Serialize(offer)),
            ["desktopNonceBase64Url"] = desktopNonceBase64Url,
            ["codeConsumedUtc"] = codeConsumedUtc,
            ["request"] = JsonNode.Parse(CompanionProtocolJson.Serialize(request)),
            ["challenge"] = JsonNode.Parse(CompanionProtocolJson.Serialize(challenge)),
            ["establishment"] = JsonNode.Parse(CompanionProtocolJson.Serialize(establishment)),
        };
        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }
}
