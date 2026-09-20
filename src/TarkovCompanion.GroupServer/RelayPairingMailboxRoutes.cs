using System.Text.Json;
using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// The pairing mailbox's HTTP routes, moved out of <c>Program.cs</c> unchanged so that a test can
/// map the same routes a deployed relay serves and drive a whole ceremony through them.
/// </summary>
/// <remarks>
/// [#290] They were top-level statements, which no test could reach, and the desktop's half of the
/// ceremony was only ever tested against the coordinator directly. The two had drifted: the
/// coordinator refused every request that arrived through this mailbox, and nothing noticed.
/// </remarks>
public static class RelayPairingMailboxRoutes
{
    public static void MapCompanionPairingMailboxRoutes(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // v2r-pairing-tablet: the paired-device pairing handshake (#277/#290). A bounded relay of the
        // plaintext wire roots in docs/PAIRED_DEVICE_PROTOCOL.md's "Pairing" section — see
        // CompanionPairingMailbox for what it does and does not do. It is deliberately separate from the
        // group room-key routes above: a paired tablet is one device of one desktop, not a group member.
        app.MapPost("/v2/companion/pairing/offers", async Task<IResult> (
            HttpRequest request,
            CompanionPairingMailbox mailbox,
            CancellationToken cancellationToken) =>
        {
            var offer = await ReadCompanionBodyAsync<PairingOffer>(request, cancellationToken).ConfigureAwait(false);
            if (offer is null)
            {
                return Results.BadRequest("A pairing offer is required.");
            }

            var remote = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var result = mailbox.RegisterOffer(offer, request.Headers["Tarkov-Pairing-Code"].ToString(), remote);
            if (!result.Succeeded)
            {
                return result.Code == "rate-limited"
                    ? Results.StatusCode(StatusCodes.Status429TooManyRequests)
                    : Results.BadRequest(result.Code);
            }

            return Results.Ok();
        });

        app.MapPost("/v2/companion/pairing/offers/resolve", (HttpRequest request, CompanionPairingMailbox mailbox) =>
        {
            var remote = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var result = mailbox.ResolveOffer(request.Headers["Tarkov-Pairing-Code"].ToString(), remote);
            if (!result.Succeeded)
            {
                return result.Code == "rate-limited"
                    ? Results.StatusCode(StatusCodes.Status429TooManyRequests)
                    : Results.NotFound();
            }

            return Results.Bytes(CompanionProtocolJson.Serialize(result.Value!), "application/json");
        });

        app.MapPost("/v2/companion/pairing/requests/{attemptId:guid}", async Task<IResult> (
            Guid attemptId,
            HttpRequest request,
            CompanionPairingMailbox mailbox,
            CancellationToken cancellationToken) =>
        {
            var value = await ReadCompanionBodyAsync<PairingRequest>(request, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                return Results.BadRequest("A pairing request is required.");
            }

            var result = mailbox.SubmitRequest(new PairingAttemptId(attemptId), value);
            return result.Succeeded ? Results.Ok() : Results.BadRequest(result.Code);
        });

        app.MapGet("/v2/companion/pairing/requests/{attemptId:guid}", (Guid attemptId, CompanionPairingMailbox mailbox) =>
            mailbox.ReadRequest(new PairingAttemptId(attemptId)) is { } value
                ? Results.Bytes(CompanionProtocolJson.Serialize(value), "application/json")
                : Results.NotFound());

        app.MapPost("/v2/companion/pairing/reveals/{attemptId:guid}", async Task<IResult> (
            Guid attemptId,
            HttpRequest request,
            CompanionPairingMailbox mailbox,
            CancellationToken cancellationToken) =>
        {
            var value = await ReadCompanionBodyAsync<PairingNonceReveal>(request, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                return Results.BadRequest("A desktop nonce reveal is required.");
            }

            var result = mailbox.SubmitNonceReveal(new PairingAttemptId(attemptId), value);
            return result.Succeeded ? Results.Ok() : Results.BadRequest(result.Code);
        });

        app.MapGet("/v2/companion/pairing/reveals/{attemptId:guid}", (Guid attemptId, CompanionPairingMailbox mailbox) =>
            mailbox.ReadNonceReveal(new PairingAttemptId(attemptId)) is { } value
                ? Results.Bytes(CompanionProtocolJson.Serialize(value), "application/json")
                : Results.NotFound());

        app.MapPost("/v2/companion/pairing/challenges/{attemptId:guid}", async Task<IResult> (
            Guid attemptId,
            HttpRequest request,
            CompanionPairingMailbox mailbox,
            CancellationToken cancellationToken) =>
        {
            var value = await ReadCompanionBodyAsync<HandshakeChallenge>(request, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                return Results.BadRequest("A handshake challenge is required.");
            }

            var result = mailbox.SubmitChallenge(new PairingAttemptId(attemptId), value);
            return result.Succeeded ? Results.Ok() : Results.BadRequest(result.Code);
        });

        app.MapGet("/v2/companion/pairing/challenges/{attemptId:guid}", (Guid attemptId, CompanionPairingMailbox mailbox) =>
            mailbox.ReadChallenge(new PairingAttemptId(attemptId)) is { } value
                ? Results.Bytes(CompanionProtocolJson.Serialize(value), "application/json")
                : Results.NotFound());

        app.MapPost("/v2/companion/pairing/proofs/{attemptId:guid}", async Task<IResult> (
            Guid attemptId,
            HttpRequest request,
            CompanionPairingMailbox mailbox,
            CancellationToken cancellationToken) =>
        {
            var value = await ReadCompanionBodyAsync<DeviceKeyProof>(request, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                return Results.BadRequest("A device key proof is required.");
            }

            var result = mailbox.SubmitProof(new PairingAttemptId(attemptId), value);
            return result.Succeeded ? Results.Ok() : Results.BadRequest(result.Code);
        });

        app.MapGet("/v2/companion/pairing/proofs/{attemptId:guid}", (Guid attemptId, CompanionPairingMailbox mailbox) =>
            mailbox.ReadProof(new PairingAttemptId(attemptId)) is { } value
                ? Results.Bytes(CompanionProtocolJson.Serialize(value), "application/json")
                : Results.NotFound());

        app.MapPost("/v2/companion/pairing/established/{attemptId:guid}", async Task<IResult> (
            Guid attemptId,
            HttpRequest request,
            CompanionPairingMailbox mailbox,
            CancellationToken cancellationToken) =>
        {
            var value = await ReadCompanionBodyAsync<SessionEstablished>(request, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                return Results.BadRequest("A session establishment is required.");
            }

            var result = mailbox.SubmitEstablished(new PairingAttemptId(attemptId), value);
            return result.Succeeded ? Results.Ok() : Results.BadRequest(result.Code);
        });

        app.MapGet("/v2/companion/pairing/established/{attemptId:guid}", (Guid attemptId, CompanionPairingMailbox mailbox) =>
            mailbox.ReadEstablished(new PairingAttemptId(attemptId)) is { } value
                ? Results.Bytes(CompanionProtocolJson.Serialize(value), "application/json")
                : Results.NotFound());

        // v2r-tablet-marks-sync: the tablet's own relay bearer secret, handed through once the desktop has
        // registered it on the relay (POST /v2/companion/relay/devices) — sealed with the pairing's own
        // desktop-to-tablet traffic key (RelayCredentialCryptography), never plaintext, so this relay never
        // holds or forwards a readable bearer secret (ABUSE-PAIRED-LIVE-BEARER-THEFT). Not a
        // CompanionProtocolJson wire root, so this is plain JSON, the same as every other GroupServer-local
        // request/response body.
        app.MapPost("/v2/companion/pairing/relay-session/{attemptId:guid}", (
            Guid attemptId,
            SealedRelayCredential body,
            CompanionPairingMailbox mailbox) =>
            mailbox.SubmitRelaySession(new PairingAttemptId(attemptId), body).Succeeded
                ? Results.Ok()
                : Results.BadRequest());

        app.MapGet("/v2/companion/pairing/relay-session/{attemptId:guid}", (Guid attemptId, CompanionPairingMailbox mailbox) =>
            mailbox.ReadRelaySession(new PairingAttemptId(attemptId)) is { } value
                ? Results.Ok(value)
                : Results.NotFound());

        app.MapPost("/v2/companion/pairing/denied/{attemptId:guid}", (Guid attemptId, CompanionPairingMailbox mailbox) =>
            mailbox.Deny(new PairingAttemptId(attemptId)).Succeeded ? Results.Ok() : Results.NotFound());

        app.MapGet("/v2/companion/pairing/denied/{attemptId:guid}", (Guid attemptId, CompanionPairingMailbox mailbox) =>
            Results.Ok(new { denied = mailbox.IsDenied(new PairingAttemptId(attemptId)) }));
    }

    /// <summary>Reads a bounded body as one paired-device wire root, or null for anything malformed.</summary>
    /// <remarks>
    /// Deserialized through <see cref="CompanionProtocolJson"/> rather than the framework's own model
    /// binder, so this reads exactly the encoding the desktop and tablet both write and validates the
    /// same record invariants (bounds, base64url shape, required fields) they do.
    /// </remarks>
    private static async Task<T?> ReadCompanionBodyAsync<T>(HttpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        using var stream = new MemoryStream();
        await request.Body.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        try
        {
            return CompanionProtocolJson.Deserialize<T>(stream.ToArray());
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException)
        {
            return null;
        }
    }
}
