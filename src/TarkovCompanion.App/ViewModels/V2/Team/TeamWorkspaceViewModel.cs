using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Controls.ApplicationLifetimes;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Tablet;
using TarkovCompanion.App.Views.V2.Tablet;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Team;

/// <summary>Which of the Team/Group/Tablet routes brought the workspace up.</summary>
/// <remarks>
/// Team, Group and Tablet all render this one workspace rather than three different pages (see
/// <see cref="TeamWorkspaceViewModel"/>'s own remarks), but each still names a real destination
/// with its own tab, so each shows its own primary pane (package 17: the shared plan, the group
/// form, the paired devices) beside one shared context panel.
/// </remarks>
public enum TeamWorkspaceSection
{
    Overview = 1,
    Group,
    Devices,
}

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

    /// <summary>Where they are and what they are doing, e.g. "Customs · In raid"; empty when neither is known.</summary>
    public string Detail { get; init; } = string.Empty;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>Package 29 (parity): "312, -104 · from a screenshot 40s ago", as V1's Group page said it; empty when no position was shared.</summary>
    public string Position { get; init; } = string.Empty;

    public bool HasPosition => Position.Length > 0;

    /// <summary>Package 29 (parity): the loadout and quests this member chose to share; empty when they shared neither.</summary>
    public string Shared { get; init; } = string.Empty;

    public bool HasShared => Shared.Length > 0;

    public bool IsLive => State == TeamPresenceState.Live;

    public bool IsStale => State == TeamPresenceState.Stale;
}

/// <summary>A quest somebody in the group shared, and how many of them are on it.</summary>
public sealed record TeamQuestRowViewModel(string Name, int Members)
{
    public string CountLabel => Members == 1 ? "1 member" : $"{Members} members";
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

    /// <summary>The waypoint's number on the map, or null for a ping.</summary>
    public string? Number { get; init; }

    /// <summary>What the row is called when its <see cref="Name"/> is only its number.</summary>
    public string Title { get; init; } = Name;

    /// <summary>The map, who marked it and how long ago, on one line.</summary>
    public string Detail { get; init; } = string.Empty;

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
/// notifications) carries no "since"/connection-health signal, so it does not fit the Presence
/// shape. Package 29 (parity) shows it as its own Party card instead, over the same
/// <see cref="SquadPageViewModel"/> V1 binds, which the shell attaches (<see cref="AttachParty"/>).
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

    private static readonly MapSceneLayerId GroupMarksLayerId = new("group-marks");
    private const string WaypointObjectPrefix = "group-waypoint:";
    private const string PingObjectPrefix = "group-ping:";

    private readonly RaidCockpitViewModel? _raidCockpit;
    private Action<V2RouteId>? _navigate;
    private GroupSnapshot _group = GroupSnapshot.Off;
    private MapSceneRendererViewModel? _mapPreview;
    private string? _mapPreviewSignature;
    private string _mapNote = "Loading map…";
    private TeamWorkspaceSection _activeSection = TeamWorkspaceSection.Overview;
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
        TimeProvider? clock = null,
        RaidCockpitViewModel? raidCockpit = null)
    {
        _groupSession = groupSession ?? throw new ArgumentNullException(nameof(groupSession));
        _groupSettings = groupSettings ?? throw new ArgumentNullException(nameof(groupSettings));
        _pairing = pairing;
        _clock = clock ?? TimeProvider.System;
        _raidCockpit = raidCockpit;
        if (_raidCockpit is not null)
        {
            // The centre map follows whichever map the Raid workspace shows (the top bar's map
            // picker); a rebuild there raises its own property changes.
            _raidCockpit.PropertyChanged += (_, _) => RefreshMapPreview();
        }

        if (_pairing is not null)
        {
            _pairing.PropertyChanged += PairingChanged;
        }

        SaveCommand = new AsyncDelegateCommand(SaveAsync);
        LeaveCommand = new AsyncDelegateCommand(LeaveAsync);
        PairTabletCommand = new DelegateCommand(OpenPairing);
        OpenSharedPlanCommand = new DelegateCommand(() => _navigate?.Invoke(V2Routes.Raid));
        ManageGroupCommand = new DelegateCommand(() => _navigate?.Invoke(V2Routes.Group));
        ManageDevicesCommand = new DelegateCommand(() => _navigate?.Invoke(V2Routes.Tablet));
    }

    /// <summary>
    /// Lets the context panel's links move the shell, which owns the router.
    /// </summary>
    /// <remarks>
    /// Set by <c>V2ShellViewModel</c> once it has a router; until then (and in tests that build
    /// this view model alone) the links do nothing rather than fail.
    /// </remarks>
    public void AttachNavigation(Action<V2RouteId> navigate) => _navigate = navigate;

    /// <summary>
    /// The in-game party, exactly as V1's Squad page shows it.
    /// </summary>
    /// <remarks>
    /// Attached rather than constructed: the Squad view model belongs to the legacy graph, which the
    /// shell holds and this view model (built in composition) does not. The legacy graph keeps it
    /// current on every runtime snapshot, so this only has to expose it.
    /// </remarks>
    public void AttachParty(SquadPageViewModel party)
    {
        ArgumentNullException.ThrowIfNull(party);
        Party = party;
        OnPropertyChanged(nameof(Party));
        OnPropertyChanged(nameof(HasParty));
    }

    public SquadPageViewModel? Party { get; private set; }

    public bool HasParty => Party is not null;

    /// <summary>Goes to the Raid map, where the group's waypoints are drawn.</summary>
    public ICommand OpenSharedPlanCommand { get; }

    public ICommand ManageGroupCommand { get; }

    public ICommand ManageDevicesCommand { get; }

    /// <summary>
    /// Which route brought this workspace up, so its own pane is the one shown.
    /// </summary>
    /// <remarks>
    /// Set by the shell (<c>V2ShellViewModel.Refresh</c>) from the current route on every
    /// refresh, the same way it keeps <see cref="Apply"/> current — this view model has no route
    /// type of its own to read one from.
    /// </remarks>
    public void SetActiveSection(TeamWorkspaceSection section)
    {
        if (SetProperty(ref _activeSection, section, nameof(ActiveSection)))
        {
            OnPropertyChanged(nameof(IsOverview));
            OnPropertyChanged(nameof(IsGroupSection));
            OnPropertyChanged(nameof(IsDevicesSection));
        }
    }

    public TeamWorkspaceSection ActiveSection => _activeSection;

    public bool IsOverview => _activeSection == TeamWorkspaceSection.Overview;

    public bool IsGroupSection => _activeSection == TeamWorkspaceSection.Group;

    public bool IsDevicesSection => _activeSection == TeamWorkspaceSection.Devices;

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

    /// <summary>Package 29 (parity): this player's own kit, as the rest of the group described it back (V1's "Your kit, as your party sees it").</summary>
    public string MyLoadout { get; private set; } = string.Empty;

    /// <summary>Level, side and scav timer the group knows about this player; empty until somebody else says.</summary>
    public string MyProfile { get; private set; } = string.Empty;

    public bool HasMyProfile => MyProfile.Length > 0;

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

    public bool IsConnected { get; private set; }

    public bool IsReconnecting { get; private set; }

    public string ConnectionDetail { get; private set; } = string.Empty;

    public IReadOnlyList<TeamPresenceRowViewModel> Presence { get; private set; } = [];

    public bool HasPresence => Presence.Count > 0;

    public bool HasNoPresence => Presence.Count == 0;

    public IReadOnlyList<TeamMarkRowViewModel> Marks { get; private set; } = [];

    public bool HasMarks => Marks.Count > 0;

    public bool HasNoMarks => Marks.Count == 0;

    public IReadOnlyList<TeamMarkRowViewModel> Waypoints { get; private set; } = [];

    public bool HasWaypoints => Waypoints.Count > 0;

    public IReadOnlyList<TeamMarkRowViewModel> Pings { get; private set; } = [];

    public bool HasPings => Pings.Count > 0;

    /// <summary>"4 waypoints · 1 ping", for the collapsed marks section's header.</summary>
    public string MarksSummary => Marks.Count == 0
        ? "None yet"
        : string.Join(" · ", new[]
        {
            Waypoints.Count switch { 0 => string.Empty, 1 => "1 waypoint", var count => $"{count} waypoints" },
            Pings.Count switch { 0 => string.Empty, 1 => "1 ping", var count => $"{count} pings" },
        }.Where(part => part.Length > 0));

    /// <summary>The centre map: the Raid workspace's current map carrying only the group's marks.</summary>
    public MapSceneRendererViewModel? MapPreview
    {
        get => _mapPreview;
        private set
        {
            if (ReferenceEquals(_mapPreview, value))
            {
                return;
            }

            if (_mapPreview is not null)
            {
                _mapPreview.ViewChangeRequested -= MapPreviewViewChangeRequested;
            }

            _mapPreview = value;
            if (value is not null)
            {
                value.ViewChangeRequested += MapPreviewViewChangeRequested;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMapPreview));
        }
    }

    public bool HasMapPreview => MapPreview is not null;

    /// <summary>Why the centre map is not showing; short and plain.</summary>
    public string MapNote
    {
        get => _mapNote;
        private set => SetProperty(ref _mapNote, value);
    }

    /// <summary>
    /// Rebuilds the centre map when the Raid map or the group's marks have changed.
    /// </summary>
    /// <remarks>
    /// Called on every shell refresh (the raid clock alone ticks once a second), so it compares a
    /// signature of what it would draw and returns early rather than rebuilding a scene each time.
    /// </remarks>
    internal void RefreshMapPreview()
    {
        if (_raidCockpit?.Renderer is not { } raidMap)
        {
            MapPreview = null;
            _mapPreviewSignature = null;
            MapNote = _raidCockpit is null ? "The map isn't available." : "Pick a map in the top bar to see the shared plan.";
            return;
        }

        var group = _group;
        var signature = string.Join(
            "|",
            raidMap.Scene.LocationId,
            raidMap.Scene.Revision.ToString(CultureInfo.InvariantCulture),
            string.Join(",", group.Waypoints.Select(waypoint => $"{waypoint.Id}:{waypoint.MapId}:{waypoint.X}:{waypoint.Z}:{waypoint.Label}:{waypoint.Reached}")),
            string.Join(",", group.Pings.Select(ping => $"{ping.Id}:{ping.MapId}:{ping.X}:{ping.Z}")));
        if (MapPreview is not null && signature == _mapPreviewSignature)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var preview = _raidCockpit.CreateMarksPreview(
            (_, isOnMap, project) => BuildGroupMarks(group, isOnMap, project, now),
            MapPreview);
        _mapPreviewSignature = preview is null ? null : signature;
        MapPreview = preview;
        MapNote = preview is null ? "This map has no 2D plan yet." : string.Empty;
    }

    private void MapPreviewViewChangeRequested(MapSceneViewChange change)
    {
        if (MapPreview is not { } preview)
        {
            return;
        }

        var result = MapSceneViewReducer.Apply(preview.Scene, change);
        if (result.Status is MapSceneViewChangeStatus.Applied or MapSceneViewChangeStatus.Unchanged)
        {
            preview.Present(result.Scene);
        }
    }

    /// <summary>Removes the group mark behind a map marker (the map's right-click, like Raid's).</summary>
    public void RemoveMarkAt(MapSceneObjectId objectId)
    {
        var value = objectId.Value;
        var prefix = value.StartsWith(WaypointObjectPrefix, StringComparison.Ordinal) ? WaypointObjectPrefix
            : value.StartsWith(PingObjectPrefix, StringComparison.Ordinal) ? PingObjectPrefix
            : null;
        if (prefix is not null && long.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            _ = RemoveMarkAsync(id);
        }
    }

    /// <summary>
    /// Every waypoint with its number: counted among the waypoints on the same map, in the
    /// relay's order, which is how MapViewModel numbers the dots it draws.
    /// </summary>
    /// <remarks>
    /// One numbering for both the marks list and the centre map's markers, so a row and the
    /// marker it names can never disagree.
    /// </remarks>
    internal static IReadOnlyList<(GroupWaypointView Waypoint, int Number)> NumberWaypoints(IReadOnlyList<GroupWaypointView> waypoints)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(GroupWaypointView, int)>(waypoints.Count);
        foreach (var waypoint in waypoints)
        {
            var key = waypoint.MapId;
            counts[key] = counts.GetValueOrDefault(key) + 1;
            result.Add((waypoint, counts[key]));
        }

        return result;
    }

    /// <summary>
    /// The group's marks on one map as scene objects: numbered waypoints, a line joining them
    /// in order, and pings. Marks on other maps, or that the map's transform cannot place, are
    /// left off the map (they stay in the list, where they can still be removed).
    /// </summary>
    internal static (MapSceneLayer? Layer, IReadOnlyList<MapSceneObject> Objects) BuildGroupMarks(
        GroupSnapshot group,
        Func<string, bool> isOnMap,
        Func<WorldPosition, MapScenePoint?> project,
        DateTimeOffset nowUtc)
    {
        var objects = new List<MapSceneObject>();
        var route = new List<MapScenePoint>();
        foreach (var (waypoint, number) in NumberWaypoints(group.Waypoints))
        {
            if (!isOnMap(waypoint.MapId) || project(new WorldPosition(waypoint.X, waypoint.Y, waypoint.Z)) is not { } point)
            {
                continue;
            }

            route.Add(point);
            var name = string.IsNullOrWhiteSpace(waypoint.Label) ? $"Waypoint {number}" : waypoint.Label!;
            objects.Add(new MapSceneObject(
                new($"{WaypointObjectPrefix}{waypoint.Id}"),
                GroupMarksLayerId,
                MapSceneObjectKind.Waypoint,
                MapSceneTruthKind.UserAuthored,
                number.ToString(CultureInfo.InvariantCulture),
                waypoint.Reached is { Length: > 0 } ? $"{name} · reached by {waypoint.Reached}" : $"{name} · marked by {waypoint.By}",
                MapSceneGeometry.At(point),
                [],
                new DataProvenance("group-relay", waypoint.CreatedUtc == DateTimeOffset.UnixEpoch ? nowUtc : waypoint.CreatedUtc)));
        }

        foreach (var ping in group.Pings)
        {
            if (!isOnMap(ping.MapId) || project(new WorldPosition(ping.X, ping.Y, ping.Z)) is not { } point)
            {
                continue;
            }

            objects.Add(new MapSceneObject(
                new($"{PingObjectPrefix}{ping.Id}"),
                GroupMarksLayerId,
                MapSceneObjectKind.Ping,
                MapSceneTruthKind.UserAuthored,
                "Ping",
                $"{ping.By} is pointing here",
                MapSceneGeometry.At(point),
                [],
                new DataProvenance("group-relay", ping.CreatedUtc)));
        }

        if (route.Count >= 2)
        {
            objects.Insert(0, new MapSceneObject(
                new("group-route"),
                GroupMarksLayerId,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.UserAuthored,
                "Shared route",
                "The group's waypoints, in order",
                new MapSceneGeometry(MapSceneGeometryKind.Line, route),
                [],
                new DataProvenance("group-relay", nowUtc)));
        }

        return objects.Count == 0
            ? (null, [])
            : (new MapSceneLayer(GroupMarksLayerId, "Group marks", 40, true), objects);
    }

    /// <summary>The quests the other members shared, most-shared first.</summary>
    public IReadOnlyList<TeamQuestRowViewModel> TeamQuests { get; private set; } = [];

    public bool HasTeamQuests => TeamQuests.Count > 0;

    /// <summary>"3 sharing", or empty when nobody else is.</summary>
    public string MemberCountLabel => Presence.Count == 0 ? string.Empty : $"{Presence.Count} sharing";

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
        IsConnected = group.IsSharing && group.StaleSince is null;
        IsReconnecting = group.IsSharing && group.StaleSince is not null;

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
                    : TeamPresenceState.Live)
            {
                Detail = string.Join(" · ", new[] { MapLabel(member.MapId), RaidStateLabel(member.RaidState) }.Where(part => part.Length > 0)),
                Position = member.Position is { } position
                    ? string.Create(
                        CultureInfo.CurrentCulture,
                        $"{position.X:F0}, {position.Z:F0} · from a screenshot {GroupPageViewModel.Age(member.PositionAge)}")
                    : string.Empty,
                Shared = string.Join(" · ", member.Loadout.Concat(member.Quests)),
            })
            .ToArray();

        MyLoadout = group.MyLoadout.Count > 0
            ? string.Join(" · ", group.MyLoadout)
            : group.IsSharing
                ? "Nobody in your party is running this yet."
                : "Turn sharing on, and a squadmate running this can tell you.";
        MyProfile = GroupPageViewModel.DescribeMe(group);

        TeamQuests = group.Members
            .SelectMany(member => member.Quests.Distinct(StringComparer.OrdinalIgnoreCase))
            .Where(quest => !string.IsNullOrWhiteSpace(quest))
            .GroupBy(quest => quest.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(quests => new TeamQuestRowViewModel(quests.Key, quests.Count()))
            .OrderByDescending(row => row.Members)
            .ThenBy(row => row.Name, StringComparer.CurrentCulture)
            .ToArray();

        var marks = new List<TeamMarkRowViewModel>(group.Waypoints.Count + group.Pings.Count);
        // Numbered by NumberWaypoints, the same numbering the centre map's markers carry (and in
        // the order MapViewModel numbers them in). Only waypoints are numbered; a ping is always
        // "Ping" and never carries a custom name.
        foreach (var (waypoint, numbered) in NumberWaypoints(group.Waypoints))
        {
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
                Number = numbered.ToString(CultureInfo.CurrentCulture),
                Title = string.IsNullOrWhiteSpace(waypoint.Label) ? $"Waypoint {numbered}" : waypoint.Label!,
                Detail = JoinDetail(MapLabel(waypoint.MapId), reached ? $"by {waypoint.By} · reached by {waypoint.Reached}" : $"by {waypoint.By}", age),
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
                Detail = JoinDetail(MapLabel(ping.MapId), $"by {ping.By}", $"{GroupSessionService.Ago(elapsed)} ago"),
            });
        }

        Marks = marks;
        Waypoints = marks.Where(mark => mark.Number is not null).ToArray();
        Pings = marks.Where(mark => mark.Number is null).ToArray();
        _group = group;

        OnPropertyChanged(nameof(MyLoadout));
        OnPropertyChanged(nameof(MyProfile));
        OnPropertyChanged(nameof(HasMyProfile));
        OnPropertyChanged(nameof(ConnectionHealth));
        OnPropertyChanged(nameof(ConnectionDetail));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsReconnecting));
        OnPropertyChanged(nameof(MemberCountLabel));
        OnPropertyChanged(nameof(TeamQuests));
        OnPropertyChanged(nameof(HasTeamQuests));
        OnPropertyChanged(nameof(Waypoints));
        OnPropertyChanged(nameof(HasWaypoints));
        OnPropertyChanged(nameof(Pings));
        OnPropertyChanged(nameof(HasPings));
        OnPropertyChanged(nameof(MarksSummary));
        RefreshMapPreview();
        OnPropertyChanged(nameof(Presence));
        OnPropertyChanged(nameof(HasPresence));
        OnPropertyChanged(nameof(HasNoPresence));
        OnPropertyChanged(nameof(Marks));
        OnPropertyChanged(nameof(HasMarks));
        OnPropertyChanged(nameof(HasNoMarks));
    }

    private static string JoinDetail(params string[] parts) => string.Join(" · ", parts.Where(part => part.Length > 0));

    /// <summary>
    /// A map's catalog slug ("streets-of-tarkov") as a player reads it ("Streets of Tarkov").
    /// </summary>
    /// <remarks>
    /// The relay carries the slug EftLogParser normalises every map to, not a display name, and
    /// this view model has no map catalog to look one up in; the slugs are the names hyphenated.
    /// </remarks>
    internal static string MapLabel(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return string.Empty;
        }

        var words = mapId.Trim().Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select((word, index) =>
            index > 0 && word is "of" ? word : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string RaidStateLabel(RaidLifecycleState state) => state switch
    {
        RaidLifecycleState.InRaid => "In raid",
        RaidLifecycleState.LoadingRaid => "Loading in",
        RaidLifecycleState.PostRaid => "After raid",
        RaidLifecycleState.Menu => "In menu",
        _ => string.Empty,
    };

    private async Task RemoveMarkAsync(long id) =>
        await _groupSession.RemoveMarkAsync(id, CancellationToken.None).ConfigureAwait(true);

    /// <summary>This desktop's own paired devices — never the group's members.</summary>
    public IReadOnlyList<PairedDeviceRowViewModel> Devices => _pairing?.Devices ?? [];

    public bool HasNoDevices => Devices.Count == 0;

    public string DevicesSummary => Devices.Count switch
    {
        0 => "No paired devices",
        1 => "1 paired device",
        var count => $"{count} paired devices",
    };

    public bool CanPairDevice => _pairing?.CanPair == true;

    public string PairingUnavailableReason => _pairing?.UnavailableReason ?? "Pairing isn't available on this device.";

    /// <summary>Why "Pair a tablet" is disabled, as its tooltip; null while pairing is available.</summary>
    public string? PairTabletTooltip => CanPairDevice ? null : PairingUnavailableReason;

    private void PairingChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(CompanionPairingViewModel.Devices) or nameof(CompanionPairingViewModel.HasNoDevices))
        {
            OnPropertyChanged(nameof(Devices));
            OnPropertyChanged(nameof(HasNoDevices));
            OnPropertyChanged(nameof(DevicesSummary));
        }
        else if (eventArgs.PropertyName is nameof(CompanionPairingViewModel.CanPair) or nameof(CompanionPairingViewModel.UnavailableReason))
        {
            OnPropertyChanged(nameof(CanPairDevice));
            OnPropertyChanged(nameof(PairingUnavailableReason));
            OnPropertyChanged(nameof(PairTabletTooltip));
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
