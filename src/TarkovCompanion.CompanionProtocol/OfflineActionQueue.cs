using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>
/// One explicit action a disconnected tablet may retain for later desktop submission. It is a
/// draft rather than a command: the revision it targets and the preview that authorizes it are
/// unknown until the tablet has current desktop state again. Independent navigation, search,
/// filter, selection, control, and capture progress have no draft type and cannot be queued.
/// </summary>
public abstract record OfflineAction
{
    private protected OfflineAction(CommandId actionId, DateTimeOffset queuedUtc, DateTimeOffset expiresUtc)
    {
        ActionId = actionId.Value == Guid.Empty
            ? throw new ArgumentException("An offline action id is required.", nameof(actionId))
            : actionId;
        QueuedUtc = ProtocolGuard.Utc(queuedUtc, nameof(queuedUtc));
        ExpiresUtc = ProtocolGuard.Utc(expiresUtc, nameof(expiresUtc));
        if (ExpiresUtc <= QueuedUtc || ExpiresUtc - QueuedUtc > ProtocolBounds.OfflineQueueLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "An offline action expires within fifteen minutes of queueing.");
        }
    }

    /// <summary>Becomes the submitted command id, so a retried submission is an idempotent duplicate.</summary>
    public CommandId ActionId { get; }

    public DateTimeOffset QueuedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public abstract CanonicalAggregateKind Aggregate { get; }

    internal abstract CompanionCommand ToCommand(AggregateRevision requestedRevision, OfflineQueuePreview preview);
}

public sealed record ShowOnDesktopOfflineAction : OfflineAction
{
    public ShowOnDesktopOfflineAction(
        CommandId actionId,
        DateTimeOffset queuedUtc,
        DateTimeOffset expiresUtc,
        WorkspaceProjection projection)
        : base(actionId, queuedUtc, expiresUtc) =>
        Projection = ProtocolGuard.NotNull(projection, nameof(projection));

    public WorkspaceProjection Projection { get; }

    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.Workspace;

    internal override CompanionCommand ToCommand(AggregateRevision requestedRevision, OfflineQueuePreview preview) =>
        new ShowOnDesktopCommand(ActionId, requestedRevision, QueuedUtc, ExpiresUtc, Projection, preview);
}

public sealed record UpsertMarkOfflineAction : OfflineAction
{
    public UpsertMarkOfflineAction(
        CommandId actionId,
        DateTimeOffset queuedUtc,
        DateTimeOffset expiresUtc,
        MarkId markId,
        long expectedMarkRevision,
        MapMarkDraft mark)
        : base(actionId, queuedUtc, expiresUtc)
    {
        MarkId = markId.Value == Guid.Empty ? throw new ArgumentException("A mark id is required.", nameof(markId)) : markId;
        ExpectedMarkRevision = ProtocolGuard.WireInteger(expectedMarkRevision, nameof(expectedMarkRevision));
        Mark = ProtocolGuard.NotNull(mark, nameof(mark));
    }

    public MarkId MarkId { get; }

    public long ExpectedMarkRevision { get; }

    public MapMarkDraft Mark { get; }

    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.Marks;

    internal override CompanionCommand ToCommand(AggregateRevision requestedRevision, OfflineQueuePreview preview) =>
        new UpsertMarkCommand(ActionId, requestedRevision, QueuedUtc, ExpiresUtc, MarkId, ExpectedMarkRevision, Mark, preview);
}

public sealed record DeleteMarkOfflineAction : OfflineAction
{
    public DeleteMarkOfflineAction(
        CommandId actionId,
        DateTimeOffset queuedUtc,
        DateTimeOffset expiresUtc,
        MarkId markId,
        long expectedMarkRevision)
        : base(actionId, queuedUtc, expiresUtc)
    {
        MarkId = markId.Value == Guid.Empty ? throw new ArgumentException("A mark id is required.", nameof(markId)) : markId;
        ExpectedMarkRevision = ProtocolGuard.Positive(ProtocolGuard.WireInteger(expectedMarkRevision, nameof(expectedMarkRevision)), nameof(expectedMarkRevision));
    }

    public MarkId MarkId { get; }

    public long ExpectedMarkRevision { get; }

    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.Marks;

    internal override CompanionCommand ToCommand(AggregateRevision requestedRevision, OfflineQueuePreview preview) =>
        new DeleteMarkCommand(ActionId, requestedRevision, QueuedUtc, ExpiresUtc, MarkId, ExpectedMarkRevision, preview);
}

public sealed record RequestCaptureIntentOfflineAction : OfflineAction
{
    public RequestCaptureIntentOfflineAction(
        CommandId actionId,
        DateTimeOffset queuedUtc,
        DateTimeOffset expiresUtc,
        CaptureIntentId intentId,
        string correlationId,
        CaptureSessionId captureSessionId,
        ScanIntent intent,
        CompanionCaptureContext context)
        : base(actionId, queuedUtc, expiresUtc)
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

    public ScanIntent Intent { get; }

    public CompanionCaptureContext Context { get; }

    public override CanonicalAggregateKind Aggregate => CanonicalAggregateKind.CaptureIntent;

    internal override CompanionCommand ToCommand(AggregateRevision requestedRevision, OfflineQueuePreview preview) =>
        new RequestCaptureIntentCommand(
            ActionId,
            requestedRevision,
            QueuedUtc,
            ExpiresUtc,
            IntentId,
            CorrelationId,
            CaptureSessionId,
            Intent,
            Context,
            preview);
}

/// <summary>
/// The tablet's bounded offline queue: at most 64 unique, unexpired explicit actions. Each action
/// is submitted only after the user previews it against the desktop state the tablet just
/// received; the resulting command binds that preview to the authority epoch and aggregate
/// revision, so an intervening change produces <see cref="CommandDisposition.RequiresPreview"/>.
/// </summary>
public sealed record OfflineActionQueue
{
    public OfflineActionQueue(IReadOnlyList<OfflineAction> actions)
    {
        Actions = ProtocolGuard.List(actions, nameof(actions), ProtocolBounds.MaxOfflineQueueItems);
        if (Actions.Select(action => action.ActionId).Distinct().Count() != Actions.Count)
        {
            throw new ArgumentException("An offline action id occurs at most once.", nameof(actions));
        }
    }

    public IReadOnlyList<OfflineAction> Actions { get; }

    public static OfflineActionQueue Empty { get; } = new([]);

    public bool IsFull => Actions.Count >= ProtocolBounds.MaxOfflineQueueItems;

    public OfflineActionQueue Enqueue(OfflineAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsFull)
        {
            throw new InvalidOperationException(
                $"The offline queue already holds its maximum {ProtocolBounds.MaxOfflineQueueItems} actions.");
        }

        if (Actions.Any(existing => existing.ActionId == action.ActionId))
        {
            throw new ArgumentException("An offline action id occurs at most once.", nameof(action));
        }

        return new OfflineActionQueue([.. Actions, action]);
    }

    public OfflineActionQueue Remove(CommandId actionId) =>
        new(Actions.Where(action => action.ActionId != actionId).ToArray());

    /// <summary>
    /// Discards every action whose fifteen-minute lifetime has ended. The lifetime bounds staleness but
    /// knows nothing of raids; the submission preview is what keeps a draft off changed state.
    /// </summary>
    public OfflineActionQueue PruneExpired(DateTimeOffset nowUtc)
    {
        var now = ProtocolGuard.Utc(nowUtc, nameof(nowUtc));
        return new OfflineActionQueue(Actions.Where(action => action.ExpiresUtc > now).ToArray());
    }

    /// <summary>
    /// Builds the one command the user approved after seeing <paramref name="previewedState"/>.
    /// The command requests the next revision of that state and carries the preview binding.
    /// </summary>
    public CompanionCommand PrepareSubmission(
        CommandId actionId,
        CanonicalCompanionState previewedState,
        DateTimeOffset previewedUtc)
    {
        ArgumentNullException.ThrowIfNull(previewedState);
        var previewed = ProtocolGuard.Utc(previewedUtc, nameof(previewedUtc));
        var action = Actions.FirstOrDefault(item => item.ActionId == actionId)
            ?? throw new ArgumentException("The offline queue does not contain that action.", nameof(actionId));
        if (previewed < action.QueuedUtc || previewed >= action.ExpiresUtc)
        {
            throw new InvalidOperationException("An offline action is previewed after queueing and before it expires.");
        }

        var cursor = previewedState.Cursor(action.Aggregate);
        return action.ToCommand(
            cursor.Revision.Next(),
            new OfflineQueuePreview(action.QueuedUtc, previewed, previewedState.AuthorityEpoch, cursor.Revision));
    }
}
