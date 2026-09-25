using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Network;
using TarkovCompanion.Core.Network;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// [#292] Setup › Data &amp; Network: Local only, and under it one row per thing that leaves the PC,
/// each with what it sends and whether it is on right now.
/// </summary>
/// <remarks>
/// A row's switch is the player's choice for that service and stays as they left it under Local only;
/// the state beside it says what is actually happening ("Local only · off"), so turning Local only
/// off brings back exactly what was on before.
/// </remarks>
public sealed class SetupNetworkControlsViewModel : BindableViewModel
{
    private readonly NetworkPolicyService _policy;
    private readonly Action<Action> _dispatch;
    private readonly Func<bool> _startedOffline;
    private bool _isLocalOnly;
    private string _localOnlyState = string.Empty;
    private bool _localOnlyInForce;
    private bool _isForced;
    private bool _wasReset;

    /// <param name="startedOffline">
    /// [#902 P6] Whether this run started without the network. The switch showed "Off" while the Data
    /// page said "Offline mode is on"; the state beside the switch now says "On · started offline".
    /// </param>
    public SetupNetworkControlsViewModel(NetworkPolicyService policy, Action<Action>? dispatch = null, Func<bool>? startedOffline = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _dispatch = dispatch ?? (static action => action());
        _startedOffline = startedOffline ?? (static () => false);
        ToggleLocalOnlyCommand = new DelegateCommand(
            () => _policy.Set(_policy.Controls with { LocalOnly = !_policy.Controls.LocalOnly }));
        Rows =
        [
            new(_policy, NetworkService.GameData),
            new(_policy, NetworkService.SquadSharing),
            new(_policy, NetworkService.UpdateChecks),
            new(_policy, NetworkService.ProblemReports),
            new(_policy, NetworkService.TarkovTracker),
        ];
        Refresh();
        _policy.Changed += (_, _) => _dispatch(Refresh);
    }

    public string Heading => SetupText.NetworkHeading;

    public string LocalOnlyTitle => SetupText.NetworkLocalOnlyTitle;

    public string LocalOnlyLine => SetupText.NetworkLocalOnlyLine;

    public string ForcedNote => SetupText.NetworkLocalOnlyForced;

    public string ResetNote => SetupText.NetworkLocalOnlyReset;

    /// <summary>
    /// network.json could not be read at startup, so Local only was turned on in its place (#888).
    /// Gone once the player sets anything here.
    /// </summary>
    public bool WasReset
    {
        get => _wasReset;
        private set => SetProperty(ref _wasReset, value);
    }

    /// <summary>What is in force: the switch, or the environment variable holding it on.</summary>
    public bool IsLocalOnly
    {
        get => _isLocalOnly;
        private set => SetProperty(ref _isLocalOnly, value);
    }

    /// <summary>TARKOV_COMPANION_OFFLINE is set, so the switch cannot turn Local only off.</summary>
    public bool IsForced
    {
        get => _isForced;
        private set
        {
            if (SetProperty(ref _isForced, value))
            {
                OnPropertyChanged(nameof(CanToggleLocalOnly));
            }
        }
    }

    public bool CanToggleLocalOnly => !IsForced;

    /// <summary>Local only is in force, by the switch, the variable, or a run that started offline.</summary>
    public bool LocalOnlyInForce
    {
        get => _localOnlyInForce;
        private set => SetProperty(ref _localOnlyInForce, value);
    }

    /// <summary>"On", "Off", or "On · started offline" when the switch is off but this run began without the network.</summary>
    public string LocalOnlyState
    {
        get => _localOnlyState;
        private set => SetProperty(ref _localOnlyState, value);
    }

    public ICommand ToggleLocalOnlyCommand { get; }

    public IReadOnlyList<SetupNetworkServiceRowViewModel> Rows { get; }

    private void Refresh()
    {
        IsForced = _policy.LocalOnlyForced;
        IsLocalOnly = IsForced || _policy.Controls.LocalOnly;
        WasReset = _policy.RecoveredFromUnreadableFile;
        var startedOffline = _startedOffline();
        LocalOnlyInForce = IsLocalOnly || startedOffline;
        LocalOnlyState = SetupText.NetworkLocalOnlyState(_policy.Controls.LocalOnly, IsForced, startedOffline);
        foreach (var row in Rows)
        {
            row.Refresh();
        }
    }
}

/// <summary>One service: what it sends, its switch (none for game data) and its state now.</summary>
public sealed class SetupNetworkServiceRowViewModel : BindableViewModel
{
    private readonly NetworkPolicyService _policy;
    private bool _isOn;
    private bool _canToggle;
    private string _stateLabel = string.Empty;
    private bool _isBlocked;

    internal SetupNetworkServiceRowViewModel(NetworkPolicyService policy, NetworkService service)
    {
        _policy = policy;
        Service = service;
        ToggleCommand = new DelegateCommand(() =>
        {
            if (HasSwitch)
            {
                _policy.Set(_policy.Controls.With(Service, !_policy.Controls.IsOn(Service)));
            }
        });
    }

    public NetworkService Service { get; }

    public string Title => SetupText.NetworkServiceTitle(Service);

    public string Line => SetupText.NetworkServiceLine(Service);

    public string AutomationId => $"v2-setup-network-{Service}";

    /// <summary>[#902 P9] The switch itself, which is where the palette lands for this service.</summary>
    public string SwitchAutomationId => $"{AutomationId}-switch";

    /// <summary>Game data has no switch of its own; only Local only stops it.</summary>
    public bool HasSwitch => Service != NetworkService.GameData;

    /// <summary>The player's choice for this service, kept under Local only.</summary>
    public bool IsOn
    {
        get => _isOn;
        private set => SetProperty(ref _isOn, value);
    }

    /// <summary>False under Local only: the switch would change nothing until it is off.</summary>
    public bool CanToggle
    {
        get => _canToggle;
        private set => SetProperty(ref _canToggle, value);
    }

    /// <summary>"On", "Local only · off" or "Off".</summary>
    public string StateLabel
    {
        get => _stateLabel;
        private set => SetProperty(ref _stateLabel, value);
    }

    public bool IsBlocked
    {
        get => _isBlocked;
        private set => SetProperty(ref _isBlocked, value);
    }

    public ICommand ToggleCommand { get; }

    internal void Refresh()
    {
        var verdict = _policy.Check(Service);
        IsOn = _policy.Controls.IsOn(Service);
        CanToggle = HasSwitch && verdict != NetworkVerdict.LocalOnly;
        StateLabel = SetupText.NetworkState(verdict);
        IsBlocked = verdict != NetworkVerdict.Allowed;
    }
}
