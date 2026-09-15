using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

public enum CanonicalAggregateKind
{
    DeviceModes = 1,
    Workspace,
    Marks,
    CaptureIntent,
    ProfilePreferences,
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

    /// <summary>A companion deep link such as <c>tarkov-companion://objective/objective-1</c>; never a shell or file URI.</summary>
    public string? OriginDeepLink { get; } = ProtocolGuard.DeepLink(OriginDeepLink, nameof(OriginDeepLink));

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

/// <summary>
/// A requested mark. Its map, floor, plane coordinates, label, and expiry are the Core
/// <see cref="MapMarkState"/>, the single v2 source for a user's own map mark; the paired protocol
/// adds only the mark kind, scope, coordinate space, projection version, optional height, and color.
/// </summary>
public sealed record MapMarkDraft
{
    public MapMarkDraft(
        MapMarkKind kind,
        MapMarkScope scope,
        MapMarkState state,
        CoordinateSpaceKind coordinateSpace,
        string projectionVersion,
        double? height,
        string color)
    {
        Kind = ProtocolGuard.Defined(kind, nameof(kind));
        Scope = ProtocolGuard.Defined(scope, nameof(scope));
        CoordinateSpace = ProtocolGuard.Defined(coordinateSpace, nameof(coordinateSpace));
        State = MarkPlacement.Validate(state, coordinateSpace, height, nameof(state));
        ProjectionVersion = ProtocolGuard.Required(projectionVersion, nameof(projectionVersion), ProtocolBounds.MaxShortStringBytes);
        Height = height;
        Color = ValidateColor(color);
    }

    public MapMarkKind Kind { get; }

    public MapMarkScope Scope { get; }

    public MapMarkState State { get; }

    public CoordinateSpaceKind CoordinateSpace { get; }

    public string ProjectionVersion { get; }

    public double? Height { get; }

    public string Color { get; }

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
        CommandId lastChangeId,
        MapMarkKind kind,
        MapMarkScope scope,
        CompanionDeviceId authorDeviceId,
        MapMarkState state,
        CoordinateSpaceKind coordinateSpace,
        string projectionVersion,
        double? height,
        string color,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc)
    {
        MarkId = markId.Value == Guid.Empty ? throw new ArgumentException("A mark id is required.", nameof(markId)) : markId;
        Revision = revision is > 0 and <= ProtocolBounds.MaxWireInteger
            ? revision
            : throw new ArgumentOutOfRangeException(nameof(revision));
        LastChangeId = lastChangeId.Value == Guid.Empty
            ? throw new ArgumentException("The change that set this mark revision is required.", nameof(lastChangeId))
            : lastChangeId;
        Kind = ProtocolGuard.Defined(kind, nameof(kind));
        Scope = ProtocolGuard.Defined(scope, nameof(scope));
        AuthorDeviceId = authorDeviceId.Value == Guid.Empty
            ? throw new ArgumentException("An author device is required.", nameof(authorDeviceId))
            : authorDeviceId;
        CoordinateSpace = ProtocolGuard.Defined(coordinateSpace, nameof(coordinateSpace));
        State = MarkPlacement.Validate(state, coordinateSpace, height, nameof(state));
        ProjectionVersion = ProtocolGuard.Required(projectionVersion, nameof(projectionVersion), ProtocolBounds.MaxShortStringBytes);
        Height = height;
        Color = MapMarkDraft.ValidateColor(color);
        CreatedUtc = ProtocolGuard.Utc(createdUtc, nameof(createdUtc));
        UpdatedUtc = ProtocolGuard.Utc(updatedUtc, nameof(updatedUtc));

        if (UpdatedUtc < CreatedUtc || ExpiresUtc <= CreatedUtc)
        {
            throw new ArgumentException("Mark timestamps are not monotonic.");
        }

        if (kind == MapMarkKind.Ping &&
            (ExpiresUtc is null || ExpiresUtc - CreatedUtc > ProtocolBounds.PingLifetime))
        {
            throw new ArgumentException("A ping expires within 45 seconds of creation and is never persistent.", nameof(state));
        }
    }

    public MarkId MarkId { get; }

    public long Revision { get; }

    /// <summary>The command or maintenance change that produced this mark revision.</summary>
    public CommandId LastChangeId { get; }

    public MapMarkKind Kind { get; }

    public MapMarkScope Scope { get; }

    public CompanionDeviceId AuthorDeviceId { get; }

    /// <summary>The Core v2 mark payload: map, floor, plane X/Y, label, and expiry.</summary>
    public MapMarkState State { get; }

    public CoordinateSpaceKind CoordinateSpace { get; }

    public string ProjectionVersion { get; }

    public double? Height { get; }

    public string Color { get; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset UpdatedUtc { get; }

    [JsonIgnore]
    public DateTimeOffset? ExpiresUtc => State.ExpiresUtc;
}

/// <summary>Validates the paired placement rules over a Core mark payload.</summary>
internal static class MarkPlacement
{
    public static MapMarkState Validate(MapMarkState? state, CoordinateSpaceKind coordinateSpace, double? height, string parameterName)
    {
        var mark = ProtocolGuard.NotNull(state, parameterName);
        ProtocolGuard.Required(mark.MapId, parameterName, ProtocolBounds.MaxShortStringBytes);
        ProtocolGuard.Optional(mark.FloorId, parameterName, ProtocolBounds.MaxShortStringBytes);
        ProtocolGuard.UtcOptional(mark.ExpiresUtc, parameterName);
        if (coordinateSpace == CoordinateSpaceKind.Normalized)
        {
            if (mark.X is < 0 or > 1 || mark.Y is < 0 or > 1 || height is not null)
            {
                throw new ArgumentOutOfRangeException(parameterName, "Normalized marks use X/Y in [0,1] and no height.");
            }
        }
        else if (Math.Abs(mark.X) > ProtocolBounds.MaxWorldCoordinateMagnitude ||
                 Math.Abs(mark.Y) > ProtocolBounds.MaxWorldCoordinateMagnitude ||
                 (height is { } value && (!double.IsFinite(value) || Math.Abs(value) > ProtocolBounds.MaxWorldCoordinateMagnitude)))
        {
            throw new ArgumentOutOfRangeException(parameterName, "World marks stay inside the protocol coordinate envelope.");
        }

        return mark;
    }
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

    public TimeSpan RequestedLease { get; } = ProtocolGuard.LeaseDuration(RequestedLease, nameof(RequestedLease));
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

        // Control and ControlPending are never free-standing: exactly the lease holder is in
        // Control and exactly the requester is pending, so no device can keep a mode whose lease
        // or request was released.
        var controlled = Devices.Where(item => item.Mode == CompanionInteractionMode.Control).ToArray();
        if (controlled.Length != (controlLease is null ? 0 : 1) ||
            (controlLease is not null && controlled[0].DeviceId != controlLease.DeviceId))
        {
            throw new ArgumentException("A control lease names the only device in Control mode.", nameof(controlLease));
        }

        var requesting = Devices.Where(item => item.Mode == CompanionInteractionMode.ControlPending).ToArray();
        if (requesting.Length != (pendingControl is null ? 0 : 1) ||
            (pendingControl is not null && requesting[0].DeviceId != pendingControl.DeviceId))
        {
            throw new ArgumentException("A pending request names the only device in ControlPending mode.", nameof(pendingControl));
        }
    }

    public AggregateCursor Cursor { get; }

    public IReadOnlyList<DeviceModeEntry> Devices { get; }

    public PendingControlRequest? PendingControl { get; }

    public ControlLease? ControlLease { get; }

    /// <summary>A paired device without an entry follows the desktop.</summary>
    public CompanionInteractionMode ModeOf(CompanionDeviceId deviceId) =>
        Devices.FirstOrDefault(item => item.DeviceId == deviceId)?.Mode ?? CompanionInteractionMode.Follow;
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

/// <summary>
/// The desktop's answer to one command. The first four members share the revision rules of the
/// v2 <c>AcknowledgementDisposition</c>; the rest are narrower paired-protocol rejections that
/// leave canonical state untouched. <see cref="CommandAcknowledgement"/> enforces the table in
/// docs/PAIRED_DEVICE_PROTOCOL.md.
/// </summary>
public enum CommandDisposition
{
    Applied = 1,
    RejectedStale,
    RejectedConflict,
    UnsupportedVersion,
    RejectedExpired,
    RejectedUnauthorized,
    RejectedInvalidState,
    RequiresPreview,
    RequiresSnapshot,
    RejectedCommandIdReuse,
    UnsupportedPreferenceSchema,
}

/// <summary>
/// Desktop-local idempotency memory for one applied command. It is never sent to a tablet: the
/// fingerprint of another device's command is not canonical state.
/// </summary>
public sealed record RecentCommandReceipt(
    CommandId CommandId,
    CommandFingerprint Fingerprint,
    CompanionDeviceId DeviceId,
    CanonicalAggregateKind Aggregate,
    AggregateRevision AppliedRevision,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc)
{
    public CommandId CommandId { get; } = CommandId.Value == Guid.Empty
        ? throw new ArgumentException("A command id is required.", nameof(CommandId))
        : CommandId;

    public CommandFingerprint Fingerprint { get; } = string.IsNullOrWhiteSpace(Fingerprint.Value)
        ? throw new ArgumentException("A command fingerprint is required.", nameof(Fingerprint))
        : Fingerprint;

    public CompanionDeviceId DeviceId { get; } = DeviceId.Value == Guid.Empty
        ? throw new ArgumentException("A device id is required.", nameof(DeviceId))
        : DeviceId;

    public CanonicalAggregateKind Aggregate { get; } = ProtocolGuard.Defined(Aggregate, nameof(Aggregate));

    public AggregateRevision AppliedRevision { get; } = AppliedRevision.Value > 0
        ? AppliedRevision
        : throw new ArgumentOutOfRangeException(nameof(AppliedRevision), "An applied command occupies a positive revision.");

    /// <summary>The command's own issue time; a retry of the same command repeats it.</summary>
    public DateTimeOffset IssuedUtc { get; } = ProtocolGuard.Utc(IssuedUtc, nameof(IssuedUtc));

    public DateTimeOffset ExpiresUtc { get; } = ProtocolGuard.Utc(ExpiresUtc, nameof(ExpiresUtc));
}

public sealed record CanonicalCompanionState
{
    public CanonicalCompanionState(
        AuthorityEpoch authorityEpoch,
        WorkspaceId workspaceId,
        string desktopInstanceId,
        GlobalRevision globalRevision,
        CompanionDeviceId desktopDeviceId,
        DeviceModeAggregate deviceModes,
        WorkspaceAggregate workspace,
        MarkAggregate marks,
        CaptureIntentAggregate captureIntent,
        ProfilePreferencesAggregate profilePreferences,
        IReadOnlyList<RecentCommandReceipt>? recentCommands = null,
        DateTimeOffset? receiptHorizonUtc = null)
    {
        AuthorityEpoch = authorityEpoch.Value == Guid.Empty
            ? throw new ArgumentException("An authority epoch is required.", nameof(authorityEpoch))
            : authorityEpoch;
        WorkspaceId = workspaceId.Value == Guid.Empty
            ? throw new ArgumentException("A workspace id is required.", nameof(workspaceId))
            : workspaceId;
        DesktopInstanceId = ProtocolGuard.Required(desktopInstanceId, nameof(desktopInstanceId), ProtocolBounds.MaxShortStringBytes);
        GlobalRevision = globalRevision;
        DesktopDeviceId = desktopDeviceId.Value == Guid.Empty
            ? throw new ArgumentException("A desktop device id is required.", nameof(desktopDeviceId))
            : desktopDeviceId;
        DeviceModes = ProtocolGuard.NotNull(deviceModes, nameof(deviceModes));
        Workspace = ProtocolGuard.NotNull(workspace, nameof(workspace));
        Marks = ProtocolGuard.NotNull(marks, nameof(marks));
        CaptureIntent = ProtocolGuard.NotNull(captureIntent, nameof(captureIntent));
        ProfilePreferences = ProtocolGuard.NotNull(profilePreferences, nameof(profilePreferences));
        RecentCommands = ProtocolGuard.List(
            recentCommands ?? [],
            nameof(recentCommands),
            ProtocolBounds.MaxRecentCommands);
        ReceiptHorizonUtc = ProtocolGuard.UtcOptional(receiptHorizonUtc, nameof(receiptHorizonUtc));

        var aggregateRevisionTotal = DeviceModes.Cursor.Revision.Value
            + Workspace.Cursor.Revision.Value
            + Marks.Cursor.Revision.Value
            + CaptureIntent.Cursor.Revision.Value
            + ProfilePreferences.Cursor.Revision.Value;
        if (GlobalRevision.Value != aggregateRevisionTotal)
        {
            throw new ArgumentException(
                "The global revision equals the sum of every aggregate revision.",
                nameof(globalRevision));
        }

        // A command id is a change identity for the whole authority lifetime, not per device;
        // otherwise two devices could each be told that "their" change occupies one revision.
        if (RecentCommands.Select(item => item.CommandId).Distinct().Count() != RecentCommands.Count)
        {
            throw new ArgumentException("Recent command ids are unique across devices.", nameof(recentCommands));
        }

        var occupiedCursors = new[]
        {
            DeviceModes.Cursor.LastChangeId,
            Workspace.Cursor.LastChangeId,
            Marks.Cursor.LastChangeId,
            CaptureIntent.Cursor.LastChangeId,
            ProfilePreferences.Cursor.LastChangeId,
        }.Where(change => change is not null).ToArray();
        if (occupiedCursors.Distinct().Count() != occupiedCursors.Length)
        {
            throw new ArgumentException("One command id cannot occupy more than one aggregate cursor.");
        }

        if (DeviceModes.Devices.Any(item => item.DeviceId == DesktopDeviceId) ||
            DeviceModes.PendingControl?.DeviceId == DesktopDeviceId ||
            DeviceModes.ControlLease?.DeviceId == DesktopDeviceId)
        {
            throw new ArgumentException("The canonical desktop is the authority, not a paired interaction mode.", nameof(deviceModes));
        }

        if (ProfilePreferences.LastChangedOrigin is { } preferenceOrigin &&
            preferenceOrigin.WorkspaceId != WorkspaceId)
        {
            throw new ArgumentException("Preference attribution belongs to this canonical workspace.", nameof(profilePreferences));
        }
    }

    public AuthorityEpoch AuthorityEpoch { get; }

    /// <summary>The v2 workspace every paired change is attributed to.</summary>
    public WorkspaceId WorkspaceId { get; }

    /// <summary>The desktop application instance that attributes maintenance changes.</summary>
    public string DesktopInstanceId { get; }

    public GlobalRevision GlobalRevision { get; }

    public CompanionDeviceId DesktopDeviceId { get; }

    public DeviceModeAggregate DeviceModes { get; }

    public WorkspaceAggregate Workspace { get; }

    public MarkAggregate Marks { get; }

    public CaptureIntentAggregate CaptureIntent { get; }

    public ProfilePreferencesAggregate ProfilePreferences { get; }

    [JsonIgnore]
    public IReadOnlyList<RecentCommandReceipt> RecentCommands { get; }

    /// <summary>
    /// The latest issue time of an unexpired receipt the bounded window had to evict. A command issued
    /// at or before it without a receipt may be the retry of an evicted change, so the reducer refuses
    /// it rather than risk applying it twice. Desktop-local; persisted beside the receipts.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? ReceiptHorizonUtc { get; }

    public AggregateCursor Cursor(CanonicalAggregateKind aggregate) => aggregate switch
    {
        CanonicalAggregateKind.DeviceModes => DeviceModes.Cursor,
        CanonicalAggregateKind.Workspace => Workspace.Cursor,
        CanonicalAggregateKind.Marks => Marks.Cursor,
        CanonicalAggregateKind.CaptureIntent => CaptureIntent.Cursor,
        CanonicalAggregateKind.ProfilePreferences => ProfilePreferences.Cursor,
        _ => throw new ArgumentOutOfRangeException(nameof(aggregate)),
    };

    internal CanonicalCompanionState With(
        GlobalRevision globalRevision,
        DeviceModeAggregate? deviceModes = null,
        WorkspaceAggregate? workspace = null,
        MarkAggregate? marks = null,
        CaptureIntentAggregate? captureIntent = null,
        ProfilePreferencesAggregate? profilePreferences = null,
        IReadOnlyList<RecentCommandReceipt>? recentCommands = null,
        DateTimeOffset? receiptHorizonUtc = null) =>
        new(
            AuthorityEpoch,
            WorkspaceId,
            DesktopInstanceId,
            globalRevision,
            DesktopDeviceId,
            deviceModes ?? DeviceModes,
            workspace ?? Workspace,
            marks ?? Marks,
            captureIntent ?? CaptureIntent,
            profilePreferences ?? ProfilePreferences,
            recentCommands ?? RecentCommands,
            receiptHorizonUtc ?? ReceiptHorizonUtc);
}

/// <summary>
/// One committed cross-device change. Besides the paired authority epoch and revisions it carries
/// the v2 attribution every cross-device change needs: the workspace, device, and instance in its
/// <see cref="WorkspaceOrigin"/>, the v2 contract version, the change id, and the UTC time. Marks and
/// capture intents project to <see cref="RevisionedState{T}"/> of the Core payloads without loss.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DeviceModeCanonicalUpdate), "deviceMode")]
[JsonDerivedType(typeof(WorkspaceCanonicalUpdate), "workspace")]
[JsonDerivedType(typeof(MarksCanonicalUpdate), "marks")]
[JsonDerivedType(typeof(CaptureCanonicalUpdate), "captureIntent")]
[JsonDerivedType(typeof(ProfilePreferencesCanonicalUpdate), "profilePreferences")]
public abstract record CanonicalUpdate
{
    private protected CanonicalUpdate(
        AuthorityEpoch authorityEpoch,
        GlobalRevision globalRevision,
        CommandId changeId,
        DateTimeOffset changedUtc,
        WorkspaceOrigin origin,
        V2ContractVersion contractVersion)
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
        Origin = ProtocolGuard.NotNull(origin, nameof(origin));
        ProtocolGuard.Required(Origin.InstanceId, nameof(origin), ProtocolBounds.MaxShortStringBytes);
        ContractVersion = V2ContractVersion.Current.CanRead(contractVersion)
            ? contractVersion
            : throw new ArgumentException("A paired update uses a readable v2 contract version.", nameof(contractVersion));
    }

    public AuthorityEpoch AuthorityEpoch { get; }

    public GlobalRevision GlobalRevision { get; }

    public CommandId ChangeId { get; }

    public DateTimeOffset ChangedUtc { get; }

    /// <summary>Audit attribution: workspace, authenticated device, origin kind, and instance. Not authentication.</summary>
    public WorkspaceOrigin Origin { get; }

    public V2ContractVersion ContractVersion { get; }

    [JsonIgnore]
    public abstract CanonicalAggregateKind Aggregate { get; }

    /// <summary>
    /// The v2 stream this aggregate's changes belong to. It is scoped to the authority epoch, whose
    /// aggregate revisions restart, so a stream's revisions only ever increase.
    /// </summary>
    [JsonIgnore]
    public StateStreamId StreamId => new($"paired/{AuthorityEpoch.Value:D}/{Aggregate}");

    private protected StateChangeId CoreChangeId => new(ChangeId.Value);
}

public sealed record DeviceModeCanonicalUpdate : CanonicalUpdate
{
    public DeviceModeCanonicalUpdate(
        AuthorityEpoch authorityEpoch,
        GlobalRevision globalRevision,
        CommandId changeId,
        DateTimeOffset changedUtc,
        WorkspaceOrigin origin,
        V2ContractVersion contractVersion,
        DeviceModeAggregate state)
        : base(authorityEpoch, globalRevision, changeId, changedUtc, origin, contractVersion) =>
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
        WorkspaceOrigin origin,
        V2ContractVersion contractVersion,
        WorkspaceAggregate state)
        : base(authorityEpoch, globalRevision, changeId, changedUtc, origin, contractVersion) =>
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
        WorkspaceOrigin origin,
        V2ContractVersion contractVersion,
        MarkAggregate state)
        : base(authorityEpoch, globalRevision, changeId, changedUtc, origin, contractVersion) =>
        State = ProtocolGuard.NotNull(state, nameof(state));

    public MarkAggregate State { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.Marks;

    /// <summary>
    /// The marks this change created or edited, each as the v2 revisioned Core mark on its own
    /// per-mark stream within the authority epoch. The stream revision is the marks aggregate revision
    /// of the change, so it increases across edits and across deleting and re-creating one mark id. A
    /// deletion or expiry has no v2 payload and appears only as the aggregate update.
    /// </summary>
    public IReadOnlyList<RevisionedState<MapMarkState>> ToRevisionedStates() =>
        State.Marks
            .Where(mark => mark.LastChangeId == ChangeId)
            .Select(mark => new RevisionedState<MapMarkState>(
                new StateStreamId($"{StreamId.Value}/{mark.MarkId.Value:D}"),
                new StateRevision(State.Cursor.Revision.Value),
                CoreChangeId,
                ContractVersion,
                Origin,
                ChangedUtc,
                mark.State))
            .ToArray();
}

public sealed record CaptureCanonicalUpdate : CanonicalUpdate
{
    public CaptureCanonicalUpdate(
        AuthorityEpoch authorityEpoch,
        GlobalRevision globalRevision,
        CommandId changeId,
        DateTimeOffset changedUtc,
        WorkspaceOrigin origin,
        V2ContractVersion contractVersion,
        CaptureIntentAggregate state)
        : base(authorityEpoch, globalRevision, changeId, changedUtc, origin, contractVersion) =>
        State = ProtocolGuard.NotNull(state, nameof(state));

    public CaptureIntentAggregate State { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.CaptureIntent;

    /// <summary>The v2 revisioned Core capture intent this change leaves, or null when none is active.</summary>
    public RevisionedState<CaptureIntentState>? ToRevisionedState() =>
        State.ActiveIntent is { } intent
            ? new RevisionedState<CaptureIntentState>(
                StreamId,
                new StateRevision(State.Cursor.Revision.Value),
                CoreChangeId,
                ContractVersion,
                Origin,
                ChangedUtc,
                intent.State)
            : null;
}
