using System.Text.Json;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>
/// A client's statement that it holds one aggregate revision and the change occupying it. In v2
/// contract terms it is always an applied acknowledgement of a desktop change: a tablet never
/// rejects canonical state, and one that cannot read an update reconnects instead of acknowledging it.
/// </summary>
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

    /// <summary>Diagnostic only; desktop ordering never uses a client clock.</summary>
    public DateTimeOffset AcknowledgedUtc { get; } = ProtocolGuard.Utc(AcknowledgedUtc, nameof(AcknowledgedUtc));

    /// <summary>
    /// True when no acknowledgement claims a revision the desktop does not hold, or a different
    /// change at a revision it does hold. A divergent change at an equal revision is never an
    /// idempotent copy.
    /// </summary>
    public static bool AgreeWith(CanonicalCompanionState canonical, IReadOnlyList<AggregateAcknowledgement> acknowledgements)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(acknowledgements);
        return acknowledgements.All(acknowledgement =>
        {
            var cursor = canonical.Cursor(acknowledgement.Aggregate);
            return acknowledgement.Revision.Value < cursor.Revision.Value ||
                   (acknowledgement.Revision == cursor.Revision && acknowledgement.AppliedChangeId == cursor.LastChangeId);
        });
    }

    internal static IReadOnlyList<AggregateAcknowledgement> RequireDistinct(
        IReadOnlyList<AggregateAcknowledgement> acknowledgements,
        string parameterName)
    {
        var list = ProtocolGuard.List(acknowledgements, parameterName, Enum.GetValues<CanonicalAggregateKind>().Length);
        if (list.Select(item => item.Aggregate).Distinct().Count() != list.Count)
        {
            throw new ArgumentException("Each aggregate is acknowledged at most once.", parameterName);
        }

        return list;
    }
}

public sealed record ReconnectRequest
{
    public ReconnectRequest(
        CompanionProtocolVersion protocolVersion,
        DeviceSessionId sessionId,
        AuthorityEpoch? authorityEpoch,
        GlobalRevision lastGlobalRevision,
        DeliverySequence lastDeliverySequence,
        IReadOnlyList<AggregateAcknowledgement> aggregateAcknowledgements)
    {
        ProtocolVersion = ProtocolGuard.Version(protocolVersion, nameof(protocolVersion));
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("A device session id is required.", nameof(sessionId))
            : sessionId;
        AuthorityEpoch = authorityEpoch is { } epoch && epoch.Value == Guid.Empty
            ? throw new ArgumentException("An authority epoch is required when present.", nameof(authorityEpoch))
            : authorityEpoch;
        LastGlobalRevision = lastGlobalRevision;
        LastDeliverySequence = lastDeliverySequence;
        AggregateAcknowledgements = AggregateAcknowledgement.RequireDistinct(aggregateAcknowledgements, nameof(aggregateAcknowledgements));
        if (authorityEpoch is null && (lastGlobalRevision.Value != 0 || AggregateAcknowledgements.Count != 0))
        {
            throw new ArgumentException("A client without a cached authority epoch has no revisions to acknowledge.", nameof(authorityEpoch));
        }
    }

    public CompanionProtocolVersion ProtocolVersion { get; }

    public DeviceSessionId SessionId { get; }

    /// <summary>Null when the tablet holds no canonical cache, for example after a browser reload.</summary>
    public AuthorityEpoch? AuthorityEpoch { get; }

    public GlobalRevision LastGlobalRevision { get; }

    /// <summary>The last device delivery sequence the client applied contiguously.</summary>
    public DeliverySequence LastDeliverySequence { get; }

    public IReadOnlyList<AggregateAcknowledgement> AggregateAcknowledgements { get; }
}

/// <summary>One retained delivery replayed with its original device sequence.</summary>
public sealed record DeliveredServerMessage
{
    public DeliveredServerMessage(DeliverySequence deliverySequence, DateTimeOffset serverUtc, ServerMessage message)
    {
        DeliverySequence = deliverySequence.Value > 0
            ? deliverySequence
            : throw new ArgumentOutOfRangeException(nameof(deliverySequence));
        ServerUtc = ProtocolGuard.Utc(serverUtc, nameof(serverUtc));
        Message = ProtocolGuard.NotNull(message, nameof(message));
    }

    public DeliverySequence DeliverySequence { get; }

    public DateTimeOffset ServerUtc { get; }

    public ServerMessage Message { get; }
}

public enum ReconnectDisposition
{
    UpToDate = 1,
    Replay,
    FullSnapshot,
    UnsupportedVersion,
}

/// <summary>
/// The desktop's reconnect answer. <see cref="ResumeAfterDeliverySequence"/> is the device stream
/// position the client adopts after applying the plan; live deliveries continue from the next one.
/// </summary>
public sealed record ReconnectPlan
{
    public ReconnectPlan(
        CompanionProtocolVersion protocolVersion,
        ReconnectDisposition disposition,
        IReadOnlyList<DeliveredServerMessage> replay,
        CanonicalCompanionState? snapshot,
        DeliverySequence resumeAfterDeliverySequence,
        string reason)
    {
        ProtocolVersion = ProtocolGuard.Version(protocolVersion, nameof(protocolVersion));
        Disposition = ProtocolGuard.Defined(disposition, nameof(disposition));
        Replay = ProtocolGuard.List(replay, nameof(replay), ProtocolBounds.MaxReplayItems);
        Snapshot = snapshot;
        ResumeAfterDeliverySequence = resumeAfterDeliverySequence;
        Reason = ProtocolGuard.Required(reason, nameof(reason), ProtocolBounds.MaxShortStringBytes);

        var contiguousReplay = Replay.Count > 0 &&
                               Replay[^1].DeliverySequence == resumeAfterDeliverySequence &&
                               Replay.Zip(Replay.Skip(1), (left, right) => right.DeliverySequence.Value == left.DeliverySequence.Value + 1)
                                   .All(value => value);
        var valid = disposition switch
        {
            ReconnectDisposition.UpToDate or ReconnectDisposition.UnsupportedVersion => Replay.Count == 0 && snapshot is null,
            ReconnectDisposition.Replay => contiguousReplay && snapshot is null,
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

    public IReadOnlyList<DeliveredServerMessage> Replay { get; }

    public CanonicalCompanionState? Snapshot { get; }

    public DeliverySequence ResumeAfterDeliverySequence { get; }

    public string Reason { get; }
}

/// <summary>A reconnect plan and the delivery ledger after the planned deliveries are handed over.</summary>
public sealed record ReconnectPlanning(ReconnectPlan Plan, DeliveryLedger Ledger);

public static class ReconnectPlanner
{
    /// <summary>
    /// Replays only when the retained stream provably covers every device sequence and every global
    /// revision after the client's position, in order, without a coalesced marker, and within the
    /// replay and payload bounds. Anything else is an authoritative snapshot.
    /// </summary>
    public static ReconnectPlanning Plan(
        CanonicalCompanionState canonical,
        ReconnectRequest request,
        DeliveryLedger ledger,
        CompanionDeviceId deviceId)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ledger);
        var lastAssigned = ledger.For(deviceId)?.LastAssigned ?? new DeliverySequence(0);
        if (!CompanionProtocolVersion.Current.CanRead(request.ProtocolVersion))
        {
            return new ReconnectPlanning(
                new ReconnectPlan(
                    CompanionProtocolVersion.Current,
                    ReconnectDisposition.UnsupportedVersion,
                    [],
                    null,
                    request.LastDeliverySequence,
                    "unsupported-version"),
                ledger);
        }

        if (request.AuthorityEpoch != canonical.AuthorityEpoch ||
            request.LastGlobalRevision.Value > canonical.GlobalRevision.Value ||
            request.LastDeliverySequence.Value > lastAssigned.Value ||
            !AggregateAcknowledgement.AgreeWith(canonical, request.AggregateAcknowledgements))
        {
            return Snapshot(canonical, ledger, deviceId, lastAssigned, "authority-or-cursor-mismatch");
        }

        if (request.LastDeliverySequence == lastAssigned)
        {
            return request.LastGlobalRevision == canonical.GlobalRevision
                ? new ReconnectPlanning(
                    new ReconnectPlan(
                        CompanionProtocolVersion.Current,
                        ReconnectDisposition.UpToDate,
                        [],
                        null,
                        lastAssigned,
                        "already-current"),
                    ledger.Acknowledge(deviceId, lastAssigned))
                : Snapshot(canonical, ledger, deviceId, lastAssigned, "revision-not-in-delivery-stream");
        }

        var retained = ledger.PendingFor(deviceId)
            .Where(item => item.Sequence.Value > request.LastDeliverySequence.Value)
            .ToArray();
        var expectedCount = lastAssigned.Value - request.LastDeliverySequence.Value;
        if (expectedCount > ProtocolBounds.MaxReplayItems ||
            retained.LongLength != expectedCount ||
            retained.Any(item => item.SnapshotRequired))
        {
            return Snapshot(canonical, ledger, deviceId, lastAssigned, "delivery-history-unavailable");
        }

        var nextSequence = request.LastDeliverySequence.Value + 1;
        var nextGlobal = request.LastGlobalRevision.Value + 1;
        foreach (var item in retained)
        {
            if (item.Sequence.Value != nextSequence)
            {
                return Snapshot(canonical, ledger, deviceId, lastAssigned, "delivery-sequence-gap");
            }

            nextSequence++;
            if (item.Message is CanonicalUpdateMessage { Update: var update })
            {
                if (update.AuthorityEpoch != canonical.AuthorityEpoch || update.GlobalRevision.Value != nextGlobal)
                {
                    return Snapshot(canonical, ledger, deviceId, lastAssigned, "global-revision-gap");
                }

                nextGlobal++;
            }
        }

        if (nextGlobal - 1 != canonical.GlobalRevision.Value)
        {
            return Snapshot(canonical, ledger, deviceId, lastAssigned, "global-revision-gap");
        }

        var plan = new ReconnectPlan(
            CompanionProtocolVersion.Current,
            ReconnectDisposition.Replay,
            retained.Select(item => new DeliveredServerMessage(item.Sequence, item.EnqueuedUtc, item.Message!)).ToArray(),
            null,
            lastAssigned,
            "bounded-replay");
        try
        {
            _ = CompanionProtocolJson.Serialize(plan);
        }
        catch (JsonException)
        {
            return Snapshot(canonical, ledger, deviceId, lastAssigned, "replay-exceeds-payload-bound");
        }

        return new ReconnectPlanning(plan, ledger.Acknowledge(deviceId, lastAssigned));
    }

    private static ReconnectPlanning Snapshot(
        CanonicalCompanionState canonical,
        DeliveryLedger ledger,
        CompanionDeviceId deviceId,
        DeliverySequence lastAssigned,
        string reason) =>
        new(
            new ReconnectPlan(
                CompanionProtocolVersion.Current,
                ReconnectDisposition.FullSnapshot,
                [],
                canonical,
                lastAssigned,
                reason),
            ledger.Acknowledge(deviceId, lastAssigned));
}
