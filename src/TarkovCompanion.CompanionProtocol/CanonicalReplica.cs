using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol;

public enum ReplicaDisposition
{
    /// <summary>The delivery advanced the stream and, where it carried state, the cache.</summary>
    Applied = 1,

    /// <summary>The delivery advanced the stream; a snapshot already contained its state.</summary>
    AlreadyReflected,

    /// <summary>The sequence was already applied; nothing changed.</summary>
    Duplicate,

    /// <summary>A gap or disagreement was found; the client sends a reconnect request.</summary>
    ResyncRequired,

    /// <summary>A delta arrived while a resynchronization is outstanding and was not applied.</summary>
    Discarded,
}

public sealed record ReplicaObservation(CanonicalReplica Replica, ReplicaDisposition Disposition, string Code);

/// <summary>
/// The executable tablet-side reading of the delivery stream. It is the reference that a browser
/// implementation mirrors: it never resolves conflicts or computes canonical state itself, it only
/// decides whether a desktop delivery can be applied in order or requires an authoritative snapshot.
/// </summary>
/// <remarks>
/// A delivery after the next expected device sequence is never applied as a delta. Neither is an
/// update from another authority epoch, or one whose global or aggregate revision is not exactly
/// the next one. Each of those makes the replica await resynchronization, during which deltas are
/// discarded; a snapshot delivery or reconnect plan ends it.
/// </remarks>
public sealed record CanonicalReplica
{
    public CanonicalReplica(CanonicalCompanionState? state, DeliverySequence lastDeliverySequence, bool awaitingResync)
    {
        State = state;
        LastDeliverySequence = lastDeliverySequence;
        AwaitingResync = awaitingResync || state is null;
    }

    public CanonicalCompanionState? State { get; }

    public DeliverySequence LastDeliverySequence { get; }

    public bool AwaitingResync { get; }

    public static CanonicalReplica Empty { get; } = new(null, new DeliverySequence(0), awaitingResync: true);

    public ReplicaObservation Observe(ServerEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Observe(envelope.DeliverySequence, envelope.Message);
    }

    /// <summary>
    /// Applies a reconnect plan only where it continues this replica's position. Plans carry no
    /// request correlation, so a plan whose resume position is behind the replica is a late answer
    /// to an earlier request and is discarded; a replay that starts after the next expected sequence
    /// cannot close the gap and requires another resynchronization.
    /// </summary>
    public ReplicaObservation ApplyReconnectPlan(ReconnectPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.ResumeAfterDeliverySequence.Value < LastDeliverySequence.Value)
        {
            return new(this, ReplicaDisposition.Discarded, "stale-reconnect-plan");
        }

        switch (plan.Disposition)
        {
            case ReconnectDisposition.FullSnapshot:
                return new(new CanonicalReplica(plan.Snapshot, plan.ResumeAfterDeliverySequence, false), ReplicaDisposition.Applied, "snapshot-applied");
            case ReconnectDisposition.UpToDate when State is not null && plan.ResumeAfterDeliverySequence == LastDeliverySequence:
                return new(new CanonicalReplica(State, LastDeliverySequence, false), ReplicaDisposition.Applied, "up-to-date");
            case ReconnectDisposition.Replay when State is not null &&
                                                  plan.Replay[0].DeliverySequence.Value <= LastDeliverySequence.Value + 1:
                var replica = new CanonicalReplica(State, LastDeliverySequence, false);
                foreach (var delivery in plan.Replay.Where(item => item.DeliverySequence.Value > LastDeliverySequence.Value))
                {
                    var observed = replica.Observe(delivery.DeliverySequence, delivery.Message);
                    if (observed.Disposition is ReplicaDisposition.ResyncRequired or ReplicaDisposition.Discarded or ReplicaDisposition.Duplicate)
                    {
                        return new(new CanonicalReplica(State, LastDeliverySequence, true), ReplicaDisposition.ResyncRequired, observed.Code);
                    }

                    replica = observed.Replica;
                }

                return new(replica, ReplicaDisposition.Applied, "replay-applied");
            default:
                return new(new CanonicalReplica(State, LastDeliverySequence, true), ReplicaDisposition.ResyncRequired, "reconnect-not-applicable");
        }
    }

    /// <summary>The reconnect request that describes exactly what this replica holds.</summary>
    public ReconnectRequest CreateReconnectRequest(
        CompanionProtocolVersion protocolVersion,
        DeviceSessionId sessionId,
        DateTimeOffset acknowledgedUtc) =>
        State is null
            ? new ReconnectRequest(protocolVersion, sessionId, null, new GlobalRevision(0), LastDeliverySequence, [])
            : new ReconnectRequest(
                protocolVersion,
                sessionId,
                State.AuthorityEpoch,
                State.GlobalRevision,
                LastDeliverySequence,
                AggregateAcknowledgements(acknowledgedUtc));

    /// <summary>The live acknowledgement of everything applied so far, or null before any state is held.</summary>
    public ClientDeliveryAcknowledgement? CreateDeliveryAcknowledgement(
        CompanionProtocolVersion protocolVersion,
        DeviceSessionId sessionId,
        DateTimeOffset acknowledgedUtc) =>
        State is null || AwaitingResync || LastDeliverySequence.Value == 0
            ? null
            : new ClientDeliveryAcknowledgement(
                protocolVersion,
                sessionId,
                State.AuthorityEpoch,
                LastDeliverySequence,
                State.GlobalRevision,
                AggregateAcknowledgements(acknowledgedUtc));

    private ReplicaObservation Observe(DeliverySequence sequence, ServerMessage message)
    {
        if (sequence.Value <= LastDeliverySequence.Value)
        {
            return new(this, ReplicaDisposition.Duplicate, "delivery-already-applied");
        }

        if (message is CanonicalSnapshotMessage snapshot)
        {
            return new(new CanonicalReplica(snapshot.State, sequence, false), ReplicaDisposition.Applied, "snapshot-applied");
        }

        if (AwaitingResync)
        {
            return new(this, ReplicaDisposition.Discarded, "awaiting-resync");
        }

        if (sequence.Value != LastDeliverySequence.Value + 1)
        {
            return RequireResync("delivery-sequence-gap");
        }

        switch (message)
        {
            case CanonicalUpdateMessage { Update: var update }:
                return ObserveUpdate(sequence, update);
            case CommandAcknowledgementMessage { Acknowledgement.CanonicalState: { } included }
                when included.AuthorityEpoch != State!.AuthorityEpoch ||
                     included.GlobalRevision.Value > State!.GlobalRevision.Value:
                return new(new CanonicalReplica(included, sequence, false), ReplicaDisposition.Applied, "acknowledgement-state-applied");
            default:
                return new(new CanonicalReplica(State, sequence, false), ReplicaDisposition.Applied, "control-message-received");
        }
    }

    private ReplicaObservation ObserveUpdate(DeliverySequence sequence, CanonicalUpdate update)
    {
        var state = State!;
        if (update.AuthorityEpoch != state.AuthorityEpoch)
        {
            return RequireResync("authority-epoch-changed");
        }

        var local = state.Cursor(update.Aggregate);
        var incoming = CursorOf(update);
        if (update.GlobalRevision.Value <= state.GlobalRevision.Value)
        {
            return incoming.Revision.Value <= local.Revision.Value
                ? new(new CanonicalReplica(state, sequence, false), ReplicaDisposition.AlreadyReflected, "update-already-reflected")
                : RequireResync("revision-disagreement");
        }

        if (update.GlobalRevision.Value != state.GlobalRevision.Value + 1)
        {
            return RequireResync("global-revision-gap");
        }

        if (incoming.Revision.Value != local.Revision.Value + 1)
        {
            return RequireResync("aggregate-revision-gap");
        }

        var next = update switch
        {
            DeviceModeCanonicalUpdate modes => state.With(update.GlobalRevision, deviceModes: modes.State),
            WorkspaceCanonicalUpdate workspace => state.With(update.GlobalRevision, workspace: workspace.State),
            MarksCanonicalUpdate marks => state.With(update.GlobalRevision, marks: marks.State),
            CaptureCanonicalUpdate capture => state.With(update.GlobalRevision, captureIntent: capture.State),
            _ => throw new ArgumentOutOfRangeException(nameof(update)),
        };
        return new(new CanonicalReplica(next, sequence, false), ReplicaDisposition.Applied, "update-applied");
    }

    private ReplicaObservation RequireResync(string code) =>
        new(new CanonicalReplica(State, LastDeliverySequence, true), ReplicaDisposition.ResyncRequired, code);

    private IReadOnlyList<AggregateAcknowledgement> AggregateAcknowledgements(DateTimeOffset acknowledgedUtc) =>
        Enum.GetValues<CanonicalAggregateKind>()
            .Select(aggregate =>
            {
                var cursor = State!.Cursor(aggregate);
                return new AggregateAcknowledgement(aggregate, cursor.Revision, cursor.LastChangeId, acknowledgedUtc);
            })
            .ToArray();

    private static AggregateCursor CursorOf(CanonicalUpdate update) => update switch
    {
        DeviceModeCanonicalUpdate modes => modes.State.Cursor,
        WorkspaceCanonicalUpdate workspace => workspace.State.Cursor,
        MarksCanonicalUpdate marks => marks.State.Cursor,
        CaptureCanonicalUpdate capture => capture.State.Cursor,
        _ => throw new ArgumentOutOfRangeException(nameof(update)),
    };
}
