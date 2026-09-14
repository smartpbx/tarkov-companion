using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

public enum CanonicalAggregateKind
{
    DeviceModes = 1,
    Workspace,
    Marks,
    CaptureIntent,
}

public enum CompanionInteractionMode
{
    Follow = 1,
    ControlPending,
    Control,
    Independent,
}

public enum WorkspaceKind
{
    Raid = 1,
    Intel,
    Plan,
    Team,
    Debrief,
}

public enum CoordinateSpaceKind
{
    World = 1,
    Normalized,
}

public enum WorkspaceSelectionKind
{
    MapLocation = 1,
    Landmark,
    Item,
    Objective,
    Plan,
    Result,
    Mark,
}

public enum WorkspaceDialogKind
{
    PairingApproval = 1,
    DeviceRevocation,
    MultiDeviceConflict,
    TeamMemberRemoval,
}

/// <summary>A versioned map coordinate; desktop pixels can never enter canonical state.</summary>
public sealed record MapCoordinate
{
    public MapCoordinate(
        string mapId,
        string? floorId,
        CoordinateSpaceKind coordinateSpace,
        string projectionVersion,
        double x,
        double? y,
        double z)
    {
        MapId = ProtocolGuard.Required(mapId, nameof(mapId), ProtocolBounds.MaxShortStringBytes);
        FloorId = ProtocolGuard.Optional(floorId, nameof(floorId), ProtocolBounds.MaxShortStringBytes);
        CoordinateSpace = ProtocolGuard.Defined(coordinateSpace, nameof(coordinateSpace));
        ProjectionVersion = ProtocolGuard.Required(
            projectionVersion,
            nameof(projectionVersion),
            ProtocolBounds.MaxShortStringBytes);

        if (!double.IsFinite(x) || !double.IsFinite(z) || (y is { } height && !double.IsFinite(height)))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Coordinates must be finite.");
        }

        if (coordinateSpace == CoordinateSpaceKind.Normalized)
        {
            if (x is < 0 or > 1 || z is < 0 or > 1 || y is not null)
            {
                throw new ArgumentOutOfRangeException(nameof(x), "Normalized coordinates use x/z in [0,1] and no height.");
            }
        }
        else if (Math.Abs(x) > ProtocolBounds.MaxWorldCoordinateMagnitude ||
                 Math.Abs(z) > ProtocolBounds.MaxWorldCoordinateMagnitude ||
                 Math.Abs(y ?? 0) > ProtocolBounds.MaxWorldCoordinateMagnitude)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "World coordinates exceed the protocol envelope.");
        }

        X = x;
        Y = y;
        Z = z;
    }

    public string MapId { get; }

    public string? FloorId { get; }

    public CoordinateSpaceKind CoordinateSpace { get; }

    public string ProjectionVersion { get; }

    public double X { get; }

    public double? Y { get; }

    public double Z { get; }
}

public sealed record WorkspaceViewport(MapCoordinate Center, double Zoom)
{
    public MapCoordinate Center { get; } = ProtocolGuard.NotNull(Center, nameof(Center));

    public double Zoom { get; } = double.IsFinite(Zoom) && Zoom is >= 0.01 and <= 100
        ? Zoom
        : throw new ArgumentOutOfRangeException(nameof(Zoom));
}

public sealed record WorkspaceSelection(
    WorkspaceSelectionKind Kind,
    string ReferenceId,
    string? OriginDeepLink,
    string? FocusToken)
{
    public WorkspaceSelectionKind Kind { get; } = ProtocolGuard.Defined(Kind, nameof(Kind));

    public string ReferenceId { get; } = ProtocolGuard.Required(
        ReferenceId,
        nameof(ReferenceId),
        ProtocolBounds.MaxShortStringBytes);

    public string? OriginDeepLink { get; } = ProtocolGuard.Optional(OriginDeepLink, nameof(OriginDeepLink));

    public string? FocusToken { get; } = ProtocolGuard.Optional(
        FocusToken,
        nameof(FocusToken),
        ProtocolBounds.MaxShortStringBytes);
}

public sealed record WorkspaceProjection
{
    public WorkspaceProjection(
        WorkspaceKind workspace,
        string? mapId,
        string? floorId,
        WorkspaceViewport? viewport,
        WorkspaceSelection? selection,
        IReadOnlyList<string> visibleObjectiveIds,
        IReadOnlyList<string> planIds,
        string? searchQuery,
        IReadOnlyList<string> resultIds,
        IReadOnlyList<string> activeLayers,
        IReadOnlyList<string> activeFilters,
        WorkspaceDialogKind? dialog)
    {
        Workspace = ProtocolGuard.Defined(workspace, nameof(workspace));
        MapId = ProtocolGuard.Optional(mapId, nameof(mapId), ProtocolBounds.MaxShortStringBytes);
        FloorId = ProtocolGuard.Optional(floorId, nameof(floorId), ProtocolBounds.MaxShortStringBytes);
        Viewport = viewport;
        Selection = selection;
        VisibleObjectiveIds = BoundedStrings(visibleObjectiveIds, nameof(visibleObjectiveIds));
        PlanIds = BoundedStrings(planIds, nameof(planIds));
        SearchQuery = ProtocolGuard.Optional(searchQuery, nameof(searchQuery));
        ResultIds = BoundedStrings(resultIds, nameof(resultIds));
        ActiveLayers = BoundedStrings(activeLayers, nameof(activeLayers));
        ActiveFilters = BoundedStrings(activeFilters, nameof(activeFilters));
        Dialog = dialog is { } visible ? ProtocolGuard.Defined(visible, nameof(dialog)) : null;

        if (viewport is not null && (MapId is null || !string.Equals(viewport.Center.MapId, MapId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("A viewport belongs to the projection's map.", nameof(viewport));
        }

        if (viewport is not null && !string.Equals(viewport.Center.FloorId, FloorId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A viewport belongs to the projection's floor.", nameof(viewport));
        }
    }

    public WorkspaceKind Workspace { get; }

    public string? MapId { get; }

    public string? FloorId { get; }

    public WorkspaceViewport? Viewport { get; }

    public WorkspaceSelection? Selection { get; }

    public IReadOnlyList<string> VisibleObjectiveIds { get; }

    public IReadOnlyList<string> PlanIds { get; }

    public string? SearchQuery { get; }

    public IReadOnlyList<string> ResultIds { get; }

    public IReadOnlyList<string> ActiveLayers { get; }

    public IReadOnlyList<string> ActiveFilters { get; }

    public WorkspaceDialogKind? Dialog { get; }

    private static IReadOnlyList<string> BoundedStrings(IReadOnlyList<string> values, string parameterName) =>
        ProtocolGuard.List(
            ProtocolGuard.List(values, parameterName)
                .Select(value => ProtocolGuard.Required(value, parameterName, ProtocolBounds.MaxShortStringBytes)),
            parameterName);
}

public enum MapMarkKind
{
    Ping = 1,
    Waypoint,
    RoutePoint,
    Note,
}

public enum MapMarkScope
{
    Private = 1,
    PairedDevice,
    Team,
}

public sealed record MapMarkDraft
{
    public MapMarkDraft(
        MapMarkKind kind,
        MapMarkScope scope,
        MapCoordinate coordinate,
        string? label,
        string color,
        DateTimeOffset? expiresUtc)
    {
        Kind = ProtocolGuard.Defined(kind, nameof(kind));
        Scope = ProtocolGuard.Defined(scope, nameof(scope));
        Coordinate = ProtocolGuard.NotNull(coordinate, nameof(coordinate));
        Label = ProtocolGuard.Optional(label, nameof(label));
        Color = ValidateColor(color);
        ExpiresUtc = ProtocolGuard.UtcOptional(expiresUtc, nameof(expiresUtc));
    }

    public MapMarkKind Kind { get; }

    public MapMarkScope Scope { get; }

    public MapCoordinate Coordinate { get; }

    public string? Label { get; }

    public string Color { get; }

    public DateTimeOffset? ExpiresUtc { get; }

    internal static string ValidateColor(string value)
    {
        var color = ProtocolGuard.Required(value, nameof(value), 9);
        if (color.Length != 7 || color[0] != '#' || color.Skip(1).Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A mark color is #RRGGBB.", nameof(value));
        }

        return color.ToUpperInvariant();
    }
}

public sealed record MapMark
{
    public MapMark(
        MarkId markId,
        long revision,
        MapMarkKind kind,
        MapMarkScope scope,
        CompanionDeviceId authorDeviceId,
        MapCoordinate coordinate,
        string? label,
        string color,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        DateTimeOffset? expiresUtc)
    {
        MarkId = markId.Value == Guid.Empty ? throw new ArgumentException("A mark id is required.", nameof(markId)) : markId;
        Revision = ProtocolGuard.Positive(revision, nameof(revision));
        Kind = ProtocolGuard.Defined(kind, nameof(kind));
        Scope = ProtocolGuard.Defined(scope, nameof(scope));
        AuthorDeviceId = authorDeviceId.Value == Guid.Empty
            ? throw new ArgumentException("An author device is required.", nameof(authorDeviceId))
            : authorDeviceId;
        Coordinate = ProtocolGuard.NotNull(coordinate, nameof(coordinate));
        Label = ProtocolGuard.Optional(label, nameof(label));
        Color = MapMarkDraft.ValidateColor(color);
        CreatedUtc = ProtocolGuard.Utc(createdUtc, nameof(createdUtc));
        UpdatedUtc = ProtocolGuard.Utc(updatedUtc, nameof(updatedUtc));
        ExpiresUtc = ProtocolGuard.UtcOptional(expiresUtc, nameof(expiresUtc));

        if (UpdatedUtc < CreatedUtc || ExpiresUtc <= CreatedUtc)
        {
            throw new ArgumentException("Mark timestamps are not monotonic.");
        }

        if (kind == MapMarkKind.Ping &&
            (ExpiresUtc is null || ExpiresUtc - CreatedUtc > ProtocolBounds.PingLifetime))
        {
            throw new ArgumentException("A ping expires within 45 seconds and is never persistent.", nameof(expiresUtc));
        }
    }

    public MarkId MarkId { get; }

    public long Revision { get; }

    public MapMarkKind Kind { get; }

    public MapMarkScope Scope { get; }

    public CompanionDeviceId AuthorDeviceId { get; }

    public MapCoordinate Coordinate { get; }

    public string? Label { get; }

    public string Color { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset UpdatedUtc { get; }

    public DateTimeOffset? ExpiresUtc { get; }
}

public sealed record AggregateCursor
{
    public AggregateCursor(AggregateRevision revision, CommandId? lastChangeId)
    {
        if ((revision.Value == 0) != (lastChangeId is null))
        {
            throw new ArgumentException("Revision zero has no change id; a positive revision names its change.", nameof(lastChangeId));
        }

        Revision = revision;
        LastChangeId = lastChangeId;
    }

    public AggregateRevision Revision { get; }

    public CommandId? LastChangeId { get; }

    public static AggregateCursor Empty { get; } = new(new AggregateRevision(0), null);
}

public sealed record DeviceModeEntry(
    CompanionDeviceId DeviceId,
    CompanionInteractionMode Mode,
    DateTimeOffset ChangedUtc)
{
    public CompanionDeviceId DeviceId { get; } = DeviceId.Value == Guid.Empty
        ? throw new ArgumentException("A device id is required.", nameof(DeviceId))
        : DeviceId;

    public CompanionInteractionMode Mode { get; } = ProtocolGuard.Defined(Mode, nameof(Mode));

    public DateTimeOffset ChangedUtc { get; } = ProtocolGuard.Utc(ChangedUtc, nameof(ChangedUtc));
}

public sealed record PendingControlRequest(
    CommandId RequestCommandId,
    CompanionDeviceId DeviceId,
    DeviceSessionId SessionId,
    DateTimeOffset RequestedUtc,
    DateTimeOffset ExpiresUtc,
    TimeSpan RequestedLease)
{
    public CommandId RequestCommandId { get; } = RequestCommandId.Value == Guid.Empty
        ? throw new ArgumentException("A request command id is required.", nameof(RequestCommandId))
        : RequestCommandId;

    public CompanionDeviceId DeviceId { get; } = DeviceId.Value == Guid.Empty
        ? throw new ArgumentException("A requesting device id is required.", nameof(DeviceId))
        : DeviceId;

    public DeviceSessionId SessionId { get; } = SessionId.Value == Guid.Empty
        ? throw new ArgumentException("A requesting session id is required.", nameof(SessionId))
        : SessionId;

    public DateTimeOffset RequestedUtc { get; } = ProtocolGuard.Utc(RequestedUtc, nameof(RequestedUtc));

    public DateTimeOffset ExpiresUtc { get; } = ProtocolGuard.Utc(ExpiresUtc, nameof(ExpiresUtc)) <= RequestedUtc
        ? throw new ArgumentOutOfRangeException(nameof(ExpiresUtc))
        : ExpiresUtc;

    public TimeSpan RequestedLease { get; } = RequestedLease > TimeSpan.Zero &&
                                              RequestedLease <= ProtocolBounds.MaximumControlLeaseLifetime
        ? RequestedLease
        : throw new ArgumentOutOfRangeException(nameof(RequestedLease));
}

public sealed record ControlLease(
    ControlLeaseId LeaseId,
    CompanionDeviceId DeviceId,
    DeviceSessionId SessionId,
    DateTimeOffset GrantedUtc,
    DateTimeOffset ExpiresUtc)
{
    public ControlLeaseId LeaseId { get; } = LeaseId.Value == Guid.Empty
        ? throw new ArgumentException("A control lease id is required.", nameof(LeaseId))
        : LeaseId;

    public CompanionDeviceId DeviceId { get; } = DeviceId.Value == Guid.Empty
        ? throw new ArgumentException("A control device id is required.", nameof(DeviceId))
        : DeviceId;

    public DeviceSessionId SessionId { get; } = SessionId.Value == Guid.Empty
        ? throw new ArgumentException("A control session id is required.", nameof(SessionId))
        : SessionId;

    public DateTimeOffset GrantedUtc { get; } = ProtocolGuard.Utc(GrantedUtc, nameof(GrantedUtc));

    public DateTimeOffset ExpiresUtc { get; } = ProtocolGuard.Utc(ExpiresUtc, nameof(ExpiresUtc)) <= GrantedUtc ||
                                               ExpiresUtc - GrantedUtc > ProtocolBounds.MaximumControlLeaseLifetime
        ? throw new ArgumentOutOfRangeException(nameof(ExpiresUtc))
        : ExpiresUtc;
}

public sealed record DeviceModeAggregate
{
    public DeviceModeAggregate(
        AggregateCursor cursor,
        IReadOnlyList<DeviceModeEntry> devices,
        PendingControlRequest? pendingControl,
        ControlLease? controlLease)
    {
        Cursor = ProtocolGuard.NotNull(cursor, nameof(cursor));
        Devices = ProtocolGuard.List(devices, nameof(devices), ProtocolBounds.MaxDevices);
        PendingControl = pendingControl;
        ControlLease = controlLease;

        if (Devices.Select(item => item.DeviceId).Distinct().Count() != Devices.Count)
        {
            throw new ArgumentException("A device has one interaction mode.", nameof(devices));
        }

        if (controlLease is not null && !Devices.Any(item =>
                item.DeviceId == controlLease.DeviceId && item.Mode == CompanionInteractionMode.Control))
        {
            throw new ArgumentException("A control lease must name the device in Control mode.", nameof(controlLease));
        }

        if (pendingControl is not null && !Devices.Any(item =>
                item.DeviceId == pendingControl.DeviceId && item.Mode == CompanionInteractionMode.ControlPending))
        {
            throw new ArgumentException("A pending request must name the device in ControlPending mode.", nameof(pendingControl));
        }
    }

    public AggregateCursor Cursor { get; }

    public IReadOnlyList<DeviceModeEntry> Devices { get; }

    public PendingControlRequest? PendingControl { get; }

    public ControlLease? ControlLease { get; }
}

public sealed record WorkspaceAggregate(AggregateCursor Cursor, WorkspaceProjection Projection)
{
    public AggregateCursor Cursor { get; } = ProtocolGuard.NotNull(Cursor, nameof(Cursor));

    public WorkspaceProjection Projection { get; } = ProtocolGuard.NotNull(Projection, nameof(Projection));
}

public sealed record MarkAggregate
{
    public MarkAggregate(AggregateCursor cursor, IReadOnlyList<MapMark> marks)
    {
        Cursor = ProtocolGuard.NotNull(cursor, nameof(cursor));
        Marks = ProtocolGuard.List(marks, nameof(marks), ProtocolBounds.MaxMarks);
        if (Marks.Select(mark => mark.MarkId).Distinct().Count() != Marks.Count)
        {
            throw new ArgumentException("Mark ids are unique.", nameof(marks));
        }
    }

    public AggregateCursor Cursor { get; }

    public IReadOnlyList<MapMark> Marks { get; }
}

public sealed record CaptureIntentAggregate(AggregateCursor Cursor, ContextualCaptureIntent? ActiveIntent)
{
    public AggregateCursor Cursor { get; } = ProtocolGuard.NotNull(Cursor, nameof(Cursor));
}

public enum CommandDisposition
{
    Applied = 1,
    Duplicate,
    PendingDesktopApproval,
    RejectedStale,
    RejectedConflict,
    RejectedExpired,
    RejectedUnauthorized,
    RejectedInvalidState,
    RequiresPreview,
    UnsupportedVersion,
}

public sealed record RecentCommandReceipt(
    CommandId CommandId,
    CompanionDeviceId DeviceId,
    CanonicalAggregateKind Aggregate,
    AggregateRevision RequestedRevision,
    AggregateRevision AppliedRevision,
    CommandDisposition Disposition,
    DateTimeOffset ExpiresUtc)
{
    public CommandId CommandId { get; } = CommandId.Value == Guid.Empty
        ? throw new ArgumentException("A command id is required.", nameof(CommandId))
        : CommandId;

    public CompanionDeviceId DeviceId { get; } = DeviceId.Value == Guid.Empty
        ? throw new ArgumentException("A device id is required.", nameof(DeviceId))
        : DeviceId;

    public CanonicalAggregateKind Aggregate { get; } = ProtocolGuard.Defined(Aggregate, nameof(Aggregate));

    public CommandDisposition Disposition { get; } = ProtocolGuard.Defined(Disposition, nameof(Disposition));

    public DateTimeOffset ExpiresUtc { get; } = ProtocolGuard.Utc(ExpiresUtc, nameof(ExpiresUtc));
}

public sealed record CanonicalCompanionState
{
    public CanonicalCompanionState(
        AuthorityEpoch authorityEpoch,
        GlobalRevision globalRevision,
        CompanionDeviceId desktopDeviceId,
        DeviceModeAggregate deviceModes,
        WorkspaceAggregate workspace,
        MarkAggregate marks,
        CaptureIntentAggregate captureIntent,
        IReadOnlyList<RecentCommandReceipt>? recentCommands = null)
    {
        AuthorityEpoch = authorityEpoch.Value == Guid.Empty
            ? throw new ArgumentException("An authority epoch is required.", nameof(authorityEpoch))
            : authorityEpoch;
        GlobalRevision = globalRevision;
        DesktopDeviceId = desktopDeviceId.Value == Guid.Empty
            ? throw new ArgumentException("A desktop device id is required.", nameof(desktopDeviceId))
            : desktopDeviceId;
        DeviceModes = ProtocolGuard.NotNull(deviceModes, nameof(deviceModes));
        Workspace = ProtocolGuard.NotNull(workspace, nameof(workspace));
        Marks = ProtocolGuard.NotNull(marks, nameof(marks));
        CaptureIntent = ProtocolGuard.NotNull(captureIntent, nameof(captureIntent));
        RecentCommands = ProtocolGuard.List(
            recentCommands ?? [],
            nameof(recentCommands),
            ProtocolBounds.MaxRecentCommands);

        if (RecentCommands.Select(item => (item.DeviceId, item.CommandId)).Distinct().Count() != RecentCommands.Count)
        {
            throw new ArgumentException("Recent command ids are unique per authenticated device.", nameof(recentCommands));
        }
    }

    public AuthorityEpoch AuthorityEpoch { get; }

    public GlobalRevision GlobalRevision { get; }

    public CompanionDeviceId DesktopDeviceId { get; }

    public DeviceModeAggregate DeviceModes { get; }

    public WorkspaceAggregate Workspace { get; }

    public MarkAggregate Marks { get; }

    public CaptureIntentAggregate CaptureIntent { get; }

    [JsonIgnore]
    public IReadOnlyList<RecentCommandReceipt> RecentCommands { get; }

    public AggregateCursor Cursor(CanonicalAggregateKind aggregate) => aggregate switch
    {
        CanonicalAggregateKind.DeviceModes => DeviceModes.Cursor,
        CanonicalAggregateKind.Workspace => Workspace.Cursor,
        CanonicalAggregateKind.Marks => Marks.Cursor,
        CanonicalAggregateKind.CaptureIntent => CaptureIntent.Cursor,
        _ => throw new ArgumentOutOfRangeException(nameof(aggregate)),
    };

    internal CanonicalCompanionState With(
        GlobalRevision globalRevision,
        DeviceModeAggregate? deviceModes = null,
        WorkspaceAggregate? workspace = null,
        MarkAggregate? marks = null,
        CaptureIntentAggregate? captureIntent = null,
        IReadOnlyList<RecentCommandReceipt>? recentCommands = null) =>
        new(
            AuthorityEpoch,
            globalRevision,
            DesktopDeviceId,
            deviceModes ?? DeviceModes,
            workspace ?? Workspace,
            marks ?? Marks,
            captureIntent ?? CaptureIntent,
            recentCommands ?? RecentCommands);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DeviceModeCanonicalUpdate), "deviceMode")]
[JsonDerivedType(typeof(WorkspaceCanonicalUpdate), "workspace")]
[JsonDerivedType(typeof(MarksCanonicalUpdate), "marks")]
[JsonDerivedType(typeof(CaptureCanonicalUpdate), "captureIntent")]
public abstract record CanonicalUpdate
{
    private protected CanonicalUpdate(
        AuthorityEpoch authorityEpoch,
        GlobalRevision globalRevision,
        CommandId changeId,
        DateTimeOffset changedUtc)
    {
        AuthorityEpoch = authorityEpoch.Value == Guid.Empty
            ? throw new ArgumentException("An authority epoch is required.", nameof(authorityEpoch))
            : authorityEpoch;
        GlobalRevision = globalRevision.Value > 0
            ? globalRevision
            : throw new ArgumentOutOfRangeException(nameof(globalRevision));
        ChangeId = changeId.Value == Guid.Empty
            ? throw new ArgumentException("A change id is required.", nameof(changeId))
            : changeId;
        ChangedUtc = ProtocolGuard.Utc(changedUtc, nameof(changedUtc));
    }

    public AuthorityEpoch AuthorityEpoch { get; }

    public GlobalRevision GlobalRevision { get; }

    public CommandId ChangeId { get; }

    public DateTimeOffset ChangedUtc { get; }

    [JsonIgnore]
    public abstract CanonicalAggregateKind Aggregate { get; }
}

public sealed record DeviceModeCanonicalUpdate : CanonicalUpdate
{
    public DeviceModeCanonicalUpdate(
        AuthorityEpoch authorityEpoch,
        GlobalRevision globalRevision,
        CommandId changeId,
        DateTimeOffset changedUtc,
        DeviceModeAggregate state)
        : base(authorityEpoch, globalRevision, changeId, changedUtc) =>
        State = ProtocolGuard.NotNull(state, nameof(state));

    public DeviceModeAggregate State { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.DeviceModes;
}

public sealed record WorkspaceCanonicalUpdate : CanonicalUpdate
{
    public WorkspaceCanonicalUpdate(
        AuthorityEpoch authorityEpoch,
        GlobalRevision globalRevision,
        CommandId changeId,
        DateTimeOffset changedUtc,
        WorkspaceAggregate state)
        : base(authorityEpoch, globalRevision, changeId, changedUtc) =>
        State = ProtocolGuard.NotNull(state, nameof(state));

    public WorkspaceAggregate State { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.Workspace;
}

public sealed record MarksCanonicalUpdate : CanonicalUpdate
{
    public MarksCanonicalUpdate(
        AuthorityEpoch authorityEpoch,
        GlobalRevision globalRevision,
        CommandId changeId,
        DateTimeOffset changedUtc,
        MarkAggregate state)
        : base(authorityEpoch, globalRevision, changeId, changedUtc) =>
        State = ProtocolGuard.NotNull(state, nameof(state));

    public MarkAggregate State { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.Marks;
}

public sealed record CaptureCanonicalUpdate : CanonicalUpdate
{
    public CaptureCanonicalUpdate(
        AuthorityEpoch authorityEpoch,
        GlobalRevision globalRevision,
        CommandId changeId,
        DateTimeOffset changedUtc,
        CaptureIntentAggregate state)
        : base(authorityEpoch, globalRevision, changeId, changedUtc) =>
        State = ProtocolGuard.NotNull(state, nameof(state));

    public CaptureIntentAggregate State { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.CaptureIntent;
}
