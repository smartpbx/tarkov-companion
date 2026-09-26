using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>What a tablet paired from the pairing panel is allowed, in one place.</summary>
/// <remarks>
/// [#407] <c>AuthenticatedCommandContext.ForPairedSession</c> takes the *intersection* of a
/// device's own grant and its current session's grant, so a capability missing from either list
/// is refused. <c>RequestControl</c> was on the device grant but not the session grant — every
/// paired tablet had it in principle and never in practice: <c>RequestControlCommand</c> and
/// <c>ControlWorkspaceCommand</c> both require it, and CanonicalStateMachine's own capability
/// switch has no other source for it. A real paired tablet could follow, show itself on the
/// desktop and drop marks, but Control — the mode the pairing concept art and the whole
/// Follow/Control/Independent UI are built around — was unreachable end to end. Measured with the
/// real browser harness (TabletScreenshotHarness): a freshly-paired tablet's own requestControl
/// came back RejectedUnauthorized every time, before this line was added.
/// </remarks>
public static class PairedTabletGrant
{
    public static PairingDeviceGrant Create(DateTimeOffset nowUtc) => new(
        DeviceAuthorizationRole.Member,
        [
            DeviceCapability.FollowDesktop,
            DeviceCapability.RequestControl,
            DeviceCapability.ShowOnDesktop,
            DeviceCapability.ManageOwnMarks,
            DeviceCapability.RequestCaptureIntent,
        ],
        [
            DeviceCapability.FollowDesktop,
            DeviceCapability.RequestControl,
            DeviceCapability.ShowOnDesktop,
            DeviceCapability.ManageOwnMarks,
            DeviceCapability.RequestCaptureIntent,
        ],
        nowUtc.AddDays(90),
        CompanionTransportKind.EndToEndRelay,
        CompanionSurfaceKind.TabletLandscape);
}

/// <summary>
/// Lets a tablet this desktop has already approved come back without anybody typing a code or
/// pressing Approve, once its session has run out.
/// </summary>
/// <remarks>
/// [#289, #290] A paired session lives twelve hours and a paired device two idle, by the
/// protocol's own bounds, so a tablet left on the desk overnight had to be paired again every
/// day. Those bounds stay. What comes back is the same handshake a first pairing runs — a fresh
/// ephemeral exchange, fresh traffic keys, a fresh session, the device's key proved over that
/// exact transcript — with the two human steps taken out, because what they establish is already
/// established: the person compared the six digits the first time, and since then this desktop
/// has held the tablet's public key and the tablet has held this desktop's. The proof at the end
/// is checked against the key on record for that device, so nothing but that tablet can finish
/// it, and a device the player has revoked is never answered at all.
///
/// The relay only says "this key came back" (it checked the key at its own door first). Nothing
/// here trusts that: a ticket for a key this desktop does not know, or has revoked, is ignored.
/// </remarks>
public sealed class PairedDeviceResumeService : IDisposable
{
    private readonly DesktopCompanionAuthority _authority;
    private readonly DesktopPairingCoordinator _coordinator;
    private readonly RelayMarksBridge _bridge;
    private readonly HttpClient _relay;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);

    public PairedDeviceResumeService(
        DesktopCompanionAuthority authority,
        DesktopPairingCoordinator coordinator,
        RelayMarksBridge bridge,
        HttpClient relay,
        TimeProvider clock)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _clock = clock ?? TimeProvider.System;
        _bridge.ResumeRequested += OnResumeRequested;
    }

    /// <summary>How often the pairing mailbox is asked for the handshake's next message.</summary>
    public TimeSpan MailboxPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>A device came back and holds a fresh session. Raised on a worker thread.</summary>
    public event Action<PairedDevice>? DeviceResumed;

    private void OnResumeRequested(RelayResumeTicket ticket)
    {
        // One handshake per device key at a time; a second ticket for a key already being
        // answered is the tablet retrying, and the first answer is still good.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inFlight.TryAdd(ticket.DeviceKeyId, started.Task))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ResumeAsync(ticket, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _bridge.Log?.Write(
                    "resume:failed",
                    $"returning device not resumed: {exception.GetType().Name}.",
                    Microsoft.Extensions.Logging.LogLevel.Warning);
                // A handshake that did not finish leaves the tablet where it was: it asks again,
                // or the player pairs it by code. Nothing half-done is kept by the coordinator
                // past the offer's own few minutes. [#846] It is told so, rather than left to time out.
                if (!_lifetime.IsCancellationRequested)
                {
                    await RefuseAsync(
                        ticket,
                        exception is DeviceNotRecognisedException ? NotRecognised : Failed).ConfigureAwait(false);
                }
            }
            finally
            {
                _inFlight.TryRemove(ticket.DeviceKeyId, out _);
                started.TrySetResult();
            }
        });
    }

    private async Task ResumeAsync(RelayResumeTicket ticket, CancellationToken cancellationToken)
    {
        var known = _authority.Snapshot.Devices
            .Where(device => device.Role != DeviceAuthorizationRole.Owner &&
                string.Equals(device.DeviceKey.KeyId.Value, ticket.DeviceKeyId, StringComparison.Ordinal))
            .OrderByDescending(device => device.CreatedUtc)
            .FirstOrDefault();
        if (known is null || known.Status is DeviceLifecycleStatus.Revoked or DeviceLifecycleStatus.Replaced)
        {
            // [#693] The relay routes a ticket by the key it has on record; this desktop answers
            // only for a device it approved. Said, because it is the one refusal nobody sees.
            _bridge.Log?.Write(
                "resume:unknown",
                known is null
                    ? "resume ticket ignored: that device is not paired with this desktop."
                    : $"resume ticket ignored: that device is {known.Status} here.",
                Microsoft.Extensions.Logging.LogLevel.Warning);
            await RefuseAsync(ticket, NotRecognised).ConfigureAwait(false);
            return;
        }

        var invitation = await _coordinator.CreateInvitationAsync(Now(), cancellationToken).ConfigureAwait(false);
        var attemptId = invitation.Offer.AttemptId;
        var code = PairedTransportBinding.NormalizePairingCode(invitation.PairingCode);
        try
        {
            using (var offer = Json(HttpMethod.Post, "v2/companion/pairing/offers", invitation.Offer))
            {
                offer.Headers.Add("Tarkov-Pairing-Code", code);
                using var registered = await _relay.SendAsync(offer, cancellationToken).ConfigureAwait(false);
                if (!registered.IsSuccessStatusCode)
                {
                    _bridge.Log?.Write(
                        "resume:offer-failed",
                        $"resume offer refused by the relay: HTTP {(int)registered.StatusCode}.",
                        Microsoft.Extensions.Logging.LogLevel.Warning);
                    throw new InvalidOperationException("The relay would not hold the resume offer.");
                }
            }

            _bridge.Log?.Write("resume:offer-opened", "resume offer opened for a returning device.");

            if (!await _bridge.AnswerResumeTicketAsync(ticket.TicketId, code, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The relay would not take the answer to the ticket.");
            }

            var deadline = invitation.Offer.ExpiresUtc;
            var request = await PollAsync<PairingRequest>($"v2/companion/pairing/requests/{attemptId.Value:D}", deadline, cancellationToken)
                .ConfigureAwait(false);

            // Whoever resolved the code, only the device this was opened for gets an answer, and
            // only with the key already approved for it.
            if (request.DeviceKey.KeyId != known.DeviceKey.KeyId ||
                !string.Equals(request.DeviceKey.CosePublicKeyBase64Url, known.DeviceKey.CosePublicKeyBase64Url, StringComparison.Ordinal))
            {
                throw new DeviceNotRecognisedException("The resume request names another device key.");
            }

            await _coordinator.AcknowledgeRelayResolvedCodeAsync(attemptId, Now(), cancellationToken).ConfigureAwait(false);
            var approval = await _coordinator.BindRequestAsync(request, CompanionProtocolVersion.Current, Now(), cancellationToken)
                .ConfigureAwait(false);
            await PostAsync($"v2/companion/pairing/reveals/{attemptId.Value:D}", approval.NonceReveal, cancellationToken).ConfigureAwait(false);

            // "Confirmed" without a person: see the remarks above. The six digits guard a first
            // meeting against somebody in the middle; here both long-term keys are already pinned
            // and both sign this handshake's transcript.
            var grant = PairedTabletGrant.Create(Now());
            var challenge = await _coordinator.ApproveAsync(
                attemptId,
                userConfirmedMatchingVerificationCode: true,
                grant,
                Now(),
                cancellationToken).ConfigureAwait(false);
            await PostAsync($"v2/companion/pairing/challenges/{attemptId.Value:D}", challenge, cancellationToken).ConfigureAwait(false);

            var proof = await PollAsync<DeviceKeyProof>($"v2/companion/pairing/proofs/{attemptId.Value:D}", deadline, cancellationToken)
                .ConfigureAwait(false);
            using var session = await _coordinator.CompletePairingAsync(attemptId, proof, Now(), cancellationToken)
                .ConfigureAwait(false);
            await PostAsync($"v2/companion/pairing/established/{attemptId.Value:D}", session.Establishment, cancellationToken)
                .ConfigureAwait(false);
            await _bridge.RegisterPairedDeviceAsync(
                attemptId,
                invitation.Offer,
                approval.NonceReveal.DesktopNonceBase64Url,
                invitation.Offer.OfferedUtc,
                request,
                challenge,
                session,
                grant.Role,
                grant.Surface,
                cancellationToken).ConfigureAwait(false);

            var resumed = _authority.Snapshot.Devices.FirstOrDefault(
                device => device.DeviceId == session.Establishment.Assignment.DeviceId);
            _bridge.Log?.Write("resume:done", "returning device resumed with a fresh session.");
            if (resumed is not null)
            {
                DeviceResumed?.Invoke(resumed);
            }
        }
        catch
        {
            try
            {
                await _coordinator.DenyAsync(attemptId, Now(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Already past the point where it can be denied, or already gone.
            }

            throw;
        }
    }

    /// <summary>
    /// The one failure that means the tablet is not this desktop's: the key that answered is not
    /// the key on record.
    /// </summary>
    /// <remarks>
    /// [#936] Every UnauthorizedAccessException used to be refused as not-recognised, and the tablet
    /// deletes its remembered desktop on that word. The coordinator throws the same type for an
    /// attempt it pruned at the offer's deadline, so a slow tablet answering on the edge was told
    /// to forget a pairing that was still good. Anything else is "failed", which it keeps.
    /// </remarks>
    private sealed class DeviceNotRecognisedException(string message) : UnauthorizedAccessException(message);

    // The wire words of RelayResumeRefusals on the relay; this project does not reference it.
    private const string NotRecognised = "not-recognised";
    private const string Failed = "failed";

    /// <summary>
    /// [#846] Before this, a refusal was local: the relay and the tablet never learned, and the
    /// tablet sat on "Reconnecting" for a minute (unanswered ticket) or 30 s (a failed step)
    /// before it showed the code form again. Best effort and bounded: an old relay answers 404,
    /// and the tablet then times out exactly as it used to.
    /// </summary>
    private async Task RefuseAsync(RelayResumeTicket ticket, string reason)
    {
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            bounded.CancelAfter(TimeSpan.FromSeconds(10));
            await _bridge.RefuseResumeTicketAsync(ticket.TicketId, reason, bounded.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _bridge.Log?.Write(
                "resume:refusal-unsent",
                $"resume refusal not delivered: {exception.GetType().Name}.",
                Microsoft.Extensions.Logging.LogLevel.Warning);
        }
    }

    private async Task<T> PollAsync<T>(string path, DateTimeOffset deadlineUtc, CancellationToken cancellationToken)
        where T : class
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await _relay.GetAsync(path, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return CompanionProtocolJson.Deserialize<T>(
                        await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
                }

                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException($"The pairing mailbox answered {(int)response.StatusCode}.");
                }
            }
            catch (HttpRequestException)
            {
                // Transient; the deadline below is what ends this.
            }

            if (Now() >= deadlineUtc)
            {
                throw new TimeoutException("The returning device did not answer before the offer expired.");
            }

            await Task.Delay(MailboxPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PostAsync<T>(string path, T value, CancellationToken cancellationToken)
        where T : class
    {
        using var request = Json(HttpMethod.Post, path, value);
        using var response = await _relay.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The pairing mailbox refused {path}.");
        }
    }

    private static HttpRequestMessage Json<T>(HttpMethod method, string path, T value)
        where T : class
    {
        var content = new ByteArrayContent(CompanionProtocolJson.Serialize(value));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpRequestMessage(method, path) { Content = content };
    }

    // Every protocol record requires exact millisecond-precision UTC.
    private DateTimeOffset Now()
    {
        var utc = _clock.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    /// <summary>Completes when nothing is being answered; a test waits on it.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_inFlight.Values.ToArray());

    public void Dispose()
    {
        _bridge.ResumeRequested -= OnResumeRequested;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
