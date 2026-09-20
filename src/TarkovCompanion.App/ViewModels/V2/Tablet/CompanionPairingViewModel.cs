using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Windows.Input;
using Avalonia.Threading;
using TarkovCompanion.App.Services.V2.Team;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

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
public sealed class PairedDeviceRowViewModel : BindableViewModel
{
    private bool _confirming;

    public PairedDeviceRowViewModel(PairedDevice device, Func<PairedDeviceRowViewModel, Task> revoke)
    {
        ArgumentNullException.ThrowIfNull(revoke);
        Device = device;
        RevokeCommand = new AsyncDelegateCommand(async () =>
        {
            // [V2 rough package 60 — Team] #289: two presses, because revoking cannot be
            // undone. The grant is gone and the tablet has to be paired again from scratch, so
            // the confirmation has to come before the act rather than as an undo after it. The
            // second press is the confirmation, and the button says which one it is on.
            if (!_confirming)
            {
                Confirming = true;
                return;
            }

            Confirming = false;
            await revoke(this).ConfigureAwait(true);
        });
    }

    public PairedDevice Device { get; }

    public string DisplayName => Device.DisplayName;

    public string Role => Device.Role.ToString();

    public string Status => Device.Status.ToString();

    public DateTimeOffset LastUsedUtc => Device.LastUsedUtc;

    public DateTimeOffset ExpiresUtc => Device.ExpiresUtc;

    public bool CanRevoke => Device.Status == DeviceLifecycleStatus.Active;

    /// <summary>Whether the next press revokes, rather than asks.</summary>
    public bool Confirming
    {
        get => _confirming;
        private set
        {
            if (SetProperty(ref _confirming, value))
            {
                OnPropertyChanged(nameof(RevokeLabel));
                OnPropertyChanged(nameof(RevokeWarning));
            }
        }
    }

    public string RevokeLabel => Confirming ? "Confirm revoke" : "Revoke";

    /// <summary>What the second press will do, said before it is pressed.</summary>
    public string RevokeWarning => Confirming
        ? $"{DisplayName} will have to be paired again. This cannot be undone."
        : string.Empty;

    /// <summary>Puts the row back to asking, for a selection change or a reload.</summary>
    public void CancelConfirmation() => Confirming = false;

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

    /// <summary>
    /// The relay refuses to be claimed at all, because its operator has not configured an
    /// owner-recovery secret.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 48] This is the state that produced the complaint. `/admin/relay/claim`
    /// answers 501 before it even looks at the admin key when
    /// <c>TARKOV_RELAY_OWNER_RECOVERY_SECRET</c> is unset, so no desktop can ever become owner and
    /// no amount of retrying or re-typing the admin key changes anything. It has to be named
    /// separately from a wrong key and from a transient refusal, because only the relay's operator
    /// can fix it.
    /// </remarks>
    NotConfiguredForClaiming,
}

public sealed class CompanionPairingViewModel : BindableViewModel, IDisposable
{
    private readonly DesktopCompanionAuthority _authority;
    private readonly DesktopPairingCoordinator? _coordinator;
    private readonly IDesktopIdentitySigner? _identitySigner;
    private readonly RelayMarksBridge? _relayMarksBridge;
    private readonly HttpClient? _relay;
    private readonly RelayOwnerClaimClient? _claimClient;
    private readonly PairedDeviceResumeService? _resume;
    private Uri? _relayOrigin;
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
    private string? _qrPath;
    private int _qrExtent;
    private string _codeExpiry = string.Empty;
    private bool _isCodeExpired;
    private DateTimeOffset? _previewExpiry;
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
        _relayOrigin = availability.RelayOrigin;
        if (availability.Coordinator is not null && availability.RelayOrigin is { } origin)
        {
            _relay = new HttpClient { BaseAddress = new Uri(origin.AbsoluteUri.TrimEnd('/') + "/") };
            _relayMarksBridge?.Configure(origin);
            if (availability.IdentitySigner is { } signer)
            {
                _claimClient = new RelayOwnerClaimClient(_relay, signer, authority, _relayMarksBridge);
                if (_relayMarksBridge is not null)
                {
                    // [#289] The relay ends an owner session after twelve hours, or two idle. The
                    // bridge asks to be let back in on this desktop's key before it gives the
                    // claim up, so the admin key is typed once on this machine, not once a day.
                    var claimClient = _claimClient;
                    _relayMarksBridge.OwnerReclaim = token => claimClient.ClaimByKeyAsync(Now(), token);
                }
            }

            if (_relayMarksBridge is not null)
            {
                // [#289, #290] And a tablet already approved here comes back the same way.
                _resume = new PairedDeviceResumeService(authority, availability.Coordinator, _relayMarksBridge, _relay, _timeProvider);
                _resume.DeviceResumed += OnDeviceResumed;
            }
        }

        RefreshDevices();
        StartPairingCommand = new AsyncDelegateCommand(StartPairingAsync);
        ApproveCommand = new AsyncDelegateCommand(ApproveAsync);
        DenyCommand = new AsyncDelegateCommand(DenyAsync);
        ClaimRelayCommand = new AsyncDelegateCommand(ClaimRelayAsync);
        // [V2 rough package 24] Control of this desktop, from the concept's own three modes.
        AllowControlCommand = new AsyncDelegateCommand(() => ResolveControlAsync(approved: true));
        DenyControlCommand = new AsyncDelegateCommand(() => ResolveControlAsync(approved: false));
        TakeBackControlCommand = new AsyncDelegateCommand(TakeBackControlAsync);
        ForgetRelayCommand = new AsyncDelegateCommand(ForgetRelayAsync);
        if (_relayMarksBridge is not null)
        {
            _relayMarksBridge.CanonicalStateChanged += OnCanonicalStateChanged;
            _relayMarksBridge.OwnerLinkChanged += OnOwnerLinkChanged;
            if (_relay is not null)
            {
                // [#289] The claim made on an earlier run, picked back up with nobody typing
                // anything. The bridge's first poll says whether the relay still honours it, and
                // OnOwnerLinkChanged is where that lands.
                ApplyOwnerLink(_relayMarksBridge.OwnerLink);
                RelayLinkRestored = RestoreRelayLinkAsync();
            }
        }

        RefreshControl(_authority.Snapshot.CanonicalState);
    }

    /// <summary>
    /// Which paired device is asking to drive this desktop, and which one is driving it.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 24, #407] Control is the tablet concept's third mode
    /// (docs/design/v2/v2-tablet-desktop-control-concept.png). The reducer has always held the
    /// lease; what was missing was anywhere on the desktop to answer a request or to take control
    /// back, so nothing could ever grant it. <see cref="TakeBackControlCommand"/> is the one
    /// action that ends a lease, which is the rule the concept states.
    /// </remarks>
    public string? ControlRequestMessage
    {
        get;
        private set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasControlRequest));
            }
        }
    }

    public bool HasControlRequest => !string.IsNullOrEmpty(ControlRequestMessage);

    /// <summary>
    /// What an admin key is, where it comes from, and what claiming does — said where it is asked
    /// for.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 48] The box used to say only "Type the relay's admin key to make this
    /// desktop its owner", which assumes the reader already knows there is such a thing and that
    /// they are the person who set it. One line, naming the variable, because the answer to "what
    /// is the admin key" is a variable name on a server.
    /// </remarks>
    public static string AdminKeyHelp =>
        "The admin key is the secret the relay's operator set as TARKOV_RELAY_ADMIN_KEY. Claiming " +
        "makes this desktop the relay's owner, which is what lets it pair devices at all. The key " +
        "is used once and never stored; the claim itself is kept.";

    /// <summary>The message for a relay whose operator has not configured claiming at all.</summary>
    public const string NotConfiguredForClaimingMessage =
        "This relay is not configured for claiming. Its operator must set " +
        "TARKOV_RELAY_OWNER_RECOVERY_SECRET and restart it; until then no desktop can become its " +
        "owner and no device can be paired.";

    /// <summary>Whether starting a pairing ceremony can succeed, as last known from this desktop.</summary>
    /// <remarks>
    /// [#289] The owner session is kept in protected storage and picked back up at startup
    /// (<c>RelayMarksBridge.RestoreAsync</c>), so this reads true after a restart without the admin
    /// key being typed again — until the relay itself ends that session, or "Forget this relay".
    /// </remarks>
    public bool CanStartPairing => CanPair && RelayClaimState == RelayOwnerClaimState.ClaimedByThisDesktop;

    /// <summary>Why "Start pairing" is unavailable, or null when it is.</summary>
    public string? StartPairingBlockedReason => CanStartPairing
        ? null
        : !CanPair
            ? UnavailableReason
            : RelayClaimState switch
            {
                // Short here on purpose: the claim card directly above is already showing the
                // whole message, and saying it twice reads as two different problems.
                RelayOwnerClaimState.NotConfiguredForClaiming =>
                    "This relay cannot be claimed yet — see above.",
                RelayOwnerClaimState.ClaimedByAnotherDesktop =>
                    "Another desktop owns this relay, so it cannot pair devices for this one.",
                _ => "Claim this relay first: enter its admin key above and press Claim.",
            };

    /// <summary>Puts this view model into one named state so a render can photograph it.</summary>
    /// <remarks>
    /// [V2 rough package 48] The pairing panel has four states worth looking at and three refusal
    /// messages, and none of them can be reached in a render without a relay and a tablet. This is
    /// the seam <c>tools/V2RenderPreview</c> uses; it sets only what the panel shows and never
    /// touches the authority, the coordinator or the relay.
    /// </remarks>
    /// <summary>
    /// Set only by <see cref="PresentForPreview"/>, so a render on Linux can draw a panel that in
    /// production needs Windows and a configured relay. Nothing else assigns it, and no production
    /// path can reach it.
    /// </summary>
    private bool _previewAvailability;

    internal void PresentForPreview(
        RelayOwnerClaimState claimState,
        CompanionPairingStage stage,
        string? pairingCode = null,
        string? verificationCode = null,
        string? requestedDisplayName = null,
        string? statusMessage = null,
        string? claimMessage = null,
        IReadOnlyList<PairedDeviceRowViewModel>? devices = null,
        DateTimeOffset? codeExpiresUtc = null)
    {
        _previewAvailability = true;
        _relayOrigin ??= new Uri("https://relay.example");
        OnPropertyChanged(nameof(ClaimedSummary));
        OnPropertyChanged(nameof(CanPair));
        OnPropertyChanged(nameof(CanClaimRelay));
        OnPropertyChanged(nameof(NeedsClaim));

        if (devices is not null)
        {
            Devices = devices;
            OnPropertyChanged(nameof(Devices));
            OnPropertyChanged(nameof(HasNoDevices));
        }

        RelayClaimState = claimState;
        RelayClaimMessage = claimMessage;
        Stage = stage;
        PairingCode = pairingCode;
        // The shape the ceremony really produces, so a render shows the symbol a tablet would
        // actually be asked to scan rather than a placeholder that happens to be shorter.
        QrPayload = pairingCode is null
            ? null
            : PairedTransportBinding.QrPayloadPrefix + pairingCode.Replace("-", string.Empty, StringComparison.Ordinal)
                + "/" + new string('a', 43);
        _previewExpiry = codeExpiresUtc;
        TickExpiry();
        VerificationCode = verificationCode;
        RequestedDisplayName = requestedDisplayName;
        StatusMessage = statusMessage;
        OnPropertyChanged(nameof(CanStartPairing));
        OnPropertyChanged(nameof(StartPairingBlockedReason));
    }

    public string? ControlHolderMessage
    {
        get;
        private set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasControlHolder));
            }
        }
    }

    public bool HasControlHolder => !string.IsNullOrEmpty(ControlHolderMessage);

    public ICommand AllowControlCommand { get; }

    public ICommand DenyControlCommand { get; }

    public ICommand TakeBackControlCommand { get; }

    private void OnCanonicalStateChanged(CanonicalCompanionState state)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshControl(state);
            return;
        }

        Dispatcher.UIThread.Post(() => RefreshControl(state));
    }

    /// <summary>Restates what canonical device modes now say, in this desktop's own words.</summary>
    internal void RefreshControl(CanonicalCompanionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var modes = state.DeviceModes;
        ControlRequestMessage = modes.PendingControl is { } pending
            ? $"{NameOf(pending.DeviceId)} is asking to control this desktop."
            : null;
        ControlHolderMessage = modes.ControlLease is { } lease
            ? $"{NameOf(lease.DeviceId)} is controlling this desktop."
            : null;
        RefreshDevices();
    }

    private string NameOf(CompanionDeviceId deviceId) =>
        _authority.Snapshot.Devices.FirstOrDefault(device => device.DeviceId == deviceId)?.DisplayName
            ?? "A paired device";

    private async Task ResolveControlAsync(bool approved)
    {
        if (_relayMarksBridge is null ||
            _authority.Snapshot.CanonicalState.DeviceModes.PendingControl is not { } pending)
        {
            return;
        }

        var state = _authority.Snapshot.CanonicalState;
        var now = Utc();
        await _relayMarksBridge.ApplyDesktopCommandAsync(
            new ResolveControlCommand(
                new CommandId(Guid.NewGuid()),
                new AggregateRevision(state.DeviceModes.Cursor.Revision.Value + 1),
                now,
                now.AddMinutes(1),
                pending.RequestCommandId,
                approved,
                approved ? new ControlLeaseId(Guid.NewGuid()) : null),
            _lifetime.Token).ConfigureAwait(true);
        RefreshControl(_authority.Snapshot.CanonicalState);
    }

    private async Task TakeBackControlAsync()
    {
        if (_relayMarksBridge is null || _authority.Snapshot.CanonicalState.DeviceModes.ControlLease is null)
        {
            return;
        }

        var state = _authority.Snapshot.CanonicalState;
        var now = Utc();
        await _relayMarksBridge.ApplyDesktopCommandAsync(
            new PreemptControlCommand(
                new CommandId(Guid.NewGuid()),
                new AggregateRevision(state.DeviceModes.Cursor.Revision.Value + 1),
                now,
                now.AddMinutes(1),
                "desktop-took-control-back"),
            _lifetime.Token).ConfigureAwait(true);
        RefreshControl(_authority.Snapshot.CanonicalState);
    }

    // Every protocol timestamp requires exact millisecond precision.
    private DateTimeOffset Utc()
    {
        var utc = _timeProvider.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    public bool CanPair => _previewAvailability || (_coordinator is not null && _relay is not null);

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
        private set
        {
            if (!SetProperty(ref _qrPayload, value))
            {
                return;
            }

            // [V2 rough package 60 — Team] #289: the payload has always been produced and never
            // drawn, so the only way onto a tablet was to type the code. A symbol that cannot be
            // built is not an error worth showing: the code beside it still works.
            QrPath = null;
            QrExtent = 0;
            if (value is not null)
            {
                try
                {
                    var code = QrCode.Encode(value);
                    QrExtent = QrGeometry.Extent(code);
                    QrPath = QrGeometry.PathData(code);
                }
                catch (ArgumentException)
                {
                    QrPath = null;
                    QrExtent = 0;
                }
            }

            OnPropertyChanged(nameof(HasQrSymbol));
        }
    }

    /// <summary>The pairing payload as path data, or null when there is none to draw.</summary>
    public string? QrPath
    {
        get => _qrPath;
        private set => SetProperty(ref _qrPath, value);
    }

    /// <summary>The symbol's width in modules, quiet zone included, for the view's viewbox.</summary>
    public int QrExtent
    {
        get => _qrExtent;
        private set => SetProperty(ref _qrExtent, value);
    }

    public bool HasQrSymbol => QrPath is not null;

    /// <summary>
    /// How long the code has left, or why it no longer has any.
    /// </summary>
    /// <remarks>
    /// The offer has always carried an expiry and the panel never showed it, so a code that had
    /// quietly gone stale looked exactly like one that had not, and the tablet's refusal was the
    /// first anybody heard of it.
    /// </remarks>
    public string CodeExpiry
    {
        get => _codeExpiry;
        private set => SetProperty(ref _codeExpiry, value);
    }

    public bool HasCodeExpiry => CodeExpiry.Length > 0;

    /// <summary>
    /// How long is left, in words.
    /// </summary>
    /// <remarks>
    /// Minutes and seconds while there are minutes, because "4m 36s" is read once and understood,
    /// and a bare "276s" is arithmetic. Under a minute it drops to seconds, which is the point at
    /// which somebody decides whether to start over rather than keep typing.
    /// </remarks>
    internal static string DescribeExpiry(TimeSpan left) => left <= TimeSpan.Zero
        ? "This code has expired. Start pairing again."
        : left.TotalMinutes >= 1
            ? string.Create(CultureInfo.CurrentCulture, $"Expires in {(int)left.TotalMinutes}m {left.Seconds:00}s")
            : string.Create(CultureInfo.CurrentCulture, $"Expires in {(int)left.TotalSeconds}s");

    /// <summary>Whether the code has run out, which is when the panel stops offering it.</summary>
    public bool IsCodeExpired
    {
        get => _isCodeExpired;
        private set => SetProperty(ref _isCodeExpired, value);
    }

    /// <summary>
    /// Recomputes the countdown. The shell calls this on its own one-second pass.
    /// </summary>
    /// <remarks>
    /// Driven from outside rather than by a timer of its own: the shell already ticks once a
    /// second for the raid clock, and a second timer would be a second thing to stop on dispose.
    /// </remarks>
    public void TickExpiry()
    {
        var expires = _offer?.ExpiresUtc ?? _previewExpiry;
        if (expires is not { } expiresUtc
            || Stage is not (CompanionPairingStage.AwaitingTablet or CompanionPairingStage.AwaitingApproval))
        {
            CodeExpiry = string.Empty;
            IsCodeExpired = false;
            OnPropertyChanged(nameof(HasCodeExpiry));
            return;
        }

        var left = expiresUtc - Now();
        IsCodeExpired = left <= TimeSpan.Zero;
        CodeExpiry = DescribeExpiry(left);
        OnPropertyChanged(nameof(HasCodeExpiry));
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

    public bool CanClaimRelay => _previewAvailability || (_identitySigner is not null && _relay is not null);

    /// <summary>
    /// Whether the claim card still has anything to ask for.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 48] An empty admin-key box sitting above a working pairing flow is noise,
    /// and worse than noise: it suggests there is something still to type when there is not.
    /// </remarks>
    public bool NeedsClaim => CanClaimRelay && RelayClaimState != RelayOwnerClaimState.ClaimedByThisDesktop;

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
                OnPropertyChanged(nameof(NeedsClaim));
                OnPropertyChanged(nameof(CanStartPairing));
                OnPropertyChanged(nameof(StartPairingBlockedReason));
            }
        }
    }

    public bool IsClaimedByThisDesktop => RelayClaimState == RelayOwnerClaimState.ClaimedByThisDesktop;

    /// <summary>The claimed line: which relay, said whether or not this run did the claiming.</summary>
    /// <remarks>
    /// [#289] The line used to be the claim attempt's own message, so a claim picked back up after
    /// a restart — where nothing was attempted — showed as an empty row.
    /// </remarks>
    public string ClaimedSummary => _relayOrigin is { } origin
        ? $"This desktop owns the relay at {origin.Host}."
        : "This desktop owns the relay.";

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
        if (_claimClient is null)
        {
            return;
        }

        var adminKey = AdminKeyInput;
        AdminKeyInput = string.Empty;
        IsClaimingRelay = true;
        try
        {
            if (string.IsNullOrWhiteSpace(adminKey))
            {
                // [#289] Nothing typed is still worth one try: a desktop that claimed this relay
                // before (and pressed "Forget this relay", say) is let back in on its key alone.
                var byKey = await _claimClient.ClaimByKeyAsync(Now(), _lifetime.Token).ConfigureAwait(true);
                (RelayClaimState, RelayClaimMessage) = byKey.Outcome == RelayClaimOutcome.KeyNotRecognised
                    ? (RelayClaimState, "Enter the relay's admin key first.")
                    : DescribeClaim(byKey, RelayClaimState);
                return;
            }

            var result = await _claimClient.ClaimAsync(adminKey, Now(), _lifetime.Token).ConfigureAwait(true);
            (RelayClaimState, RelayClaimMessage) = DescribeClaim(result, RelayClaimState);
        }
        finally
        {
            IsClaimingRelay = false;
        }
    }

    /// <summary>What one claim attempt means for the panel: the state it leaves, and what to say.</summary>
    internal static (RelayOwnerClaimState State, string Message) DescribeClaim(
        RelayClaimResult result,
        RelayOwnerClaimState current) => result.Outcome switch
    {
        RelayClaimOutcome.Claimed =>
            (RelayOwnerClaimState.ClaimedByThisDesktop, "Claimed. This desktop is now the relay's owner."),
        RelayClaimOutcome.AlreadyClaimedByThisDesktop =>
            (RelayOwnerClaimState.ClaimedByThisDesktop, "This relay is already claimed by this desktop."),
        RelayClaimOutcome.ClaimedByAnotherDesktop =>
            (RelayOwnerClaimState.ClaimedByAnotherDesktop, "This relay is already claimed by another desktop."),
        RelayClaimOutcome.NotConfiguredForClaiming =>
            (RelayOwnerClaimState.NotConfiguredForClaiming, NotConfiguredForClaimingMessage),
        RelayClaimOutcome.AdminKeyRefused => (current, "That admin key was not accepted."),
        RelayClaimOutcome.KeyNotRecognised => (current, "This relay does not know this desktop. Enter its admin key."),
        RelayClaimOutcome.RateLimited => (current, "Too many claim attempts. Try again in a minute."),
        RelayClaimOutcome.Unreachable => (current, "Could not reach the group relay."),
        // The relay will not replace an owner it has heard from recently, this desktop included.
        _ when result.Code == "owner-already-live" =>
            (current, "The relay still holds an earlier claim. It can be claimed again once that one has been idle for two hours."),
        _ => (current, result.Code is { Length: > 0 } code
            ? $"The relay refused the claim: {code}."
            : "The relay refused the claim."),
    };

    /// <summary>"Forget this relay": drops the kept claim, here and in protected storage.</summary>
    public ICommand ForgetRelayCommand { get; }

    private async Task ForgetRelayAsync()
    {
        if (_relayMarksBridge is null)
        {
            return;
        }

        await _relayMarksBridge.ForgetOwnerAsync(_lifetime.Token).ConfigureAwait(true);
        RelayClaimState = RelayOwnerClaimState.NotClaimed;
        RelayClaimMessage = "Forgotten. Press Claim to claim this relay again.";
    }

    /// <summary>Completes once the kept claim has been looked for; a test waits on it.</summary>
    internal Task RelayLinkRestored { get; } = Task.CompletedTask;

    /// <summary>How often the relay's pairing mailbox is asked for the ceremony's next message.</summary>
    internal TimeSpan MailboxPollInterval
    {
        get => _mailboxPollInterval;
        set
        {
            _mailboxPollInterval = value;
            if (_resume is not null)
            {
                _resume.MailboxPollInterval = value;
            }
        }
    }

    private TimeSpan _mailboxPollInterval = TimeSpan.FromSeconds(2);

    private async Task RestoreRelayLinkAsync()
    {
        try
        {
            await _relayMarksBridge!.RestoreAsync(_lifetime.Token).ConfigureAwait(true);
            // Said here as well as through the event: this continuation is already where the
            // panel lives, so the first answer does not wait on a dispatcher pass.
            ApplyOwnerLink(_relayMarksBridge.OwnerLink);
        }
        catch (OperationCanceledException)
        {
            // Closing during startup; nothing was half-applied.
        }
    }

    private void OnDeviceResumed(PairedDevice device)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshDevices();
            return;
        }

        Dispatcher.UIThread.Post(RefreshDevices);
    }

    /// <summary>Completes when no returning tablet is being answered; a test waits on it.</summary>
    internal Task ResumesSettled => _resume?.WhenIdleAsync() ?? Task.CompletedTask;

    private void OnOwnerLinkChanged(RelayOwnerLinkState link)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyOwnerLink(link);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyOwnerLink(link));
    }

    /// <summary>Says what the bridge has learned about this desktop's owner session.</summary>
    internal void ApplyOwnerLink(RelayOwnerLinkState link)
    {
        switch (link)
        {
            case RelayOwnerLinkState.Verified:
                RelayClaimState = RelayOwnerClaimState.ClaimedByThisDesktop;
                if (RelayClaimMessage is null || RelayClaimMessage == UnreachableClaimMessage)
                {
                    RelayClaimMessage = null;
                }

                break;
            case RelayOwnerLinkState.Restored:
                // Kept from an earlier run. Shown as claimed straight away: the relay has not
                // refused it, and a panel that asked for the admin key for the two seconds before
                // the first poll answered would be asking for something it does not need.
                RelayClaimState = RelayOwnerClaimState.ClaimedByThisDesktop;
                break;
            case RelayOwnerLinkState.Unreachable:
                RelayClaimMessage = UnreachableClaimMessage;
                break;
            case RelayOwnerLinkState.Rejected:
                RelayClaimState = RelayOwnerClaimState.NotClaimed;
                RelayClaimMessage = "Another desktop has claimed this relay since. Enter the admin key to claim it back.";
                break;
        }
    }

    private const string UnreachableClaimMessage = "Could not reach the group relay. Still trying.";

    public void Dispose()
    {
        if (_relayMarksBridge is not null)
        {
            _relayMarksBridge.CanonicalStateChanged -= OnCanonicalStateChanged;
            _relayMarksBridge.OwnerLinkChanged -= OnOwnerLinkChanged;
        }

        if (_resume is not null)
        {
            _resume.DeviceResumed -= OnDeviceResumed;
            _resume.Dispose();
        }

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

        // Checked before an invitation is minted, not after the relay has refused it. A device
        // paired to a relay this desktop does not own can never live-sync (RelayMarksBridge has no
        // owner credential to route its frames with), so starting the ceremony here would produce
        // a tablet that pairs and then does nothing.
        if (RelayClaimState == RelayOwnerClaimState.NotConfiguredForClaiming)
        {
            StatusMessage = NotConfiguredForClaimingMessage;
            return;
        }

        if (RelayClaimState == RelayOwnerClaimState.ClaimedByAnotherDesktop)
        {
            StatusMessage = "Another desktop owns this relay, so it cannot pair devices for this one.";
            return;
        }

        if (RelayClaimState != RelayOwnerClaimState.ClaimedByThisDesktop)
        {
            StatusMessage = "Claim this relay first: enter its admin key above and press Claim.";
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
            var registered = await PostForStatusAsync(
                "v2/companion/pairing/offers",
                invitation.Offer,
                new KeyValuePair<string, string>("Tarkov-Pairing-Code", invitation.PairingCode),
                _ceremony!.Token).ConfigureAwait(true);
            if (registered.Status is not HttpStatusCode.OK and not HttpStatusCode.NoContent)
            {
                StatusMessage = DescribeOfferRefusal(registered);
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

            await Task.Delay(MailboxPollInterval, cancellationToken).ConfigureAwait(true);
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
            // This request came out of the relay's mailbox, which is where the code was resolved.
            await _coordinator.AcknowledgeRelayResolvedCodeAsync(_attemptId, Now(), cancellationToken)
                .ConfigureAwait(true);
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
            var grant = PairedTabletGrant.Create(Now());
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

            await Task.Delay(MailboxPollInterval, cancellationToken).ConfigureAwait(true);
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
            RelayDeviceRegistration? registration = null;
            if (_relayMarksBridge is not null && _offer is not null && _request is not null &&
                _challenge is not null && _approval is not null && _grant is not null)
            {
                // Registers this same completed pairing on the relay (separately from the local
                // DesktopCompanionAuthority record RegisterPairingAsync just made) so the hub can
                // route the new tablet's opaque frames, and delivers its starting canonical
                // snapshot. Best-effort: a relay that is unreachable or not yet claimed leaves the
                // pairing itself intact — only live sync for this device is unavailable.
                registration = await _relayMarksBridge.RegisterPairedDeviceAsync(
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

            // A pairing the relay would not register is a tablet that pairs and then shows
            // nothing, which used to be reported as a plain success.
            StatusMessage = registration is { Registered: false } refused
                ? $"Paired \"{RequestedDisplayName}\", but the relay would not carry it ({refused.Code}). Update the relay."
                : $"Paired \"{RequestedDisplayName}\".";
            RefreshDevices();
            ResetCeremony();
        }
        catch (UnauthorizedAccessException)
        {
            StatusMessage = "The tablet's device-key proof did not verify. It was not paired.";
            ResetCeremony();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            // This runs from a poll nobody awaits, so anything that escaped here left the panel on
            // "Completing" for good while the tablet timed out on its own.
            StatusMessage = "Pairing could not be completed. Start pairing again.";
            RefreshDevices();
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
            if (_relayMarksBridge is not null)
            {
                // [#290] And on the relay, and out of protected storage: a revoked tablet's keys
                // are gone from this desktop and its relay session stops authenticating, so it
                // cannot go on reading the map it was just cut off from.
                await _relayMarksBridge.RevokePairedDeviceAsync(row.Device.DeviceId, _lifetime.Token)
                    .ConfigureAwait(true);
            }

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

    /// <summary>
    /// The same post, but reporting what the relay actually said.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 48] A bool could only ever produce one message for every refusal, which
    /// is how "Try again shortly" came to be shown for a state that retrying could not fix.
    /// </remarks>
    private async Task<RelayRefusal> PostForStatusAsync<T>(
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
        var body = response.IsSuccessStatusCode
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
        return new RelayRefusal(response.StatusCode, body.Trim().Trim('"'));
    }

    /// <summary>What the relay said when it refused: its status, and the code in its body.</summary>
    internal sealed record RelayRefusal(HttpStatusCode Status, string Code);

    /// <summary>
    /// The relay's own refusal code, said in words, and never with a retry suggestion attached to a
    /// state that retrying cannot change.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 48] The one message this replaced — "The relay would not accept a new
    /// pairing invitation. Try again shortly." — was true of every cause and useful for one. The
    /// relay's code is appended where it is not already the whole message, so the next occurrence
    /// is diagnosable from the screen instead of from its journal.
    /// </remarks>
    internal static string DescribeOfferRefusal(RelayRefusal refusal) => refusal.Status switch
    {
        HttpStatusCode.TooManyRequests =>
            "The relay is rate-limiting pairing attempts. Try again in a minute.",
        _ => refusal.Code switch
        {
            "offer-future-dated" or "offer-expired" =>
                "This desktop's clock and the relay's disagree by too much to pair. Check the time " +
                "on both, then start pairing again.",
            "pairing-code-malformed" =>
                "The relay could not read the pairing code this desktop generated. This desktop and " +
                "the relay are probably different builds; update the relay.",
            "attempt-duplicate" =>
                "That invitation already exists on the relay. Press Start pairing to make a new one.",
            "invitation-limit" =>
                "The relay is already holding as many pairing invitations as it allows. Wait for " +
                "them to expire, or revoke a device, then start pairing again.",
            "" =>
                "The relay refused the invitation without saying why.",
            var code when code.Contains(' ', StringComparison.Ordinal) =>
                // A sentence rather than a code: the relay could not read the invitation at all,
                // which is what a relay older than this desktop looks like.
                $"The relay could not read this invitation ({code}) — it is probably an older build " +
                "than this desktop. Update the relay.",
            var code => $"The relay refused the invitation: {code}.",
        },
    };

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
