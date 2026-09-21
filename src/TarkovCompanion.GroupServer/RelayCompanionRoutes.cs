using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.StateSync;
using TarkovCompanion.GroupServer.Storage;
using TarkovCompanion.GroupServer.Tenancy;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// A completed pairing a caller presents over HTTP: the same four pieces
/// <see cref="RelayDeviceRegistry.RecoverOwnerAsync"/> and <c>AddPairedDeviceAsync</c> both need to
/// build a <see cref="PairingAttempt"/>. Not a <c>CompanionProtocolJson</c> wire root itself — each
/// of its four fields already is one, and is read through that same safe path individually, so
/// adding this admin/owner-only envelope never touches the paired-device schema or its golden
/// fixtures.
/// </summary>
public sealed record RelayDeviceClaim(
    PairingOffer Offer,
    string DesktopNonceBase64Url,
    DateTimeOffset CodeConsumedUtc,
    PairingRequest Request,
    HandshakeChallenge Challenge,
    SessionEstablished Establishment)
{
    public PairingAttempt ToCompletedAttempt() => new(
        Offer,
        DesktopNonceBase64Url,
        PairingAttemptStage.Completed,
        CodeConsumedUtc,
        Request,
        Challenge,
        Establishment,
        Establishment.EstablishedUtc);
}

public sealed record RelayOwnerStatus(bool Claimed, string? OwnerDeviceId);

public sealed record RelaySessionCredentialResponse(
    Guid SessionId,
    Guid ChannelId,
    string Credential,
    string CsrfToken,
    DateTimeOffset ExpiresUtc)
{
    public static RelaySessionCredentialResponse From(RelaySessionCredential credential) => new(
        credential.SessionId.Value,
        credential.ChannelId.Value,
        credential.Secret,
        credential.CsrfToken,
        credential.ExpiresUtc);
}

/// <param name="ResumeRequests">
/// [#289] Only ever filled for the owner: paired devices that have proved their key and are
/// waiting for this desktop to open them a fresh session. It rides on the read the desktop already
/// makes every two seconds rather than costing a second one. An older desktop ignores it.
/// </param>
public sealed record RelayFrameBatchResponse(
    int ProtocolVersion,
    bool RequiresReconnect,
    DateTimeOffset ServerUtc,
    IReadOnlyList<RelayFrameEnvelope> Frames,
    IReadOnlyList<RelayResumeRequest>? ResumeRequests = null,
    RelayHeldMap? Map = null)
{
    public static RelayFrameBatchResponse From(
        RelayFrameBatch batch,
        IReadOnlyList<RelayResumeRequest>? resumeRequests = null,
        RelayHeldMap? map = null) => new(
        batch.ProtocolVersion.Major,
        batch.RequiresReconnect,
        batch.ServerUtc,
        batch.Frames.Select(RelayFrameEnvelope.From).ToArray(),
        resumeRequests is { Count: > 0 } ? resumeRequests : null,
        map);
}

/// <summary>Where a reader's cursor belongs after it asked the relay to clear a broken queue.</summary>
public sealed record RelayFrameResetResponse(long After);

/// <summary>A returning paired device waiting on its desktop, as the owner is told about it.</summary>
public sealed record RelayResumeRequest(Guid TicketId, string DeviceKeyId);

/// <summary>What the relay hands a caller to sign: see <see cref="RelayPossessionChallenges"/>.</summary>
public sealed record RelayPossessionChallengeResponse(Guid ChallengeId, string NonceBase64Url, DateTimeOffset ExpiresUtc);

public sealed record RelayResumeTicketResponse(Guid TicketId, DateTimeOffset ExpiresUtc, string? PairingCode);

/// <summary>
/// <see cref="Frame"/> is embedded as the exact bytes <see cref="CompanionProtocolJson"/> writes for
/// an <see cref="OpaqueRelayFrame"/> — not re-serialized by the default request JSON options this
/// envelope's own fields use, which would flatten none of its nested identifier types the way the
/// paired-device wire format requires. A caller reads it back with
/// <c>CompanionProtocolJson.Deserialize&lt;OpaqueRelayFrame&gt;(frame.GetRawText())</c>.
/// </summary>
public sealed record RelayFrameEnvelope(long DeliveryId, JsonElement Frame)
{
    public static RelayFrameEnvelope From(RelayQueuedFrame queued) =>
        new(queued.DeliveryId, JsonDocument.Parse(CompanionProtocolJson.Serialize(queued.Frame)).RootElement.Clone());
}

/// <summary>Bounded per-source and relay-wide admission for the admin-key owner-claim route.</summary>
/// <remarks>
/// Mirrors <c>CompanionPairingMailbox</c>'s own per-source rate limit (ABUSE-ADMIN-KEY-GUESS): a
/// digest of the caller's address, never the raw address, and a second relay-wide limiter so a
/// botnet spraying distinct source addresses still cannot run many admin-key guesses in aggregate.
/// </remarks>
public sealed class RelayOwnerClaimGate
{
    private const string GlobalPartition = "relay-owner-claim";
    private readonly RelayRateLimiter _perSource;
    private readonly RelayRateLimiter _global;
    private readonly byte[] _sourceHashKey = RandomNumberGenerator.GetBytes(32);

    public RelayOwnerClaimGate(TimeProvider timeProvider)
        : this(timeProvider, perSourceLimit: 5, globalLimit: 20)
    {
    }

    /// <summary>
    /// The same two-level admission with other numbers. The key-possession routes have their own
    /// gate so that nobody hammering them — they need no secret to reach — can use up the budget
    /// an operator's admin-key claim depends on, and the other way round.
    /// </summary>
    public RelayOwnerClaimGate(TimeProvider timeProvider, int perSourceLimit, int globalLimit)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _perSource = new RelayRateLimiter(timeProvider, limit: perSourceLimit, window: TimeSpan.FromMinutes(1));
        _global = new RelayRateLimiter(timeProvider, limit: globalLimit, window: TimeSpan.FromMinutes(1), maximumPartitions: 1);
    }

    public RelayRateDecision Admit(IPAddress? remoteAddress)
    {
        var sourceHash = RelayRateLimiter.HashSource(_sourceHashKey, remoteAddress ?? IPAddress.Loopback);
        var perSource = _perSource.TryConsume(sourceHash);
        return perSource.Allowed ? _global.TryConsume(RelayRateLimiter.HashSource(_sourceHashKey, GlobalPartitionAddress)) : perSource;
    }

    // A fixed, never-routable address so every caller consumes the same relay-wide partition,
    // rather than a caller-suppliable value (ABUSE-PAIRED-RATE-PARTITION-SPOOF).
    private static readonly IPAddress GlobalPartitionAddress = IPAddress.Parse("240.0.0.1");
}

/// <summary>
/// Composes <see cref="RelayDeviceRegistry"/> and <see cref="OpaqueRelayFrameHub"/> (v2r-relay-owner,
/// #278/#290): claiming the relay's owner with the operator's admin key, an owner registering a
/// completed tablet pairing, and publishing/reading/acknowledging the opaque frames that carry
/// their traffic. Every handler here is a thin wrapper over already-tested registry/hub methods,
/// the same shape the pairing mailbox routes above use for <c>CompanionPairingMailbox</c>.
/// </summary>
public static class RelayCompanionRoutes
{
    private const string SessionHeader = "X-Relay-Session";
    private const int BodyReadBufferBytes = 64 * 1024;

    /// <summary>The revision of the map this answer carries; a tablet sends it back as <c>since</c>.</summary>
    public const string MapRevisionHeader = "X-Relay-Map-Revision";

    /// <summary>
    /// Milliseconds since this relay last heard from the desktop that owns it. On every answer to
    /// a map read, found or not, and stamped after any hold, so it is true when it arrives.
    /// </summary>
    public const string OwnerSeenHeader = "X-Relay-Owner-Seen-Ms";

    /// <summary>How long a tablet says it will hold, before the relay's own bound.</summary>
    public const string WaitQuery = "wait";

    /// <summary>The revision the tablet already has, from the answer it got last time.</summary>
    public const string SinceQuery = "since";
    private const string CredentialHeader = "X-Relay-Credential";

    /// <summary>The route a desktop registers itself on; <see cref="RelayAccess.IsGroupPath"/> names it too.</summary>
    public const string RegisterDesktopPath = "/v2/companion/relay/desktops/register";

    /// <summary>
    /// The relay as it was composed before #553: one registry, one hub, one map store. They become
    /// the legacy tenant of a directory that keeps any other desktop in memory, which is what a
    /// test host wants; the relay itself composes a directory with a folder behind it.
    /// </summary>
    public static void MapRelayCompanionRoutes(
        this WebApplication app,
        RelayDeviceRegistry? registry,
        OpaqueRelayFrameHub? hub,
        OwnerRecoveryProtector? recovery,
        RelayOwnerClaimGate claimGate,
        RelayMapSurfaceStore? mapSurfaces = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var timeProvider = app.Services.GetService<TimeProvider>() ?? TimeProvider.System;
        RelayTenantDirectory? directory = null;
        OwnerRecoveryProtector? ownedProtector = null;
        if (registry is not null)
        {
            var protector = recovery ?? (ownedProtector = new OwnerRecoveryProtector(RandomNumberGenerator.GetBytes(32), timeProvider));
            directory = RelayTenantDirectory.OpenAsync(timeProvider, protector, registry, hub, mapSurfaces)
                .AsTask().GetAwaiter().GetResult();
        }

        if (ownedProtector is not null)
        {
            app.Lifetime.ApplicationStopped.Register(ownedProtector.Dispose);
        }

        app.MapRelayCompanionRoutes(directory, recovery, claimGate);
    }

    public static void MapRelayCompanionRoutes(
        this WebApplication app,
        RelayTenantDirectory? directory,
        OwnerRecoveryProtector? recovery,
        RelayOwnerClaimGate claimGate)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(claimGate);
        // One process, one set of outstanding nonces: memory-only by design. Resume tickets are
        // each desktop's own and live with its tenant.
        var timeProvider = app.Services.GetService<TimeProvider>() ?? TimeProvider.System;
        var registry = directory?.Legacy.Registry;
        MapKeyPossessionRoutes(app, directory, timeProvider);

        // Claims the relay's owner with the admin key (ABUSE-PAIRED-OWNER-RECOVERY-EXPOSURE: this
        // route refuses outright, before the admin key is even checked, unless an operator secret
        // is configured — there is otherwise no way to reach OwnerRecoveryProtector.CreateGrant at
        // all). Rate limited per source and relay-wide (ABUSE-ADMIN-KEY-GUESS) before the body is
        // even read.
        app.MapPost("/admin/relay/claim", (HttpRequest request, CancellationToken cancellationToken) =>
            HandleClaimAsync(request, registry, recovery, claimGate, cancellationToken));

        // Whether this relay already has a live owner, and which device key thumbprint holds it —
        // gated by the admin key, the same as every other relay-wide status this operator panel
        // shows, since a key thumbprint is only meaningful to somebody who already holds that key.
        app.MapGet("/admin/relay/owner", Results<Ok<RelayOwnerStatus>, UnauthorizedHttpResult> (HttpRequest request) =>
        {
            if (!RelayAdmin.IsAuthorised(request))
            {
                return TypedResults.Unauthorized();
            }

            var owner = registry?.ActiveOwners().FirstOrDefault();
            return TypedResults.Ok(new RelayOwnerStatus(owner is not null, owner?.DeviceId.Value.ToString("N")));
        });

        // An authenticated owner registers a completed tablet pairing on the relay, so the hub
        // below can route opaque frames to and from it. ABUSE-PAIRED-HANDSHAKE-AUTHORITY-INJECTION:
        // this never accepts a completed pairing from an unauthenticated caller — only a session the
        // registry itself just authenticated as the live owner may call it, the same authority
        // AddPairedDeviceAsync already requires.
        app.MapPost("/v2/companion/relay/devices", async Task<IResult> (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (directory is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (await AuthenticateAsync(request, directory, cancellationToken).ConfigureAwait(false) is not var (tenant, principal))
            {
                return Results.Unauthorized();
            }

            var body = await ReadDeviceRegistrationAsync(request, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return Results.BadRequest("A completed pairing, role, and surface are required.");
            }

            var registered = await directory.AddPairedDeviceAsync(
                tenant,
                principal,
                body.Value.Pairing.ToCompletedAttempt(),
                body.Value.Role,
                body.Value.Surface,
                cancellationToken).ConfigureAwait(false);
            return registered.Succeeded
                ? Results.Ok(RelaySessionCredentialResponse.From(registered.Value!))
                : Results.BadRequest(registered.Code);
        });

        // [#290] The desktop revoking a paired device. Revoking used to stop at the desktop: its
        // own authority refused the tablet's commands, but this registry was never told, so the
        // tablet's session went on authenticating here and it kept reading the desktop's map.
        // Owner-only by the registry's own rule (RelayPermission.RevokeDevice), and an owner cannot
        // revoke itself out of the relay through it.
        app.MapPost("/v2/companion/relay/devices/{deviceId:guid}/revoke", async Task<IResult> (
            Guid deviceId,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (directory is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (await AuthenticateAsync(request, directory, cancellationToken).ConfigureAwait(false) is not var (tenant, principal))
            {
                return Results.Unauthorized();
            }

            if (deviceId == Guid.Empty)
            {
                return Results.BadRequest("device-not-active");
            }

            var revoked = await tenant.Registry.RevokeDeviceAsync(
                principal,
                new CompanionDeviceId(deviceId),
                "revoked-by-desktop",
                cancellationToken).ConfigureAwait(false);
            // Already gone is the outcome the caller wanted, not a failure to report.
            return revoked.Succeeded || revoked.Code == "device-not-active"
                ? Results.Ok()
                : Results.BadRequest(revoked.Code);
        });

        // Publishes one opaque frame. The hub decides direction and recipients from the frame's own
        // session id against the authenticated principal — see OpaqueRelayFrameHub's own remarks.
        app.MapPost("/v2/companion/relay/frames", async Task<IResult> (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (directory is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (await AuthenticateAsync(request, directory, cancellationToken).ConfigureAwait(false) is not var (tenant, principal))
            {
                return Results.Unauthorized();
            }

            if (tenant.Hub is not { } hub)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var frame = await ReadCompanionBodyAsync<OpaqueRelayFrame>(request, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return Results.BadRequest("An opaque relay frame is required.");
            }

            var published = await hub.PublishAsync(principal, frame, cancellationToken).ConfigureAwait(false);
            return published.Accepted ? Results.Ok() : Results.BadRequest(published.Code);
        });

        // Reads this session's queued frames after a delivery id, oldest first.
        app.MapGet("/v2/companion/relay/frames", async Task<IResult> (
            HttpRequest request,
            long? after,
            CancellationToken cancellationToken) =>
        {
            if (directory is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (await AuthenticateAsync(request, directory, cancellationToken).ConfigureAwait(false) is not var (tenant, principal))
            {
                return Results.Unauthorized();
            }

            if (tenant.Hub is not { } hub)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var batch = hub.Read(principal, after.GetValueOrDefault());
            var waiting = principal.Role == DeviceAuthorizationRole.Owner
                ? tenant.Tickets.Unanswered()
                    .Select(ticket => new RelayResumeRequest(ticket.TicketId, ticket.DeviceKeyId))
                    .ToArray()
                : null;
            // [#407] And what this relay holds of the owner's map, so a desktop learns that a
            // restart emptied it (or that a picture never arrived) on its next read.
            return Results.Ok(RelayFrameBatchResponse.From(batch, waiting, tenant.Maps?.Describe(principal)));
        });

        // [#407] The other half of `requiresReconnect`. The hub has always known how to clear a
        // queue that overflowed, and nothing could ask it to: once a session's queue had dropped
        // a frame, every read said "reconnect" for as long as the session lived, and a tablet
        // answered each one with a full resync request, every second and a half.
        app.MapPost("/v2/companion/relay/frames/reset", async Task<IResult> (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (directory is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (await AuthenticateAsync(request, directory, cancellationToken).ConfigureAwait(false) is not var (tenant, principal))
            {
                return Results.Unauthorized();
            }

            if (tenant.Hub is not { } hub)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var reset = hub.ResetAfterReconnect(principal);
            // The caller's cursor belongs wherever this queue now is: zero after a relay restart
            // (there is no queue), the last id issued after an overflow.
            return reset.Accepted
                ? Results.Ok(new RelayFrameResetResponse(hub.LastIssuedDeliveryId(principal)))
                : Results.BadRequest(reset.Code);
        });

        // Acknowledges every frame through a delivery id, freeing this session's queue.
        app.MapPost("/v2/companion/relay/frames/{deliveryId:long}/ack", async Task<IResult> (
            long deliveryId,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (directory is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (await AuthenticateAsync(request, directory, cancellationToken).ConfigureAwait(false) is not var (tenant, principal))
            {
                return Results.Unauthorized();
            }

            if (tenant.Hub is not { } hub)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var acknowledged = hub.Acknowledge(principal, deliveryId);
            return acknowledged.Accepted ? Results.Ok() : Results.BadRequest(acknowledged.Code);
        });

        // [V2 rough package 24, #407] The desktop's current map, for its paired tablets. Artwork
        // cannot travel as a sealed frame (a relay payload root is bounded at 64 KiB), so the
        // reviewed picture and the scene that places objects on it are their own authenticated
        // resources — served only to a session this relay has just authenticated, never publicly.
        app.MapPost("/v2/companion/relay/map", async Task<IResult> (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (directory is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (await AuthenticateAsync(request, directory, cancellationToken).ConfigureAwait(false) is not var (tenant, principal))
            {
                return Results.Unauthorized();
            }

            if (tenant.Maps is not { } mapSurfaces)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var body = await ReadBoundedBodyAsync(request, cancellationToken, RelayMapSurfaceStore.MaximumSurfaceBytes)
                .ConfigureAwait(false);
            if (body is null)
            {
                return Results.BadRequest("A map surface is required.");
            }

            var published = mapSurfaces.Publish(principal, body);
            return published.Accepted ? Results.Ok() : Results.BadRequest(published.Code);
        });

        // The picture itself, uploaded separately and only when its content hash changes: the
        // desktop republishes its scene on every raid tick and its artwork almost never.
        app.MapPost("/v2/companion/relay/map/artwork", async Task<IResult> (
            HttpRequest request,
            string? sha256,
            CancellationToken cancellationToken) =>
        {
            if (directory is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (await AuthenticateAsync(request, directory, cancellationToken).ConfigureAwait(false) is not var (tenant, principal))
            {
                return Results.Unauthorized();
            }

            if (tenant.Maps is not { } mapSurfaces)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var body = await ReadBoundedBodyAsync(request, cancellationToken, RelayMapSurfaceStore.MaximumArtworkBytes)
                .ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(sha256))
            {
                return Results.BadRequest("Artwork bytes and their content hash are required.");
            }

            var mediaType = request.ContentType is { Length: > 0 } declared
                ? declared.Split(';', 2)[0].Trim()
                : string.Empty;
            var published = mapSurfaces.PublishArtwork(principal, mediaType, sha256, body);
            return published.Accepted ? Results.Ok() : Results.BadRequest(published.Code);
        });

        // What a paired tablet draws. No group key reaches this: only a live relay session
        // credential does, so a revoked device sees nothing at all here.
        //
        // [V2 rough package 34] `?since=<revision>&wait=<seconds>` holds the read until the
        // desktop publishes something newer, the same shape #422 gave the group exchange. Both
        // parameters or neither: one without the other is a caller that cannot tell a change from
        // the answer it already had. A caller that names neither is answered immediately, which is
        // what keeps an older tablet page working against this relay, and an older relay — which
        // ignores both and sends no revision header — working against a newer page.
        app.MapGet("/v2/companion/relay/map", async Task<IResult> (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (directory is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (await AuthenticateAsync(request, directory, cancellationToken).ConfigureAwait(false) is not var (tenant, principal))
            {
                return Results.Unauthorized();
            }

            if (tenant.Maps is not { } mapSurfaces)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var entry = HoldFor(request) is var (since, wait)
                ? await mapSurfaces.WaitAsync(principal, since, wait, cancellationToken).ConfigureAwait(false)
                : mapSurfaces.Read(principal);
            if (tenant.Registry.OwnerLastSeenUtc() is { } seen)
            {
                var elapsed = (app.Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow() - seen;
                request.HttpContext.Response.Headers[OwnerSeenHeader] =
                    Math.Max(0, (long)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            }

            if (entry is null)
            {
                return Results.NotFound();
            }

            // The body is the desktop's own bytes and the relay never parses them, so the
            // revision it assigned travels beside them rather than inside them.
            request.HttpContext.Response.Headers[MapRevisionHeader] = entry.Revision.ToString(CultureInfo.InvariantCulture);
            return Results.Bytes(entry.SurfaceJson, "application/json; charset=utf-8");
        });

        app.MapGet("/v2/companion/relay/map/artwork", async Task<IResult> (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (directory is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (await AuthenticateAsync(request, directory, cancellationToken).ConfigureAwait(false) is not var (tenant, principal))
            {
                return Results.Unauthorized();
            }

            if (tenant.Maps is not { } mapSurfaces)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var entry = mapSurfaces.Read(principal);
            return entry?.Artwork is null || entry.ArtworkMediaType is null
                ? Results.NotFound()
                : Results.Bytes(entry.Artwork, entry.ArtworkMediaType, entityTag: new('"' + entry.ArtworkSha256 + '"'));
        });
    }

    /// <summary>
    /// [#289] Coming back without the admin key and without pairing again, by proving possession
    /// of a key this relay already has on record. See <see cref="RelayPossessionChallenges"/>.
    /// </summary>
    private static void MapKeyPossessionRoutes(
        WebApplication app,
        RelayTenantDirectory? directory,
        TimeProvider timeProvider)
    {
        // The keyless owner resume an older desktop makes acts on the one registry it ever knew.
        var registry = directory?.Legacy.Registry;
        var challenges = new RelayPossessionChallenges(timeProvider);
        // No secret is needed to reach these, so they are admitted on their own budget: wide
        // enough for a household of tablets behind one address coming back at once, and nothing
        // spent here can starve the admin-key claim route.
        var gate = new RelayOwnerClaimGate(timeProvider, perSourceLimit: 60, globalLimit: 240);

        app.MapPost("/v2/companion/relay/possession/challenge", IResult (HttpRequest request) =>
        {
            if (registry is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (!gate.Admit(request.HttpContext.Connection.RemoteIpAddress).Allowed)
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var issued = challenges.Issue();
            return Results.Ok(new RelayPossessionChallengeResponse(issued.ChallengeId, issued.NonceBase64Url, issued.ExpiresUtc));
        });

        // The owner coming back. The body is the same self-pairing the admin-key claim carries,
        // built around this relay's nonce, so its signature is the proof: there is no admin key
        // here, and nothing that is not the key on record gets past the registry.
        app.MapPost("/v2/companion/relay/owner/resume", async Task<IResult> (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (registry is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (!gate.Admit(request.HttpContext.Connection.RemoteIpAddress).Allowed)
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var claim = await ReadRelayDeviceClaimAsync(request, cancellationToken).ConfigureAwait(false);
            if (claim is null)
            {
                return Results.BadRequest("claim-not-completed");
            }

            PairingAttempt attempt;
            try
            {
                // Building the attempt is what checks the signature and that it covers this
                // request, nonce included.
                attempt = claim.ToCompletedAttempt();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest("claim-not-completed");
            }

            if (!challenges.TryConsume(attempt.Request!.ClientNonceBase64Url))
            {
                return Results.BadRequest("challenge-rejected");
            }

            var resumed = await registry.ResumeOwnerByKeyAsync(attempt, CompanionSurfaceKind.Desktop, cancellationToken)
                .ConfigureAwait(false);
            return resumed.Succeeded
                ? Results.Ok(RelaySessionCredentialResponse.From(resumed.Value!))
                : Results.BadRequest(resumed.Code);
        });

        // [#553] A desktop registering itself, or coming back. Two things are asked of it and both
        // are things it already has: the group key, checked exactly as every group route checks it
        // (the header GroupKey.TryRead reads, the room GroupKey.RoomFor derives, and the operator's
        // room list when there is one), and its own identity key, proved by the same self-pairing
        // built around this relay's nonce that the owner resume above carries. No admin key, no
        // claim: a desktop is the owner of its own tablets and of nothing else.
        var roomRegistry = app.Services.GetService<GroupRoomRegistry>();
        app.MapPost(RegisterDesktopPath, async Task<IResult> (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (directory is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (!gate.Admit(request.HttpContext.Connection.RemoteIpAddress).Allowed)
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            if (!GroupKey.TryRead(request, out var groupKey))
            {
                return Results.Unauthorized();
            }

            var room = GroupKey.RoomFor(groupKey);
            if (roomRegistry is not null && !roomRegistry.Allows(room))
            {
                return Results.Json("room-refused", statusCode: StatusCodes.Status403Forbidden);
            }

            var claim = await ReadRelayDeviceClaimAsync(request, cancellationToken).ConfigureAwait(false);
            if (claim is null)
            {
                return Results.BadRequest("claim-not-completed");
            }

            PairingAttempt attempt;
            try
            {
                attempt = claim.ToCompletedAttempt();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest("claim-not-completed");
            }

            if (!challenges.TryConsume(attempt.Request!.ClientNonceBase64Url))
            {
                return Results.BadRequest("challenge-rejected");
            }

            var registered = await directory.RegisterDesktopAsync(room, attempt, cancellationToken).ConfigureAwait(false);
            return registered.Succeeded
                ? Results.Ok(RelaySessionCredentialResponse.From(registered.Value!))
                : Results.BadRequest(registered.Code);
        });

        // A paired device coming back. It proves the key this relay has on record for it and is
        // given a ticket; its desktop sees the ticket on its next read and opens it a session.
        // The relay never issues a device a session by itself: only its desktop can, and the
        // desktop checks the same key again in the handshake that follows.
        app.MapPost("/v2/companion/relay/resume/requests", async Task<IResult> (
            HttpRequest request,
            string? deviceKeyId,
            string? desktopKeyId,
            CancellationToken cancellationToken) =>
        {
            if (directory is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (!gate.Admit(request.HttpContext.Connection.RemoteIpAddress).Allowed)
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            var proof = await ReadCompanionBodyAsync<DeviceKeyProof>(request, cancellationToken).ConfigureAwait(false);
            if (proof is null || string.IsNullOrWhiteSpace(deviceKeyId) || deviceKeyId.Length > 64)
            {
                return Results.BadRequest("proof-required");
            }

            DeviceKeyId namedKey;
            try
            {
                namedKey = new DeviceKeyId(deviceKeyId);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest("proof-required");
            }

            // [#553] Which desktop: the one the tablet names (the key it pinned when it paired),
            // and for a page from before it said so, the desktop that heard from it last. A device
            // somebody revoked is never preferred over one that is merely away.
            DeviceKeyId? namedDesktop = null;
            if (!string.IsNullOrWhiteSpace(desktopKeyId) && desktopKeyId.Length <= 64)
            {
                try
                {
                    namedDesktop = new DeviceKeyId(desktopKeyId);
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest("proof-required");
                }
            }

            var candidates = directory.FindPairedDevices(namedKey, namedDesktop);
            if (candidates.Count == 0)
            {
                return Results.Json("device-unknown", statusCode: StatusCodes.Status403Forbidden);
            }

            var (tenant, device) = candidates
                .OrderByDescending(found => found.Device.Status is DeviceLifecycleStatus.Active or DeviceLifecycleStatus.Expired)
                .ThenByDescending(found => found.Device.LastUsedUtc)
                .First();

            if (device.Status is not (DeviceLifecycleStatus.Active or DeviceLifecycleStatus.Expired))
            {
                return Results.Json("device-revoked", statusCode: StatusCodes.Status403Forbidden);
            }

            var nonce = challenges.TryConsume(proof.ChallengeId.Value);
            if (nonce is null)
            {
                return Results.BadRequest("challenge-rejected");
            }

            if (!PairingCryptography.VerifyDeviceAssertion(
                    device.DeviceKey,
                    proof.Assertion,
                    RelayPossessionChallenges.DeviceDoorChallenge(nonce)))
            {
                return Results.Json("proof-rejected", statusCode: StatusCodes.Status403Forbidden);
            }

            var ticket = tenant.Tickets.Open(device.DeviceKey.KeyId);
            return Results.Ok(new RelayResumeTicketResponse(ticket.TicketId, ticket.ExpiresUtc, null));
        });

        // Read by the device that was given the ticket; its id is the only thing that names it.
        app.MapGet("/v2/companion/relay/resume/requests/{ticketId:guid}", IResult (Guid ticketId) =>
            directory?.Tenants.Select(tenant => tenant.Tickets.Find(ticketId)).FirstOrDefault(found => found is not null) is { } ticket
                ? Results.Ok(new RelayResumeTicketResponse(ticket.TicketId, ticket.ExpiresUtc, ticket.PairingCode))
                : Results.NotFound());

        // The owner's answer: the code of the offer it has just opened in the pairing mailbox.
        app.MapPost("/v2/companion/relay/resume/requests/{ticketId:guid}/offer", async Task<IResult> (
            Guid ticketId,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (directory is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            if (await AuthenticateAsync(request, directory, cancellationToken).ConfigureAwait(false) is not var (tenant, principal) ||
                principal.Role != DeviceAuthorizationRole.Owner)
            {
                return Results.Unauthorized();
            }

            var code = request.Headers.TryGetValue("Tarkov-Pairing-Code", out var values) && values.Count == 1
                ? values[0]
                : null;
            if (string.IsNullOrWhiteSpace(code) || code.Length > 32 || !code.All(char.IsAsciiLetterOrDigit))
            {
                return Results.BadRequest("pairing-code-required");
            }

            return tenant.Tickets.Answer(ticketId, code) ? Results.Ok() : Results.NotFound();
        });
    }

    /// <summary>
    /// The <c>/admin/relay/claim</c> handler, extracted so a test can drive it directly against a
    /// <see cref="DefaultHttpContext"/> without a running server — the ordering here (admit, then
    /// authorise) is exactly what ABUSE-ADMIN-KEY-GUESS requires and is easy to silently invert
    /// while touching this route.
    /// </summary>
    public static async Task<IResult> HandleClaimAsync(
        HttpRequest request,
        RelayDeviceRegistry? registry,
        OwnerRecoveryProtector? recovery,
        RelayOwnerClaimGate claimGate,
        CancellationToken cancellationToken)
    {
        if (recovery is null || registry is null)
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }

        // Admitted before the key is even checked, so a wrong guess still spends this caller's
        // (and the relay-wide) budget — the whole point of rate limiting an admin-key check is
        // bounding guesses, including the ones that fail (ABUSE-ADMIN-KEY-GUESS).
        var admission = claimGate.Admit(request.HttpContext.Connection.RemoteIpAddress);
        if (!admission.Allowed)
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (!RelayAdmin.IsAuthorised(request))
        {
            return Results.Unauthorized();
        }

        var claim = await ReadRelayDeviceClaimAsync(request, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return Results.BadRequest("A completed self-pairing is required.");
        }

        var attempt = claim.ToCompletedAttempt();
        var deviceId = attempt.Establishment!.Assignment.DeviceId;
        var keyId = attempt.Request!.DeviceKey.KeyId;
        var grant = recovery.CreateGrant(deviceId, keyId);
        var recovered = await registry.RecoverOwnerAsync(grant, attempt, CompanionSurfaceKind.Desktop, cancellationToken)
            .ConfigureAwait(false);
        return recovered.Succeeded
            ? Results.Ok(RelaySessionCredentialResponse.From(recovered.Value!))
            : Results.BadRequest(recovered.Code);
    }

    /// <summary>Both hold parameters, or null when the caller named fewer than both.</summary>
    private static (long Since, TimeSpan Wait)? HoldFor(HttpRequest request)
    {
        if (!request.Query.TryGetValue(SinceQuery, out var sinceValues) ||
            !long.TryParse(sinceValues.ToString(), CultureInfo.InvariantCulture, out var since) ||
            since < 0 ||
            !request.Query.TryGetValue(WaitQuery, out var waitValues) ||
            !double.TryParse(waitValues.ToString(), CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) ||
            seconds <= 0)
        {
            return null;
        }

        return (since, TimeSpan.FromSeconds(Math.Min(seconds, RelayMapSurfaceStore.MaximumWait.TotalSeconds)));
    }

    /// <summary>
    /// Bearer authentication over two explicit headers, never a cookie: this transport is a native
    /// desktop/tablet <c>HttpClient</c> setting headers it chose, not a browser attaching ambient
    /// credentials, so CSRF (which only defends a cookie a browser sends automatically) does not
    /// apply here the way it does to <c>RelayDeviceRegistry.ValidateAndRotateCsrfAsync</c>'s other,
    /// cookie-carried callers.
    /// </summary>
    /// <remarks>
    /// [#553] The session id names the tenant, and the tenant's own registry decides whether the
    /// credential is good. What comes back is the pair, so that everything a handler then touches
    /// — hub, map, tickets, device list — is the authenticated desktop's and no other's.
    /// </remarks>
    private static async ValueTask<(RelayTenant Tenant, RelayPrincipal Principal)?> AuthenticateAsync(
        HttpRequest request,
        RelayTenantDirectory directory,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue(SessionHeader, out var sessionValues) || sessionValues.Count != 1 ||
            !Guid.TryParse(sessionValues[0], out var sessionGuid) || sessionGuid == Guid.Empty ||
            !request.Headers.TryGetValue(CredentialHeader, out var credentialValues) || credentialValues.Count != 1)
        {
            return null;
        }

        var sessionId = new DeviceSessionId(sessionGuid);
        if (directory.FindBySession(sessionId) is not { } tenant)
        {
            return null;
        }

        var result = await tenant.Registry.AuthenticateAsync(
            sessionId,
            credentialValues[0],
            cancellationToken).ConfigureAwait(false);
        return result.Authenticated ? (tenant, result.Principal!) : null;
    }

    private static async Task<RelayDeviceClaim?> ReadRelayDeviceClaimAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        return TryParseClaim(bytes);
    }

    /// <summary>
    /// Parses the desktop's self-claim wire shape:
    /// <c>{offer, desktopNonceBase64Url, codeConsumedUtc, request, challenge, establishment}</c>.
    /// </summary>
    public static RelayDeviceClaim? TryParseClaim(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            return ParseClaim(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<(RelayDeviceClaim Pairing, DeviceAuthorizationRole Role, CompanionSurfaceKind Surface)?> ReadDeviceRegistrationAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("pairing", out var pairingElement) ||
                !root.TryGetProperty("role", out var roleElement) || roleElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("surface", out var surfaceElement) || surfaceElement.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<DeviceAuthorizationRole>(roleElement.GetString(), ignoreCase: true, out var role) ||
                !Enum.IsDefined(role) ||
                !Enum.TryParse<CompanionSurfaceKind>(surfaceElement.GetString(), ignoreCase: true, out var surface) ||
                !Enum.IsDefined(surface))
            {
                return null;
            }

            var pairing = ParseClaim(pairingElement);
            return pairing is null ? null : (pairing, role, surface);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static RelayDeviceClaim? ParseClaim(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("desktopNonceBase64Url", out var nonceElement) ||
            nonceElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("codeConsumedUtc", out var codeConsumedElement) ||
            !codeConsumedElement.TryGetDateTimeOffset(out var codeConsumedUtc))
        {
            return null;
        }

        var offer = CompanionProtocolJson.Deserialize<PairingOffer>(RawUtf8(root, "offer"));
        var requestPiece = CompanionProtocolJson.Deserialize<PairingRequest>(RawUtf8(root, "request"));
        var challenge = CompanionProtocolJson.Deserialize<HandshakeChallenge>(RawUtf8(root, "challenge"));
        var establishment = CompanionProtocolJson.Deserialize<SessionEstablished>(RawUtf8(root, "establishment"));
        return new RelayDeviceClaim(offer, nonceElement.GetString()!, codeConsumedUtc, requestPiece, challenge, establishment);
    }

    private static byte[] RawUtf8(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element)
            ? Encoding.UTF8.GetBytes(element.GetRawText())
            : throw new JsonException($"'{property}' is required.");

    private static async Task<byte[]?> ReadBoundedBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken,
        int? maximumBytes = null)
    {
        var limit = maximumBytes ?? ProtocolBounds.MaxPayloadBytes * 4;

        // Kestrel's own global limit (Program.cs, 32 KiB) is smaller than anything this file
        // accepts, so without this override every body over 32 KiB is refused with a 413 before
        // the handler runs at all — a paired frame batch, a 1 MiB map surface, a 24 MiB rasterized
        // plan alike. The same defect #414 fixed for /report. Raised to exactly the bound this
        // call is about to enforce, never to a relay-wide maximum: an oversized body is then
        // refused by our own check, with our own code, rather than by the transport.
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            // One read buffer above the bound, not exactly it: the loop below stops on the first
            // chunk that crosses `limit`, so leaving that chunk's worth of headroom is what makes
            // an oversized body come back as this relay's own refusal rather than as a transport
            // 413 raced against it. Nothing more than that is ever buffered.
            sizeFeature.MaxRequestBodySize = limit + BodyReadBufferBytes;
        }

        if (request.ContentLength > limit)
        {
            return null;
        }

        // Bounded while it is read, not after: a caller that declares no length (or lies about
        // it) must not be able to make this buffer an unbounded body in memory.
        using var bounded = new MemoryStream();
        var buffer = new byte[BodyReadBufferBytes];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (bounded.Length + read > limit)
            {
                return null;
            }

            bounded.Write(buffer, 0, read);
        }

        var bytes = bounded.ToArray();
        return bytes.Length == 0 ? null : bytes;
    }

    /// <summary>Reads a bounded body as one paired-device wire root, or null for anything malformed.</summary>
    private static async Task<T?> ReadCompanionBodyAsync<T>(HttpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        var bytes = await ReadBoundedBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return CompanionProtocolJson.Deserialize<T>(bytes);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException)
        {
            return null;
        }
    }
}
