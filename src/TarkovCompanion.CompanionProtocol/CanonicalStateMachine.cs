using System.Security.Cryptography;
using System.Text;
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

    public CompanionSurfaceKind Surface { get; }

    public IReadOnlyList<DeviceCapability> Capabilities { get; }

    public DateTimeOffset ReceivedUtc { get; }

    public bool IsDesktop { get; }

    public bool Has(DeviceCapability capability) => IsDesktop || Capabilities.Contains(capability);
}

public sealed record CommandReduction(
    CanonicalCompanionState State,
    CommandAcknowledgement Acknowledgement,
    CanonicalUpdate? Update);

public sealed record MaintenanceReduction(
    CanonicalCompanionState State,
    IReadOnlyList<CanonicalUpdate> Updates);

/// <summary>The deterministic desktop-canonical reducer used by every direct or relay transport.</summary>
public static class DesktopCanonicalStateMachine
{
    public static CommandReduction Apply(
        CanonicalCompanionState state,
        ClientCommandEnvelope envelope,
        AuthenticatedCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        var command = envelope.Command;

        if (context.IsDesktop != (context.DeviceId == state.DesktopDeviceId) ||
            envelope.SessionId != context.SessionId)
        {
            return Reject(state, command, CommandDisposition.RejectedUnauthorized, "authenticated-session-mismatch");
        }

        if (!CompanionProtocolVersion.Current.CanRead(envelope.ProtocolVersion))
        {
            return Reject(state, command, CommandDisposition.UnsupportedVersion, "unsupported-version");
        }

        var prior = state.RecentCommands.FirstOrDefault(item =>
            item.DeviceId == context.DeviceId && item.CommandId == command.CommandId && item.ExpiresUtc > context.ReceivedUtc);
        if (prior is not null)
        {
            return new CommandReduction(
                state,
                Acknowledge(
                    state,
                    command,
                    CommandDisposition.Duplicate,
                    "duplicate-command",
                    prior.AppliedRevision,
                    canonicalState: null),
                null);
        }

        if (context.ReceivedUtc >= command.ExpiresUtc)
        {
            return Reject(state, command, CommandDisposition.RejectedExpired, "command-expired");
        }

        if (!CanExecute(command, context))
        {
            return Reject(state, command, CommandDisposition.RejectedUnauthorized, "capability-denied");
        }

        if (command.OfflineQueuePreview is { } preview)
        {
            if (!CanQueue(command))
            {
                return Reject(state, command, CommandDisposition.RejectedInvalidState, "command-cannot-be-queued");
            }

            if (preview.PreviewedUtc > context.ReceivedUtc ||
                preview.PreviewedAuthorityEpoch != state.AuthorityEpoch ||
                preview.PreviewedAggregateRevision != state.Cursor(command.Aggregate).Revision)
            {
                return Reject(
                    state,
                    command,
                    CommandDisposition.RequiresPreview,
                    "offline-action-needs-current-preview",
                    includeCanonical: true);
            }
        }

        var cursor = state.Cursor(command.Aggregate);
        if (command.RequestedRevision.Value <= cursor.Revision.Value)
        {
            var disposition = command.RequestedRevision == cursor.Revision
                ? CommandDisposition.RejectedConflict
                : CommandDisposition.RejectedStale;
            return Reject(
                state,
                command,
                disposition,
                disposition == CommandDisposition.RejectedConflict
                    ? "revision-occupied-by-another-command"
                    : "stale-aggregate-revision",
                includeCanonical: true);
        }

        if (command.RequestedRevision != cursor.Revision.Next())
        {
            return Reject(
                state,
                command,
                CommandDisposition.RejectedConflict,
                "revision-gap",
                includeCanonical: true);
        }

        try
        {
            return command switch
            {
                SetInteractionModeCommand setMode => ApplySetMode(state, setMode, context),
                RequestControlCommand requestControl => ApplyRequestControl(state, requestControl, context),
                ResolveControlCommand resolveControl => ApplyResolveControl(state, resolveControl, context),
                PreemptControlCommand preemptControl => ApplyPreemptControl(state, preemptControl, context),
                ControlWorkspaceCommand controlWorkspace => ApplyControlWorkspace(state, controlWorkspace, context),
                ShowOnDesktopCommand show => ApplyShowOnDesktop(state, show, context),
                UpsertMarkCommand upsert => ApplyUpsertMark(state, upsert, context),
                DeleteMarkCommand delete => ApplyDeleteMark(state, delete, context),
                RequestCaptureIntentCommand capture => ApplyRequestCapture(state, capture, context),
                ReportCaptureProgressCommand progress => ApplyCaptureProgress(state, progress, context),
                PublishCaptureResultCommand result => ApplyCaptureResult(state, result, context),
                ReviewCaptureResultCommand review => ApplyCaptureReview(state, review, context),
                CorrectCaptureResultCommand correction => ApplyCaptureCorrection(state, correction, context),
                _ => Reject(state, command, CommandDisposition.RejectedInvalidState, "unknown-command"),
            };
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
    }

    /// <summary>
    /// Applies server-time expiry as ordinary revisioned updates. Synthetic change ids are derived
    /// from the prior state and instant, so replaying maintenance is deterministic.
    /// </summary>
    public static MaintenanceReduction ApplyMaintenance(CanonicalCompanionState state, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        var current = state;
        var updates = new List<CanonicalUpdate>();

        if (current.DeviceModes.ControlLease is { } lease && lease.ExpiresUtc <= now)
        {
            var change = SyntheticId(current, CanonicalAggregateKind.DeviceModes, now, "control-lease-expired");
            var modes = SetMode(current.DeviceModes.Devices, lease.DeviceId, CompanionInteractionMode.Follow, now);
            var aggregate = new DeviceModeAggregate(
                new AggregateCursor(current.DeviceModes.Cursor.Revision.Next(), change),
                modes,
                current.DeviceModes.PendingControl,
                null);
            var committed = CommitMaintenance(current, aggregate, change, now);
            current = committed.State;
            updates.Add(committed.Update);
        }

        if (current.DeviceModes.PendingControl is { } pending && pending.ExpiresUtc <= now)
        {
            var change = SyntheticId(current, CanonicalAggregateKind.DeviceModes, now, "control-request-expired");
            var modes = SetMode(current.DeviceModes.Devices, pending.DeviceId, CompanionInteractionMode.Follow, now);
            var aggregate = new DeviceModeAggregate(
                new AggregateCursor(current.DeviceModes.Cursor.Revision.Next(), change),
                modes,
                null,
                current.DeviceModes.ControlLease);
            var committed = CommitMaintenance(current, aggregate, change, now);
            current = committed.State;
            updates.Add(committed.Update);
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
            updates.Add(new MarksCanonicalUpdate(current.AuthorityEpoch, global, change, now, aggregate));
        }

        if (current.CaptureIntent.ActiveIntent is { } capture &&
            capture.ExpiresUtc <= now &&
            capture.Status is not (ContextualCaptureStatus.Complete or ContextualCaptureStatus.Cancelled or
                ContextualCaptureStatus.Failed or ContextualCaptureStatus.Expired))
        {
            var change = SyntheticId(current, CanonicalAggregateKind.CaptureIntent, now, "capture-intent-expired");
            var expired = CopyCapture(capture, status: ContextualCaptureStatus.Expired);
            var aggregate = new CaptureIntentAggregate(
                new AggregateCursor(current.CaptureIntent.Cursor.Revision.Next(), change),
                expired);
            var global = current.GlobalRevision.Next();
            current = current.With(global, captureIntent: aggregate);
            updates.Add(new CaptureCanonicalUpdate(current.AuthorityEpoch, global, change, now, aggregate));
        }

        return new MaintenanceReduction(current, ProtocolGuard.List(updates, nameof(updates)));
    }

    private static CommandReduction ApplySetMode(
        CanonicalCompanionState state,
        SetInteractionModeCommand command,
        AuthenticatedCommandContext context)
    {
        var modes = SetMode(state.DeviceModes.Devices, context.DeviceId, command.Mode, context.ReceivedUtc);
        var pending = state.DeviceModes.PendingControl?.DeviceId == context.DeviceId
            ? null
            : state.DeviceModes.PendingControl;
        var lease = state.DeviceModes.ControlLease?.DeviceId == context.DeviceId
            ? null
            : state.DeviceModes.ControlLease;
        return CommitMode(state, command, context, modes, pending, lease, CommandDisposition.Applied, "mode-applied");
    }

    private static CommandReduction ApplyRequestControl(
        CanonicalCompanionState state,
        RequestControlCommand command,
        AuthenticatedCommandContext context)
    {
        if (context.IsDesktop || state.DeviceModes.PendingControl is not null ||
            state.DeviceModes.ControlLease?.DeviceId == context.DeviceId)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "control-request-not-available");
        }

        var pending = new PendingControlRequest(
            command.CommandId,
            context.DeviceId,
            context.SessionId,
            context.ReceivedUtc,
            command.ExpiresUtc,
            command.RequestedLease);
        var modes = SetMode(
            state.DeviceModes.Devices,
            context.DeviceId,
            CompanionInteractionMode.ControlPending,
            context.ReceivedUtc);
        return CommitMode(
            state,
            command,
            context,
            modes,
            pending,
            state.DeviceModes.ControlLease,
            CommandDisposition.PendingDesktopApproval,
            "desktop-approval-required");
    }

    private static CommandReduction ApplyResolveControl(
        CanonicalCompanionState state,
        ResolveControlCommand command,
        AuthenticatedCommandContext context)
    {
        var pending = state.DeviceModes.PendingControl;
        if (pending is null || pending.RequestCommandId != command.RequestCommandId || pending.ExpiresUtc <= context.ReceivedUtc)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "pending-control-request-not-found");
        }

        var modes = state.DeviceModes.Devices;
        ControlLease? lease = null;
        if (command.Approved)
        {
            if (state.DeviceModes.ControlLease is { } existing)
            {
                modes = SetMode(modes, existing.DeviceId, CompanionInteractionMode.Follow, context.ReceivedUtc);
            }

            modes = SetMode(modes, pending.DeviceId, CompanionInteractionMode.Control, context.ReceivedUtc);
            lease = new ControlLease(
                command.LeaseId!.Value,
                pending.DeviceId,
                pending.SessionId,
                context.ReceivedUtc,
                context.ReceivedUtc.Add(pending.RequestedLease));
        }
        else
        {
            modes = SetMode(modes, pending.DeviceId, CompanionInteractionMode.Follow, context.ReceivedUtc);
        }

        return CommitMode(
            state,
            command,
            context,
            modes,
            null,
            lease,
            CommandDisposition.Applied,
            command.Approved ? "control-granted" : "control-denied");
    }

    private static CommandReduction ApplyPreemptControl(
        CanonicalCompanionState state,
        PreemptControlCommand command,
        AuthenticatedCommandContext context)
    {
        if (!context.IsDesktop || state.DeviceModes.ControlLease is not { } lease)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "no-control-lease-to-preempt");
        }

        var modes = SetMode(state.DeviceModes.Devices, lease.DeviceId, CompanionInteractionMode.Follow, context.ReceivedUtc);
        return CommitMode(
            state,
            command,
            context,
            modes,
            state.DeviceModes.PendingControl,
            null,
            CommandDisposition.Applied,
            "desktop-preempted-control");
    }

    private static CommandReduction ApplyControlWorkspace(
        CanonicalCompanionState state,
        ControlWorkspaceCommand command,
        AuthenticatedCommandContext context)
    {
        var lease = state.DeviceModes.ControlLease;
        if (lease is null || lease.DeviceId != context.DeviceId || lease.SessionId != context.SessionId ||
            lease.ExpiresUtc <= context.ReceivedUtc)
        {
            return Reject(state, command, CommandDisposition.RejectedUnauthorized, "active-control-lease-required");
        }

        var projection = ApplyWorkspaceAction(state.Workspace.Projection, command.Action, context);
        return CommitWorkspace(state, command, context, projection, "workspace-action-applied");
    }

    private static CommandReduction ApplyShowOnDesktop(
        CanonicalCompanionState state,
        ShowOnDesktopCommand command,
        AuthenticatedCommandContext context)
    {
        if (ModeOf(state, context.DeviceId) != CompanionInteractionMode.Independent)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "show-requires-independent-mode");
        }

        return CommitWorkspace(state, command, context, command.Projection, "independent-view-shown");
    }

    private static CommandReduction ApplyUpsertMark(
        CanonicalCompanionState state,
        UpsertMarkCommand command,
        AuthenticatedCommandContext context)
    {
        if (command.Mark.Scope == MapMarkScope.Team && !context.Has(DeviceCapability.PublishTeamMarks))
        {
            return Reject(state, command, CommandDisposition.RejectedUnauthorized, "team-publication-capability-required");
        }

        var marks = state.Marks.Marks.ToList();
        var index = marks.FindIndex(mark => mark.MarkId == command.MarkId);
        MapMark next;
        if (index < 0)
        {
            if (command.ExpectedMarkRevision != 0 || marks.Count >= ProtocolBounds.MaxMarks)
            {
                return Reject(state, command, CommandDisposition.RejectedConflict, "mark-create-conflict", includeCanonical: true);
            }

            next = CreateMark(command.MarkId, 1, command.Mark, context.DeviceId, context.ReceivedUtc);
            marks.Add(next);
        }
        else
        {
            var current = marks[index];
            if (current.Revision != command.ExpectedMarkRevision)
            {
                return Reject(state, command, CommandDisposition.RejectedConflict, "mark-revision-conflict", includeCanonical: true);
            }

            if (!context.IsDesktop && current.AuthorDeviceId != context.DeviceId)
            {
                return Reject(state, command, CommandDisposition.RejectedUnauthorized, "mark-author-required");
            }

            next = CreateMark(
                current.MarkId,
                checked(current.Revision + 1),
                command.Mark,
                current.AuthorDeviceId,
                context.ReceivedUtc,
                current.CreatedUtc);
            marks[index] = next;
        }

        var aggregate = new MarkAggregate(
            new AggregateCursor(command.RequestedRevision, command.CommandId),
            marks);
        return Commit(state, command, context, aggregate, CommandDisposition.Applied, "mark-applied");
    }

    private static CommandReduction ApplyDeleteMark(
        CanonicalCompanionState state,
        DeleteMarkCommand command,
        AuthenticatedCommandContext context)
    {
        var marks = state.Marks.Marks.ToList();
        var index = marks.FindIndex(mark => mark.MarkId == command.MarkId);
        if (index < 0 || marks[index].Revision != command.ExpectedMarkRevision)
        {
            return Reject(state, command, CommandDisposition.RejectedConflict, "mark-delete-conflict", includeCanonical: true);
        }

        if (!context.IsDesktop && marks[index].AuthorDeviceId != context.DeviceId)
        {
            return Reject(state, command, CommandDisposition.RejectedUnauthorized, "mark-author-required");
        }

        marks.RemoveAt(index);
        var aggregate = new MarkAggregate(
            new AggregateCursor(command.RequestedRevision, command.CommandId),
            marks);
        return Commit(state, command, context, aggregate, CommandDisposition.Applied, "mark-deleted");
    }

    private static CommandReduction ApplyRequestCapture(
        CanonicalCompanionState state,
        RequestCaptureIntentCommand command,
        AuthenticatedCommandContext context)
    {
        var expires = new[] { command.ExpiresUtc, context.ReceivedUtc.Add(ProtocolBounds.CaptureIntentLifetime) }.Min();
        var intent = new ContextualCaptureIntent(
            command.IntentId,
            command.CorrelationId,
            command.CaptureSessionId,
            command.Purpose,
            context.DeviceId,
            context.Surface,
            context.ReceivedUtc,
            expires,
            ContextualCaptureStatus.Armed,
            command.Context,
            [
                new ContextualCaptureProgress(
                    0,
                    ContextualCaptureProgressPhase.Armed,
                    context.ReceivedUtc,
                    0,
                    null,
                    null,
                    "recognition-intent-armed"),
            ],
            null,
            [],
            null,
            []);
        var aggregate = new CaptureIntentAggregate(
            new AggregateCursor(command.RequestedRevision, command.CommandId),
            intent);
        return Commit(state, command, context, aggregate, CommandDisposition.Applied, "capture-intent-armed");
    }

    private static CommandReduction ApplyCaptureProgress(
        CanonicalCompanionState state,
        ReportCaptureProgressCommand command,
        AuthenticatedCommandContext context)
    {
        var current = RequireCapture(state, command.IntentId, context.ReceivedUtc);
        if (command.Phase is ContextualCaptureProgressPhase.AwaitingReview or ContextualCaptureProgressPhase.Complete)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "result-command-required");
        }

        var progress = current.Progress.Append(new ContextualCaptureProgress(
            current.Progress.Count,
            command.Phase,
            context.ReceivedUtc,
            command.Percent,
            command.ArtifactId,
            command.CaptureOrdinal,
            command.Detail)).ToArray();
        var status = command.Phase switch
        {
            ContextualCaptureProgressPhase.Armed => ContextualCaptureStatus.Armed,
            ContextualCaptureProgressPhase.AwaitingUserCapture => ContextualCaptureStatus.AwaitingUserCapture,
            ContextualCaptureProgressPhase.Cancelled => ContextualCaptureStatus.Cancelled,
            ContextualCaptureProgressPhase.Failed => ContextualCaptureStatus.Failed,
            _ => ContextualCaptureStatus.InProgress,
        };
        var next = CopyCapture(current, status: status, progress: progress);
        var aggregate = new CaptureIntentAggregate(
            new AggregateCursor(command.RequestedRevision, command.CommandId),
            next);
        return Commit(state, command, context, aggregate, CommandDisposition.Applied, "capture-progress-applied");
    }

    private static CommandReduction ApplyCaptureResult(
        CanonicalCompanionState state,
        PublishCaptureResultCommand command,
        AuthenticatedCommandContext context)
    {
        var current = RequireCapture(state, command.IntentId, context.ReceivedUtc);
        if (command.Result.CompletedUtc > context.ReceivedUtc ||
            command.Guidance.Select(item => item.Order).Distinct().Count() != command.Guidance.Count)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "invalid-capture-result");
        }

        var correlated = current.Progress.LastOrDefault(item =>
            item.ArtifactId is not null &&
            item.CaptureOrdinal == command.Result.CaptureOrdinal &&
            string.Equals(item.ArtifactId, command.Result.ArtifactId, StringComparison.Ordinal));
        if (correlated is null)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "result-artifact-not-correlated");
        }

        var progress = current.Progress.Append(new ContextualCaptureProgress(
            current.Progress.Count,
            ContextualCaptureProgressPhase.AwaitingReview,
            context.ReceivedUtc,
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
        var aggregate = new CaptureIntentAggregate(
            new AggregateCursor(command.RequestedRevision, command.CommandId),
            next);
        return Commit(state, command, context, aggregate, CommandDisposition.Applied, "capture-result-applied");
    }

    private static CommandReduction ApplyCaptureReview(
        CanonicalCompanionState state,
        ReviewCaptureResultCommand command,
        AuthenticatedCommandContext context)
    {
        var current = RequireCapture(state, command.IntentId, context.ReceivedUtc);
        if (current.Status != ContextualCaptureStatus.AwaitingReview || current.Result is null)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "capture-not-awaiting-review");
        }

        var review = new ContextualCaptureReview(
            command.Disposition,
            context.DeviceId,
            context.ReceivedUtc,
            command.Note);
        var status = command.Disposition == CaptureReviewDisposition.Accepted
            ? ContextualCaptureStatus.Complete
            : ContextualCaptureStatus.AwaitingReview;
        var progress = command.Disposition == CaptureReviewDisposition.Accepted
            ? current.Progress.Append(new ContextualCaptureProgress(
                current.Progress.Count,
                ContextualCaptureProgressPhase.Complete,
                context.ReceivedUtc,
                100,
                current.Result.ArtifactId,
                current.Result.CaptureOrdinal,
                "result-accepted")).ToArray()
            : current.Progress;
        var next = CopyCapture(
            current,
            status: status,
            progress: progress,
            review: review,
            replaceReview: true);
        var aggregate = new CaptureIntentAggregate(
            new AggregateCursor(command.RequestedRevision, command.CommandId),
            next);
        return Commit(state, command, context, aggregate, CommandDisposition.Applied, "capture-review-applied");
    }

    private static CommandReduction ApplyCaptureCorrection(
        CanonicalCompanionState state,
        CorrectCaptureResultCommand command,
        AuthenticatedCommandContext context)
    {
        var current = RequireCapture(state, command.IntentId, context.ReceivedUtc);
        if (current.Status != ContextualCaptureStatus.AwaitingReview || current.Result is null)
        {
            return Reject(state, command, CommandDisposition.RejectedInvalidState, "capture-not-correctable");
        }

        var correction = new ContextualCaptureCorrection(
            current.Corrections.Count + 1,
            command.Kind,
            command.FieldId,
            command.CorrectedValue,
            context.DeviceId,
            context.ReceivedUtc,
            command.Reason);
        var next = CopyCapture(current, corrections: current.Corrections.Append(correction).ToArray());
        var aggregate = new CaptureIntentAggregate(
            new AggregateCursor(command.RequestedRevision, command.CommandId),
            next);
        return Commit(state, command, context, aggregate, CommandDisposition.Applied, "capture-correction-appended");
    }

    private static bool CanExecute(CompanionCommand command, AuthenticatedCommandContext context) => command switch
    {
        SetInteractionModeCommand => context.Has(DeviceCapability.FollowDesktop),
        RequestControlCommand => context.Has(DeviceCapability.RequestControl),
        ResolveControlCommand => context.Has(DeviceCapability.ResolveControlRequests),
        PreemptControlCommand => context.IsDesktop,
        ControlWorkspaceCommand => context.Has(DeviceCapability.RequestControl),
        ShowOnDesktopCommand => context.Has(DeviceCapability.ShowOnDesktop),
        UpsertMarkCommand or DeleteMarkCommand => context.Has(DeviceCapability.ManageOwnMarks),
        RequestCaptureIntentCommand => context.Has(DeviceCapability.RequestCaptureIntent),
        ReportCaptureProgressCommand or PublishCaptureResultCommand => context.Has(DeviceCapability.ReportCaptureProgress),
        ReviewCaptureResultCommand or CorrectCaptureResultCommand => context.Has(DeviceCapability.ReviewCaptureResult),
        _ => false,
    };

    private static bool CanQueue(CompanionCommand command) => command is
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
        DateTimeOffset? created = null)
    {
        var expires = draft.Kind == MapMarkKind.Ping
            ? draft.ExpiresUtc ?? now.Add(ProtocolBounds.PingLifetime)
            : draft.ExpiresUtc;
        if (expires <= now ||
            (draft.Kind == MapMarkKind.Ping && expires - now > ProtocolBounds.PingLifetime))
        {
            throw new ArgumentException("The mark expiry is stale or exceeds the ping lifetime.");
        }

        return new MapMark(
            id,
            revision,
            draft.Kind,
            draft.Scope,
            author,
            draft.Coordinate,
            draft.Label,
            draft.Color,
            created ?? now,
            now,
            expires);
    }

    private static ContextualCaptureIntent RequireCapture(
        CanonicalCompanionState state,
        CaptureIntentId intentId,
        DateTimeOffset now)
    {
        var capture = state.CaptureIntent.ActiveIntent;
        if (capture is null || capture.IntentId != intentId || capture.ExpiresUtc <= now ||
            capture.Status is ContextualCaptureStatus.Complete or ContextualCaptureStatus.Cancelled or
                ContextualCaptureStatus.Failed or ContextualCaptureStatus.Expired)
        {
            throw new InvalidOperationException("The active capture intent cannot make that transition.");
        }

        return capture;
    }

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
            current.Purpose,
            current.InitiatingDeviceId,
            current.InitiatingSurface,
            current.RequestedUtc,
            current.ExpiresUtc,
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

    private static CompanionInteractionMode? ModeOf(CanonicalCompanionState state, CompanionDeviceId deviceId) =>
        state.DeviceModes.Devices.FirstOrDefault(item => item.DeviceId == deviceId)?.Mode;

    private static CommandReduction CommitMode(
        CanonicalCompanionState state,
        CompanionCommand command,
        AuthenticatedCommandContext context,
        IReadOnlyList<DeviceModeEntry> modes,
        PendingControlRequest? pending,
        ControlLease? lease,
        CommandDisposition disposition,
        string code)
    {
        var aggregate = new DeviceModeAggregate(
            new AggregateCursor(command.RequestedRevision, command.CommandId),
            modes,
            pending,
            lease);
        return Commit(state, command, context, aggregate, disposition, code);
    }

    private static CommandReduction CommitWorkspace(
        CanonicalCompanionState state,
        CompanionCommand command,
        AuthenticatedCommandContext context,
        WorkspaceProjection projection,
        string code)
    {
        var aggregate = new WorkspaceAggregate(
            new AggregateCursor(command.RequestedRevision, command.CommandId),
            projection);
        return Commit(state, command, context, aggregate, CommandDisposition.Applied, code);
    }

    private static CommandReduction Commit(
        CanonicalCompanionState state,
        CompanionCommand command,
        AuthenticatedCommandContext context,
        object aggregate,
        CommandDisposition disposition,
        string code)
    {
        var global = state.GlobalRevision.Next();
        var receipt = new RecentCommandReceipt(
            command.CommandId,
            context.DeviceId,
            command.Aggregate,
            command.RequestedRevision,
            command.RequestedRevision,
            disposition,
            command.ExpiresUtc);
        var receipts = state.RecentCommands
            .Where(item => item.ExpiresUtc > context.ReceivedUtc)
            .Append(receipt)
            .TakeLast(ProtocolBounds.MaxRecentCommands)
            .ToArray();

        CanonicalCompanionState next;
        CanonicalUpdate update;
        switch (aggregate)
        {
            case DeviceModeAggregate modes:
                next = state.With(global, deviceModes: modes, recentCommands: receipts);
                update = new DeviceModeCanonicalUpdate(
                    state.AuthorityEpoch,
                    global,
                    command.CommandId,
                    context.ReceivedUtc,
                    modes);
                break;
            case WorkspaceAggregate workspace:
                next = state.With(global, workspace: workspace, recentCommands: receipts);
                update = new WorkspaceCanonicalUpdate(
                    state.AuthorityEpoch,
                    global,
                    command.CommandId,
                    context.ReceivedUtc,
                    workspace);
                break;
            case MarkAggregate marks:
                next = state.With(global, marks: marks, recentCommands: receipts);
                update = new MarksCanonicalUpdate(
                    state.AuthorityEpoch,
                    global,
                    command.CommandId,
                    context.ReceivedUtc,
                    marks);
                break;
            case CaptureIntentAggregate capture:
                next = state.With(global, captureIntent: capture, recentCommands: receipts);
                update = new CaptureCanonicalUpdate(
                    state.AuthorityEpoch,
                    global,
                    command.CommandId,
                    context.ReceivedUtc,
                    capture);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(aggregate));
        }

        return new CommandReduction(
            next,
            Acknowledge(next, command, disposition, code, command.RequestedRevision, canonicalState: null),
            update);
    }

    private static CommandReduction Reject(
        CanonicalCompanionState state,
        CompanionCommand command,
        CommandDisposition disposition,
        string code,
        bool includeCanonical = false) =>
        new(
            state,
            Acknowledge(
                state,
                command,
                disposition,
                code,
                state.Cursor(command.Aggregate).Revision,
                includeCanonical ? state : null),
            null);

    private static CommandAcknowledgement Acknowledge(
        CanonicalCompanionState state,
        CompanionCommand command,
        CommandDisposition disposition,
        string code,
        AggregateRevision appliedRevision,
        CanonicalCompanionState? canonicalState) =>
        new(
            command.CommandId,
            command.Aggregate,
            command.RequestedRevision,
            appliedRevision,
            disposition is CommandDisposition.Applied or CommandDisposition.Duplicate or
                CommandDisposition.PendingDesktopApproval
                ? command.CommandId
                : state.Cursor(command.Aggregate).LastChangeId,
            state.GlobalRevision,
            state.AuthorityEpoch,
            disposition,
            code,
            canonicalState);

    private static (CanonicalCompanionState State, DeviceModeCanonicalUpdate Update) CommitMaintenance(
        CanonicalCompanionState state,
        DeviceModeAggregate aggregate,
        CommandId change,
        DateTimeOffset now)
    {
        var global = state.GlobalRevision.Next();
        var next = state.With(global, deviceModes: aggregate);
        return (next, new DeviceModeCanonicalUpdate(state.AuthorityEpoch, global, change, now, aggregate));
    }

    private static CommandId SyntheticId(
        CanonicalCompanionState state,
        CanonicalAggregateKind aggregate,
        DateTimeOffset now,
        string reason)
    {
        var material = Encoding.UTF8.GetBytes(
            $"{state.AuthorityEpoch.Value:D}|{state.GlobalRevision.Value}|{aggregate}|{now:O}|{reason}");
        var hash = SHA256.HashData(material);
        return new CommandId(new Guid(hash.AsSpan(0, 16)));
    }
}
