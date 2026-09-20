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

    public RelayOwnerClaimClient(
        HttpClient relay,
        IDesktopIdentitySigner signer,
        DesktopCompanionAuthority authority,
        RelayMarksBridge? bridge)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _bridge = bridge;
    }

    public async Task<RelayClaimResult> ClaimAsync(string adminKey, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminKey);
        try
        {
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

    private sealed record SessionCredentialWire(Guid SessionId, Guid ChannelId, string Credential, string CsrfToken, DateTimeOffset ExpiresUtc);
}
