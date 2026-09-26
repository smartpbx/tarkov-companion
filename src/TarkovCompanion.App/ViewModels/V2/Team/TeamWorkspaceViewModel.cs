using TarkovCompanion.App.Localization;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Controls.ApplicationLifetimes;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Tablet;
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
        TeamPresenceState.Live => TeamText.Live,
        TeamPresenceState.Stale => TeamText.Stale,
        _ => TeamText.Offline,
    };

    /// <summary>Where they are and what they are doing, e.g. "Customs · In raid"; empty when neither is known.</summary>
    public string Detail { get; init; } = string.Empty;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>Package 29 (parity): "312, -104 · from a screenshot 40 s ago", as V1's Group page said it; empty when no position was shared.</summary>
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
    public string CountLabel => TeamText.MemberCount(Members);
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

    /// <summary>Explicit relay facts: author, lifetime, scope, and any state the snapshot carries.</summary>
    public string MetadataLabel { get; init; } = string.Empty;

    public ICommand? RemoveCommand { get; init; }

    /// <summary>#289: one of ours whose send failed; it goes out when the relay is back.</summary>
    public bool IsQueued { get; init; }
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
public sealed partial class TeamWorkspaceViewModel : BindableViewModel
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
    private readonly TarkovCompanion.Application.Services.Devices.RelayClockOffsetTracker? _relayClock;

    private static readonly MapSceneLayerId GroupMarksLayerId = new("group-marks");
    private const string WaypointObjectPrefix = "group-waypoint:";
    private const string PingObjectPrefix = "group-ping:";

    private readonly RaidCockpitViewModel? _raidCockpit;
    private Action<V2RouteId>? _navigate;
    private GroupSnapshot _group = GroupSnapshot.Off;
    private MapSceneRendererViewModel? _mapPreview;
    private string? _mapPreviewSignature;
    private string _mapNote = TeamText.LoadingMap;
    private TeamWorkspaceSection _activeSection = TeamWorkspaceSection.Overview;
    private bool _confirmingLeave;
    private string _status = TeamText.LoadingSettings;
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
        RaidCockpitViewModel? raidCockpit = null,
        // [#780] The squad's quests, named from this player's catalog. Optional like the rest.
        SquadQuestFeed? squadQuests = null,
        // [#289] The extract, note and ready state this player shares. Optional like the rest.
        GroupSquadStatus? squadStatus = null,
        // [#891] The relay stamps squad marks with its own clock; this PC's may be hours out.
        TarkovCompanion.Application.Services.Devices.RelayClockOffsetTracker? relayClock = null,
        // [#902] Local only, or the Squad sharing permission, can stop what these switches start.
        TarkovCompanion.Core.Network.INetworkPolicy? networkPolicy = null,
        Action<Action>? dispatch = null,
        // [#712 T7] This player's own Loadout check and level, and the maps table for plan names.
        GroupReadyCheckShare? readyCheck = null,
        TarkovCompanion.Core.Abstractions.IMapDataService? mapData = null)
    {
        _relayClock = relayClock;
        _groupSession = groupSession ?? throw new ArgumentNullException(nameof(groupSession));
        _groupSettings = groupSettings ?? throw new ArgumentNullException(nameof(groupSettings));
        _pairing = pairing;
        _clock = clock ?? TimeProvider.System;
        _raidCockpit = raidCockpit;
        AttachSquadQuests(squadQuests);
        AttachSquadStatus(squadStatus);
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
        PairTabletCommand = new DelegateCommand(ShowDevices);
        OpenSharedPlanCommand = new DelegateCommand(() => _navigate?.Invoke(V2Routes.Raid));
        ManageGroupCommand = new DelegateCommand(() => _navigate?.Invoke(V2Routes.Group));
        ManageDevicesCommand = new DelegateCommand(() => _navigate?.Invoke(V2Routes.Tablet));
        // Every other workspace has one. Without it, a Team pane that failed to read its settings
        // had no way back short of restarting the application.
        ReloadCommand = new AsyncDelegateCommand(LoadAsync);
        AttachSharingState(networkPolicy, dispatch);
        AttachSquadPlan(readyCheck, mapData);
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

    /// <summary>Reads the stored settings again, after a load that failed.</summary>
    public ICommand ReloadCommand { get; }

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
    /// <remarks>
    /// This was the one workspace load with no catch of its own, and the shell discarded the task
    /// it returned. A settings file that could not be read therefore produced an empty Team pane
    /// with nothing on it and nothing in the log, and it stayed that way until the application was
    /// restarted — "panes/tabs not rendering at all until a restart", reported 2026-09-19. It now
    /// says what happened in the log and on the pane, and <see cref="ReloadCommand"/> is a way back
    /// without a restart.
    /// </remarks>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A switch flipped a moment ago is written first, or this read would put it back.
            await PendingSave.ConfigureAwait(true);
            var stored = await _groupSettings.GetAsync(cancellationToken).ConfigureAwait(true);
            ApplyStored(stored);
            Status = HasUnsavedFields ? TeamText.UnsavedFields : stored.IsEnabled ? TeamText.Saved : TeamText.NotSharing;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The raw exception goes to the log, the pane says what a player can do about it.
            // Not rethrown: Reload runs this same method, and a Reload that throws would travel
            // out through the command's async void and be handled by the window instead of by the
            // pane the player is looking at.
            CrashLog.Write("workspace-fault/team", $"load: {exception}");
            Status = TeamText.SettingsUnreadable;
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Whether anything is published at all — the one switch that joins or leaves a group.</summary>
    /// <remarks>[#902] Saved the moment it is flipped, like the two below it.</remarks>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                SwitchFlipped();
            }
        }
    }

    public string ServerUri
    {
        get => _serverUri;
        set
        {
            if (SetProperty(ref _serverUri, value))
            {
                FieldEdited();
            }
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
            {
                FieldEdited();
            }
        }
    }

    /// <summary>The one thing a group agrees between themselves — typing a new one creates a group; an existing one joins it.</summary>
    public string Key
    {
        get => _key;
        set
        {
            if (SetProperty(ref _key, value))
            {
                FieldEdited();
            }
        }
    }

    public bool SharesLoadout
    {
        get => _sharesLoadout;
        set
        {
            if (SetProperty(ref _sharesLoadout, value))
            {
                SwitchFlipped();
            }
        }
    }

    public bool SharesQuests
    {
        get => _sharesQuests;
        set
        {
            if (SetProperty(ref _sharesQuests, value))
            {
                SwitchFlipped();
            }
        }
    }

    /// <summary>Package 29 (parity): this player's own kit, as the rest of the group described it back (V1's "Your kit, as your party sees it").</summary>
    public string MyLoadout { get; private set; } = string.Empty;

    /// <summary>Level, side and scav timer the group knows about this player; empty until somebody else says.</summary>
    public string MyProfile { get; private set; } = string.Empty;

    public bool HasMyProfile => MyProfile.Length > 0;

    public string LeaveLabel => _confirmingLeave ? TeamText.ConfirmLeave : TeamText.LeaveGroup;

    public ICommand SaveCommand { get; }

    public ICommand LeaveCommand { get; }

    public ICommand PairTabletCommand { get; }

    private async Task SaveAsync()
    {
        var settings = new GroupSharingSettings(IsEnabled, Trimmed(ServerUri), Trimmed(DisplayName), Trimmed(Key), SharesLoadout, SharesQuests)
        {
            SharesReadyCheck = SharesReadyCheck,
        };
        if (!await SaveOwnAsync(_ => settings).ConfigureAwait(true))
        {
            return;
        }

        _confirmingLeave = false;
        OnPropertyChanged(nameof(LeaveLabel));
        Status = SavedStatus(settings);
    }

    /// <summary>Leaves the group on the second press, so a stray click cannot end sharing by accident.</summary>
    private async Task LeaveAsync()
    {
        if (!_confirmingLeave)
        {
            _confirmingLeave = true;
            OnPropertyChanged(nameof(LeaveLabel));
            Status = TeamText.PressConfirmLeave;
            return;
        }

        _confirmingLeave = false;
        OnPropertyChanged(nameof(LeaveLabel));
        IsEnabled = false;
        await PendingSave.ConfigureAwait(true);
    }

    private static string? Trimmed(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public string ConnectionHealth { get; private set; } = TeamText.NotInAGroup;

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
        ? TeamText.MarksNoneYet
        : string.Join(" · ", new[]
        {
            Waypoints.Count switch { 0 => string.Empty, var count => TeamText.WaypointCount(count) },
            Pings.Count switch { 0 => string.Empty, var count => TeamText.PingCount(count) },
            // #289: said in the header too, so a collapsed list still admits it.
            Marks.Count(mark => mark.IsQueued) switch { 0 => string.Empty, var count => TeamText.QueuedCount(count) },
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

            var dropped = _mapPreview;
            _mapPreview = value;
            if (value is not null)
            {
                value.ViewChangeRequested += MapPreviewViewChangeRequested;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMapPreview));
            // [#775] After the view has moved off it: its leases keep the cockpit's pictures alive.
            dropped?.ReleasePictures();
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
            MapNote = _raidCockpit is null ? TeamText.MapUnavailable : TeamText.PickAMap;
            return;
        }

        var group = _group;
        var signature = string.Join(
            "|",
            raidMap.Scene.LocationId,
            raidMap.Scene.Revision.ToString(CultureInfo.InvariantCulture),
            // [#775] A replaced cockpit picture re-presents the preview rather than being kept.
            _raidCockpit.BackgroundSha,
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
        MapNote = preview is null ? TeamText.NoPlanForMap : string.Empty;
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
            var name = string.IsNullOrWhiteSpace(waypoint.Label) ? TeamText.WaypointNumbered(number) : waypoint.Label!;
            objects.Add(new MapSceneObject(
                new($"{WaypointObjectPrefix}{waypoint.Id}"),
                GroupMarksLayerId,
                MapSceneObjectKind.Waypoint,
                MapSceneTruthKind.UserAuthored,
                number.ToString(CultureInfo.InvariantCulture),
                waypoint.Reached is { Length: > 0 } ? TeamText.NameReachedBy(name, waypoint.Reached!) : TeamText.NameMarkedBy(name, waypoint.By),
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
                TeamText.Ping,
                TeamText.PointingHere(ping.By),
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
                TeamText.SharedRoute,
                TeamText.SharedRouteDetail,
                new MapSceneGeometry(MapSceneGeometryKind.Line, route),
                [],
                new DataProvenance("group-relay", nowUtc)));
        }

        return objects.Count == 0
            ? (null, [])
            : (new MapSceneLayer(GroupMarksLayerId, TeamText.GroupMarks, 40, true), objects);
    }

    /// <summary>The quests the other members shared, most-shared first.</summary>
    public IReadOnlyList<TeamQuestRowViewModel> TeamQuests { get; private set; } = [];

    public bool HasTeamQuests => TeamQuests.Count > 0;

    /// <summary>"3 sharing", or empty when nobody else is.</summary>
    public string MemberCountLabel => Presence.Count == 0 ? string.Empty : TeamText.SharingCount(Presence.Count);

    /// <summary>Rebuilds presence and marks from the runtime snapshot the shell already refreshes on.</summary>
    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var group = snapshot.Group;
        var now = _clock.GetUtcNow();

        ConnectionHealth = !group.IsSharing
            ? TeamText.NotInAGroup
            : group.StaleSince is not null
                ? TeamText.Reconnecting
                : TeamText.Connected;
        ConnectionDetail = SetupText.GroupStatus(group);
        IsConnected = group.IsSharing && group.StaleSince is null;
        IsReconnecting = group.IsSharing && group.StaleSince is not null;

        // While reconnecting, the members list is the last good read rather than a current one,
        // so every member is shown as offline instead of live/stale — a stale connection cannot
        // vouch for anybody's individual state.
        var reconnecting = group.IsSharing && group.StaleSince is not null;
        Presence = group.Members
            .Select(member => new TeamPresenceRowViewModel(
                member.Name,
                member.Since is { } since ? TeamText.Ago(UnitText.Duration(since)) : TeamText.JustNow,
                reconnecting ? TeamPresenceState.Offline
                    : member.HasGoneQuiet ? TeamPresenceState.Stale
                    : TeamPresenceState.Live)
            {
                Detail = string.Join(" · ", new[] { MapLabel(member.MapId), RaidStateLabel(member.RaidState) }.Where(part => part.Length > 0)),
                Position = member.Position is { } position
                    ? TeamText.PositionFromScreenshot(position.X, position.Z, GroupPageViewModel.Age(member.PositionAge))
                    : string.Empty,
                // [#289] Their ready state, extract and note first: the squad's question before a raid.
                Shared = JoinDetail([DescribeStatus(member), .. member.Loadout, .. member.Quests]),
            })
            .ToArray();

        MyLoadout = group.MyLoadout.Count > 0
            ? string.Join(" · ", group.MyLoadout)
            : group.IsSharing
                ? TeamText.NobodyRunning
                : TeamText.TurnSharingOn;
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
        // [#891] Waypoints and pings carry the relay's time, so their ages are measured on it: a
        // PC four hours fast read every ping as "4 h ago · Expiring" the moment it arrived.
        var relayNow = _relayClock?.ToRelayTime(now) ?? now;
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
                ? TeamText.AgeUnknown
                : TeamText.Ago(UnitText.Duration(relayNow - waypoint.CreatedUtc));
            marks.Add(new(
                waypoint.Id,
                TeamText.Waypoint,
                name,
                waypoint.MapId,
                reached ? TeamText.MarkedByReachedBy(waypoint.By, waypoint.Reached!) : TeamText.MarkedBy(waypoint.By),
                age,
                null,
                reached)
            {
                RemoveCommand = new AsyncDelegateCommand(() => RemoveMarkAsync(
                    waypoint.Id,
                    new RemovedMark(waypoint.MapId, new WorldPosition(waypoint.X, waypoint.Y, waypoint.Z), waypoint.Label, IsPing: false, name))),
                Number = numbered.ToString(CultureInfo.CurrentCulture),
                Title = string.IsNullOrWhiteSpace(waypoint.Label) ? TeamText.WaypointNumbered(numbered) : waypoint.Label!,
                Detail = JoinDetail(MapLabel(waypoint.MapId), age),
                // #289: scope and time first, so a narrow panel trims the author rather than them.
                MetadataLabel = JoinDetail(
                    TeamText.ScopeSquad,
                    OwnMarkTtl(waypoint.Id, now) ?? TeamText.TtlUntilRemoved,
                    reached ? TeamText.ByReachedBy(waypoint.By, waypoint.Reached!) : TeamText.By(waypoint.By),
                    reconnecting ? TeamText.OfflineSnapshot : string.Empty),
            });
        }

        foreach (var ping in group.Pings)
        {
            var elapsed = relayNow - ping.CreatedUtc;
            var remaining = PingLifetime - elapsed;
            marks.Add(new(
                ping.Id,
                TeamText.Ping,
                string.IsNullOrWhiteSpace(ping.Label) ? TeamText.Ping : ping.Label!,
                ping.MapId,
                TeamText.PingedBy(ping.By),
                TeamText.Ago(UnitText.Duration(elapsed)),
                remaining > TimeSpan.Zero ? TeamText.TimeLeft(UnitText.Duration(remaining)) : TeamText.Expiring,
                false)
            {
                RemoveCommand = new AsyncDelegateCommand(() => RemoveMarkAsync(
                    ping.Id,
                    new RemovedMark(ping.MapId, new WorldPosition(ping.X, ping.Y, ping.Z), ping.Label, IsPing: true, TeamText.Ping))),
                Detail = JoinDetail(MapLabel(ping.MapId), TeamText.Ago(UnitText.Duration(elapsed))),
                MetadataLabel = JoinDetail(
                    TeamText.ScopeSquad,
                    OwnMarkTtl(ping.Id, now) ?? TeamText.Ttl(remaining > TimeSpan.Zero ? TeamText.TimeLeft(UnitText.Duration(remaining)) : TeamText.Expiring),
                    TeamText.By(ping.By),
                    reconnecting ? TeamText.OfflineSnapshot : string.Empty),
            });
        }

        marks.AddRange(PrivateMarkRows(now));
        marks.AddRange(QueuedMarkRows(now));
        Marks = marks;
        Waypoints = marks.Where(mark => mark.Number is not null).ToArray();
        Pings = marks.Where(mark => mark.Number is null).ToArray();
        _group = group;
        RefreshModeWarning(group);
        RefreshSquadStatus(group);
        RefreshSquadPlan(group);

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
    internal static string MapLabel(string? mapId) => TarkovCompanion.Core.Domain.Maps.MapDisplayName.FromId(mapId);

    private static string RaidStateLabel(RaidLifecycleState state) => state switch
    {
        RaidLifecycleState.InRaid => TeamText.InRaid,
        RaidLifecycleState.LoadingRaid => TeamText.LoadingIn,
        RaidLifecycleState.PostRaid => TeamText.AfterRaid,
        RaidLifecycleState.Menu => TeamText.InMenu,
        _ => string.Empty,
    };

    /// <summary>What a removed mark was, so it can be put back.</summary>
    private sealed record RemovedMark(string MapId, WorldPosition Position, string? Label, bool IsPing, string Name);

    private RemovedMark? _undoable;

    /// <summary>Whether the last removal can still be put back.</summary>
    public bool CanUndoRemove => _undoable is not null;

    /// <summary>What Undo would put back, named, so it is not a blind button.</summary>
    public string UndoRemoveLabel => _undoable is { } mark ? TeamText.PutBack(mark.Name) : string.Empty;

    /// <summary>Puts the last removed mark back.</summary>
    public ICommand UndoRemoveCommand => _undoRemoveCommand ??= new AsyncDelegateCommand(UndoRemoveAsync);

    private ICommand? _undoRemoveCommand;

    /// <summary>
    /// Removes a mark and offers to put it back.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 60 — Team] #289 asks for preview, confirmation or undo "as appropriate",
    /// and for a mark undo is the appropriate one: a confirmation dialog on every removed ping
    /// would be in the way constantly, and putting one back costs nothing. Revoking a device is
    /// the opposite case and is confirmed instead, because it cannot be put back.
    ///
    /// It comes back as a *new* mark with a new id and a new time, because that is all the relay
    /// offers; the label and place are the same. Anything watching mark ids sees a new one.
    /// </remarks>
    private async Task RemoveMarkAsync(long id, RemovedMark? removed = null)
    {
        if (await _groupSession.RemoveMarkAsync(id, CancellationToken.None).ConfigureAwait(true))
        {
            SetUndoable(removed);
        }
    }

    private async Task UndoRemoveAsync()
    {
        if (_undoable is not { } mark)
        {
            return;
        }

        SetUndoable(null);
        await _groupSession
            .MarkAsync(mark.MapId, mark.Position, mark.Label, mark.IsPing, CancellationToken.None)
            .ConfigureAwait(true);
    }

    private void SetUndoable(RemovedMark? mark)
    {
        _undoable = mark;
        OnPropertyChanged(nameof(CanUndoRemove));
        OnPropertyChanged(nameof(UndoRemoveLabel));
    }

    /// <summary>This desktop's own paired devices — never the group's members.</summary>
    public IReadOnlyList<PairedDeviceRowViewModel> Devices => _pairing?.Devices ?? [];

    public bool HasNoDevices => Devices.Count == 0;

    public string DevicesSummary => Devices.Count switch
    {
        0 => TeamText.NoPairedDevices,
        var count => TeamText.PairedDeviceCount(count),
    };

    public bool CanPairDevice => _pairing?.CanPair == true;

    public string PairingUnavailableReason => _pairing?.UnavailableReason ?? TeamText.PairingUnavailableHere;

    /// <summary>Why "Pair a tablet" is disabled, as its tooltip; null while pairing is available.</summary>
    public string? PairTabletTooltip => CanPairDevice ? null : PairingUnavailableReason;

    /// <summary>
    /// What a paired device is doing to this desktop right now, and the one action that ends it.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 24, #407] Control is only safe if the desktop says out loud that
    /// something else is driving it and can take it back without hunting for a setting — which is
    /// what the tablet concept (docs/design/v2/v2-tablet-desktop-control-concept.png) shows.
    /// </remarks>
    /// <summary>
    /// The pairing panel, bound directly rather than forwarded property by property.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 48] Pairing used to be a bare popout <c>Window</c> with its own title bar
    /// and no way back to the page behind it — the only V2 surface that was not in the shell. It is
    /// a section of this workspace now, so the view binds its fields through here instead of this
    /// view model growing a forwarding property for each one.
    /// </remarks>
    public CompanionPairingViewModel? Pairing => _pairing;

    public string? ControlRequestMessage => _pairing?.ControlRequestMessage;

    public bool HasControlRequest => _pairing?.HasControlRequest == true;

    public string? ControlHolderMessage => _pairing?.ControlHolderMessage;

    public bool HasControlHolder => _pairing?.HasControlHolder == true;

    public ICommand? AllowControlCommand => _pairing?.AllowControlCommand;

    public ICommand? DenyControlCommand => _pairing?.DenyControlCommand;

    public ICommand? TakeBackControlCommand => _pairing?.TakeBackControlCommand;

    private void PairingChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(CompanionPairingViewModel.ControlRequestMessage)
            or nameof(CompanionPairingViewModel.HasControlRequest)
            or nameof(CompanionPairingViewModel.ControlHolderMessage)
            or nameof(CompanionPairingViewModel.HasControlHolder))
        {
            OnPropertyChanged(nameof(ControlRequestMessage));
            OnPropertyChanged(nameof(HasControlRequest));
            OnPropertyChanged(nameof(ControlHolderMessage));
            OnPropertyChanged(nameof(HasControlHolder));
        }

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

    /// <summary>
    /// Brings pairing on screen, which now means going to the section it lives in.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 48] This used to construct and show <c>CompanionPairingWindow</c>. The
    /// window is deleted; leaving it would have been a second way in to the same ceremony, which is
    /// how the two drifted in the first place. The automation ids on both "Pair a tablet" buttons
    /// are unchanged, so anything that pressed them still reaches pairing — it just stays in the
    /// shell now.
    /// </remarks>
    private void ShowDevices()
    {
        if (ActiveSection != TeamWorkspaceSection.Devices)
        {
            _navigate?.Invoke(V2Routes.Tablet);
        }
    }
}
