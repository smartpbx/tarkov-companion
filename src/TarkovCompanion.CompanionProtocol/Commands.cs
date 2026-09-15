using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>
/// Metadata added only after the desktop has shown an offline action against current canonical
/// state. Independent browsing itself has no command type and therefore cannot be replayed.
/// </summary>
public sealed record OfflineQueuePreview
{
    public OfflineQueuePreview(
        DateTimeOffset queuedUtc,
        DateTimeOffset previewedUtc,
        AuthorityEpoch previewedAuthorityEpoch,
        AggregateRevision previewedAggregateRevision)
    {
        QueuedUtc = ProtocolGuard.Utc(queuedUtc, nameof(queuedUtc));
        PreviewedUtc = ProtocolGuard.Utc(previewedUtc, nameof(previewedUtc));
        PreviewedAuthorityEpoch = previewedAuthorityEpoch.Value == Guid.Empty
            ? throw new ArgumentException("A preview authority epoch is required.", nameof(previewedAuthorityEpoch))
            : previewedAuthorityEpoch;
        PreviewedAggregateRevision = previewedAggregateRevision;

        if (PreviewedUtc < QueuedUtc || PreviewedUtc - QueuedUtc > ProtocolBounds.OfflineQueueLifetime)
        {
            throw new ArgumentException("An offline action is previewed within its bounded queue lifetime.");
        }
    }

    public DateTimeOffset QueuedUtc { get; }

    public DateTimeOffset PreviewedUtc { get; }

    public AuthorityEpoch PreviewedAuthorityEpoch { get; }

    public AggregateRevision PreviewedAggregateRevision { get; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetInteractionModeCommand), "setInteractionMode")]
[JsonDerivedType(typeof(RequestControlCommand), "requestControl")]
[JsonDerivedType(typeof(ResolveControlCommand), "resolveControl")]
[JsonDerivedType(typeof(PreemptControlCommand), "preemptControl")]
[JsonDerivedType(typeof(UpdateDesktopWorkspaceCommand), "updateDesktopWorkspace")]
[JsonDerivedType(typeof(ControlWorkspaceCommand), "controlWorkspace")]
[JsonDerivedType(typeof(ShowOnDesktopCommand), "showOnDesktop")]
[JsonDerivedType(typeof(UpsertMarkCommand), "upsertMark")]
[JsonDerivedType(typeof(DeleteMarkCommand), "deleteMark")]
[JsonDerivedType(typeof(RequestCaptureIntentCommand), "requestCaptureIntent")]
[JsonDerivedType(typeof(ReportCaptureProgressCommand), "reportCaptureProgress")]
[JsonDerivedType(typeof(PublishCaptureResultCommand), "publishCaptureResult")]
[JsonDerivedType(typeof(ReviewCaptureResultCommand), "reviewCaptureResult")]
[JsonDerivedType(typeof(CorrectCaptureResultCommand), "correctCaptureResult")]
public abstract record CompanionCommand
{
    private protected CompanionCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        OfflineQueuePreview? offlineQueuePreview)
    {
        CommandId = commandId.Value == Guid.Empty
            ? throw new ArgumentException("A command id is required.", nameof(commandId))
            : commandId;
        RequestedRevision = requestedRevision.Value > 0
            ? requestedRevision
            : throw new ArgumentOutOfRangeException(nameof(requestedRevision), "A command requests a positive revision.");
        IssuedUtc = ProtocolGuard.Utc(issuedUtc, nameof(issuedUtc));
        ExpiresUtc = ProtocolGuard.Utc(expiresUtc, nameof(expiresUtc));
        OfflineQueuePreview = offlineQueuePreview;

        var maximumLifetime = offlineQueuePreview is null
            ? ProtocolBounds.CommandLifetime
            : ProtocolBounds.OfflineQueueLifetime;
        if (ExpiresUtc <= IssuedUtc || ExpiresUtc - IssuedUtc > maximumLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "A command must expire inside its bounded lifetime.");
        }

        if (offlineQueuePreview is not null &&
            (offlineQueuePreview.QueuedUtc < IssuedUtc || offlineQueuePreview.PreviewedUtc > ExpiresUtc))
        {
            throw new ArgumentException("Offline queue and preview times stay within the command lifetime.", nameof(offlineQueuePreview));
        }
    }

    public CommandId CommandId { get; }

    /// <summary>The exact next aggregate revision this command intends to create.</summary>
    public AggregateRevision RequestedRevision { get; }

    public DateTimeOffset IssuedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public OfflineQueuePreview? OfflineQueuePreview { get; }

    [JsonIgnore]
    public abstract CanonicalAggregateKind Aggregate { get; }
}

public sealed record SetInteractionModeCommand : CompanionCommand
{
    public SetInteractionModeCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        CompanionInteractionMode mode)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null)
    {
        Mode = ProtocolGuard.Defined(mode, nameof(mode));
        if (mode is not (CompanionInteractionMode.Follow or CompanionInteractionMode.Independent))
        {
            throw new ArgumentException("Control is entered only through the desktop-approved lease flow.", nameof(mode));
        }
    }

    public CompanionInteractionMode Mode { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.DeviceModes;
}

public sealed record RequestControlCommand : CompanionCommand
{
    public RequestControlCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        TimeSpan requestedLease)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null) =>
        RequestedLease = ProtocolGuard.LeaseDuration(requestedLease, nameof(requestedLease));

    public TimeSpan RequestedLease { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.DeviceModes;
}

public sealed record ResolveControlCommand : CompanionCommand
{
    public ResolveControlCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        CommandId requestCommandId,
        bool approved,
        ControlLeaseId? leaseId)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null)
    {
        RequestCommandId = requestCommandId.Value == Guid.Empty
            ? throw new ArgumentException("A pending request id is required.", nameof(requestCommandId))
            : requestCommandId;
        Approved = approved;
        if (approved != (leaseId is not null))
        {
            throw new ArgumentException("Approval creates a lease id; denial does not.", nameof(leaseId));
        }

        LeaseId = leaseId;
    }

    public CommandId RequestCommandId { get; }

    public bool Approved { get; }

    public ControlLeaseId? LeaseId { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.DeviceModes;
}

public sealed record PreemptControlCommand : CompanionCommand
{
    public PreemptControlCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        string reason)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null) =>
        Reason = ProtocolGuard.Required(reason, nameof(reason));

    public string Reason { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.DeviceModes;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NavigateWorkspaceAction), "navigate")]
[JsonDerivedType(typeof(SearchWorkspaceAction), "search")]
[JsonDerivedType(typeof(FilterWorkspaceAction), "filter")]
[JsonDerivedType(typeof(SelectWorkspaceAction), "select")]
[JsonDerivedType(typeof(OpenWorkspaceDialogAction), "openDialog")]
public abstract record WorkspaceAction
{
    private protected WorkspaceAction()
    {
    }
}

public sealed record NavigateWorkspaceAction(
    WorkspaceKind Workspace,
    string? MapId,
    string? FloorId,
    WorkspaceViewport? Viewport) : WorkspaceAction
{
    public WorkspaceKind Workspace { get; } = ProtocolGuard.Defined(Workspace, nameof(Workspace));

    public string? MapId { get; } = ProtocolGuard.Optional(MapId, nameof(MapId), ProtocolBounds.MaxShortStringBytes);

    public string? FloorId { get; } = ProtocolGuard.Optional(FloorId, nameof(FloorId), ProtocolBounds.MaxShortStringBytes);
}

public sealed record SearchWorkspaceAction(string? Query) : WorkspaceAction
{
    public string? Query { get; } = ProtocolGuard.Optional(Query, nameof(Query));
}

public sealed record FilterWorkspaceAction(
    IReadOnlyList<string> ActiveLayers,
    IReadOnlyList<string> ActiveFilters) : WorkspaceAction
{
    public IReadOnlyList<string> ActiveLayers { get; } = Strings(ActiveLayers, nameof(ActiveLayers));

    public IReadOnlyList<string> ActiveFilters { get; } = Strings(ActiveFilters, nameof(ActiveFilters));

    private static IReadOnlyList<string> Strings(IReadOnlyList<string> values, string parameterName) =>
        ProtocolGuard.List(
            ProtocolGuard.List(values, parameterName)
                .Select(value => ProtocolGuard.Required(value, parameterName, ProtocolBounds.MaxShortStringBytes)),
            parameterName);
}

public sealed record SelectWorkspaceAction(WorkspaceSelection? Selection) : WorkspaceAction;

public sealed record OpenWorkspaceDialogAction(WorkspaceDialogKind? Dialog) : WorkspaceAction
{
    public WorkspaceDialogKind? Dialog { get; } = Dialog is { } value
        ? ProtocolGuard.Defined(value, nameof(Dialog))
        : null;
}

public sealed record ControlWorkspaceCommand : CompanionCommand
{
    public ControlWorkspaceCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        WorkspaceAction action)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null) =>
        Action = ProtocolGuard.NotNull(action, nameof(action));

    public WorkspaceAction Action { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.Workspace;
}

/// <summary>
/// A local desktop navigation change. The reducer accepts this only from its authenticated
/// desktop context, but representing it as an ordinary revisioned command keeps tablets from
/// missing local UI changes or relying on a second state-update path.
/// </summary>
public sealed record UpdateDesktopWorkspaceCommand : CompanionCommand
{
    public UpdateDesktopWorkspaceCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        WorkspaceProjection projection)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null) =>
        Projection = ProtocolGuard.NotNull(projection, nameof(projection));

    public WorkspaceProjection Projection { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.Workspace;
}

public sealed record ShowOnDesktopCommand : CompanionCommand
{
    public ShowOnDesktopCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        WorkspaceProjection projection,
        OfflineQueuePreview? offlineQueuePreview = null)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, offlineQueuePreview) =>
        Projection = ProtocolGuard.NotNull(projection, nameof(projection));

    public WorkspaceProjection Projection { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.Workspace;
}

public sealed record UpsertMarkCommand : CompanionCommand
{
    public UpsertMarkCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        MarkId markId,
        long expectedMarkRevision,
        MapMarkDraft mark,
        OfflineQueuePreview? offlineQueuePreview = null)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, offlineQueuePreview)
    {
        MarkId = markId.Value == Guid.Empty ? throw new ArgumentException("A mark id is required.", nameof(markId)) : markId;
        ExpectedMarkRevision = ProtocolGuard.NonNegative(expectedMarkRevision, nameof(expectedMarkRevision));
        Mark = ProtocolGuard.NotNull(mark, nameof(mark));
    }

    public MarkId MarkId { get; }

    /// <summary>Zero creates; a positive value must equal the mark revision being edited.</summary>
    public long ExpectedMarkRevision { get; }

    public MapMarkDraft Mark { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.Marks;
}

public sealed record DeleteMarkCommand : CompanionCommand
{
    public DeleteMarkCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        MarkId markId,
        long expectedMarkRevision,
        OfflineQueuePreview? offlineQueuePreview = null)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, offlineQueuePreview)
    {
        MarkId = markId.Value == Guid.Empty ? throw new ArgumentException("A mark id is required.", nameof(markId)) : markId;
        ExpectedMarkRevision = ProtocolGuard.Positive(expectedMarkRevision, nameof(expectedMarkRevision));
    }

    public MarkId MarkId { get; }

    public long ExpectedMarkRevision { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.Marks;
}

public sealed record RequestCaptureIntentCommand : CompanionCommand
{
    public RequestCaptureIntentCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        CaptureIntentId intentId,
        string correlationId,
        CaptureSessionId captureSessionId,
        ScanIntent intent,
        CompanionCaptureContext context,
        OfflineQueuePreview? offlineQueuePreview = null)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, offlineQueuePreview)
    {
        IntentId = intentId.Value == Guid.Empty ? throw new ArgumentException("An intent id is required.", nameof(intentId)) : intentId;
        CorrelationId = ProtocolGuard.Required(correlationId, nameof(correlationId), ProtocolBounds.MaxShortStringBytes);
        CaptureSessionId = captureSessionId.Value == Guid.Empty
            ? throw new ArgumentException("A capture session id is required.", nameof(captureSessionId))
            : captureSessionId;
        Intent = PairedScanIntents.Require(intent, nameof(intent));
        Context = ProtocolGuard.NotNull(context, nameof(context));
    }

    public CaptureIntentId IntentId { get; }

    public string CorrelationId { get; }

    public CaptureSessionId CaptureSessionId { get; }

    /// <summary>The frozen #264 scan intent to arm; flea recognition is not a paired capture intent.</summary>
    public ScanIntent Intent { get; }

    public CompanionCaptureContext Context { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.CaptureIntent;
}

public sealed record ReportCaptureProgressCommand : CompanionCommand
{
    public ReportCaptureProgressCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        CaptureIntentId intentId,
        ContextualCaptureProgressPhase phase,
        int? percent,
        string? artifactId,
        int? captureOrdinal,
        string? detail)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null)
    {
        IntentId = intentId.Value == Guid.Empty ? throw new ArgumentException("An intent id is required.", nameof(intentId)) : intentId;
        Phase = ProtocolGuard.Defined(phase, nameof(phase));
        Percent = percent is null or (>= 0 and <= 100) ? percent : throw new ArgumentOutOfRangeException(nameof(percent));
        ArtifactId = ProtocolGuard.Optional(artifactId, nameof(artifactId), ProtocolBounds.MaxShortStringBytes);
        CaptureOrdinal = captureOrdinal is null or >= 0 ? captureOrdinal : throw new ArgumentOutOfRangeException(nameof(captureOrdinal));
        Detail = ProtocolGuard.Optional(detail, nameof(detail));
    }

    public CaptureIntentId IntentId { get; }

    public ContextualCaptureProgressPhase Phase { get; }

    public int? Percent { get; }

    public string? ArtifactId { get; }

    public int? CaptureOrdinal { get; }

    public string? Detail { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.CaptureIntent;
}

public sealed record PublishCaptureResultCommand : CompanionCommand
{
    public PublishCaptureResultCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        CaptureIntentId intentId,
        ContextualCaptureResult result,
        IReadOnlyList<ContextualCaptureGuidance> guidance)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null)
    {
        IntentId = intentId.Value == Guid.Empty ? throw new ArgumentException("An intent id is required.", nameof(intentId)) : intentId;
        Result = ProtocolGuard.NotNull(result, nameof(result));
        Guidance = ProtocolGuard.List(guidance, nameof(guidance));
    }

    public CaptureIntentId IntentId { get; }

    public ContextualCaptureResult Result { get; }

    public IReadOnlyList<ContextualCaptureGuidance> Guidance { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.CaptureIntent;
}

public sealed record ReviewCaptureResultCommand : CompanionCommand
{
    public ReviewCaptureResultCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        CaptureIntentId intentId,
        CaptureReviewDisposition disposition,
        string? note)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null)
    {
        IntentId = intentId.Value == Guid.Empty ? throw new ArgumentException("An intent id is required.", nameof(intentId)) : intentId;
        Disposition = ProtocolGuard.Defined(disposition, nameof(disposition));
        Note = ProtocolGuard.Optional(note, nameof(note));
    }

    public CaptureIntentId IntentId { get; }

    public CaptureReviewDisposition Disposition { get; }

    public string? Note { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.CaptureIntent;
}

public sealed record CorrectCaptureResultCommand : CompanionCommand
{
    public CorrectCaptureResultCommand(
        CommandId commandId,
        AggregateRevision requestedRevision,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        CaptureIntentId intentId,
        CaptureCorrectionKind kind,
        string fieldId,
        string correctedValue,
        string? reason)
        : base(commandId, requestedRevision, issuedUtc, expiresUtc, null)
    {
        IntentId = intentId.Value == Guid.Empty ? throw new ArgumentException("An intent id is required.", nameof(intentId)) : intentId;
        Kind = ProtocolGuard.Defined(kind, nameof(kind));
        FieldId = ProtocolGuard.Required(fieldId, nameof(fieldId), ProtocolBounds.MaxShortStringBytes);
        CorrectedValue = ProtocolGuard.Required(correctedValue, nameof(correctedValue));
        Reason = ProtocolGuard.Optional(reason, nameof(reason));
    }

    public CaptureIntentId IntentId { get; }

    public CaptureCorrectionKind Kind { get; }

    public string FieldId { get; }

    public string CorrectedValue { get; }

    public string? Reason { get; }

    [JsonIgnore]
    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.CaptureIntent;
}
