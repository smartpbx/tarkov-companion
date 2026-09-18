using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// <summary>The artwork bytes behind a <see cref="TabletMapSurface"/>, uploaded only when they change.</summary>
public sealed record TabletMapArtworkBytes(string MediaType, string ContentSha256, byte[] Bytes);

/// <summary>Where the desktop hands its current map to whatever is carrying it to paired tablets.</summary>
/// <remarks>
/// An interface so the V2 raid cockpit — which is where the scene, the plan rectangle and the
/// decoded artwork all already are — can publish without taking a dependency on the relay
/// transport, and so a test can watch what it published.
/// </remarks>
public interface ITabletMapSurfaceSink
{
    /// <param name="surfaceJson">
    /// The surface already serialized. The caller has it in hand because it compares one publish
    /// with the last to decide whether there is anything to send at all, and serializing a
    /// megabyte twice per change to hand over a record would undo the saving.
    /// </param>
    ValueTask PublishMapSurfaceAsync(
        byte[] surfaceJson,
        TabletMapArtworkBytes? artwork,
        CancellationToken cancellationToken = default);
}

public sealed class RelayMarksBridge : IAsyncDisposable, ITabletMapSurfaceSink
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
    private string? _publishedArtworkSha;

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
        PairingAttemptId attemptId,
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

        var registeredJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var registered = JsonSerializer.Deserialize<RelaySessionCredentialWire>(registeredJson, WireJsonOptions);
        if (registered is not null)
        {
            // Handed to the tablet through the same bounded pairing mailbox that carried
            // `established` — the tablet has everything else it needs (session id, channel id,
            // key epoch) from its own copy of that message, so only the bearer secret travels here.
            // Sealed with this same pairing's desktop-to-tablet traffic key first
            // (RelayCredentialCryptography): the relay only ever holds and forwards ciphertext, and
            // a caller who merely resolved the offer during the mailbox's window cannot read it
            // (ABUSE-PAIRED-LIVE-BEARER-THEFT).
            var sealedCredential = RelayCredentialCryptography.Seal(
                session.DesktopToTabletKey.Span,
                attemptId.Value,
                registered.Credential,
                registered.ExpiresUtc);
            using var credentialRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"v2/companion/pairing/relay-session/{attemptId.Value:D}")
            {
                Content = JsonContent.Create(sealedCredential, options: WireJsonOptions),
            };
            using var credentialResponse = await relay.SendAsync(credentialRequest, cancellationToken).ConfigureAwait(false);
            _ = credentialResponse; // best-effort; a tablet that missed it can be re-paired
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

    private sealed record RelaySessionCredentialWire(Guid SessionId, Guid ChannelId, string Credential, string CsrfToken, DateTimeOffset ExpiresUtc);

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

        // [V2 rough package 24] Control mode: a paired device the desktop has granted a lease to
        // just moved canonical workspace state, so the desktop's own map has to move with it. The
        // reducer has already decided whether that device may — an unauthorized control command
        // never reaches Applied — so this only carries the result.
        if (application.Acknowledgement.Disposition == CommandDisposition.Applied &&
            command.Command is ControlWorkspaceCommand)
        {
            DesktopWorkspaceRequested?.Invoke(application.State.CanonicalState.Workspace.Projection);
        }

        CanonicalStateChanged?.Invoke(application.State.CanonicalState);

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

    /// <summary>
    /// Raised after a paired device in Control mode has moved canonical workspace state, with the
    /// projection the desktop must now be showing.
    /// </summary>
    public event Action<WorkspaceProjection>? DesktopWorkspaceRequested;

    /// <summary>
    /// Raised after any paired command lands, so the desktop's own panels see a control request
    /// arrive without polling the authority.
    /// </summary>
    public event Action<CanonicalCompanionState>? CanonicalStateChanged;

    /// <summary>
    /// Publishes the desktop's current map for its paired tablets: the scene as JSON, and the
    /// reviewed artwork behind it when that picture has changed.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 24, #407] Artwork is uploaded only when its content hash changes, because
    /// a scene is republished on every raid tick and a rasterized plan is megabytes. The surface
    /// always names the hash it expects, so a tablet that has the picture already keeps drawing it
    /// and one that does not fetches it.
    /// </remarks>
    public async ValueTask PublishMapSurfaceAsync(
        byte[] surfaceJson,
        TabletMapArtworkBytes? artwork,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(surfaceJson);
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

        using var surfaceRequest = new HttpRequestMessage(HttpMethod.Post, "v2/companion/relay/map")
        {
            Content = new ByteArrayContent(surfaceJson),
        };
        surfaceRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        AddBearer(surfaceRequest, owner);
        using var surfaceResponse = await relay.SendAsync(surfaceRequest, cancellationToken).ConfigureAwait(false);
        if (!surfaceResponse.IsSuccessStatusCode)
        {
            return;
        }

        if (artwork is null ||
            string.Equals(_publishedArtworkSha, artwork.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var artworkRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"v2/companion/relay/map/artwork?sha256={Uri.EscapeDataString(artwork.ContentSha256)}")
        {
            Content = new ByteArrayContent(artwork.Bytes),
        };
        artworkRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(artwork.MediaType);
        AddBearer(artworkRequest, owner);
        using var artworkResponse = await relay.SendAsync(artworkRequest, cancellationToken).ConfigureAwait(false);
        if (artworkResponse.IsSuccessStatusCode)
        {
            _publishedArtworkSha = artwork.ContentSha256;
        }
    }

    /// <summary>
    /// Applies one command the desktop itself issues and delivers what it produced to every paired
    /// tablet: the desktop's own workspace changes, and its answers to a control request.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 24] Follow has to mean something: a tablet mirrors the desktop only if the
    /// desktop's own navigation reaches canonical state, which is what
    /// <c>UpdateDesktopWorkspaceCommand</c> is for and what nothing called. The deliveries come back
    /// through exactly the path a tablet's own command already uses.
    /// </remarks>
    public async Task<CommandDisposition> ApplyDesktopCommandAsync(
        CompanionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var application = await _authority.ApplyDesktopCommandAsync(command, Now(), cancellationToken).ConfigureAwait(false);
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

        return application.Acknowledgement.Disposition;
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
