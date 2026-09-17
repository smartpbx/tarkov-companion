using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.App.ViewModels.V2.Tablet;

/// <summary>
/// Whether this desktop can pair a tablet, and with what.
/// </summary>
/// <remarks>
/// Always registered, so <see cref="TarkovCompanion.App.ViewModels.V2.Shell.V2ShellViewModel"/> has
/// one dependency to take regardless of platform or configuration. <see cref="Coordinator"/> and
/// <see cref="RelayOrigin"/> are both null unless this is Windows (the desktop identity key is
/// DPAPI-protected) and a group relay with an HTTPS DNS origin is configured, because that origin
/// is what the production <c>IDeviceKeyProofVerifier</c> pins a device-key proof to.
/// </remarks>
public sealed record CompanionPairingAvailability(
    DesktopPairingCoordinator? Coordinator,
    Uri? RelayOrigin,
    IDesktopIdentitySigner? IdentitySigner = null)
{
    public static CompanionPairingAvailability Unavailable { get; } = new(null, null);
}

/// <summary>One row of the paired-device list.</summary>
public sealed class PairedDeviceRowViewModel
{
    public PairedDeviceRowViewModel(PairedDevice device, Func<PairedDeviceRowViewModel, Task> revoke)
    {
        Device = device;
        RevokeCommand = new AsyncDelegateCommand(() => revoke(this));
    }

    public PairedDevice Device { get; }

    public string DisplayName => Device.DisplayName;

    public string Role => Device.Role.ToString();

    public string Status => Device.Status.ToString();

    public DateTimeOffset LastUsedUtc => Device.LastUsedUtc;

    public DateTimeOffset ExpiresUtc => Device.ExpiresUtc;

    public bool CanRevoke => Device.Status == DeviceLifecycleStatus.Active;

    public ICommand RevokeCommand { get; }
}

/// <summary>The stage the pairing panel is showing.</summary>
public enum CompanionPairingStage
{
    Idle = 1,
    AwaitingTablet,
    AwaitingApproval,
    Completing,
}

/// <summary>
/// Drives one pairing ceremony (docs/PAIRED_DEVICE_PROTOCOL.md, "Pairing") against the relay's
/// pairing mailbox, and shows the desktop's paired-device list.
/// </summary>
/// <remarks>
/// The relay only carries the ceremony's plaintext messages; every cryptographic decision —
/// binding the request, computing the verification code, verifying the device-key proof — is
/// <see cref="DesktopPairingCoordinator"/>'s, unchanged from its own tests. This view model adds
/// nothing to that trust boundary; it only calls it and shows the result.
/// </remarks>
/// <summary>Whether this relay is claimed, and by whom, as last checked from this desktop.</summary>
public enum RelayOwnerClaimState
{
    Unknown = 1,
    NotClaimed,
    ClaimedByThisDesktop,
    ClaimedByAnotherDesktop,
}

public sealed class CompanionPairingViewModel : BindableViewModel, IDisposable
{
    private readonly DesktopCompanionAuthority _authority;
    private readonly DesktopPairingCoordinator? _coordinator;
    private readonly IDesktopIdentitySigner? _identitySigner;
    private readonly RelayMarksBridge? _relayMarksBridge;
    private readonly HttpClient? _relay;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _ceremony;
    private PairingAttemptId _attemptId;
    private DesktopPairingApproval? _approval;
    private PairingOffer? _offer;
    private PairingRequest? _request;
    private HandshakeChallenge? _challenge;
    private PairingDeviceGrant? _grant;

    private CompanionPairingStage _stage = CompanionPairingStage.Idle;
    private string? _qrPayload;
    private string? _pairingCode;
    private string? _requestedDisplayName;
    private string? _verificationCode;
    private string? _statusMessage;
    private bool _isBusy;
    private string _adminKeyInput = string.Empty;
    private RelayOwnerClaimState _relayClaimState = RelayOwnerClaimState.Unknown;
    private string? _relayClaimMessage;
    private bool _isClaimingRelay;

    public CompanionPairingViewModel(
        DesktopCompanionAuthority authority,
        CompanionPairingAvailability availability,
        TimeProvider timeProvider,
        RelayMarksBridge? relayMarksBridge = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(availability);
        _authority = authority;
        _coordinator = availability.Coordinator;
        _identitySigner = availability.IdentitySigner;
        _relayMarksBridge = relayMarksBridge;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (availability.Coordinator is not null && availability.RelayOrigin is { } origin)
        {
            _relay = new HttpClient { BaseAddress = new Uri(origin.AbsoluteUri.TrimEnd('/') + "/") };
            _relayMarksBridge?.Configure(origin);
        }

        RefreshDevices();
        StartPairingCommand = new AsyncDelegateCommand(StartPairingAsync);
        ApproveCommand = new AsyncDelegateCommand(ApproveAsync);
        DenyCommand = new AsyncDelegateCommand(DenyAsync);
        ClaimRelayCommand = new AsyncDelegateCommand(ClaimRelayAsync);
    }

    public bool CanPair => _coordinator is not null && _relay is not null;

    public string UnavailableReason => CanPair
        ? string.Empty
        : "Pairing needs Windows and a group relay configured with an https address (Settings > Group).";

    public IReadOnlyList<PairedDeviceRowViewModel> Devices { get; private set; } = [];

    public bool HasNoDevices => Devices.Count == 0;

    public CompanionPairingStage Stage
    {
        get => _stage;
        private set
        {
            if (SetProperty(ref _stage, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(IsAwaitingTablet));
                OnPropertyChanged(nameof(IsAwaitingApproval));
            }
        }
    }

    public bool IsIdle => Stage == CompanionPairingStage.Idle;

    public bool IsAwaitingTablet => Stage == CompanionPairingStage.AwaitingTablet;

    public bool IsAwaitingApproval => Stage == CompanionPairingStage.AwaitingApproval;

    public string? QrPayload
    {
        get => _qrPayload;
        private set => SetProperty(ref _qrPayload, value);
    }

    public string? PairingCode
    {
        get => _pairingCode;
        private set => SetProperty(ref _pairingCode, value);
    }

    public string? RequestedDisplayName
    {
        get => _requestedDisplayName;
        private set => SetProperty(ref _requestedDisplayName, value);
    }

    public string? VerificationCode
    {
        get => _verificationCode;
        private set => SetProperty(ref _verificationCode, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public ICommand StartPairingCommand { get; }

    public ICommand ApproveCommand { get; }

    public ICommand DenyCommand { get; }

    public ICommand ClaimRelayCommand { get; }

    public bool CanClaimRelay => _identitySigner is not null && _relay is not null;

    /// <summary>Typed once to claim the relay; never persisted, and cleared as soon as the attempt finishes.</summary>
    public string AdminKeyInput
    {
        get => _adminKeyInput;
        set => SetProperty(ref _adminKeyInput, value);
    }

    public RelayOwnerClaimState RelayClaimState
    {
        get => _relayClaimState;
        private set
        {
            if (SetProperty(ref _relayClaimState, value))
            {
                OnPropertyChanged(nameof(IsClaimedByThisDesktop));
            }
        }
    }

    public bool IsClaimedByThisDesktop => RelayClaimState == RelayOwnerClaimState.ClaimedByThisDesktop;

    public string? RelayClaimMessage
    {
        get => _relayClaimMessage;
        private set
        {
            if (SetProperty(ref _relayClaimMessage, value))
            {
                OnPropertyChanged(nameof(HasRelayClaimMessage));
            }
        }
    }

    public bool HasRelayClaimMessage => !string.IsNullOrEmpty(RelayClaimMessage);

    public bool IsClaimingRelay
    {
        get => _isClaimingRelay;
        private set => SetProperty(ref _isClaimingRelay, value);
    }

    /// <summary>
    /// Claims this relay's owner with the admin key typed into <see cref="AdminKeyInput"/> (v2r-relay-owner,
    /// #278). Checks <c>/admin/relay/owner</c> first so a relay already claimed by another desktop is
    /// reported without spending this desktop's own claim-route rate-limit budget on an attempt that
    /// can only fail.
    /// </summary>
    private async Task ClaimRelayAsync()
    {
        if (_relay is null || _identitySigner is null)
        {
            return;
        }

        var adminKey = AdminKeyInput;
        AdminKeyInput = string.Empty;
        if (string.IsNullOrWhiteSpace(adminKey))
        {
            RelayClaimMessage = "Enter the relay's admin key first.";
            return;
        }

        IsClaimingRelay = true;
        try
        {
            var material = DesktopRelayOwnerClaim.Build(
                _identitySigner,
                _authority.Snapshot.CanonicalState.DesktopDeviceId,
                Now());

            var status = await GetRelayOwnerStatusAsync(adminKey, _lifetime.Token).ConfigureAwait(true);
            if (status is { Claimed: true })
            {
                RelayClaimState = string.Equals(status.OwnerDeviceId, OwnDeviceIdHex(material), StringComparison.Ordinal)
                    ? RelayOwnerClaimState.ClaimedByThisDesktop
                    : RelayOwnerClaimState.ClaimedByAnotherDesktop;
                RelayClaimMessage = RelayClaimState == RelayOwnerClaimState.ClaimedByThisDesktop
                    ? "This relay is already claimed by this desktop."
                    : "This relay is already claimed by another desktop.";
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "admin/relay/claim")
            {
                Content = new ByteArrayContent(material.ToJsonBody()),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Admin-Key", adminKey);
            using var response = await _relay.SendAsync(request, _lifetime.Token).ConfigureAwait(true);
            if (response.IsSuccessStatusCode)
            {
                RelayClaimState = RelayOwnerClaimState.ClaimedByThisDesktop;
                RelayClaimMessage = "Claimed. This desktop is now the relay's owner.";
                var claimedJson = await response.Content.ReadAsStringAsync(_lifetime.Token).ConfigureAwait(true);
                var credential = JsonSerializer.Deserialize<RelaySessionCredentialResponse>(claimedJson, JsonOptions);
                if (credential is not null)
                {
                    _relayMarksBridge?.SetOwnerCredential(credential.SessionId, credential.Credential, credential.ExpiresUtc);
                }
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                RelayClaimMessage = "That admin key was not accepted.";
            }
            else if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                RelayClaimMessage = "Too many claim attempts. Try again in a minute.";
            }
            else
            {
                RelayClaimMessage = "The relay refused the claim.";
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            RelayClaimMessage = "Could not reach the group relay.";
        }
        finally
        {
            IsClaimingRelay = false;
        }
    }

    private async Task<RelayOwnerStatusResponse?> GetRelayOwnerStatusAsync(string adminKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "admin/relay/owner");
        request.Headers.Add("X-Admin-Key", adminKey);
        using var response = await _relay!.SendAsync(request, cancellationToken).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
        return JsonSerializer.Deserialize<RelayOwnerStatusResponse>(json, JsonOptions);
    }

    // The relay names its owner only by CompanionDeviceId's "N" hex form (RelayCompanionRoutes),
    // never its device key — comparing that against the id this claim would register is enough to
    // tell "claimed by this desktop" from "claimed by another" without the relay handing back
    // anything a passive listener could use.
    private static string OwnDeviceIdHex(DesktopRelayOwnerClaimMaterial material) =>
        material.Establishment.Assignment.DeviceId.Value.ToString("N");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record RelayOwnerStatusResponse(bool Claimed, string? OwnerDeviceId);

    private sealed record RelaySessionCredentialResponse(Guid SessionId, Guid ChannelId, string Credential, string CsrfToken, DateTimeOffset ExpiresUtc);

    public void Dispose()
    {
        _ceremony?.Cancel();
        _ceremony?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _relay?.Dispose();
    }

    private void RefreshDevices()
    {
        Devices = _authority.Snapshot.Devices
            .Select(device => new PairedDeviceRowViewModel(device, RevokeAsync))
            .ToArray();
        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(HasNoDevices));
    }

    private async Task StartPairingAsync()
    {
        if (_coordinator is null || _relay is null)
        {
            return;
        }

        ResetCeremony();
        IsBusy = true;
        StatusMessage = null;
        try
        {
            var invitation = await _coordinator.CreateInvitationAsync(Now()).ConfigureAwait(true);
            _attemptId = invitation.Offer.AttemptId;
            _offer = invitation.Offer;
            var registered = await PostAsync(
                "v2/companion/pairing/offers",
                invitation.Offer,
                new KeyValuePair<string, string>("Tarkov-Pairing-Code", invitation.PairingCode),
                _ceremony!.Token).ConfigureAwait(true);
            if (!registered)
            {
                StatusMessage = "The relay would not accept a new pairing invitation. Try again shortly.";
                Stage = CompanionPairingStage.Idle;
                return;
            }

            QrPayload = invitation.QrPayload;
            PairingCode = invitation.PairingCode;
            Stage = CompanionPairingStage.AwaitingTablet;
            _ = PollForRequestAsync(_ceremony.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            StatusMessage = "Could not reach the group relay.";
            Stage = CompanionPairingStage.Idle;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PollForRequestAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var request = await GetAsync<PairingRequest>(
                    $"v2/companion/pairing/requests/{_attemptId.Value:D}",
                    cancellationToken).ConfigureAwait(true);
                if (request is not null)
                {
                    await OnRequestReceivedAsync(request, cancellationToken).ConfigureAwait(true);
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Transient; the next poll tries again until the ceremony is cancelled or completes.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task OnRequestReceivedAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        if (_coordinator is null)
        {
            return;
        }

        try
        {
            var approval = await _coordinator.BindRequestAsync(request, CompanionProtocolVersion.Current, Now())
                .ConfigureAwait(true);
            _approval = approval;
            _request = request;
            await PostAsync(
                "v2/companion/pairing/reveals",
                approval.NonceReveal,
                _attemptId,
                cancellationToken).ConfigureAwait(true);
            RequestedDisplayName = approval.RequestedDisplayName;
            VerificationCode = approval.VerificationCode;
            Stage = CompanionPairingStage.AwaitingApproval;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusMessage = "The tablet's pairing request could not be bound. Start over.";
            Stage = CompanionPairingStage.Idle;
        }
    }

    private async Task ApproveAsync()
    {
        if (_coordinator is null || _approval is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var grant = new PairingDeviceGrant(
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
                    DeviceCapability.ShowOnDesktop,
                    DeviceCapability.ManageOwnMarks,
                    DeviceCapability.RequestCaptureIntent,
                ],
                Now().AddDays(90),
                CompanionTransportKind.EndToEndRelay,
                CompanionSurfaceKind.TabletLandscape);
            _grant = grant;
            var challenge = await _coordinator.ApproveAsync(
                _attemptId,
                userConfirmedMatchingVerificationCode: true,
                grant,
                Now()).ConfigureAwait(true);
            _challenge = challenge;
            await PostAsync("v2/companion/pairing/challenges", challenge, _attemptId, _ceremony!.Token)
                .ConfigureAwait(true);
            Stage = CompanionPairingStage.Completing;
            _ = PollForProofAsync(_ceremony.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusMessage = "Approval failed.";
            Stage = CompanionPairingStage.Idle;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PollForProofAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var proof = await GetAsync<DeviceKeyProof>(
                    $"v2/companion/pairing/proofs/{_attemptId.Value:D}",
                    cancellationToken).ConfigureAwait(true);
                if (proof is not null)
                {
                    await OnProofReceivedAsync(proof, cancellationToken).ConfigureAwait(true);
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Transient; keep polling.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task OnProofReceivedAsync(DeviceKeyProof proof, CancellationToken cancellationToken)
    {
        if (_coordinator is null)
        {
            return;
        }

        try
        {
            using var session = await _coordinator.CompletePairingAsync(_attemptId, proof, Now())
                .ConfigureAwait(true);
            await PostAsync("v2/companion/pairing/established", session.Establishment, _attemptId, cancellationToken)
                .ConfigureAwait(true);
            if (_relayMarksBridge is not null && _offer is not null && _request is not null &&
                _challenge is not null && _approval is not null && _grant is not null)
            {
                // Registers this same completed pairing on the relay (separately from the local
                // DesktopCompanionAuthority record RegisterPairingAsync just made) so the hub can
                // route the new tablet's opaque frames, and delivers its starting canonical
                // snapshot. Best-effort: a relay that is unreachable or not yet claimed leaves the
                // pairing itself intact — only live sync for this device is unavailable.
                await _relayMarksBridge.RegisterPairedDeviceAsync(
                    _attemptId,
                    _offer,
                    _approval.NonceReveal.DesktopNonceBase64Url,
                    _offer.OfferedUtc,
                    _request,
                    _challenge,
                    session,
                    _grant.Role,
                    _grant.Surface,
                    cancellationToken).ConfigureAwait(true);
            }

            StatusMessage = $"Paired \"{RequestedDisplayName}\".";
            RefreshDevices();
            ResetCeremony();
        }
        catch (UnauthorizedAccessException)
        {
            StatusMessage = "The tablet's device-key proof did not verify. It was not paired.";
            ResetCeremony();
        }
    }

    private async Task DenyAsync()
    {
        if (_coordinator is null)
        {
            return;
        }

        try
        {
            await _coordinator.DenyAsync(_attemptId, Now()).ConfigureAwait(true);
            await PostAsync($"v2/companion/pairing/denied/{_attemptId.Value:D}", _ceremony?.Token ?? default)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Already resolved; nothing further to tell the mailbox.
        }
        finally
        {
            StatusMessage = "Declined.";
            ResetCeremony();
        }
    }

    private async Task RevokeAsync(PairedDeviceRowViewModel row)
    {
        try
        {
            await _authority.RevokeDeviceAsync(row.Device.DeviceId, Now(), "Revoked from the pairing panel.")
                .ConfigureAwait(true);
            RefreshDevices();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusMessage = $"Could not revoke \"{row.DisplayName}\".";
        }
    }

    private void ResetCeremony()
    {
        _ceremony?.Cancel();
        _ceremony?.Dispose();
        _ceremony = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _approval = null;
        _offer = null;
        _request = null;
        _challenge = null;
        _grant = null;
        QrPayload = null;
        PairingCode = null;
        RequestedDisplayName = null;
        VerificationCode = null;
        Stage = CompanionPairingStage.Idle;
    }

    // DesktopPairingCoordinator (and every protocol record it builds) requires exact
    // millisecond-precision UTC and throws otherwise; TimeProvider.System.GetUtcNow() is
    // sub-millisecond, so every real (non-test-clock) pairing ceremony call needs this truncated.
    private DateTimeOffset Now()
    {
        var utc = _timeProvider.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    private async Task<bool> PostAsync<T>(
        string path,
        T value,
        KeyValuePair<string, string> header,
        CancellationToken cancellationToken)
        where T : class
    {
        using var content = new ByteArrayContent(CompanionProtocolJson.Serialize(value));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add(header.Key, header.Value);
        using var response = await _relay!.SendAsync(request, cancellationToken).ConfigureAwait(true);
        return response.IsSuccessStatusCode;
    }

    private Task<bool> PostAsync<T>(string routePrefix, T value, PairingAttemptId attemptId, CancellationToken cancellationToken)
        where T : class =>
        PostAsync($"{routePrefix}/{attemptId.Value:D}", value, cancellationToken);

    private async Task<bool> PostAsync<T>(string path, T value, CancellationToken cancellationToken)
        where T : class
    {
        using var content = new ByteArrayContent(CompanionProtocolJson.Serialize(value));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _relay!.PostAsync(path, content, cancellationToken).ConfigureAwait(true);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> PostAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _relay!.PostAsync(path, content: null, cancellationToken).ConfigureAwait(true);
        return response.IsSuccessStatusCode;
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await _relay!.GetAsync(path, cancellationToken).ConfigureAwait(true);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(true);
        return CompanionProtocolJson.Deserialize<T>(bytes);
    }
}
