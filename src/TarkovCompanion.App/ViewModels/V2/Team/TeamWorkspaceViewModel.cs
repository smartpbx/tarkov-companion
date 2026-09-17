using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Controls.ApplicationLifetimes;
using TarkovCompanion.App.ViewModels.V2.Tablet;
using TarkovCompanion.App.Views.V2.Tablet;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.ViewModels.V2.Team;

/// <summary>Whether a group member's last report is current, ageing, or too old to trust.</summary>
public enum TeamPresenceState
{
    Live = 1,
    Stale,
    Offline,
}

/// <summary>One group member, as the relay last described them.</summary>
public sealed record TeamPresenceRowViewModel(string Name, string SinceLabel, TeamPresenceState State)
{
    public string StateLabel => State switch
    {
        TeamPresenceState.Live => "Live",
        TeamPresenceState.Stale => "Stale",
        _ => "Offline",
    };

    public string StateColor => State switch
    {
        TeamPresenceState.Live => "#77B895",
        TeamPresenceState.Stale => "#C6A15B",
        _ => "#8F9BA6",
    };
}

/// <summary>One of the group's marks — a waypoint or a ping — with who, when, and (for a ping) how long it has left.</summary>
public sealed record TeamMarkRowViewModel(
    long Id,
    string Kind,
    string Name,
    string MapId,
    string ByLabel,
    string AgeLabel,
    string? RemainingLabel,
    bool IsReached)
{
    public bool HasRemaining => RemainingLabel is { Length: > 0 };

    public ICommand? RemoveCommand { get; init; }
}

/// <summary>
/// The V2 Team workspace: the group's presence and marks, joining/leaving/creating a group, and
/// this desktop's paired devices — replacing the V1 Squad and Group pages' passthroughs.
/// </summary>
/// <remarks>
/// Built directly over the services those V1 pages already use (<see cref="GroupSessionService"/>
/// for marks and its published <see cref="GroupSnapshot"/> for presence, <see
/// cref="IGroupSettingsStore"/> for the join/leave/create form) rather than wrapping the V1 page
/// view models, the same way <c>DebriefWorkspaceViewModel</c> replaced the History passthrough.
///
/// The in-game party (V1's Squad page — who you are running with, from the game's own party
/// notifications) is not folded in here yet: it carries no "since"/connection-health signal, so it
/// does not fit the Presence shape the rough scope asks for. Deferred to polish.
/// </remarks>
public sealed class TeamWorkspaceViewModel : BindableViewModel
{
    /// <summary>
    /// How long a ping is shown before the relay forgets it.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>GroupServer.GroupMarks.PingLifetime</c>, which the relay does not publish. The
    /// server, not this countdown, is what actually removes an expired ping; this only tells the
    /// player how much longer "look here" is worth trusting.
    /// </remarks>
    private static readonly TimeSpan PingLifetime = TimeSpan.FromSeconds(45);

    private readonly GroupSessionService _groupSession;
    private readonly IGroupSettingsStore _groupSettings;
    private readonly CompanionPairingViewModel? _pairing;
    private readonly TimeProvider _clock;

    private bool _confirmingLeave;
    private string _status = "Loading group settings…";
    private bool _isEnabled;
    private string _serverUri = string.Empty;
    private string _displayName = string.Empty;
    private string _key = string.Empty;
    private bool _sharesLoadout;
    private bool _sharesQuests;

    public TeamWorkspaceViewModel(
        GroupSessionService groupSession,
        IGroupSettingsStore groupSettings,
        CompanionPairingViewModel? pairing = null,
        TimeProvider? clock = null)
    {
        _groupSession = groupSession ?? throw new ArgumentNullException(nameof(groupSession));
        _groupSettings = groupSettings ?? throw new ArgumentNullException(nameof(groupSettings));
        _pairing = pairing;
        _clock = clock ?? TimeProvider.System;
        if (_pairing is not null)
        {
            _pairing.PropertyChanged += PairingChanged;
        }

        SaveCommand = new AsyncDelegateCommand(SaveAsync);
        LeaveCommand = new AsyncDelegateCommand(LeaveAsync);
        PairTabletCommand = new DelegateCommand(OpenPairing);
    }

    public Task LoadAsync() => LoadAsync(CancellationToken.None);

    /// <summary>Reads the stored group settings into the join/leave/create form.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var stored = await _groupSettings.GetAsync(cancellationToken).ConfigureAwait(true);
        IsEnabled = stored.IsEnabled;
        ServerUri = stored.ServerUri ?? string.Empty;
        DisplayName = stored.DisplayName ?? string.Empty;
        Key = stored.Key ?? string.Empty;
        SharesLoadout = stored.SharesLoadout;
        SharesQuests = stored.SharesQuests;
        Status = stored.IsEnabled ? "Saved" : "Not sharing";
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Whether anything is published at all — the one switch that joins or leaves a group.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string ServerUri
    {
        get => _serverUri;
        set => SetProperty(ref _serverUri, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    /// <summary>The one thing a group agrees between themselves — typing a new one creates a group; an existing one joins it.</summary>
    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
    }

    public bool SharesLoadout
    {
        get => _sharesLoadout;
        set => SetProperty(ref _sharesLoadout, value);
    }

    public bool SharesQuests
    {
        get => _sharesQuests;
        set => SetProperty(ref _sharesQuests, value);
    }

    public string LeaveLabel => _confirmingLeave ? "Confirm leave" : "Leave group";

    public ICommand SaveCommand { get; }

    public ICommand LeaveCommand { get; }

    public ICommand PairTabletCommand { get; }

    private async Task SaveAsync()
    {
        var settings = new GroupSharingSettings(IsEnabled, Trimmed(ServerUri), Trimmed(DisplayName), Trimmed(Key), SharesLoadout, SharesQuests);
        await _groupSettings.SaveAsync(settings, CancellationToken.None).ConfigureAwait(true);
        _confirmingLeave = false;
        OnPropertyChanged(nameof(LeaveLabel));
        Status = !settings.IsEnabled
            ? "Saved · sharing is off"
            : settings.MissingPiece is { } missing
                ? $"Saved · still needs {missing}"
                : "Saved · sharing starts in a few seconds";
    }

    /// <summary>Leaves the group on the second press, so a stray click cannot end sharing by accident.</summary>
    private async Task LeaveAsync()
    {
        if (!_confirmingLeave)
        {
            _confirmingLeave = true;
            OnPropertyChanged(nameof(LeaveLabel));
            Status = "Press “Confirm leave” to stop sharing.";
            return;
        }

        IsEnabled = false;
        await SaveAsync().ConfigureAwait(true);
    }

    private static string? Trimmed(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public string ConnectionHealth { get; private set; } = "Not in a group";

    public string ConnectionDetail { get; private set; } = string.Empty;

    public IReadOnlyList<TeamPresenceRowViewModel> Presence { get; private set; } = [];

    public bool HasPresence => Presence.Count > 0;

    public bool HasNoPresence => Presence.Count == 0;

    public IReadOnlyList<TeamMarkRowViewModel> Marks { get; private set; } = [];

    public bool HasMarks => Marks.Count > 0;

    public bool HasNoMarks => Marks.Count == 0;

    /// <summary>Rebuilds presence and marks from the runtime snapshot the shell already refreshes on.</summary>
    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var group = snapshot.Group;
        var now = _clock.GetUtcNow();

        ConnectionHealth = !group.IsSharing
            ? "Not in a group"
            : group.StaleSince is not null
                ? "Reconnecting"
                : "Connected";
        ConnectionDetail = group.Detail;

        // While reconnecting, the members list is the last good read rather than a current one,
        // so every member is shown as offline instead of live/stale — a stale connection cannot
        // vouch for anybody's individual state.
        var reconnecting = group.IsSharing && group.StaleSince is not null;
        Presence = group.Members
            .Select(member => new TeamPresenceRowViewModel(
                member.Name,
                member.Since is { } since ? $"{GroupSessionService.Ago(since)} ago" : "just now",
                reconnecting ? TeamPresenceState.Offline
                    : member.HasGoneQuiet ? TeamPresenceState.Stale
                    : TeamPresenceState.Live))
            .ToArray();

        var marks = new List<TeamMarkRowViewModel>(group.Waypoints.Count + group.Pings.Count);
        // Numbered in the same pass and the same order MapViewModel numbers them in, so a mark's
        // number here matches the one on the map. Only waypoints are numbered; a ping is always
        // "Ping" and never carries a custom name.
        var numbered = 0;
        foreach (var waypoint in group.Waypoints)
        {
            numbered++;
            var reached = waypoint.Reached is { Length: > 0 };
            var name = string.IsNullOrWhiteSpace(waypoint.Label)
                ? numbered.ToString(CultureInfo.CurrentCulture)
                : waypoint.Label!;
            var age = waypoint.CreatedUtc == DateTimeOffset.UnixEpoch
                ? "age unknown"
                : $"{GroupSessionService.Ago(now - waypoint.CreatedUtc)} ago";
            marks.Add(new(
                waypoint.Id,
                "Waypoint",
                name,
                waypoint.MapId,
                reached ? $"marked by {waypoint.By} · reached by {waypoint.Reached}" : $"marked by {waypoint.By}",
                age,
                null,
                reached)
            {
                RemoveCommand = new AsyncDelegateCommand(() => RemoveMarkAsync(waypoint.Id)),
            });
        }

        foreach (var ping in group.Pings)
        {
            var elapsed = now - ping.CreatedUtc;
            var remaining = PingLifetime - elapsed;
            marks.Add(new(
                ping.Id,
                "Ping",
                string.IsNullOrWhiteSpace(ping.Label) ? "Ping" : ping.Label!,
                ping.MapId,
                $"pinged by {ping.By}",
                $"{GroupSessionService.Ago(elapsed)} ago",
                remaining > TimeSpan.Zero ? $"{GroupSessionService.Ago(remaining)} left" : "expiring",
                false)
            {
                RemoveCommand = new AsyncDelegateCommand(() => RemoveMarkAsync(ping.Id)),
            });
        }

        Marks = marks;

        OnPropertyChanged(nameof(ConnectionHealth));
        OnPropertyChanged(nameof(ConnectionDetail));
        OnPropertyChanged(nameof(Presence));
        OnPropertyChanged(nameof(HasPresence));
        OnPropertyChanged(nameof(HasNoPresence));
        OnPropertyChanged(nameof(Marks));
        OnPropertyChanged(nameof(HasMarks));
        OnPropertyChanged(nameof(HasNoMarks));
    }

    private async Task RemoveMarkAsync(long id) =>
        await _groupSession.RemoveMarkAsync(id, CancellationToken.None).ConfigureAwait(true);

    /// <summary>This desktop's own paired devices — never the group's members.</summary>
    public IReadOnlyList<PairedDeviceRowViewModel> Devices => _pairing?.Devices ?? [];

    public bool HasNoDevices => Devices.Count == 0;

    public bool CanPairDevice => _pairing?.CanPair == true;

    public string PairingUnavailableReason => _pairing?.UnavailableReason ?? "Pairing isn't available on this device.";

    private void PairingChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(CompanionPairingViewModel.Devices) or nameof(CompanionPairingViewModel.HasNoDevices))
        {
            OnPropertyChanged(nameof(Devices));
            OnPropertyChanged(nameof(HasNoDevices));
        }
    }

    /// <summary>Opens the pairing ceremony (#383) as its own window, the same one Settings opens.</summary>
    private void OpenPairing()
    {
        if (_pairing is null)
        {
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new CompanionPairingWindow(_pairing);
            if (desktop.MainWindow is { } owner)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }
        }
    }
}
