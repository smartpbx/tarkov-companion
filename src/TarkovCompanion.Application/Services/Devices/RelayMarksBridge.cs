using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.Application.Services.Devices;

public sealed record DesktopCaptureIntentRequest(
    RequestCaptureIntentCommand Command,
    CompanionDeviceId DeviceId,
    string DeviceName);

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

    /// <summary>#289: who the tablet said the mark is for. Private stays private; anything else is the squad's.</summary>
    public RaidMarkScope Scope { get; init; } = RaidMarkScope.Squad;

    public RaidMarkLifetime Lifetime { get; init; } = RaidMarkLifetime.UntilRemoved;

    /// <summary>#290: the tablet route this waypoint is a stop on.</summary>
    public RaidMarkRoute? Route { get; init; }

    /// <summary>#290: the palette colour the tablet chose, or null for the kind's own.</summary>
    public string? Colour { get; init; }
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
    /// <returns>
    /// Whether the carrier took it. False is "nothing is carrying maps right now" (no relay, or
    /// not claimed yet), and a caller that compares publishes must not count it as one: the map
    /// that was current when the relay was finally claimed was otherwise never sent.
    /// </returns>
    ValueTask<bool> PublishMapSurfaceAsync(
        byte[] surfaceJson,
        TabletMapArtworkBytes? artwork,
        CancellationToken cancellationToken = default);
}

/// <summary>What this desktop knows about its owner session on the relay.</summary>
public enum RelayOwnerLinkState
{
    /// <summary>No owner session is held: never claimed from here, forgotten, or a different relay.</summary>
    None = 1,

    /// <summary>A stored session was picked up after a restart and the relay has not answered yet.</summary>
    Restored,

    /// <summary>The relay accepted this session on the last call.</summary>
    Verified,

    /// <summary>The relay refused this session: it expired or was replaced, and must be claimed again.</summary>
    Rejected,

    /// <summary>The relay could not be reached; the session is kept and tried again.</summary>
    Unreachable,
}

/// <summary>What registering a just-paired device on the relay came to.</summary>
/// <param name="Registered">Whether the relay now routes this device's traffic.</param>
/// <param name="Code">The relay's own refusal code, or a local reason, when it does not.</param>
/// <summary>A paired device that proved its key to the relay and is waiting for a fresh session.</summary>
public sealed record RelayResumeTicket(Guid TicketId, string DeviceKeyId);

public sealed record RelayDeviceRegistration(bool Registered, string Code)
{
    public static RelayDeviceRegistration Done { get; } = new(true, "registered");
}

public sealed partial class RelayMarksBridge : IAsyncDisposable, ITabletMapSurfaceSink
{
    /// <summary>How many sender sequences one write to protected storage reserves.</summary>
    internal const long SequenceBlock = 1024;

    private const string SessionHeader = "X-Relay-Session";
    private const string CredentialHeader = "X-Relay-Credential";
    private const string FramesHeldHeader = "X-Relay-Frames-Held";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FrameLifetime = TimeSpan.FromSeconds(30);

    private readonly DesktopCompanionAuthority _authority;
    private readonly IRaidMarkStore _marks;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, PairedSessionState> _sessionsById = new();
    private readonly Dictionary<Guid, Guid> _localMarkIdByCanonicalMarkId = new();
    private readonly RelayLinkVault? _vault;
    private readonly RelayClockOffsetTracker? _clockOffset;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private HttpClient? _relay;
    private Uri? _origin;
    private OwnerCredential? _owner;
    private CancellationTokenSource? _loop;
    private long _afterDeliveryId;
    private string? _publishedArtworkSha;
    private string? _refusedArtworkSha;
    private byte[]? _currentSurfaceJson;
    private TabletMapArtworkBytes? _currentArtwork;
    private RelayOwnerLinkState _ownerLink = RelayOwnerLinkState.None;
    private readonly HashSet<Guid> _resumeTicketsSeen = [];

    public RelayMarksBridge(
        DesktopCompanionAuthority authority,
        IRaidMarkStore marks,
        TimeProvider timeProvider,
        RelayLinkVault? vault = null,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        RelayClockOffsetTracker? clockOffset = null)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _marks = marks ?? throw new ArgumentNullException(nameof(marks));
        _clock = timeProvider ?? TimeProvider.System;
        _vault = vault;
        _clockOffset = clockOffset;
        Log = logger is null ? null : new RelayLinkLog(logger, _clock);
    }

    /// <summary>[#693] Where the relay link says what it did; null writes nothing.</summary>
    internal RelayLinkLog? Log { get; }

    /// <summary>
    /// Points the bridge at a relay. Naming a different relay than before drops the old one's
    /// owner session and routes, because both belong to the host that issued them.
    /// </summary>
    public void Configure(Uri relayOrigin)
    {
        ArgumentNullException.ThrowIfNull(relayOrigin);
        HttpClient? replaced = null;
        lock (_gate)
        {
            if (_origin is not null && _relay is not null &&
                string.Equals(RelayLinkVault.Normalize(_origin), RelayLinkVault.Normalize(relayOrigin), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            replaced = _relay;
            _origin = relayOrigin;
            _relay = _clockOffset is null
                ? new HttpClient()
                : new HttpClient(new RelayClockTrackingHandler(_clockOffset, _clock));
            _relay.BaseAddress = new Uri(relayOrigin.AbsoluteUri.TrimEnd('/') + "/");
            _owner = null;
            _afterDeliveryId = 0;
            _publishedArtworkSha = null;
        }

        replaced?.Dispose();
        SetOwnerLink(RelayOwnerLinkState.None);
    }

    /// <summary>The relay <see cref="Configure"/> last pointed this bridge at, or null before the first call.</summary>
    public Uri? ConfiguredOrigin
    {
        get
        {
            lock (_gate)
            {
                return _origin;
            }
        }
    }

    /// <summary>What is known about this desktop's owner session on the relay right now.</summary>
    public RelayOwnerLinkState OwnerLink
    {
        get
        {
            lock (_gate)
            {
                return _ownerLink;
            }
        }
    }

    /// <summary>Raised when <see cref="OwnerLink"/> changes, on whatever thread noticed.</summary>
    public event Action<RelayOwnerLinkState>? OwnerLinkChanged;

    /// <summary>
    /// How this desktop claims the relay again on its identity key, when it can. Asked whenever the
    /// relay refuses the kept owner session, before that session is given up on.
    /// </summary>
    /// <remarks>
    /// [#289] Set by whoever holds the identity signer (the pairing panel). Without it a refused
    /// session is dropped and the panel asks for the admin key, which is what every refusal used
    /// to come to: twelve hours after a claim, or two idle.
    /// </remarks>
    public Func<CancellationToken, Task<RelayClaimResult>>? OwnerReclaim { get; set; }

    /// <summary>
    /// A paired device came back to the relay on its key and is waiting for this desktop to open
    /// it a session. Raised from the poll, once per ticket.
    /// </summary>
    public event Action<RelayResumeTicket>? ResumeRequested;

    /// <summary>
    /// Picks the relay link back up after a desktop restart: the stored owner session, and the
    /// traffic keys of every paired session the authority still holds as active.
    /// </summary>
    /// <remarks>
    /// [#289, #290] Returns whether an owner session was found. It is not yet known to be good —
    /// the relay may have ended it — so the link reads <see cref="RelayOwnerLinkState.Restored"/>
    /// until the first poll answers, and <see cref="OwnerLinkChanged"/> says which way it went.
    /// </remarks>
    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        Uri? origin;
        lock (_gate)
        {
            origin = _origin;
        }

        if (_vault is null || origin is null)
        {
            return false;
        }

        var snapshot = _authority.Snapshot;
        var now = Now();
        foreach (var session in snapshot.Sessions.Where(item => item.Status == DeviceSessionStatus.Active && now < item.ExpiresUtc))
        {
            var stored = await _vault.LoadSessionAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
            if (stored is null || stored.DeviceId != session.DeviceId.Value || stored.KeyEpoch != session.KeyEpoch)
            {
                continue;
            }

            // Everything below the stored reservation may already have been sent, so this run
            // reserves its own block above it before it sends anything.
            var restored = new PairedSessionState(
                session.DeviceId,
                session.SessionId,
                new RelayChannelId(stored.ChannelId),
                stored.KeyEpoch,
                Convert.FromBase64String(stored.TabletToDesktopKeyBase64),
                Convert.FromBase64String(stored.DesktopToTabletKeyBase64),
                stored.ReservedThroughSequence);
            lock (_gate)
            {
                _sessionsById[session.SessionId.Value] = restored;
            }
        }

        var owner = await _vault.LoadOwnerAsync(snapshot.CanonicalState.DesktopDeviceId, origin, cancellationToken)
            .ConfigureAwait(false);
        if (owner is null)
        {
            return false;
        }

        lock (_gate)
        {
            _owner = new OwnerCredential(owner.SessionId, owner.Credential, owner.ExpiresUtc);
        }

        SetOwnerLink(RelayOwnerLinkState.Restored);
        EnsureLoopStarted();
        ScheduleMarkExpiry();
        return true;
    }

    /// <summary>Adopts a fresh claim and keeps it for the next restart.</summary>
    public async Task AdoptOwnerCredentialAsync(
        Guid sessionId,
        string credential,
        DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default)
    {
        SetOwnerCredential(sessionId, credential, expiresUtc);
        Uri? origin;
        lock (_gate)
        {
            origin = _origin;
        }

        if (_vault is not null && origin is not null)
        {
            await _vault.SaveOwnerAsync(
                _authority.Snapshot.CanonicalState.DesktopDeviceId,
                new StoredRelayOwnerSession(RelayLinkVault.Normalize(origin), sessionId, credential, expiresUtc),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>"Forget this relay": drops the owner session here and in protected storage.</summary>
    public async Task ForgetOwnerAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _owner = null;
        }

        if (_vault is not null)
        {
            await _vault.ForgetOwnerAsync(_authority.Snapshot.CanonicalState.DesktopDeviceId, cancellationToken)
                .ConfigureAwait(false);
        }

        SetOwnerLink(RelayOwnerLinkState.None);
    }

    /// <summary>
    /// Cuts a revoked device off: its keys leave this process and protected storage, and the relay
    /// is told to end its session so it stops reading the map as well.
    /// </summary>
    public async Task RevokePairedDeviceAsync(CompanionDeviceId deviceId, CancellationToken cancellationToken = default)
    {
        PairedSessionState[] removed;
        HttpClient? relay;
        OwnerCredential? owner;
        lock (_gate)
        {
            removed = _sessionsById.Values.Where(state => state.DeviceId == deviceId).ToArray();
            foreach (var state in removed)
            {
                _sessionsById.Remove(state.SessionId.Value);
            }

            relay = _relay;
            owner = _owner;
        }

        foreach (var state in removed)
        {
            state.Clear();
            if (_vault is not null)
            {
                await _vault.ForgetSessionAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
            }
        }

        if (relay is null || owner is null)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"v2/companion/relay/devices/{deviceId.Value:D}/revoke");
            AddBearer(request, owner);
            using var response = await relay.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _ = response; // a relay that cannot be reached still loses the device's keys above
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The desktop's own authority has already revoked it; nothing it sends is opened.
        }
    }

    private void SetOwnerLink(RelayOwnerLinkState next)
    {
        lock (_gate)
        {
            if (_ownerLink == next)
            {
                return;
            }

            _ownerLink = next;
        }

        Log?.Write("owner-link:" + next, next switch
        {
            RelayOwnerLinkState.Verified => "owner session verified; this desktop is on the relay.",
            RelayOwnerLinkState.Rejected => "owner session refused and not regained; this desktop is off the relay until it registers again.",
            RelayOwnerLinkState.Unreachable => "relay unreachable; the owner session is kept and retried.",
            RelayOwnerLinkState.Restored => "owner session restored from the last run; checking it.",
            _ => "no owner session.",
        });
        OwnerLinkChanged?.Invoke(next);
    }

    /// <summary>Called once "Claim this relay" succeeds, so the poll loop can start reading this desktop's own queue.</summary>
    public void SetOwnerCredential(Guid sessionId, string credential, DateTimeOffset expiresUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        lock (_gate)
        {
            if (_owner?.SessionId != sessionId)
            {
                // The relay numbers deliveries per session, from one. A cursor carried over from
                // the session this replaces would read past everything the new one is sent.
                _afterDeliveryId = 0;
            }

            _owner = new OwnerCredential(sessionId, credential, expiresUtc);
        }

        SetOwnerLink(RelayOwnerLinkState.Verified);
        EnsureLoopStarted();
    }

    /// <summary>
    /// Answers a returning device's ticket with the code of the offer just opened for it, as this
    /// relay's owner. False when the relay would not take it (the ticket lapsed, or no claim).
    /// </summary>
    public async Task<bool> AnswerResumeTicketAsync(Guid ticketId, string pairingCode, CancellationToken cancellationToken = default)
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
            Log?.Write("resume-answer:no-owner", "resume ticket not answered: no owner session.", Microsoft.Extensions.Logging.LogLevel.Warning);
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"v2/companion/relay/resume/requests/{ticketId:D}/offer");
        request.Headers.Add("Tarkov-Pairing-Code", pairingCode);
        AddBearer(request, owner);
        using var response = await relay.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            Log?.Write("resume-answer:posted", "resume answer posted.");
            return true;
        }

        Log?.Write(
            "resume-answer:failed",
            $"resume answer refused by the relay: HTTP {(int)response.StatusCode}.",
            Microsoft.Extensions.Logging.LogLevel.Warning);
        return false;
    }

    /// <summary>
    /// [#846] Tells a returning device, through the relay, that this desktop will not let it back
    /// in, so its page shows the code form at once instead of timing out. <paramref name="reason"/>
    /// is <c>not-recognised</c> or <c>failed</c>. False when the relay would not take it: the
    /// ticket lapsed, no owner session, or a relay from before #846 (404), where the tablet still
    /// times out as it always did.
    /// </summary>
    public async Task<bool> RefuseResumeTicketAsync(Guid ticketId, string reason, CancellationToken cancellationToken = default)
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
            Log?.Write("resume-refusal:no-owner", "resume refusal not sent: no owner session.", Microsoft.Extensions.Logging.LogLevel.Warning);
            return false;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"v2/companion/relay/resume/requests/{ticketId:D}/refusal?reason={Uri.EscapeDataString(reason)}");
        AddBearer(request, owner);
        using var response = await relay.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            Log?.Write("resume-refusal:posted", $"resume refusal posted ({reason}).");
            return true;
        }

        Log?.Write(
            "resume-refusal:failed",
            $"resume refusal refused by the relay: HTTP {(int)response.StatusCode}.",
            Microsoft.Extensions.Logging.LogLevel.Warning);
        return false;
    }

    /// <summary>
    /// Registers a just-completed tablet pairing on the relay (so the hub can route its traffic),
    /// then immediately delivers the canonical snapshot <c>DesktopCompanionAuthority.RegisterPairingAsync</c>
    /// already queued for it locally.
    /// </summary>
    public async Task<RelayDeviceRegistration> RegisterPairedDeviceAsync(
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
            return new RelayDeviceRegistration(false, "relay-not-claimed");
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
            var refusal = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            refusal = refusal.Trim().Trim('"');
            Log?.Write(
                "device-register:failed",
                $"paired device not registered on the relay: HTTP {(int)response.StatusCode}" +
                    (refusal.Length is > 0 and <= 64 ? $" ({refusal})." : "."),
                Microsoft.Extensions.Logging.LogLevel.Warning);
            return new RelayDeviceRegistration(
                false,
                refusal.Length is > 0 and <= 64 ? refusal : $"relay-{(int)response.StatusCode}");
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
            session.DesktopToTabletKey.ToArray(),
            reservedThroughSequence: 0);
        PairedSessionState[] superseded;
        lock (_gate)
        {
            // The same tablet pairing again replaces its old device on the authority, so whatever
            // this bridge still holds for a session the authority no longer has is dead weight.
            var active = _authority.Snapshot.Sessions
                .Where(item => item.Status == DeviceSessionStatus.Active)
                .Select(item => item.SessionId.Value)
                .ToHashSet();
            superseded = _sessionsById.Values.Where(item => !active.Contains(item.SessionId.Value)).ToArray();
            foreach (var old in superseded)
            {
                _sessionsById.Remove(old.SessionId.Value);
            }

            _sessionsById[assignment.SessionId.Value] = state;
        }

        foreach (var old in superseded)
        {
            old.Clear();
            if (_vault is not null)
            {
                await _vault.ForgetSessionAsync(old.SessionId, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var delivery in session.Mutation.Deliveries)
        {
            await PublishDeliveryAsync(state, delivery, _authority.Snapshot.CanonicalState, cancellationToken).ConfigureAwait(false);
        }

        Log?.Write("device-register:done", "paired device registered on the relay.");
        // [#693] With no tablet, the map was kept here rather than sent; this one needs it now.
        byte[]? heldSurface;
        TabletMapArtworkBytes? heldArtwork;
        lock (_gate)
        {
            heldSurface = _currentSurfaceJson;
            heldArtwork = _currentArtwork;
        }

        if (heldSurface is not null)
        {
            await UploadMapAsync(relay, owner, heldSurface, heldArtwork, cancellationToken).ConfigureAwait(false);
        }

        return RelayDeviceRegistration.Done;
    }

    private sealed record RelaySessionCredentialWire(Guid SessionId, Guid ChannelId, string Credential, string CsrfToken, DateTimeOffset ExpiresUtc);

    /// <summary>
    /// Whether this bridge reads the relay on its own once it has an owner session. On in the app.
    /// </summary>
    /// <remarks>
    /// [#604] Its reads are held and follow each other at once, so a test that needs the desktop
    /// to stop reading for a while (to overflow its queue) cannot wait out a poll interval any
    /// more; it turns this off and reads with <see cref="PollOnceAsync"/> itself.
    /// </remarks>
    public bool ReadsOnItsOwn { get; set; } = true;

    private void EnsureLoopStarted()
    {
        if (!ReadsOnItsOwn)
        {
            return;
        }

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

    /// <summary>How long one of the loop's reads asks the relay to hold, within its own bound.</summary>
    private static readonly TimeSpan HeldReadWait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The least time between two held reads that came back with nothing, so a relay that keeps
    /// answering at once (a resume ticket, a reconnect) is never read in a tight loop.
    /// </summary>
    private static readonly TimeSpan EmptyReadFloor = TimeSpan.FromMilliseconds(250);

    /// <remarks>
    /// [#604] Each read is held by the relay until a frame is queued, and the next one is sent the
    /// moment it comes back. This used to read, then sleep two seconds: a tablet in Control moved
    /// the desk in two-second jumps. A relay from before the hold answers at once and sends no
    /// <c>X-Relay-Frames-Held</c>, and this falls back to the old two-second rhythm against it.
    /// </remarks>
    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = PollInterval;
            var started = _clock.GetTimestamp();
            try
            {
                var read = await PollAsync(HeldReadWait, cancellationToken).ConfigureAwait(false);
                if (read.Held)
                {
                    delay = read.Frames > 0 || _clock.GetElapsedTime(started) >= EmptyReadFloor
                        ? TimeSpan.Zero
                        : EmptyReadFloor;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Transient; the next tick tries again. The owner session is kept: a relay that
                // is down for a minute has not un-claimed anything.
                if (exception is not JsonException && !cancellationToken.IsCancellationRequested && HasOwner())
                {
                    SetOwnerLink(RelayOwnerLinkState.Unreachable);
                }
            }

            if (delay <= TimeSpan.Zero)
            {
                continue;
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Reads and handles whatever is queued for this desktop, once, without holding.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken) =>
        await PollAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);

    private readonly record struct FrameRead(bool Held, int Frames);

    /// <remarks>
    /// The request itself is sent outside <see cref="_pollGate"/>, because a held read can take
    /// twenty seconds and a direct <see cref="PollOnceAsync"/> must not wait behind it. Handling
    /// is inside it, one at a time, and skips any frame another read has already handled: the
    /// loop and a direct caller share the delivery cursor.
    /// </remarks>
    private async Task<FrameRead> PollAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        await ExpireDueMarksAsync(cancellationToken).ConfigureAwait(false);
        HttpClient? relay;
        OwnerCredential? owner;
        lock (_gate)
        {
            relay = _relay;
            owner = _owner;
        }

        if (relay is null || owner is null)
        {
            return default;
        }

        var after = Interlocked.Read(ref _afterDeliveryId);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"v2/companion/relay/frames?after={after}&wait={wait.TotalSeconds:0.###}"));
        AddBearer(request, owner);
        using var response = await relay.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await HandleFrameReadAsync(relay, owner, after, response, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task<FrameRead> HandleFrameReadAsync(
        HttpClient relay,
        OwnerCredential owner,
        long after,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var held = response.Headers.Contains(FramesHeldHeader);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Refused a session this desktop has already moved on from. The loop's held read and a
            // direct poll both go out on the credential of the moment; when the relay refuses the
            // old one, the first to be handled reclaims, and the second used to reclaim again —
            // replacing the session the first had just adopted, so the next call on it (a device
            // revoke, say) was refused too, and a revoke that is refused is not retried. The path
            // found behind a revoked tablet resuming once on #783's gate (RelayLinkNextDayTests).
            // Only a refusal of the session still held here says anything about the claim.
            lock (_gate)
            {
                if (!ReferenceEquals(_owner, owner))
                {
                    return default;
                }
            }

            // [#289] The relay has ended this owner session: it expired (twelve hours, or two
            // idle), or another claim replaced it. The first is every morning, so before giving
            // the claim up this desktop asks to be let back in on its key.
            if (OwnerReclaim is { } reclaim)
            {
                var reclaimed = await reclaim(cancellationToken).ConfigureAwait(false);
                Log?.Write(
                    "owner-reclaim:" + reclaimed.Outcome,
                    $"owner session ended by the relay (HTTP 401); asked again on this desktop's key: {reclaimed.Outcome}" +
                        (reclaimed.Code is { } reclaimCode ? $" ({reclaimCode})." : "."),
                    reclaimed.Outcome == RelayClaimOutcome.Claimed
                        ? Microsoft.Extensions.Logging.LogLevel.Information
                        : Microsoft.Extensions.Logging.LogLevel.Warning);
                if (reclaimed.Outcome == RelayClaimOutcome.Claimed)
                {
                    return default; // adopted; the next read is on the new session
                }

                if (reclaimed.Outcome is RelayClaimOutcome.Unreachable or RelayClaimOutcome.RateLimited)
                {
                    SetOwnerLink(RelayOwnerLinkState.Unreachable);
                    return default; // kept, and asked again on the next tick
                }
            }

            // Holding on to it would only repeat the refusal every two seconds and keep telling
            // the player the relay is claimed when it is not.
            var dropped = false;
            lock (_gate)
            {
                if (ReferenceEquals(_owner, owner))
                {
                    _owner = null;
                    dropped = true;
                }
            }

            if (dropped)
            {
                if (_vault is not null)
                {
                    await _vault.ForgetOwnerAsync(_authority.Snapshot.CanonicalState.DesktopDeviceId, cancellationToken)
                        .ConfigureAwait(false);
                }

                SetOwnerLink(RelayOwnerLinkState.Rejected);
            }

            return default;
        }

        if (!response.IsSuccessStatusCode)
        {
            return default;
        }

        SetOwnerLink(RelayOwnerLinkState.Verified);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var frames = ParseFrameBatch(json);
        foreach (var ticket in ParseResumeRequests(json))
        {
            bool first;
            lock (_gate)
            {
                first = _resumeTicketsSeen.Add(ticket.TicketId);
                if (_resumeTicketsSeen.Count > 256)
                {
                    _resumeTicketsSeen.Clear(); // tickets live five minutes; this is only a de-duplicator
                    _resumeTicketsSeen.Add(ticket.TicketId);
                }
            }

            if (first)
            {
                Log?.Write("resume-ticket", "a paired device asked to come back; resume ticket seen.");
                ResumeRequested?.Invoke(ticket);
            }
        }

        var handled = 0;
        foreach (var (deliveryId, frame) in frames)
        {
            // Another read (the loop's, or a direct PollOnceAsync) may have handled it already.
            if (deliveryId <= _afterDeliveryId)
            {
                continue;
            }

            if (frame is not null)
            {
                await HandleInboundFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _afterDeliveryId, Math.Max(_afterDeliveryId, deliveryId));
            handled++;
        }

        // [#604] A batch of Control moves is one move for the desktop's map: only the last one
        // is where the tablet's finger now is, and handing the desk every step in between would
        // have it replay the drag it has already fallen behind on.
        RaisePendingDesktopWorkspace();

        if (handled > 0 && !held)
        {
            // An older relay. A held read acknowledges through its own `after` on the next read,
            // so this round trip is only spent where the relay cannot do that.
            using var ack = new HttpRequestMessage(HttpMethod.Post, $"v2/companion/relay/frames/{_afterDeliveryId}/ack");
            AddBearer(ack, owner);
            using var ackResponse = await relay.SendAsync(ack, cancellationToken).ConfigureAwait(false);
            _ = ackResponse; // best-effort; an unacknowledged delivery is simply re-read next poll
        }

        var status = ParseBatchStatus(json);
        // A read that started before another one moved the cursor on is behind by construction,
        // and the relay says "reconnect" to it; that is not a gap worth resetting the queue over.
        if (status.RequiresReconnect && frames.Count == 0 && after == _afterDeliveryId)
        {
            // [#407] The relay dropped something from this queue (or restarted and lost it). What
            // a tablet sent and the relay dropped is gone either way, and each tablet's own resync
            // heals its side; what is left to do here is tell the relay the gap has been seen, or
            // it goes on saying "reconnect" on every read for as long as this session lives.
            using var reset = new HttpRequestMessage(HttpMethod.Post, "v2/companion/relay/frames/reset");
            AddBearer(reset, owner);
            using var resetResponse = await relay.SendAsync(reset, cancellationToken).ConfigureAwait(false);
            if (resetResponse.IsSuccessStatusCode)
            {
                var resetJson = await resetResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _afterDeliveryId, ParseResetCursor(resetJson));
            }
        }

        await ReconcileMapAsync(relay, owner, status.Map, cancellationToken).ConfigureAwait(false);
        return new FrameRead(held, handled);
    }

    private bool HasOwner()
    {
        lock (_gate)
        {
            return _owner is not null;
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

        var authenticatedFrame = new AuthenticatedPairedFrame(
            frame.SessionId,
            frame.KeyEpoch,
            "relay-marks-bridge",
            Now());
        if (payload.Kind == RelayPayloadKind.ReconnectRequest)
        {
            await AnswerReconnectAsync(state, authenticatedFrame, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (payload.Kind != RelayPayloadKind.ClientCommandEnvelope)
        {
            return;
        }

        var command = CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(payload.Json.Span);
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
        var controlMove = application.Acknowledgement.Disposition == CommandDisposition.Applied &&
            command.Command is ControlWorkspaceCommand;
        if (controlMove)
        {
            _pendingDesktopWorkspace = application.State.CanonicalState.Workspace.Projection;
        }

        // #271: capture requests used to stop in canonical state. As with a Control workspace
        // move, only an Applied command crosses this seam; capability, session, revision and
        // payload validation have already happened in DesktopCompanionAuthority.
        if (application.Acknowledgement.Disposition == CommandDisposition.Applied &&
            command.Command is RequestCaptureIntentCommand capture)
        {
            var deviceName = application.State.Devices
                .FirstOrDefault(device => device.DeviceId == state.DeviceId)?.DisplayName ?? "Paired tablet";
            DesktopCaptureIntentRequested?.Invoke(new(capture, state.DeviceId, deviceName));
        }

        CanonicalStateChanged?.Invoke(application.State.CanonicalState);
        ScheduleMarkExpiry();

        // [#604] Queued, not awaited: the next frame in the batch (usually the next move of the
        // same drag) is handled while these are still on their way, in order.
        foreach (var delivery in application.Deliveries)
        {
            PairedSessionState? target;
            lock (_gate)
            {
                target = _sessionsById.Values.FirstOrDefault(candidate => candidate.DeviceId == delivery.DeviceId);
            }

            if (target is not null)
            {
                QueueDelivery(target, delivery, application.State.CanonicalState, controlMove);
            }
        }
    }

    /// <summary>
    /// A tablet that lost its memory (a page reload, a browser restart) or fell behind asks where
    /// canonical state now is, and is told — the protocol's own reconnect path, which had a planner
    /// and nothing that called it.
    /// </summary>
    /// <remarks>
    /// [#290] It doubles as the tablet's sign of life. The authority expires a device it has not
    /// heard from, and a tablet that only follows never sends a command, so without this a tablet
    /// left on the desk would be dropped mid-session for saying nothing.
    /// </remarks>
    private async Task AnswerReconnectAsync(
        PairedSessionState state,
        AuthenticatedPairedFrame authenticatedFrame,
        RelayPayload payload,
        CancellationToken cancellationToken)
    {
        ReconnectApplication application;
        try
        {
            var request = CompanionProtocolJson.Deserialize<ReconnectRequest>(payload.Json.Span);
            application = await _authority.PlanReconnectAsync(authenticatedFrame, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return;
        }

        await PublishSealedAsync(
            state,
            RelayPayloadKind.ReconnectPlan,
            CompanionProtocolJson.Serialize(application.Plan),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Raised after a paired device in Control mode has moved canonical workspace state, with the
    /// projection the desktop must now be showing.
    /// </summary>
    public event Action<WorkspaceProjection>? DesktopWorkspaceRequested;

    /// <summary>
    /// Raised only after the authority applies a paired device's capture request, so the desktop
    /// can arm its local capture coordinator through the same presentation path as its own panel.
    /// </summary>
    public event Action<DesktopCaptureIntentRequest>? DesktopCaptureIntentRequested;

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
    public async ValueTask<bool> PublishMapSurfaceAsync(
        byte[] surfaceJson,
        TabletMapArtworkBytes? artwork,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(surfaceJson);
        HttpClient? relay;
        OwnerCredential? owner;
        bool noTablet;
        lock (_gate)
        {
            // Kept whether or not it can be sent now. What the relay holds is reconciled against
            // this on every read of the desktop's queue (ReconcileMapAsync), which is how a map
            // published before the claim, or lost to a relay restart, still reaches the tablets.
            _currentSurfaceJson = surfaceJson;
            _currentArtwork = artwork;
            relay = _relay;
            owner = _owner;
            noTablet = _sessionsById.Count == 0;
        }

        if (relay is null || owner is null)
        {
            return false;
        }

        if (noTablet)
        {
            // [#693] Nothing on the relay reads this desktop's map until a tablet is paired, and a
            // raid republishes it several times a second: three desktops with no tablet between
            // them were sending the relay eight surfaces a second, up to 400 KB each. It is kept
            // above and sent when a tablet registers (RegisterPairedDeviceAsync).
            return true;
        }

        return await UploadMapAsync(relay, owner, surfaceJson, artwork, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> UploadMapAsync(
        HttpClient relay,
        OwnerCredential owner,
        byte[] surfaceJson,
        TabletMapArtworkBytes? artwork,
        CancellationToken cancellationToken)
    {
        using var surfaceRequest = new HttpRequestMessage(HttpMethod.Post, "v2/companion/relay/map")
        {
            Content = new ByteArrayContent(surfaceJson),
        };
        surfaceRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        AddBearer(surfaceRequest, owner);
        using var surfaceResponse = await relay.SendAsync(surfaceRequest, cancellationToken).ConfigureAwait(false);
        if (!surfaceResponse.IsSuccessStatusCode)
        {
            return false;
        }

        if (artwork is null ||
            string.Equals(_publishedArtworkSha, artwork.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        await UploadArtworkAsync(relay, owner, artwork, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task UploadArtworkAsync(
        HttpClient relay,
        OwnerCredential owner,
        TabletMapArtworkBytes artwork,
        CancellationToken cancellationToken)
    {
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
            _refusedArtworkSha = null;
        }
        else if ((int)artworkResponse.StatusCode is >= 400 and < 500)
        {
            // The relay will not take this picture (too large, a type it does not serve). Offering
            // it again on every read would be megabytes every two seconds for the same answer.
            _refusedArtworkSha = artwork.ContentSha256;
        }
    }

    /// <summary>
    /// Makes what the relay holds match what this desktop last published, from what the relay
    /// says it holds on each read of the desktop's queue.
    /// </summary>
    /// <remarks>
    /// [#407] The relay keeps the map in memory, and this desktop uploads only on change. So a
    /// relay restart left every tablet with no map (and, once a scene change brought the scene
    /// back, no picture behind it, because the picture's hash had not changed here) until the
    /// player happened to change maps. The same hole swallowed a map published before the relay
    /// was claimed. An older relay says nothing about what it holds, and nothing is done.
    /// </remarks>
    private async Task ReconcileMapAsync(HttpClient relay, OwnerCredential owner, HeldMapWire? held, CancellationToken cancellationToken)
    {
        byte[]? surface;
        TabletMapArtworkBytes? artwork;
        bool noTablet;
        lock (_gate)
        {
            surface = _currentSurfaceJson;
            artwork = _currentArtwork;
            noTablet = _sessionsById.Count == 0;
        }

        if (held is null || surface is null || noTablet)
        {
            return;
        }

        if (!held.Held)
        {
            _publishedArtworkSha = null;
            await UploadMapAsync(relay, owner, surface, artwork, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (artwork is not null &&
            !string.Equals(held.ArtworkSha256, artwork.ContentSha256, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_refusedArtworkSha, artwork.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            await UploadArtworkAsync(relay, owner, artwork, cancellationToken).ConfigureAwait(false);
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

        ScheduleMarkExpiry();
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
                var created = await _marks.PlaceAsync(
                        action.MapId,
                        action.FloorId,
                        action.X,
                        action.Y,
                        action.Label,
                        action.Scope,
                        action.Lifetime,
                        cancellationToken,
                        action.Route,
                        action.Colour)
                    .ConfigureAwait(false);
                lock (_gate)
                {
                    _localMarkIdByCanonicalMarkId[canonicalMarkId] = created.Id;
                }

                break;
            }

            case MarkReconciliationKind.Update when localId is { } editId:
                // Options first: narrowing to "Just me" must land before a move would resend it.
                if (_marks.Marks.FirstOrDefault(mark => mark.Id == editId) is { } existing &&
                    (existing.Scope != action.Scope || existing.Lifetime != action.Lifetime))
                {
                    await _marks.SetOptionsAsync(editId, action.Scope, action.Lifetime, cancellationToken).ConfigureAwait(false);
                }

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

    /// <summary>
    /// The lifetime a tablet chose, or the one its kind and expiry imply when it predates the
    /// choice: a ping, a waypoint that stays, or a waypoint ending in about five or fifteen minutes.
    /// </summary>
    internal static RaidMarkLifetime LocalLifetime(MapMarkDraft mark, DateTimeOffset issuedUtc)
    {
        if (mark.Lifetime is { } chosen)
        {
            return chosen switch
            {
                MapMarkLifetime.Ping => RaidMarkLifetime.Ping,
                MapMarkLifetime.FiveMinutes => RaidMarkLifetime.FiveMinutes,
                MapMarkLifetime.FifteenMinutes => RaidMarkLifetime.FifteenMinutes,
                MapMarkLifetime.ThisRaid => RaidMarkLifetime.ThisRaid,
                _ => RaidMarkLifetime.UntilRemoved,
            };
        }

        if (mark.Kind == MapMarkKind.Ping)
        {
            return RaidMarkLifetime.Ping;
        }

        return mark.State.ExpiresUtc is not { } expires ? RaidMarkLifetime.UntilRemoved
            : expires - issuedUtc <= TimeSpan.FromMinutes(10) ? RaidMarkLifetime.FiveMinutes
            : RaidMarkLifetime.FifteenMinutes;
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
                    upsert.Mark.State.Label)
                {
                    Scope = upsert.Mark.Scope == MapMarkScope.Private ? RaidMarkScope.Private : RaidMarkScope.Squad,
                    Lifetime = LocalLifetime(upsert.Mark, upsert.IssuedUtc),
                    Route = upsert.Mark.RouteId is { } routeId && upsert.Mark.RouteStep is { } step ? new RaidMarkRoute(routeId, step) : null,
                    // #290: every tablet sends a colour, and only a palette one was chosen; the
                    // fixed ping/waypoint colours older pages send mean "the kind's own".
                    Colour = TarkovCompanion.Core.Common.MarkPalette.Normalize(upsert.Mark.Color),
                };

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

        var message = delivery.Item.Resolve(current);
        var serverEnvelope = new ServerEnvelope(
            CompanionProtocolVersion.Current,
            state.SessionId,
            current.DesktopDeviceId,
            Now(),
            delivery.Item.Sequence,
            message);
        await PublishSealedAsync(
            state,
            RelayPayloadKind.ServerEnvelope,
            CompanionProtocolJson.Serialize(serverEnvelope),
            cancellationToken).ConfigureAwait(false);
    }

    private Task PublishSealedAsync(
        PairedSessionState state,
        RelayPayloadKind kind,
        byte[] json,
        CancellationToken cancellationToken) =>
        EnqueueSealed(state, kind, json, supersedeKey: null).WaitAsync(cancellationToken);

    /// <summary>Seals and posts one frame now. Only the outbound queue calls this, one at a time.</summary>
    private async Task SendSealedNowAsync(
        PairedSessionState state,
        RelayPayloadKind kind,
        byte[] json,
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
        var senderSequence = await state.NextSenderSequenceAsync(_vault, cancellationToken).ConfigureAwait(false);
        var frame = PairingCryptography.SealRelayFrame(
            state.DesktopToTabletKey,
            PairingTrafficDirection.DesktopToTablet,
            kind,
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

        StopMarkExpiry();

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
        byte[] desktopToTabletKey,
        long reservedThroughSequence)
    {
        private readonly SemaphoreSlim _sequenceGate = new(1, 1);

        // Starts at the reservation rather than below it: whatever a previous run reserved it may
        // have used, and a sender sequence is this session's AES-GCM nonce.
        private long _senderSequence = reservedThroughSequence;
        private long _reservedThrough = reservedThroughSequence;

        public CompanionDeviceId DeviceId { get; } = deviceId;

        public DeviceSessionId SessionId { get; } = sessionId;

        public RelayChannelId ChannelId { get; } = channelId;

        public long KeyEpoch { get; } = keyEpoch;

        public byte[] TabletToDesktopKey { get; } = tabletToDesktopKey;

        public byte[] DesktopToTabletKey { get; } = desktopToTabletKey;

        /// <summary>
        /// The next sender sequence, reserving another block in protected storage first whenever
        /// the last one is used up — so the reservation on disk is always ahead of what was sent.
        /// </summary>
        public async ValueTask<long> NextSenderSequenceAsync(RelayLinkVault? vault, CancellationToken cancellationToken)
        {
            await _sequenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_senderSequence >= _reservedThrough)
                {
                    var reserved = _reservedThrough + SequenceBlock;
                    if (vault is not null)
                    {
                        await vault.SaveSessionAsync(
                            new StoredPairedSession(
                                DeviceId.Value,
                                SessionId.Value,
                                ChannelId.Value,
                                KeyEpoch,
                                Convert.ToBase64String(TabletToDesktopKey),
                                Convert.ToBase64String(DesktopToTabletKey),
                                reserved),
                            cancellationToken).ConfigureAwait(false);
                    }

                    _reservedThrough = reserved;
                }

                return ++_senderSequence;
            }
            finally
            {
                _sequenceGate.Release();
            }
        }

        /// <summary>Zeroes the traffic keys of a session that has been revoked or replaced.</summary>
        public void Clear()
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(TabletToDesktopKey);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(DesktopToTabletKey);
        }
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

    /// <summary>The returning devices a frame batch names; none from a relay that predates them.</summary>
    public static IReadOnlyList<RelayResumeTicket> ParseResumeRequests(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RelayResumeBatchWire>(json, WireJsonOptions)?.ResumeRequests is { } waiting
                ? waiting.Where(item => item.TicketId != Guid.Empty && !string.IsNullOrEmpty(item.DeviceKeyId)).ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record RelayResumeBatchWire(IReadOnlyList<RelayResumeTicket>? ResumeRequests);

    private static BatchStatusWire ParseBatchStatus(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BatchStatusWire>(json, WireJsonOptions) ?? new BatchStatusWire(false, null);
        }
        catch (JsonException)
        {
            return new BatchStatusWire(false, null);
        }
    }

    private static long ParseResetCursor(string json)
    {
        try
        {
            return Math.Max(0, JsonSerializer.Deserialize<FrameResetWire>(json, WireJsonOptions)?.After ?? 0);
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private sealed record FrameResetWire(long After);

    private sealed record BatchStatusWire(bool RequiresReconnect, HeldMapWire? Map);

    private sealed record HeldMapWire(bool Held, long Revision, string? ArtworkSha256);

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
