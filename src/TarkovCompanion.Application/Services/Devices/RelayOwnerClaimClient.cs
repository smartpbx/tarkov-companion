using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>What one attempt to claim the relay came to.</summary>
public enum RelayClaimOutcome
{
    Claimed = 1,
    AlreadyClaimedByThisDesktop,
    ClaimedByAnotherDesktop,

    /// <summary>The relay's operator has not configured claiming at all (it answers 501).</summary>
    NotConfiguredForClaiming,
    AdminKeyRefused,
    RateLimited,
    Refused,
    Unreachable,

    /// <summary>
    /// The relay does not know this desktop's key: never claimed from here, claimed since by
    /// another desktop, or a relay too old to ask. Only the admin key can claim it.
    /// </summary>
    KeyNotRecognised,

    /// <summary>[#553] The relay refused the group key: not a room it serves.</summary>
    GroupKeyRefused,
}

/// <param name="Code">The relay's own refusal code, when it gave one.</param>
public sealed record RelayClaimResult(RelayClaimOutcome Outcome, string? Code = null);

/// <summary>
/// Claims the relay's owner for this desktop with the operator's admin key, and hands the session
/// the relay issues to <see cref="RelayMarksBridge"/> — which keeps it, so it is asked for once.
/// </summary>
/// <remarks>
/// [#289] Lifted out of the pairing panel's view model so the whole path — claim, restart, still
/// claimed — can be driven against a real relay without a window. The admin key is used for the
/// two calls here and goes nowhere else: what survives a restart is the session the relay issued.
/// </remarks>
public sealed class RelayOwnerClaimClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _relay;
    private readonly IDesktopIdentitySigner _signer;
    private readonly DesktopCompanionAuthority _authority;
    private readonly RelayMarksBridge? _bridge;
    private readonly Func<CancellationToken, Task<string?>>? _groupKey;

    /// <param name="groupKey">
    /// [#553] Reads the group key the player has set, or null. With one, this desktop registers
    /// itself on the relay; without one it can only resume a claim an older build made.
    /// </param>
    public RelayOwnerClaimClient(
        HttpClient relay,
        IDesktopIdentitySigner signer,
        DesktopCompanionAuthority authority,
        RelayMarksBridge? bridge,
        Func<CancellationToken, Task<string?>>? groupKey = null)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _bridge = bridge;
        _groupKey = groupKey;
    }

    /// <summary>
    /// Claims with nothing typed: proves to the relay that this desktop holds the key it first
    /// claimed with. Works whatever became of the earlier session — expired, idle, or still live.
    /// </summary>
    /// <remarks>
    /// [#289] This is what runs at startup and whenever the relay refuses the kept session, so the
    /// admin key is typed once per machine rather than once per day. The relay hands out a nonce;
    /// the claim built around it is signed by the identity key; the relay checks that signature
    /// against the owner key it recorded at the first claim. A relay that does not know the key
    /// answers <see cref="RelayClaimOutcome.KeyNotRecognised"/>, and the admin key is the way in.
    /// </remarks>
    public async Task<RelayClaimResult> ClaimByKeyAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var result = await ClaimByKeyCoreAsync(nowUtc, cancellationToken).ConfigureAwait(false);
        // [#693] A desktop the relay refuses to register is not on the relay at all, so none of
        // its tablets can come back; that has to be somewhere a person can read it.
        _bridge?.Log?.Write(
            "register:" + result.Outcome,
            $"registering this desktop on the relay: {result.Outcome}" + (result.Code is { } code ? $" ({code})." : "."),
            result.Outcome == RelayClaimOutcome.Claimed
                ? Microsoft.Extensions.Logging.LogLevel.Information
                : Microsoft.Extensions.Logging.LogLevel.Warning);
        return result;
    }

    private async Task<RelayClaimResult> ClaimByKeyCoreAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            using var asked = await _relay.PostAsync("v2/companion/relay/possession/challenge", null, cancellationToken)
                .ConfigureAwait(false);
            if (asked.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new RelayClaimResult(RelayClaimOutcome.RateLimited);
            }

            if (!asked.IsSuccessStatusCode)
            {
                // A relay from before this route existed answers 404 or 405.
                return new RelayClaimResult(RelayClaimOutcome.KeyNotRecognised, "key-claim-unsupported");
            }

            var challenge = JsonSerializer.Deserialize<PossessionChallengeWire>(
                await asked.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
                JsonOptions);
            if (challenge?.NonceBase64Url is not { Length: > 0 } nonce)
            {
                return new RelayClaimResult(RelayClaimOutcome.KeyNotRecognised, "key-claim-unsupported");
            }

            var material = DesktopRelayOwnerClaim.Build(
                _signer,
                _authority.Snapshot.CanonicalState.DesktopDeviceId,
                nowUtc,
                nonce);
            // [#553] Registering is what a desktop does now: its group key says it belongs on
            // this relay and the signature says which desktop it is. Nobody claims anything, and
            // the same call made again (a restart, the next morning) is the same desktop coming
            // back to its own tablets.
            var groupKey = _groupKey is null ? null : await _groupKey(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(groupKey))
            {
                using var register = new HttpRequestMessage(HttpMethod.Post, "v2/companion/relay/desktops/register")
                {
                    Content = new ByteArrayContent(material.ToJsonBody()),
                };
                register.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                register.Headers.Add("X-Group-Key", groupKey.Trim());
                using var registered = await _relay.SendAsync(register, cancellationToken).ConfigureAwait(false);
                if (registered.IsSuccessStatusCode)
                {
                    await AdoptAsync(registered, cancellationToken).ConfigureAwait(false);
                    return new RelayClaimResult(RelayClaimOutcome.Claimed);
                }

                // A relay from before #553 has no such route and never saw the nonce, so the same
                // body is still good for the owner resume below.
                if (registered.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed))
                {
                    var refusal = (await registered.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim().Trim('"');
                    return registered.StatusCode switch
                    {
                        HttpStatusCode.TooManyRequests => new RelayClaimResult(RelayClaimOutcome.RateLimited),
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                            new RelayClaimResult(RelayClaimOutcome.GroupKeyRefused),
                        _ => new RelayClaimResult(RelayClaimOutcome.Refused, refusal.Length is > 0 and <= 64 ? refusal : null),
                    };
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "v2/companion/relay/owner/resume")
            {
                Content = new ByteArrayContent(material.ToJsonBody()),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _relay.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await AdoptAsync(response, cancellationToken).ConfigureAwait(false);
                return new RelayClaimResult(RelayClaimOutcome.Claimed);
            }

            var code = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim().Trim('"');
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new RelayClaimResult(RelayClaimOutcome.RateLimited);
            }

            // With no group key set this is all a desktop can try, and the caller says so.
            return new RelayClaimResult(RelayClaimOutcome.KeyNotRecognised, code.Length is > 0 and <= 64 ? code : null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new RelayClaimResult(RelayClaimOutcome.Unreachable);
        }
        catch (JsonException)
        {
            return new RelayClaimResult(RelayClaimOutcome.KeyNotRecognised, "key-claim-unsupported");
        }
    }

    private async Task AdoptAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var claimedJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var credential = JsonSerializer.Deserialize<SessionCredentialWire>(claimedJson, JsonOptions);
        if (credential is not null && _bridge is not null)
        {
            await _bridge.AdoptOwnerCredentialAsync(
                credential.SessionId,
                credential.Credential,
                credential.ExpiresUtc,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<RelayClaimResult> ClaimAsync(string adminKey, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminKey);
        try
        {
            // [#289] The key first. A desktop that claimed this relay before is let back in on its
            // key whatever state its earlier claim is in, which the admin-key route below would
            // refuse as "owner-already-live" for two hours after "Forget this relay".
            var byKey = await ClaimByKeyAsync(nowUtc, cancellationToken).ConfigureAwait(false);
            if (byKey.Outcome is RelayClaimOutcome.Claimed)
            {
                return byKey;
            }

            var material = DesktopRelayOwnerClaim.Build(
                _signer,
                _authority.Snapshot.CanonicalState.DesktopDeviceId,
                nowUtc);

            // Asked first so a relay another desktop owns is reported without spending this
            // desktop's claim-route budget on an attempt that can only fail.
            var status = await GetOwnerStatusAsync(adminKey, cancellationToken).ConfigureAwait(false);
            if (status is { Claimed: true })
            {
                var mine = string.Equals(
                    status.OwnerDeviceId,
                    material.Establishment.Assignment.DeviceId.Value.ToString("N"),
                    StringComparison.Ordinal);
                if (!mine)
                {
                    return new RelayClaimResult(RelayClaimOutcome.ClaimedByAnotherDesktop);
                }

                if (_bridge?.OwnerLink == RelayOwnerLinkState.Verified)
                {
                    return new RelayClaimResult(RelayClaimOutcome.AlreadyClaimedByThisDesktop);
                }

                // This desktop owns the relay but no longer holds the session that goes with it
                // (it was forgotten, or protected storage was cleared). Owning without a session
                // can neither pair nor route, so the claim is attempted rather than reported as
                // done. The relay refuses it as "owner-already-live" until the earlier claim has
                // been idle for its inactivity window; that rule is the relay's and stays as it is.
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "admin/relay/claim")
            {
                Content = new ByteArrayContent(material.ToJsonBody()),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Admin-Key", adminKey);
            using var response = await _relay.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await AdoptAsync(response, cancellationToken).ConfigureAwait(false);
                return new RelayClaimResult(RelayClaimOutcome.Claimed);
            }

            var code = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim().Trim('"');
            return response.StatusCode switch
            {
                // Refused before the admin key was read, so re-typing it cannot help and neither
                // can waiting: the relay's operator has to set the secret.
                HttpStatusCode.NotImplemented => new RelayClaimResult(RelayClaimOutcome.NotConfiguredForClaiming),
                HttpStatusCode.Unauthorized => new RelayClaimResult(RelayClaimOutcome.AdminKeyRefused),
                HttpStatusCode.TooManyRequests => new RelayClaimResult(RelayClaimOutcome.RateLimited),
                _ => new RelayClaimResult(RelayClaimOutcome.Refused, code.Length is > 0 and <= 64 ? code : null),
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new RelayClaimResult(RelayClaimOutcome.Unreachable);
        }
    }

    private async Task<OwnerStatusWire?> GetOwnerStatusAsync(string adminKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "admin/relay/owner");
        request.Headers.Add("X-Admin-Key", adminKey);
        using var response = await _relay.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<OwnerStatusWire>(json, JsonOptions);
    }

    // The relay names its owner only by CompanionDeviceId's "N" hex form (RelayCompanionRoutes),
    // never its device key — comparing that against the id this claim would register is enough to
    // tell "claimed by this desktop" from "claimed by another" without the relay handing back
    // anything a passive listener could use.
    private sealed record OwnerStatusWire(bool Claimed, string? OwnerDeviceId);

    private sealed record PossessionChallengeWire(Guid ChallengeId, string NonceBase64Url, DateTimeOffset ExpiresUtc);

    private sealed record SessionCredentialWire(Guid SessionId, Guid ChannelId, string Credential, string CsrfToken, DateTimeOffset ExpiresUtc);
}
