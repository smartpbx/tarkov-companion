using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>
/// Identity and time established by the authenticated desktop boundary. None of these values is
/// accepted from a client envelope.
/// </summary>
public sealed record AuthenticatedCommandContext
{
    public AuthenticatedCommandContext(
        CompanionDeviceId deviceId,
        DeviceSessionId sessionId,
        DeviceKeyId deviceKeyId,
        string instanceId,
        CompanionProtocolVersion negotiatedVersion,
        CompanionSurfaceKind surface,
        IReadOnlyList<DeviceCapability> capabilities,
        DateTimeOffset receivedUtc,
        bool isDesktop)
    {
        DeviceId = deviceId.Value == Guid.Empty
            ? throw new ArgumentException("An authenticated device is required.", nameof(deviceId))
            : deviceId;
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("An authenticated session is required.", nameof(sessionId))
            : sessionId;
        DeviceKeyId = string.IsNullOrWhiteSpace(deviceKeyId.Value)
            ? throw new ArgumentException("An authenticated device key is required.", nameof(deviceKeyId))
            : deviceKeyId;
        InstanceId = ProtocolGuard.Required(instanceId, nameof(instanceId), ProtocolBounds.MaxShortStringBytes);
        NegotiatedVersion = ProtocolGuard.Version(negotiatedVersion, nameof(negotiatedVersion));
        Surface = ProtocolGuard.Defined(surface, nameof(surface));
        Capabilities = ProtocolGuard.List(
            ProtocolGuard.List(capabilities, nameof(capabilities), 32).Distinct(),
            nameof(capabilities),
            32);
        ReceivedUtc = ProtocolGuard.Utc(receivedUtc, nameof(receivedUtc));
        IsDesktop = isDesktop;
    }

    public CompanionDeviceId DeviceId { get; }

    public DeviceSessionId SessionId { get; }

    public DeviceKeyId DeviceKeyId { get; }

    /// <summary>
    /// The application instance that sent the command: the desktop instance, or the
    /// <see cref="ClientHello.ClientInstanceId"/> of the connection that carries this session. It is
    /// v2 audit attribution, never authentication.
    /// </summary>
    public string InstanceId { get; }

    public CompanionProtocolVersion NegotiatedVersion { get; }

    public CompanionSurfaceKind Surface { get; }

    public IReadOnlyList<DeviceCapability> Capabilities { get; }

    public DateTimeOffset ReceivedUtc { get; }

    public bool IsDesktop { get; }

    public bool Has(DeviceCapability capability) => IsDesktop || Capabilities.Contains(capability);

    /// <summary>
    /// Builds a paired-device context only from a live, key-bound session record whose frame was
    /// authenticated with that session's traffic keys. Capabilities are the intersection of the
    /// device grant and the session grant.
    /// </summary>
    public static AuthenticatedCommandContext ForPairedSession(
        PairedDevice device,
        DeviceSession session,
        string clientInstanceId,
        DateTimeOffset receivedUtc)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(session);
        if (!DeviceLifecycle.IsLive(session, device, receivedUtc))
        {
            throw new UnauthorizedAccessException("Only a live session bound to a live device key can issue commands.");
        }

        return new AuthenticatedCommandContext(
            device.DeviceId,
            session.SessionId,
            session.DeviceKeyId,
            clientInstanceId,
            session.ProtocolVersion,
            session.Surface,
            session.Capabilities.Intersect(device.Capabilities).ToArray(),
            receivedUtc,
            isDesktop: false);
    }
}

public sealed record CommandReduction(
    CanonicalCompanionState State,
    CommandAcknowledgement Acknowledgement,
    CanonicalUpdate? Update);

public sealed record MaintenanceReduction(
    CanonicalCompanionState State,
    IReadOnlyList<CanonicalUpdate> Updates);

/// <summary>The deterministic desktop-canonical reducer used by every direct or relay transport.</summary>
/// <remarks>
/// A hostile or malformed command never escapes this boundary as an exception: every path returns
/// the unchanged state and a typed rejection. A rejection never names the rejected command as the
/// applied change, and committed state always fits the delivery budget, so the returned
/// acknowledgement and update can always be delivered.
/// </remarks>
public static class DesktopCanonicalStateMachine
{
    /// <summary>Maintenance change ids are RFC 9562 version-8 UUIDs; no client command may use that version.</summary>
    private const int ReservedChangeIdVersion = 8;

    public static CommandReduction Apply(
        CanonicalCompanionState state,
        ClientCommandEnvelope envelope,
        AuthenticatedCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        var command = envelope.Command;
        try
        {
            return ApplyCore(state, envelope, context);
        }
        catch (ArgumentException)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "invalid-command-state");
        }
        catch (InvalidOperationException)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "invalid-transition");
        }
        catch (UnauthorizedAccessException)
        {
            return Reject(state, command, CommandDisposition.RejectedUnauthorized, "capability-denied");
        }
        catch (OverflowException)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "revision-overflow");
        }
        catch (JsonException)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "command-not-canonicalizable");
        }
        catch (NotSupportedException)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "command-not-canonicalizable");
        }
    }

    /// <summary>
    /// Applies server-time expiry and live-authority reconciliation as ordinary revisioned updates.
    /// A lease or pending request whose session or device is no longer live returns that device to
    /// Follow even if the transport never reported the disconnect; a terminated device leaves the
    /// mode table. Synthetic change ids derive from the prior state and instant, so replaying
    /// maintenance is deterministic. Maintenance never adds a mark, device, or capture; it only
    /// removes entries or rewrites a status, mode, cursor, or timestamp in place, which
    /// <see cref="ProtocolBounds.MaintenanceReserveBytes"/> of committed headroom always absorbs, so
    /// maintained state stays deliverable without a rejection path.
    /// </summary>
    public static MaintenanceReduction ApplyMaintenance(
        CanonicalCompanionState state,
        DateTimeOffset nowUtc,
        IReadOnlyList<PairedDevice> pairedDevices,
        IReadOnlyList<DeviceSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pairedDevices);
        ArgumentNullException.ThrowIfNull(sessions);
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        var liveDevices = pairedDevices
            .Where(device => DeviceLifecycle.IsLive(device, now))
            .Select(device => device.DeviceId)
            .ToHashSet();
        var liveSessions = sessions
            .Where(session => pairedDevices.Any(device => DeviceLifecycle.IsLive(session, device, now)))
            .Select(session => session.SessionId)
            .ToHashSet();
        var current = state;
        var updates = new List<CanonicalUpdate>();

        var modes = current.DeviceModes;
        IReadOnlyList<DeviceModeEntry> devices = modes.Devices.Where(entry => liveDevices.Contains(entry.DeviceId)).ToArray();
        var changed = devices.Count != modes.Devices.Count;
        var lease = modes.ControlLease;
        if (lease is not null &&
            (lease.ExpiresUtc <= now || !liveSessions.Contains(lease.SessionId) || !liveDevices.Contains(lease.DeviceId)))
        {
            if (liveDevices.Contains(lease.DeviceId))
            {
                devices = SetMode(devices, lease.DeviceId, CompanionInteractionMode.Follow, now);
            }

            lease = null;
            changed = true;
        }

        var pending = modes.PendingControl;
        if (pending is not null &&
            (pending.ExpiresUtc <= now || !liveSessions.Contains(pending.SessionId) || !liveDevices.Contains(pending.DeviceId)))
        {
            if (liveDevices.Contains(pending.DeviceId))
            {
                devices = SetMode(devices, pending.DeviceId, CompanionInteractionMode.Follow, now);
            }

            pending = null;
            changed = true;
        }

        if (changed)
        {
            var committed = CommitModeChange(current, devices, pending, lease, now, "device-modes-maintained");
            current = committed.State;
            updates.AddRange(committed.Updates);
        }

        var liveMarks = current.Marks.Marks.Where(mark => mark.ExpiresUtc is null || mark.ExpiresUtc > now).ToArray();
        if (liveMarks.Length != current.Marks.Marks.Count)
        {
            var change = SyntheticId(current, CanonicalAggregateKind.Marks, now, "marks-expired");
            var aggregate = new MarkAggregate(
                new AggregateCursor(current.Marks.Cursor.Revision.Next(), change),
                liveMarks);
            var global = current.GlobalRevision.Next();
            current = current.With(global, marks: aggregate);
            updates.Add(new MarksCanonicalUpdate(
                current.AuthorityEpoch,
                global,
                change,
                now,
                DesktopOrigin(current),
                V2ContractVersion.Current,
                aggregate));
        }

        if (current.CaptureIntent.ActiveIntent is { } capture &&
            capture.ExpiresUtc <= now &&
            !IsTerminal(capture.Status))
        {
            var change = SyntheticId(current, CanonicalAggregateKind.CaptureIntent, now, "capture-intent-expired");
            var expired = CopyCapture(capture, status: ContextualCaptureStatus.Expired);
            var aggregate = new CaptureIntentAggregate(
                new AggregateCursor(current.CaptureIntent.Cursor.Revision.Next(), change),
                expired);
            var global = current.GlobalRevision.Next();
            current = current.With(global, captureIntent: aggregate);
            updates.Add(new CaptureCanonicalUpdate(
                current.AuthorityEpoch,
                global,
                change,
                now,
                DesktopOrigin(current),
                V2ContractVersion.Current,
                aggregate));
        }

        var (retained, horizon) = RetainReceipts(current, current.RecentCommands, now);
        current = current.With(current.GlobalRevision, recentCommands: retained, receiptHorizonUtc: horizon);
        return new MaintenanceReduction(current, ProtocolGuard.List(updates, nameof(updates)));
    }

    /// <summary>
    /// Applies an authenticated session disconnect, close, revocation, expiry, or replacement. A
    /// session that owned the pending request or control lease returns its device to Follow and
    /// releases it; a stale termination cannot disturb a newer session or persistent Independent mode.
    /// </summary>
    public static MaintenanceReduction ApplySessionTermination(
        CanonicalCompanionState state,
        DeviceSession terminatedSession)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(terminatedSession);
        if (terminatedSession.Status == DeviceSessionStatus.Active || terminatedSession.EndedUtc is not { } endedUtc)
        {
            throw new ArgumentException("A terminal session with its trusted end time is required.", nameof(terminatedSession));
        }

        var modes = state.DeviceModes;
        var pendingOwned = modes.PendingControl?.SessionId == terminatedSession.SessionId;
        var leaseOwned = modes.ControlLease?.SessionId == terminatedSession.SessionId;
        if (!pendingOwned && !leaseOwned)
        {
            return new MaintenanceReduction(state, []);
        }

        var devices = modes.Devices;
        if (pendingOwned)
        {
            devices = SetMode(devices, modes.PendingControl!.DeviceId, CompanionInteractionMode.Follow, endedUtc);
        }

        if (leaseOwned)
        {
            devices = SetMode(devices, modes.ControlLease!.DeviceId, CompanionInteractionMode.Follow, endedUtc);
        }

        return CommitModeChange(
            state,
            devices,
            pendingOwned ? null : modes.PendingControl,
            leaseOwned ? null : modes.ControlLease,
            endedUtc,
            $"session-{terminatedSession.Status.ToString().ToLowerInvariant()}");
    }

    /// <summary>
    /// Applies a terminal device lifecycle change. A revoked, expired, or replaced device leaves the
    /// mode table (an absent device follows the desktop) and relinquishes any pending request or lease.
    /// </summary>
    public static MaintenanceReduction ApplyDeviceTermination(
        CanonicalCompanionState state,
        PairedDevice terminatedDevice)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(terminatedDevice);
        if (terminatedDevice.Status == DeviceLifecycleStatus.Active)
        {
            throw new ArgumentException("A revoked, expired, or replaced device is required.", nameof(terminatedDevice));
        }

        var modes = state.DeviceModes;
        var deviceId = terminatedDevice.DeviceId;
        var listed = modes.Devices.Any(item => item.DeviceId == deviceId);
        var pendingOwned = modes.PendingControl?.DeviceId == deviceId;
        var leaseOwned = modes.ControlLease?.DeviceId == deviceId;
        if (!listed && !pendingOwned && !leaseOwned)
        {
            return new MaintenanceReduction(state, []);
        }

        return CommitModeChange(
            state,
            modes.Devices.Where(item => item.DeviceId != deviceId).ToArray(),
            pendingOwned ? null : modes.PendingControl,
            leaseOwned ? null : modes.ControlLease,
            terminatedDevice.StatusChangedUtc,
            $"device-{terminatedDevice.Status.ToString().ToLowerInvariant()}");
    }

    private static CommandReduction ApplyCore(
        CanonicalCompanionState state,
        ClientCommandEnvelope envelope,
        AuthenticatedCommandContext context)
    {
        var command = envelope.Command;
        var now = context.ReceivedUtc;

        if (context.IsDesktop != (context.DeviceId == state.DesktopDeviceId) ||
            envelope.SessionId != context.SessionId)
        {
            return Reject(state, command, CommandDisposition.RejectedUnauthorized, "authenticated-session-mismatch");
        }

        if (!CompanionProtocolVersion.Current.CanRead(envelope.ProtocolVersion))
        {
            return Reject(state, command, CommandDisposition.UnsupportedVersion, "protocol-version-unreadable");
        }

        // A readable version this session did not negotiate is a peer bug, not version skew: the
        // v2 unsupported-version rule applies only when the receiver cannot read the change.
        if (envelope.ProtocolVersion != context.NegotiatedVersion)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "version-not-negotiated");
        }

        var fingerprint = CanonicalCommandFingerprint.Compute(command);
        var receipt = state.RecentCommands.FirstOrDefault(item => item.CommandId == command.CommandId);
        if (receipt is not null)
        {
            if (receipt.DeviceId != context.DeviceId || receipt.Fingerprint != fingerprint)
            {
                return Reject(state, command, CommandDisposition.RejectedCommandIdReuse, "command-id-reused");
            }

            // The retry may carry a refreshed revision, lifetime, or preview; the action it names
            // landed at the receipt's revision, which is what the acknowledgement reports.
            return new CommandReduction(
                state,
                Acknowledge(
                    state,
                    command,
                    CommandDisposition.Applied,
                    "duplicate-command",
                    receipt.AppliedRevision,
                    receipt.AppliedRevision,
                    command.CommandId),
                null);
        }

        if (ProtocolGuard.UuidVersion(command.CommandId.Value) == ReservedChangeIdVersion ||
            OccupiesAnyCursor(state, command.CommandId))
        {
            return Reject(state, command, CommandDisposition.RejectedCommandIdReuse, "command-id-reserved");
        }

        if (now >= command.ExpiresUtc)
        {
            return Reject(state, command, CommandDisposition.RejectedExpired, "command-expired");
        }

        if (command.IssuedUtc - now > ProtocolBounds.MaxClientClockSkew)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "command-issued-in-future");
        }

        // Without a receipt, a command issued no later than an evicted unexpired receipt may be that
        // change's retry; refusing it keeps the at-most-once guarantee when the window overflows.
        if (state.ReceiptHorizonUtc is { } horizon && command.IssuedUtc <= horizon)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "idempotency-window-exceeded");
        }

        if (!CanExecute(command, context))
        {
            return Reject(state, command, CommandDisposition.RejectedUnauthorized, "capability-denied");
        }

        if (envelope.AuthorityEpoch != state.AuthorityEpoch)
        {
            return Reject(state, command, CommandDisposition.RequiresSnapshot, "authority-epoch-mismatch");
        }

        var cursor = state.Cursor(command.Aggregate);
        if (command.OfflineQueuePreview is { } preview)
        {
            if (!IsQueueEligible(command))
            {
                return Reject(state, command, CommandDisposition.RejectedInvalidState, "command-cannot-be-queued");
            }

            if (preview.PreviewedUtc - now > ProtocolBounds.MaxClientClockSkew ||
                preview.PreviewedAuthorityEpoch != state.AuthorityEpoch ||
                preview.PreviewedAggregateRevision != cursor.Revision)
            {
                return Reject(state, command, CommandDisposition.RequiresPreview, "offline-action-needs-current-preview");
            }
        }

        if (command.RequestedRevision.Value < cursor.Revision.Value)
        {
            return Reject(state, command, CommandDisposition.RejectedStale, "stale-aggregate-revision");
        }

        if (command.RequestedRevision == cursor.Revision)
        {
            return Reject(state, command, CommandDisposition.RejectedConflict, "revision-occupied-by-another-command");
        }

        if (command.RequestedRevision != cursor.Revision.Next())
        {
            return Reject(state, command, CommandDisposition.RequiresSnapshot, "revision-gap");
        }

        var scope = new Scope(state, command, context, fingerprint);
        var reduction = command switch
        {
            SetInteractionModeCommand setMode => ApplySetMode(scope, setMode),
            RequestControlCommand requestControl => ApplyRequestControl(scope, requestControl),
            ResolveControlCommand resolveControl => ApplyResolveControl(scope, resolveControl),
            PreemptControlCommand => ApplyPreemptControl(scope),
            UpdateDesktopWorkspaceCommand updateDesktop => ApplyDesktopWorkspace(scope, updateDesktop),
            ControlWorkspaceCommand controlWorkspace => ApplyControlWorkspace(scope, controlWorkspace),
            ShowOnDesktopCommand show => ApplyShowOnDesktop(scope, show),
            UpsertMarkCommand upsert => ApplyUpsertMark(scope, upsert),
            DeleteMarkCommand delete => ApplyDeleteMark(scope, delete),
            RequestCaptureIntentCommand capture => ApplyRequestCapture(scope, capture),
            ReportCaptureProgressCommand progress => ApplyCaptureProgress(scope, progress),
            PublishCaptureResultCommand result => ApplyCaptureResult(scope, result),
            ReviewCaptureResultCommand review => ApplyCaptureReview(scope, review),
            CorrectCaptureResultCommand correction => ApplyCaptureCorrection(scope, correction),
            ActivateProfilePreferencesCommand activatePreferences => ApplyActivatePreferences(scope, activatePreferences),
            MutateProfilePreferencesCommand mutatePreferences => ApplyPreferenceMutation(scope, mutatePreferences),
            ResetProfilePreferencesCommand resetPreferences => ApplyResetPreferences(scope, resetPreferences),
            DeleteProfilePreferencesCommand deletePreferences => ApplyDeletePreferences(scope, deletePreferences),
            _ => Reject(state, command, CommandDisposition.RejectedInvalidState, "unknown-command"),
        };

        if (reduction.Update is not null && !CanonicalDeliveryBudget.Fits(reduction.State))
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "canonical-state-exceeds-delivery-bound");
        }

        return reduction;
    }

    private static CommandReduction ApplySetMode(Scope scope, SetInteractionModeCommand command)
    {
        var context = scope.Context;
        if (context.IsDesktop)
        {
            return Reject(scope, CommandDisposition.RejectedInvalidState, "desktop-has-no-interaction-mode");
        }

        var modes = scope.State.DeviceModes;
        return CommitModes(
            scope,
            SetMode(modes.Devices, context.DeviceId, command.Mode, scope.Now),
            modes.PendingControl?.DeviceId == context.DeviceId ? null : modes.PendingControl,
            modes.ControlLease?.DeviceId == context.DeviceId ? null : modes.ControlLease,
            "mode-applied");
    }

    private static CommandReduction ApplyRequestControl(Scope scope, RequestControlCommand command)
    {
        var context = scope.Context;
        var modes = scope.State.DeviceModes;

        // An expired request or lease that maintenance has not yet collected is already absent: it
        // must not block a new request, and an expired lease of the requester is not control.
        var livePending = modes.PendingControl is { } current && current.ExpiresUtc > scope.Now ? current : null;
        var liveLease = modes.ControlLease is { } held && held.ExpiresUtc > scope.Now ? held : null;
        if (context.IsDesktop || livePending is not null || liveLease?.DeviceId == context.DeviceId)
        {
            return Reject(scope, CommandDisposition.RejectedInvalidState, "control-request-not-available");
        }

        var devices = modes.Devices;
        if (modes.PendingControl is { } expiredPending && livePending is null)
        {
            devices = SetMode(devices, expiredPending.DeviceId, CompanionInteractionMode.Follow, scope.Now);
        }

        if (modes.ControlLease is { } expiredLease && liveLease is null)
        {
            devices = SetMode(devices, expiredLease.DeviceId, CompanionInteractionMode.Follow, scope.Now);
        }

        var pending = new PendingControlRequest(
            command.CommandId,
            context.DeviceId,
            context.SessionId,
            scope.Now,
            command.ExpiresUtc,
            command.RequestedLease);
        return CommitModes(
            scope,
            SetMode(devices, context.DeviceId, CompanionInteractionMode.ControlPending, scope.Now),
            pending,
            liveLease,
            "desktop-approval-required");
    }

    private static CommandReduction ApplyResolveControl(Scope scope, ResolveControlCommand command)
    {
        var context = scope.Context;
        var modes = scope.State.DeviceModes;
        var pending = modes.PendingControl;
        if (pending is null || pending.RequestCommandId != command.RequestCommandId || pending.ExpiresUtc <= scope.Now)
        {
            return Reject(scope, CommandDisposition.RejectedInvalidState, "pending-control-request-not-found");
        }

        if (!context.IsDesktop && pending.DeviceId == context.DeviceId)
        {
            return Reject(scope, CommandDisposition.RejectedUnauthorized, "control-self-approval-denied");
        }

        var devices = modes.Devices;
        var lease = modes.ControlLease;
        if (command.Approved)
        {
            if (lease is not null)
            {
                devices = SetMode(devices, lease.DeviceId, CompanionInteractionMode.Follow, scope.Now);
            }

            devices = SetMode(devices, pending.DeviceId, CompanionInteractionMode.Control, scope.Now);
            lease = new ControlLease(
                command.LeaseId!.Value,
                pending.DeviceId,
                pending.SessionId,
                scope.Now,
                scope.Now.Add(pending.RequestedLease));
        }
        else
        {
            devices = SetMode(devices, pending.DeviceId, CompanionInteractionMode.Follow, scope.Now);
        }

        return CommitModes(scope, devices, null, lease, command.Approved ? "control-granted" : "control-denied");
    }

    private static CommandReduction ApplyPreemptControl(Scope scope)
    {
        var modes = scope.State.DeviceModes;
        if (!scope.Context.IsDesktop || modes.ControlLease is not { } lease)
        {
            return Reject(scope, CommandDisposition.RejectedInvalidState, "no-control-lease-to-preempt");
        }

        return CommitModes(
            scope,
            SetMode(modes.Devices, lease.DeviceId, CompanionInteractionMode.Follow, scope.Now),
            modes.PendingControl,
            null,
            "desktop-preempted-control");
    }

    private static CommandReduction ApplyControlWorkspace(Scope scope, ControlWorkspaceCommand command)
    {
        var context = scope.Context;
        var lease = scope.State.DeviceModes.ControlLease;
        if (lease is null || lease.DeviceId != context.DeviceId || lease.SessionId != context.SessionId ||
            lease.ExpiresUtc <= scope.Now)
        {
            return Reject(scope, CommandDisposition.RejectedUnauthorized, "active-control-lease-required");
        }

        var projection = ApplyWorkspaceAction(scope.State.Workspace.Projection, command.Action, context);
        return CommitWorkspace(scope, projection, "workspace-action-applied");
    }

    private static CommandReduction ApplyDesktopWorkspace(Scope scope, UpdateDesktopWorkspaceCommand command) =>
        scope.Context.IsDesktop
            ? CommitWorkspace(scope, command.Projection, "desktop-workspace-applied")
            : Reject(scope, CommandDisposition.RejectedUnauthorized, "desktop-origin-required");

    private static CommandReduction ApplyShowOnDesktop(Scope scope, ShowOnDesktopCommand command)
    {
        var context = scope.Context;
        if (context.IsDesktop || scope.State.DeviceModes.ModeOf(context.DeviceId) != CompanionInteractionMode.Independent)
        {
            return Reject(scope, CommandDisposition.RejectedInvalidState, "show-requires-independent-mode");
        }

        // A shown view replaces the whole projection, so it must not open or dismiss a pairing,
        // revocation, conflict, or team-removal dialog without the administrative capability.
        if (command.Projection.Dialog != scope.State.Workspace.Projection.Dialog &&
            !context.Has(DeviceCapability.ManageDevices))
        {
            return Reject(scope, CommandDisposition.RejectedUnauthorized, "sensitive-dialog-capability-required");
        }

        return CommitWorkspace(scope, command.Projection, "independent-view-shown");
    }

    private static CommandReduction ApplyUpsertMark(Scope scope, UpsertMarkCommand command)
    {
        var context = scope.Context;
        var marks = scope.State.Marks.Marks.ToList();
        var index = marks.FindIndex(mark => mark.MarkId == command.MarkId);
        var touchesTeam = command.Mark.Scope == MapMarkScope.Team || (index >= 0 && marks[index].Scope == MapMarkScope.Team);
        if (touchesTeam && !context.Has(DeviceCapability.PublishTeamMarks))
        {
            return Reject(scope, CommandDisposition.RejectedUnauthorized, "team-publication-capability-required");
        }

        MapMark next;
        if (index < 0)
        {
            // The aggregate revision matched, so a mark revision disagreement means the client's
            // cached marks are not the canonical ones; the snapshot lets it re-preview the edit.
            if (command.ExpectedMarkRevision != 0)
            {
                return Reject(scope, CommandDisposition.RequiresSnapshot, "mark-not-found");
            }

            if (marks.Count >= ProtocolBounds.MaxMarks)
            {
                return Reject(scope, CommandDisposition.RejectedInvalidState, "mark-bound-reached");
            }

            next = CreateMark(command.MarkId, 1, command.Mark, context.DeviceId, scope.Now, scope.Command.CommandId);
            marks.Add(next);
        }
        else
        {
            var current = marks[index];
            if (current.Revision != command.ExpectedMarkRevision)
            {
                return Reject(scope, CommandDisposition.RequiresSnapshot, "mark-revision-mismatch");
            }

            if (!context.IsDesktop && current.AuthorDeviceId != context.DeviceId)
            {
                return Reject(scope, CommandDisposition.RejectedUnauthorized, "mark-author-required");
            }

            next = CreateMark(
                current.MarkId,
                current.Revision + 1,
                command.Mark,
                current.AuthorDeviceId,
                scope.Now,
                scope.Command.CommandId,
                current.CreatedUtc);
            marks[index] = next;
        }

        return Commit(
            scope,
            new MarkAggregate(new AggregateCursor(scope.Command.RequestedRevision, scope.Command.CommandId), marks),
            "mark-applied");
    }

    private static CommandReduction ApplyDeleteMark(Scope scope, DeleteMarkCommand command)
    {
        var context = scope.Context;
        var marks = scope.State.Marks.Marks.ToList();
        var index = marks.FindIndex(mark => mark.MarkId == command.MarkId);
        if (index < 0 || marks[index].Revision != command.ExpectedMarkRevision)
        {
            return Reject(scope, CommandDisposition.RequiresSnapshot, index < 0 ? "mark-not-found" : "mark-revision-mismatch");
        }

        if (!context.IsDesktop && marks[index].AuthorDeviceId != context.DeviceId)
        {
            return Reject(scope, CommandDisposition.RejectedUnauthorized, "mark-author-required");
        }

        if (marks[index].Scope == MapMarkScope.Team && !context.Has(DeviceCapability.PublishTeamMarks))
        {
            return Reject(scope, CommandDisposition.RejectedUnauthorized, "team-publication-capability-required");
        }

        marks.RemoveAt(index);
        return Commit(
            scope,
            new MarkAggregate(new AggregateCursor(scope.Command.RequestedRevision, scope.Command.CommandId), marks),
            "mark-deleted");
    }

    private static CommandReduction ApplyRequestCapture(Scope scope, RequestCaptureIntentCommand command)
    {
        var context = scope.Context;
        var active = scope.State.CaptureIntent.ActiveIntent;
        if (active is not null && active.ExpiresUtc > scope.Now && !IsTerminal(active.Status))
        {
            return Reject(scope, CommandDisposition.RejectedInvalidState, "capture-intent-already-active");
        }

        var lifetimeEnd = scope.Now.Add(ProtocolBounds.CaptureIntentLifetime);
        var expires = command.ExpiresUtc < lifetimeEnd ? command.ExpiresUtc : lifetimeEnd;
        var intent = new ContextualCaptureIntent(
            command.IntentId,
            command.CorrelationId,
            command.CaptureSessionId,
            new CaptureIntentState(command.Intent, scope.Now, expires),
            context.DeviceId,
            context.Surface,
            ContextualCaptureStatus.Armed,
            command.Context,
            [
                new ContextualCaptureProgress(
                    0,
                    ContextualCaptureProgressPhase.Armed,
                    scope.Now,
                    0,
                    null,
                    null,
                    "recognition-intent-armed"),
            ],
            null,
            [],
            null,
            []);
        return CommitCapture(scope, intent, "capture-intent-armed");
    }

    private static CommandReduction ApplyCaptureProgress(Scope scope, ReportCaptureProgressCommand command)
    {
        var current = RequireCapture(scope.State, command.IntentId, scope.Now);
        if (command.Phase is ContextualCaptureProgressPhase.AwaitingReview or ContextualCaptureProgressPhase.Complete)
        {
            return Reject(scope, CommandDisposition.RejectedInvalidState, "result-command-required");
        }

        var progress = current.Progress.Append(new ContextualCaptureProgress(
            current.Progress.Count,
            command.Phase,
            scope.Now,
            command.Percent,
            command.ArtifactId,
            command.CaptureOrdinal,
            command.Detail)).ToArray();
        var perCapture = command.CaptureOrdinal is not null;
        var status = command.Phase switch
        {
            // A cancelled or failed artifact ends only that capture: the session stays open for the
            // user's next visible capture, or keeps its published result awaiting review.
            ContextualCaptureProgressPhase.Cancelled or ContextualCaptureProgressPhase.Failed when perCapture =>
                current.Result is not null ? current.Status : ContextualCaptureStatus.AwaitingUserCapture,
            ContextualCaptureProgressPhase.Cancelled => ContextualCaptureStatus.Cancelled,
            ContextualCaptureProgressPhase.Failed => ContextualCaptureStatus.Failed,

            // A guided follow-up capture keeps a published result awaiting review until it is replaced.
            _ when current.Result is not null => current.Status,
            ContextualCaptureProgressPhase.Armed => ContextualCaptureStatus.Armed,
            ContextualCaptureProgressPhase.AwaitingUserCapture => ContextualCaptureStatus.AwaitingUserCapture,
            _ => ContextualCaptureStatus.InProgress,
        };
        return CommitCapture(scope, CopyCapture(current, status: status, progress: progress), "capture-progress-applied");
    }

    private static CommandReduction ApplyCaptureResult(Scope scope, PublishCaptureResultCommand command)
    {
        var current = RequireCapture(scope.State, command.IntentId, scope.Now);
        if (current.Review is not null || current.Corrections.Count > 0)
        {
            // Review and corrections are append-only evidence about the published result; a
            // re-recognition starts a new capture intent instead of silently orphaning them.
            return Reject(scope, CommandDisposition.RejectedInvalidState, "capture-result-already-reviewed");
        }

        if (command.Result.CompletedUtc < current.RequestedUtc ||
            command.Result.CompletedUtc > scope.Now ||
            command.Result.Provenance.ObservedUtc > command.Result.CompletedUtc ||
            command.Guidance.Select(item => item.Order).Distinct().Count() != command.Guidance.Count)
        {
            return Reject(scope, CommandDisposition.RejectedInvalidState, "invalid-capture-result");
        }

        var correlated = current.Progress.LastOrDefault(item =>
            item.ArtifactId is not null &&
            item.CaptureOrdinal == command.Result.CaptureOrdinal &&
            string.Equals(item.ArtifactId, command.Result.ArtifactId, StringComparison.Ordinal));
        if (correlated is null)
        {
            return Reject(scope, CommandDisposition.RejectedInvalidState, "result-artifact-not-correlated");
        }

        if (correlated.Phase is ContextualCaptureProgressPhase.Cancelled or ContextualCaptureProgressPhase.Failed)
        {
            return Reject(scope, CommandDisposition.RejectedInvalidState, "result-artifact-ended");
        }

        if (command.Result.CompletedUtc < correlated.ChangedUtc)
        {
            return Reject(scope, CommandDisposition.RejectedInvalidState, "invalid-capture-result");
        }

        var progress = current.Progress.Append(new ContextualCaptureProgress(
            current.Progress.Count,
            ContextualCaptureProgressPhase.AwaitingReview,
            scope.Now,
            100,
            command.Result.ArtifactId,
            command.Result.CaptureOrdinal,
            "result-awaiting-review")).ToArray();
        var next = CopyCapture(
            current,
            status: ContextualCaptureStatus.AwaitingReview,
            progress: progress,
            result: command.Result,
            guidance: command.Guidance,
            replaceResult: true);
        return CommitCapture(scope, next, "capture-result-applied");
    }

    private static CommandReduction ApplyCaptureReview(Scope scope, ReviewCaptureResultCommand command)
    {
        var current = RequireCapture(scope.State, command.IntentId, scope.Now);
        if (current.Status != ContextualCaptureStatus.AwaitingReview || current.Result is null)
        {
            return Reject(scope, CommandDisposition.RejectedInvalidState, "capture-not-awaiting-review");
        }

        var accepted = command.Disposition == CaptureReviewDisposition.Accepted;
        var review = new ContextualCaptureReview(command.Disposition, scope.Context.DeviceId, scope.Now, command.Note);
        var progress = accepted
            ? current.Progress.Append(new ContextualCaptureProgress(
                current.Progress.Count,
                ContextualCaptureProgressPhase.Complete,
                scope.Now,
                100,
                current.Result.ArtifactId,
                current.Result.CaptureOrdinal,
                "result-accepted")).ToArray()
            : current.Progress;
        var next = CopyCapture(
            current,
            status: accepted ? ContextualCaptureStatus.Complete : ContextualCaptureStatus.AwaitingReview,
            progress: progress,
            review: review,
            replaceReview: true);
        return CommitCapture(scope, next, "capture-review-applied");
    }

    private static CommandReduction ApplyCaptureCorrection(Scope scope, CorrectCaptureResultCommand command)
    {
        var current = RequireCapture(scope.State, command.IntentId, scope.Now);
        if (current.Status != ContextualCaptureStatus.AwaitingReview || current.Result is null)
        {
            return Reject(scope, CommandDisposition.RejectedInvalidState, "capture-not-correctable");
        }

        var correction = new ContextualCaptureCorrection(
            current.Corrections.Count + 1,
            command.Kind,
            command.FieldId,
            command.CorrectedValue,
            scope.Context.DeviceId,
            scope.Now,
            command.Reason);
        return CommitCapture(
            scope,
            CopyCapture(current, corrections: current.Corrections.Append(correction).ToArray()),
            "capture-correction-appended");
    }

    private static CommandReduction ApplyActivatePreferences(
        Scope scope,
        ActivateProfilePreferencesCommand command)
    {
        if (!scope.Context.IsDesktop)
        {
            return Reject(scope, CommandDisposition.RejectedUnauthorized, "desktop-profile-activation-required");
        }

        if (!PreferenceSchemaVersion.Current.CanRead(command.Preferences.SchemaVersion))
        {
            return Reject(scope, CommandDisposition.UnsupportedPreferenceSchema, "preference-schema-unreadable");
        }

        // Version 1.0 is the first schema. A later implementation may migrate readable older
        // minors here, but canonical state is always rewritten at the current version before it is
        // delivered or persisted.
        var normalized = PreferenceSchemaPolicy.Normalize(command.Preferences);
        return CommitPreferences(scope, normalized, "profile-preferences-activated");
    }

    private static CommandReduction ApplyPreferenceMutation(
        Scope scope,
        MutateProfilePreferencesCommand command)
    {
        if (!PreferenceSchemaVersion.Current.CanRead(command.SchemaVersion))
        {
            return Reject(scope, CommandDisposition.UnsupportedPreferenceSchema, "preference-schema-unreadable");
        }

        var current = scope.State.ProfilePreferences.ActiveProfile;
        if (current is null || current.Context != command.Context)
        {
            return Reject(scope, CommandDisposition.RequiresSnapshot, "preference-profile-context-mismatch");
        }

        var items = current.Items.ToList();
        var protectedRules = current.ProtectedItemRules.ToList();
        var overrides = current.RecommendationOverrides.ToList();
        var loadouts = current.FavoriteLoadouts.ToList();
        var shared = current.SharedPersonalization.ToList();
        switch (command.Mutation)
        {
            case SetItemPreferenceMutation set:
                Upsert(items, item => item.ItemId, set.Preference.ItemId, set.Preference);
                break;
            case RemoveItemPreferenceMutation remove:
                items.RemoveAll(item => string.Equals(item.ItemId, remove.ItemId, StringComparison.Ordinal));
                break;
            case UpsertProtectedItemRuleMutation upsert:
                Upsert(protectedRules, rule => rule.RuleId, upsert.Rule.RuleId, upsert.Rule);
                break;
            case DeleteProtectedItemRuleMutation delete:
                protectedRules.RemoveAll(rule => string.Equals(rule.RuleId, delete.RuleId, StringComparison.Ordinal));
                break;
            case SetRecommendationOverrideMutation set:
                Upsert(overrides, value => value.ItemId, set.Override.ItemId, set.Override);
                break;
            case DeleteRecommendationOverrideMutation delete:
                overrides.RemoveAll(value => string.Equals(value.ItemId, delete.ItemId, StringComparison.Ordinal));
                break;
            case UpsertFavoriteLoadoutMutation upsert:
                Upsert(loadouts, loadout => loadout.LoadoutId, upsert.Loadout.LoadoutId, upsert.Loadout);
                break;
            case DeleteFavoriteLoadoutMutation delete:
                loadouts.RemoveAll(loadout => string.Equals(loadout.LoadoutId, delete.LoadoutId, StringComparison.Ordinal));
                break;
            case SetSharedPersonalizationMutation set:
                var sharedIndex = shared.FindIndex(value =>
                    value.Kind == set.Value.Kind &&
                    string.Equals(value.ReferenceId, set.Value.ReferenceId, StringComparison.Ordinal));
                if (sharedIndex < 0)
                {
                    shared.Add(set.Value);
                }
                else
                {
                    shared[sharedIndex] = set.Value;
                }

                break;
            case DeleteSharedPersonalizationMutation delete:
                shared.RemoveAll(value =>
                    value.Kind == delete.Kind &&
                    string.Equals(value.ReferenceId, delete.ReferenceId, StringComparison.Ordinal));
                break;
            default:
                return Reject(scope, CommandDisposition.RejectedInvalidState, "unknown-preference-mutation");
        }

        var next = new ProfilePreferencesDocument(
            current.Context,
            PreferenceSchemaVersion.Current,
            items,
            protectedRules,
            overrides,
            loadouts,
            shared);
        return CommitPreferences(scope, next, "profile-preference-mutated");
    }

    private static CommandReduction ApplyResetPreferences(
        Scope scope,
        ResetProfilePreferencesCommand command)
    {
        var current = ValidatePreferenceTarget(scope, command.Context, command.SchemaVersion);
        return current.Reduction ?? CommitPreferences(
            scope,
            ProfilePreferencesDocument.Empty(current.Document!.Context),
            "profile-preferences-reset");
    }

    private static CommandReduction ApplyDeletePreferences(
        Scope scope,
        DeleteProfilePreferencesCommand command)
    {
        var current = ValidatePreferenceTarget(scope, command.Context, command.SchemaVersion);
        return current.Reduction ?? CommitPreferences(scope, null, "profile-preferences-deleted");
    }

    private static (ProfilePreferencesDocument? Document, CommandReduction? Reduction) ValidatePreferenceTarget(
        Scope scope,
        PreferenceProfileContext context,
        PreferenceSchemaVersion schemaVersion)
    {
        if (!PreferenceSchemaVersion.Current.CanRead(schemaVersion))
        {
            return (null, Reject(scope, CommandDisposition.UnsupportedPreferenceSchema, "preference-schema-unreadable"));
        }

        var current = scope.State.ProfilePreferences.ActiveProfile;
        return current is null || current.Context != context
            ? (null, Reject(scope, CommandDisposition.RequiresSnapshot, "preference-profile-context-mismatch"))
            : (current, null);
    }

    private static void Upsert<T>(List<T> values, Func<T, string> key, string expectedKey, T value)
    {
        var index = values.FindIndex(item => string.Equals(key(item), expectedKey, StringComparison.Ordinal));
        if (index < 0)
        {
            values.Add(value);
        }
        else
        {
            values[index] = value;
        }
    }

    private static bool CanExecute(CompanionCommand command, AuthenticatedCommandContext context) => command switch
    {
        SetInteractionModeCommand => context.Has(DeviceCapability.FollowDesktop),
        RequestControlCommand => context.Has(DeviceCapability.RequestControl),
        ResolveControlCommand => context.Has(DeviceCapability.ResolveControlRequests),
        PreemptControlCommand => context.IsDesktop,
        UpdateDesktopWorkspaceCommand => context.IsDesktop,
        ControlWorkspaceCommand => context.Has(DeviceCapability.RequestControl),
        ShowOnDesktopCommand => context.Has(DeviceCapability.ShowOnDesktop),
        UpsertMarkCommand or DeleteMarkCommand => context.Has(DeviceCapability.ManageOwnMarks),
        RequestCaptureIntentCommand => context.Has(DeviceCapability.RequestCaptureIntent),
        ReportCaptureProgressCommand or PublishCaptureResultCommand => context.IsDesktop,
        ReviewCaptureResultCommand or CorrectCaptureResultCommand => context.Has(DeviceCapability.ReviewCaptureResult),
        ActivateProfilePreferencesCommand => context.IsDesktop,
        MutateProfilePreferencesCommand or ResetProfilePreferencesCommand or DeleteProfilePreferencesCommand =>
            context.Has(DeviceCapability.ManageProfilePreferences),
        _ => false,
    };

    /// <summary>Only Show on desktop, mark mutation, and capture-intent request may carry an offline preview.</summary>
    public static bool IsQueueEligible(CompanionCommand command) => command is
        ShowOnDesktopCommand or UpsertMarkCommand or DeleteMarkCommand or RequestCaptureIntentCommand;

    private static WorkspaceProjection ApplyWorkspaceAction(
        WorkspaceProjection current,
        WorkspaceAction action,
        AuthenticatedCommandContext context) => action switch
    {
        NavigateWorkspaceAction navigate => new WorkspaceProjection(
            navigate.Workspace,
            navigate.MapId,
            navigate.FloorId,
            navigate.Viewport,
            current.Selection,
            current.VisibleObjectiveIds,
            current.PlanIds,
            current.SearchQuery,
            current.ResultIds,
            current.ActiveLayers,
            current.ActiveFilters,
            current.Dialog),
        SearchWorkspaceAction search => new WorkspaceProjection(
            current.Workspace,
            current.MapId,
            current.FloorId,
            current.Viewport,
            current.Selection,
            current.VisibleObjectiveIds,
            current.PlanIds,
            search.Query,
            current.ResultIds,
            current.ActiveLayers,
            current.ActiveFilters,
            current.Dialog),
        FilterWorkspaceAction filter => new WorkspaceProjection(
            current.Workspace,
            current.MapId,
            current.FloorId,
            current.Viewport,
            current.Selection,
            current.VisibleObjectiveIds,
            current.PlanIds,
            current.SearchQuery,
            current.ResultIds,
            filter.ActiveLayers,
            filter.ActiveFilters,
            current.Dialog),
        SelectWorkspaceAction select => new WorkspaceProjection(
            current.Workspace,
            current.MapId,
            current.FloorId,
            current.Viewport,
            select.Selection,
            current.VisibleObjectiveIds,
            current.PlanIds,
            current.SearchQuery,
            current.ResultIds,
            current.ActiveLayers,
            current.ActiveFilters,
            current.Dialog),
        OpenWorkspaceDialogAction dialog when context.IsDesktop || context.Has(DeviceCapability.ManageDevices) =>
            new WorkspaceProjection(
                current.Workspace,
                current.MapId,
                current.FloorId,
                current.Viewport,
                current.Selection,
                current.VisibleObjectiveIds,
                current.PlanIds,
                current.SearchQuery,
                current.ResultIds,
                current.ActiveLayers,
                current.ActiveFilters,
                dialog.Dialog),
        OpenWorkspaceDialogAction => throw new UnauthorizedAccessException("A sensitive dialog requires an explicit capability."),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static MapMark CreateMark(
        MarkId id,
        long revision,
        MapMarkDraft draft,
        CompanionDeviceId author,
        DateTimeOffset now,
        CommandId changeId,
        DateTimeOffset? created = null)
    {
        // A ping's lifetime runs from its creation, so editing it can never extend it past 45 seconds.
        var createdUtc = created ?? now;
        var requested = draft.State;
        var expires = draft.Kind == MapMarkKind.Ping
            ? requested.ExpiresUtc ?? createdUtc.Add(ProtocolBounds.PingLifetime)
            : requested.ExpiresUtc;
        if (expires <= now ||
            (draft.Kind == MapMarkKind.Ping && expires - createdUtc > ProtocolBounds.PingLifetime))
        {
            throw new ArgumentException("The mark expiry is stale or exceeds the ping lifetime.");
        }

        return new MapMark(
            id,
            revision,
            changeId,
            draft.Kind,
            draft.Scope,
            author,
            new MapMarkState(requested.MapId, requested.FloorId, requested.X, requested.Y, requested.Label, expires),
            draft.CoordinateSpace,
            draft.ProjectionVersion,
            draft.Height,
            draft.Color,
            createdUtc,
            now);
    }

    private static ContextualCaptureIntent RequireCapture(
        CanonicalCompanionState state,
        CaptureIntentId intentId,
        DateTimeOffset now)
    {
        var capture = state.CaptureIntent.ActiveIntent;
        if (capture is null || capture.IntentId != intentId || capture.ExpiresUtc <= now || IsTerminal(capture.Status))
        {
            throw new InvalidOperationException("The active capture intent cannot make that transition.");
        }

        return capture;
    }

    private static bool IsTerminal(ContextualCaptureStatus status) => status is
        ContextualCaptureStatus.Complete or ContextualCaptureStatus.Cancelled or
        ContextualCaptureStatus.Failed or ContextualCaptureStatus.Expired;

    private static ContextualCaptureIntent CopyCapture(
        ContextualCaptureIntent current,
        ContextualCaptureStatus? status = null,
        IReadOnlyList<ContextualCaptureProgress>? progress = null,
        ContextualCaptureResult? result = null,
        IReadOnlyList<ContextualCaptureGuidance>? guidance = null,
        ContextualCaptureReview? review = null,
        IReadOnlyList<ContextualCaptureCorrection>? corrections = null,
        bool replaceResult = false,
        bool replaceReview = false) =>
        new(
            current.IntentId,
            current.CorrelationId,
            current.CaptureSessionId,
            current.State,
            current.InitiatingDeviceId,
            current.InitiatingSurface,
            status ?? current.Status,
            current.Context,
            progress ?? current.Progress,
            replaceResult ? result : current.Result,
            guidance ?? current.Guidance,
            replaceReview ? review : current.Review,
            corrections ?? current.Corrections);

    private static IReadOnlyList<DeviceModeEntry> SetMode(
        IReadOnlyList<DeviceModeEntry> entries,
        CompanionDeviceId deviceId,
        CompanionInteractionMode mode,
        DateTimeOffset changedUtc)
    {
        var copy = entries.ToList();
        var index = copy.FindIndex(item => item.DeviceId == deviceId);
        var entry = new DeviceModeEntry(deviceId, mode, changedUtc);
        if (index < 0)
        {
            if (copy.Count >= ProtocolBounds.MaxDevices)
            {
                throw new InvalidOperationException("The paired device bound has been reached.");
            }

            copy.Add(entry);
        }
        else
        {
            copy[index] = entry;
        }

        return copy;
    }

    private static CommandReduction CommitModes(
        Scope scope,
        IReadOnlyList<DeviceModeEntry> devices,
        PendingControlRequest? pending,
        ControlLease? lease,
        string code) =>
        Commit(
            scope,
            new DeviceModeAggregate(
                new AggregateCursor(scope.Command.RequestedRevision, scope.Command.CommandId),
                devices,
                pending,
                lease),
            code);

    private static CommandReduction CommitWorkspace(Scope scope, WorkspaceProjection projection, string code) =>
        Commit(
            scope,
            new WorkspaceAggregate(new AggregateCursor(scope.Command.RequestedRevision, scope.Command.CommandId), projection),
            code);

    private static CommandReduction CommitCapture(Scope scope, ContextualCaptureIntent intent, string code) =>
        Commit(
            scope,
            new CaptureIntentAggregate(new AggregateCursor(scope.Command.RequestedRevision, scope.Command.CommandId), intent),
            code);

    private static CommandReduction CommitPreferences(
        Scope scope,
        ProfilePreferencesDocument? preferences,
        string code)
    {
        var origin = new WorkspaceOrigin(
            scope.State.WorkspaceId,
            scope.Context.DeviceId,
            scope.Context.IsDesktop ? WorkspaceOriginKind.DesktopApplication : WorkspaceOriginKind.PairedDevice,
            scope.Context.InstanceId);
        return Commit(
            scope,
            new ProfilePreferencesAggregate(
                new AggregateCursor(scope.Command.RequestedRevision, scope.Command.CommandId),
                preferences,
                origin,
                scope.Now),
            code);
    }

    private static CommandReduction Commit(Scope scope, object aggregate, string code)
    {
        var state = scope.State;
        var command = scope.Command;
        var global = state.GlobalRevision.Next();
        var origin = new WorkspaceOrigin(
            state.WorkspaceId,
            scope.Context.DeviceId,
            scope.Context.IsDesktop ? WorkspaceOriginKind.DesktopApplication : WorkspaceOriginKind.PairedDevice,
            scope.Context.InstanceId);
        var contract = V2ContractVersion.Current;
        CanonicalCompanionState staged;
        CanonicalUpdate update;
        switch (aggregate)
        {
            case DeviceModeAggregate modes:
                staged = state.With(global, deviceModes: modes);
                update = new DeviceModeCanonicalUpdate(state.AuthorityEpoch, global, command.CommandId, scope.Now, origin, contract, modes);
                break;
            case WorkspaceAggregate workspace:
                staged = state.With(global, workspace: workspace);
                update = new WorkspaceCanonicalUpdate(state.AuthorityEpoch, global, command.CommandId, scope.Now, origin, contract, workspace);
                break;
            case MarkAggregate marks:
                staged = state.With(global, marks: marks);
                update = new MarksCanonicalUpdate(state.AuthorityEpoch, global, command.CommandId, scope.Now, origin, contract, marks);
                break;
            case CaptureIntentAggregate capture:
                staged = state.With(global, captureIntent: capture);
                update = new CaptureCanonicalUpdate(state.AuthorityEpoch, global, command.CommandId, scope.Now, origin, contract, capture);
                break;
            case ProfilePreferencesAggregate preferences:
                staged = state.With(global, profilePreferences: preferences);
                update = new ProfilePreferencesCanonicalUpdate(
                    state.AuthorityEpoch,
                    global,
                    command.CommandId,
                    scope.Now,
                    origin,
                    contract,
                    preferences);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(aggregate));
        }

        var receipt = new RecentCommandReceipt(
            command.CommandId,
            scope.Fingerprint,
            scope.Context.DeviceId,
            command.Aggregate,
            command.RequestedRevision,
            command.IssuedUtc,
            command.ExpiresUtc);
        var (receipts, horizon) = RetainReceipts(
            staged,
            state.RecentCommands.Where(item => item.CommandId != command.CommandId).Append(receipt),
            scope.Now);
        var next = staged.With(
            global,
            recentCommands: receipts,
            receiptHorizonUtc: horizon);
        return new CommandReduction(
            next,
            Acknowledge(next, command, CommandDisposition.Applied, code, command.RequestedRevision, command.RequestedRevision, command.CommandId),
            update);
    }

    /// <summary>
    /// A receipt is retained until its command expires, and always while its change still occupies
    /// its aggregate cursor, so the newest change's exact retry is recognized after expiry and a
    /// rejection can never have to name the rejected command as the applied change. When the bound
    /// forces an unexpired receipt out, the receipt horizon advances to its issue time. A conforming
    /// retransmission repeats the original issue time, so an evicted command fails closed instead of
    /// applying twice.
    /// </summary>
    private static (IReadOnlyList<RecentCommandReceipt> Receipts, DateTimeOffset? Horizon) RetainReceipts(
        CanonicalCompanionState state,
        IEnumerable<RecentCommandReceipt> receipts,
        DateTimeOffset now)
    {
        var horizon = state.ReceiptHorizonUtc;
        var retained = receipts.Where(item => IsRetained(state, item, now)).ToList();
        while (retained.Count > ProtocolBounds.MaxRecentCommands)
        {
            var oldestUnpinned = retained.FindIndex(item => !IsPinned(state, item));
            if (oldestUnpinned < 0)
            {
                break;
            }

            var evicted = retained[oldestUnpinned];
            horizon = Later(horizon, evicted.IssuedUtc);
            retained.RemoveAt(oldestUnpinned);
        }

        return (retained, horizon);
    }

    private static DateTimeOffset Later(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is { } value && value >= candidate ? value : candidate;

    private static bool IsRetained(CanonicalCompanionState state, RecentCommandReceipt receipt, DateTimeOffset now) =>
        receipt.ExpiresUtc > now || IsPinned(state, receipt);

    private static bool IsPinned(CanonicalCompanionState state, RecentCommandReceipt receipt) =>
        state.Cursor(receipt.Aggregate).LastChangeId == receipt.CommandId;

    private static bool OccupiesAnyCursor(CanonicalCompanionState state, CommandId commandId) =>
        Enum.GetValues<CanonicalAggregateKind>().Any(aggregate => state.Cursor(aggregate).LastChangeId == commandId);

    private static CommandReduction Reject(Scope scope, CommandDisposition disposition, string code) =>
        Reject(scope.State, scope.Command, disposition, code);

    /// <summary>
    /// A rejection that carries canonical state describes the aggregate cursor it holds; any other
    /// rejection describes no revision (zero and no change id), so no rejection can be read as naming
    /// the rejected command, or a reused identifier's original change, as the applied change.
    /// </summary>
    private static CommandReduction Reject(
        CanonicalCompanionState state,
        CompanionCommand command,
        CommandDisposition disposition,
        string code)
    {
        var cursor = state.Cursor(command.Aggregate);
        if (CommandAcknowledgement.RequiresCanonicalState(disposition) && cursor.LastChangeId == command.CommandId)
        {
            // Unreachable: the change occupying a cursor always keeps its receipt, so its id reaches
            // the duplicate or reuse check first. Kept so the typed rejection can never throw.
            disposition = CommandDisposition.RejectedCommandIdReuse;
            code = "command-id-reused";
        }

        var carriesState = CommandAcknowledgement.RequiresCanonicalState(disposition);
        return new CommandReduction(
            state,
            Acknowledge(
                state,
                command,
                disposition,
                code,
                command.RequestedRevision,
                carriesState ? cursor.Revision : new AggregateRevision(0),
                carriesState ? cursor.LastChangeId : null),
            null);
    }

    private static CommandAcknowledgement Acknowledge(
        CanonicalCompanionState state,
        CompanionCommand command,
        CommandDisposition disposition,
        string code,
        AggregateRevision requestedRevision,
        AggregateRevision appliedRevision,
        CommandId? appliedChangeId) =>
        new(
            command.CommandId,
            command.Aggregate,
            requestedRevision,
            appliedRevision,
            appliedChangeId,
            state.GlobalRevision,
            state.AuthorityEpoch,
            disposition,
            code,
            CommandAcknowledgement.RequiresCanonicalState(disposition) ? state : null);

    private static MaintenanceReduction CommitModeChange(
        CanonicalCompanionState state,
        IReadOnlyList<DeviceModeEntry> devices,
        PendingControlRequest? pending,
        ControlLease? lease,
        DateTimeOffset changedUtc,
        string reason)
    {
        var change = SyntheticId(state, CanonicalAggregateKind.DeviceModes, changedUtc, reason);
        var aggregate = new DeviceModeAggregate(
            new AggregateCursor(state.DeviceModes.Cursor.Revision.Next(), change),
            devices,
            pending,
            lease);
        var global = state.GlobalRevision.Next();
        var next = state.With(global, deviceModes: aggregate);
        return new MaintenanceReduction(
            next,
            [new DeviceModeCanonicalUpdate(
                state.AuthorityEpoch,
                global,
                change,
                changedUtc,
                DesktopOrigin(state),
                V2ContractVersion.Current,
                aggregate)]);
    }

    private static CommandId SyntheticId(
        CanonicalCompanionState state,
        CanonicalAggregateKind aggregate,
        DateTimeOffset now,
        string reason)
    {
        var material = Encoding.UTF8.GetBytes(
            $"{state.AuthorityEpoch.Value:D}|{state.GlobalRevision.Value}|{aggregate}|{now.ToUnixTimeMilliseconds()}|{reason}");
        return new CommandId(ProtocolGuard.UuidVersion8(SHA256.HashData(material)));
    }

    private static WorkspaceOrigin DesktopOrigin(CanonicalCompanionState state) =>
        new(state.WorkspaceId, state.DesktopDeviceId, WorkspaceOriginKind.DesktopApplication, state.DesktopInstanceId);

    private sealed record Scope(
        CanonicalCompanionState State,
        CompanionCommand Command,
        AuthenticatedCommandContext Context,
        CommandFingerprint Fingerprint)
    {
        public DateTimeOffset Now => Context.ReceivedUtc;
    }
}
