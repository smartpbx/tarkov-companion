using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

public sealed record AggregateAcknowledgement(
    CanonicalAggregateKind Aggregate,
    AggregateRevision Revision,
    CommandId? AppliedChangeId,
    DateTimeOffset AcknowledgedUtc)
{
    public CanonicalAggregateKind Aggregate { get; } = ProtocolGuard.Defined(Aggregate, nameof(Aggregate));

    public CommandId? AppliedChangeId { get; } =
        (Revision.Value == 0) == (AppliedChangeId is null)
            ? AppliedChangeId
            : throw new ArgumentException("Revision zero has no change; a positive acknowledgement names it.", nameof(AppliedChangeId));

    public DateTimeOffset AcknowledgedUtc { get; } = ProtocolGuard.Utc(AcknowledgedUtc, nameof(AcknowledgedUtc));
}

public sealed record ReconnectRequest
{
    public ReconnectRequest(
        CompanionProtocolVersion protocolVersion,
        DeviceSessionId sessionId,
        AuthorityEpoch authorityEpoch,
        GlobalRevision lastGlobalRevision,
        DeliverySequence lastDeliverySequence,
        IReadOnlyList<AggregateAcknowledgement> aggregateAcknowledgements)
    {
        ProtocolVersion = protocolVersion.IsDefined
            ? protocolVersion
            : throw new ArgumentException("A protocol version is required.", nameof(protocolVersion));
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("A device session id is required.", nameof(sessionId))
            : sessionId;
        AuthorityEpoch = authorityEpoch.Value == Guid.Empty
            ? throw new ArgumentException("An authority epoch is required.", nameof(authorityEpoch))
            : authorityEpoch;
        LastGlobalRevision = lastGlobalRevision;
        LastDeliverySequence = lastDeliverySequence;
        AggregateAcknowledgements = ProtocolGuard.List(aggregateAcknowledgements, nameof(aggregateAcknowledgements), 4);
        if (AggregateAcknowledgements.Select(item => item.Aggregate).Distinct().Count() != AggregateAcknowledgements.Count)
        {
            throw new ArgumentException("Each aggregate is acknowledged at most once.", nameof(aggregateAcknowledgements));
        }
    }

    public CompanionProtocolVersion ProtocolVersion { get; }

    public DeviceSessionId SessionId { get; }

    public AuthorityEpoch AuthorityEpoch { get; }

    public GlobalRevision LastGlobalRevision { get; }

    public DeliverySequence LastDeliverySequence { get; }

    public IReadOnlyList<AggregateAcknowledgement> AggregateAcknowledgements { get; }
}

public sealed record DeliveredCanonicalUpdate(DeliverySequence DeliverySequence, CanonicalUpdate Update)
{
    public DeliverySequence DeliverySequence { get; } = DeliverySequence.Value > 0
        ? DeliverySequence
        : throw new ArgumentOutOfRangeException(nameof(DeliverySequence));

    public CanonicalUpdate Update { get; } = ProtocolGuard.NotNull(Update, nameof(Update));
}

public enum ReconnectDisposition
{
    UpToDate = 1,
    Replay,
    FullSnapshot,
    UnsupportedVersion,
}

public sealed record ReconnectPlan
{
    public ReconnectPlan(
        CompanionProtocolVersion protocolVersion,
        ReconnectDisposition disposition,
        IReadOnlyList<DeliveredCanonicalUpdate> replay,
        CanonicalCompanionState? snapshot,
        string reason)
    {
        ProtocolVersion = protocolVersion;
        Disposition = ProtocolGuard.Defined(disposition, nameof(disposition));
        Replay = ProtocolGuard.List(replay, nameof(replay));
        Snapshot = snapshot;
        Reason = ProtocolGuard.Required(reason, nameof(reason), ProtocolBounds.MaxShortStringBytes);

        var valid = disposition switch
        {
            ReconnectDisposition.UpToDate or ReconnectDisposition.UnsupportedVersion => Replay.Count == 0 && snapshot is null,
            ReconnectDisposition.Replay => Replay.Count > 0 && snapshot is null,
            ReconnectDisposition.FullSnapshot => Replay.Count == 0 && snapshot is not null,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("Reconnect payload is inconsistent with its disposition.");
        }
    }

    public CompanionProtocolVersion ProtocolVersion { get; }

    public ReconnectDisposition Disposition { get; }

    public IReadOnlyList<DeliveredCanonicalUpdate> Replay { get; }

    public CanonicalCompanionState? Snapshot { get; }

    public string Reason { get; }
}

public static class ReconnectPlanner
{
    public static ReconnectPlan Plan(
        CanonicalCompanionState canonical,
        ReconnectRequest request,
        IReadOnlyList<DeliveredCanonicalUpdate> retainedDeliveries)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(request);
        var deliveries = ProtocolGuard.List(retainedDeliveries, nameof(retainedDeliveries));

        if (!CompanionProtocolVersion.Current.CanRead(request.ProtocolVersion))
        {
            return new ReconnectPlan(
                CompanionProtocolVersion.Current,
                ReconnectDisposition.UnsupportedVersion,
                [],
                null,
                "unsupported-version");
        }

        if (request.AuthorityEpoch != canonical.AuthorityEpoch ||
            request.LastGlobalRevision.Value > canonical.GlobalRevision.Value ||
            !AcknowledgementsMatch(canonical, request.AggregateAcknowledgements))
        {
            return Snapshot(canonical, "authority-or-cursor-mismatch");
        }

        if (request.LastGlobalRevision == canonical.GlobalRevision)
        {
            return new ReconnectPlan(
                CompanionProtocolVersion.Current,
                ReconnectDisposition.UpToDate,
                [],
                null,
                "already-current");
        }

        var replay = deliveries
            .Where(item => item.Update.AuthorityEpoch == canonical.AuthorityEpoch &&
                           item.DeliverySequence.Value > request.LastDeliverySequence.Value &&
                           item.Update.GlobalRevision.Value > request.LastGlobalRevision.Value)
            .OrderBy(item => item.DeliverySequence.Value)
            .ToArray();
        var globalRevisions = replay.Select(item => item.Update.GlobalRevision.Value).ToArray();
        var expectedCount = canonical.GlobalRevision.Value - request.LastGlobalRevision.Value;
        var contiguous = expectedCount <= ProtocolBounds.MaxCollectionItems &&
                         globalRevisions.LongLength == expectedCount &&
                         globalRevisions.FirstOrDefault() == request.LastGlobalRevision.Value + 1 &&
                         globalRevisions.LastOrDefault() == canonical.GlobalRevision.Value &&
                         globalRevisions.Zip(globalRevisions.Skip(1), (left, right) => right == left + 1).All(value => value);
        if (!contiguous)
        {
            return Snapshot(canonical, "bounded-replay-not-provable");
        }

        return new ReconnectPlan(
            CompanionProtocolVersion.Current,
            ReconnectDisposition.Replay,
            replay,
            null,
            "bounded-replay");
    }

    private static bool AcknowledgementsMatch(
        CanonicalCompanionState state,
        IReadOnlyList<AggregateAcknowledgement> acknowledgements)
    {
        foreach (var acknowledgement in acknowledgements)
        {
            var cursor = state.Cursor(acknowledgement.Aggregate);
            if (acknowledgement.Revision.Value > cursor.Revision.Value ||
                (acknowledgement.Revision == cursor.Revision &&
                 acknowledgement.AppliedChangeId != cursor.LastChangeId))
            {
                return false;
            }
        }

        return true;
    }

    private static ReconnectPlan Snapshot(CanonicalCompanionState state, string reason) =>
        new(
            CompanionProtocolVersion.Current,
            ReconnectDisposition.FullSnapshot,
            [],
            state,
            reason);
}
