using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.StateSync;
using TarkovCompanion.GroupServer.Storage;

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

public sealed record RelayFrameBatchResponse(int ProtocolVersion, bool RequiresReconnect, DateTimeOffset ServerUtc, IReadOnlyList<RelayFrameEnvelope> Frames)
{
    public static RelayFrameBatchResponse From(RelayFrameBatch batch) => new(
        batch.ProtocolVersion.Major,
        batch.RequiresReconnect,
        batch.ServerUtc,
        batch.Frames.Select(RelayFrameEnvelope.From).ToArray());
}

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
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _perSource = new RelayRateLimiter(timeProvider, limit: 5, window: TimeSpan.FromMinutes(1));
        _global = new RelayRateLimiter(timeProvider, limit: 20, window: TimeSpan.FromMinutes(1), maximumPartitions: 1);
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
    private const string CredentialHeader = "X-Relay-Credential";

    public static void MapRelayCompanionRoutes(
        this WebApplication app,
        RelayDeviceRegistry? registry,
        OpaqueRelayFrameHub? hub,
        OwnerRecoveryProtector? recovery,
        RelayOwnerClaimGate claimGate,
        RelayMapSurfaceStore? mapSurfaces = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(claimGate);

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
            if (registry is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var principal = await AuthenticateAsync(request, registry, cancellationToken).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var body = await ReadDeviceRegistrationAsync(request, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return Results.BadRequest("A completed pairing, role, and surface are required.");
            }

            var registered = await registry.AddPairedDeviceAsync(
                principal,
                body.Value.Pairing.ToCompletedAttempt(),
                body.Value.Role,
                body.Value.Surface,
                cancellationToken).ConfigureAwait(false);
            return registered.Succeeded
                ? Results.Ok(RelaySessionCredentialResponse.From(registered.Value!))
                : Results.BadRequest(registered.Code);
        });

        // Publishes one opaque frame. The hub decides direction and recipients from the frame's own
        // session id against the authenticated principal — see OpaqueRelayFrameHub's own remarks.
        app.MapPost("/v2/companion/relay/frames", async Task<IResult> (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (registry is null || hub is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var principal = await AuthenticateAsync(request, registry, cancellationToken).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Unauthorized();
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
            if (registry is null || hub is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var principal = await AuthenticateAsync(request, registry, cancellationToken).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var batch = hub.Read(principal, after.GetValueOrDefault());
            return Results.Ok(RelayFrameBatchResponse.From(batch));
        });

        // Acknowledges every frame through a delivery id, freeing this session's queue.
        app.MapPost("/v2/companion/relay/frames/{deliveryId:long}/ack", async Task<IResult> (
            long deliveryId,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (registry is null || hub is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var principal = await AuthenticateAsync(request, registry, cancellationToken).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Unauthorized();
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
            if (registry is null || mapSurfaces is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var principal = await AuthenticateAsync(request, registry, cancellationToken).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Unauthorized();
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
            if (registry is null || mapSurfaces is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var principal = await AuthenticateAsync(request, registry, cancellationToken).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Unauthorized();
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
        app.MapGet("/v2/companion/relay/map", async Task<IResult> (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (registry is null || mapSurfaces is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var principal = await AuthenticateAsync(request, registry, cancellationToken).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var entry = mapSurfaces.Read(principal);
            return entry is null
                ? Results.NotFound()
                : Results.Bytes(entry.SurfaceJson, "application/json; charset=utf-8");
        });

        app.MapGet("/v2/companion/relay/map/artwork", async Task<IResult> (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (registry is null || mapSurfaces is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            var principal = await AuthenticateAsync(request, registry, cancellationToken).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Unauthorized();
            }

            var entry = mapSurfaces.Read(principal);
            return entry?.Artwork is null || entry.ArtworkMediaType is null
                ? Results.NotFound()
                : Results.Bytes(entry.Artwork, entry.ArtworkMediaType, entityTag: new('"' + entry.ArtworkSha256 + '"'));
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

    /// <summary>
    /// Bearer authentication over two explicit headers, never a cookie: this transport is a native
    /// desktop/tablet <c>HttpClient</c> setting headers it chose, not a browser attaching ambient
    /// credentials, so CSRF (which only defends a cookie a browser sends automatically) does not
    /// apply here the way it does to <c>RelayDeviceRegistry.ValidateAndRotateCsrfAsync</c>'s other,
    /// cookie-carried callers.
    /// </summary>
    private static async ValueTask<RelayPrincipal?> AuthenticateAsync(
        HttpRequest request,
        RelayDeviceRegistry registry,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue(SessionHeader, out var sessionValues) || sessionValues.Count != 1 ||
            !Guid.TryParse(sessionValues[0], out var sessionGuid) ||
            !request.Headers.TryGetValue(CredentialHeader, out var credentialValues) || credentialValues.Count != 1)
        {
            return null;
        }

        var result = await registry.AuthenticateAsync(
            new DeviceSessionId(sessionGuid),
            credentialValues[0],
            cancellationToken).ConfigureAwait(false);
        return result.Authenticated ? result.Principal : null;
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
