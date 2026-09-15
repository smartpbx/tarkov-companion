using System.Text.Json;
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
    public CanonicalReplica(
        CanonicalCompanionState? state,
        DeliverySequence lastDeliverySequence,
        bool awaitingResync,
        DateTimeOffset? lastServerUtc = null,
        CompanionDeviceId? lastAuthenticatedOriginDeviceId = null)
    {
        State = state;
        LastDeliverySequence = lastDeliverySequence;
        AwaitingResync = awaitingResync || state is null;
        LastServerUtc = ProtocolGuard.UtcOptional(lastServerUtc, nameof(lastServerUtc));
        LastAuthenticatedOriginDeviceId = lastAuthenticatedOriginDeviceId is { } origin && origin.Value == Guid.Empty
            ? throw new ArgumentException("An authenticated origin device is required when present.", nameof(lastAuthenticatedOriginDeviceId))
            : lastAuthenticatedOriginDeviceId;
        if ((LastServerUtc is null) != (LastAuthenticatedOriginDeviceId is null))
        {
            throw new ArgumentException("Accepted server time and authenticated origin are recorded together.");
        }
    }

    public CanonicalCompanionState? State { get; }

    public DeliverySequence LastDeliverySequence { get; }

    public bool AwaitingResync { get; }

    /// <summary>The trusted server time on the newest accepted delivery, used to reject temporal rollback.</summary>
    public DateTimeOffset? LastServerUtc { get; }

    public CompanionDeviceId? LastAuthenticatedOriginDeviceId { get; }

    public static CanonicalReplica Empty { get; } = new(null, new DeliverySequence(0), awaitingResync: true);

    public ReplicaObservation Observe(
        ServerEnvelope envelope,
        DeviceSessionId authenticatedSessionId,
        CompanionProtocolVersion negotiatedVersion)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var version = ProtocolGuard.Version(negotiatedVersion, nameof(negotiatedVersion));
        if (authenticatedSessionId.Value == Guid.Empty || envelope.SessionId != authenticatedSessionId)
        {
            return new(this, ReplicaDisposition.Discarded, "authenticated-session-mismatch");
        }

        if (!CompanionProtocolVersion.Current.CanRead(envelope.ProtocolVersion) || envelope.ProtocolVersion != version)
        {
            return new(this, ReplicaDisposition.Discarded, "protocol-version-mismatch");
        }

        if (envelope.DeliverySequence.Value > LastDeliverySequence.Value &&
            LastServerUtc is { } lastServerUtc && envelope.ServerUtc < lastServerUtc)
        {
            return RequireResync("server-time-regressed");
        }

        if (envelope.Message is CanonicalUpdateMessage update &&
            update.Update.Origin.DeviceId != envelope.AuthenticatedOriginDeviceId)
        {
            return RequireResync("authenticated-origin-mismatch");
        }

        return Observe(
            envelope.DeliverySequence,
            envelope.ServerUtc,
            envelope.AuthenticatedOriginDeviceId,
            envelope.Message);
    }

    /// <summary>
    /// Applies only the response to the named outstanding request on this authenticated session. A
    /// new authority lifetime is adopted only if the request still describes this replica, preventing
    /// a delayed response from rolling a newer cache back to an unrelated epoch. The caller supplies
    /// the desktop send time authenticated by the response frame so reconnect advances the same
    /// temporal rollback fence as live delivery.
    /// </summary>
    public ReplicaObservation ApplyReconnectPlan(
        ReconnectPlan plan,
        ReconnectRequest request,
        DeviceSessionId authenticatedSessionId,
        CompanionProtocolVersion negotiatedVersion,
        DateTimeOffset authenticatedServerUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);
        var version = ProtocolGuard.Version(negotiatedVersion, nameof(negotiatedVersion));
        var serverUtc = ProtocolGuard.Utc(authenticatedServerUtc, nameof(authenticatedServerUtc));
        if (authenticatedSessionId.Value == Guid.Empty ||
            request.SessionId != authenticatedSessionId ||
            plan.SessionId != authenticatedSessionId)
        {
            return new(this, ReplicaDisposition.Discarded, "authenticated-session-mismatch");
        }

        if (request.ProtocolVersion != version || plan.ProtocolVersion != version ||
            !CompanionProtocolVersion.Current.CanRead(plan.ProtocolVersion))
        {
            return new(this, ReplicaDisposition.Discarded, "protocol-version-mismatch");
        }

        if (plan.RequestId != request.RequestId)
        {
            return new(this, ReplicaDisposition.Discarded, "reconnect-request-mismatch");
        }

        // The reconnect response travels in an authenticated desktop-to-tablet frame rather than a
        // ServerEnvelope. Its trusted send time still advances the same rollback fence, and no
        // replayed delivery can claim to have been produced after the response that contains it.
        if ((LastServerUtc is { } lastServerUtc && serverUtc < lastServerUtc) ||
            plan.Replay.Any(delivery => delivery.ServerUtc > serverUtc))
        {
            return new(this, ReplicaDisposition.Discarded, "reconnect-server-time-invalid");
        }

        // A snapshot from another authority lifetime restarts the delivery stream at its position.
        if (plan.Snapshot is { } snapshot && (State is null || snapshot.AuthorityEpoch != State.AuthorityEpoch))
        {
            return RequestDescribesReplica(request)
                ? new(
                    new CanonicalReplica(snapshot, plan.ResumeAfterDeliverySequence, false, serverUtc, snapshot.DesktopDeviceId),
                    ReplicaDisposition.Applied,
                    "snapshot-applied")
                : new(this, ReplicaDisposition.Discarded, "stale-reconnect-plan");
        }

        // A snapshot marker may have been resolved after the reconnect response was planned. Its
        // newer sequence can therefore carry state newer than the delayed plan's same-epoch
        // snapshot. Never trade that state for an older or divergent aggregate vector merely
        // because the plan's resume position is still ahead.
        if (plan.Snapshot is { } sameEpochSnapshot && State is { } current &&
            !SnapshotDominates(sameEpochSnapshot, current))
        {
            return new(this, ReplicaDisposition.Discarded, "stale-reconnect-plan");
        }

        if (plan.ResumeAfterDeliverySequence.Value < LastDeliverySequence.Value)
        {
            return new(this, ReplicaDisposition.Discarded, "stale-reconnect-plan");
        }

        switch (plan.Disposition)
        {
            case ReconnectDisposition.FullSnapshot:
                return new(
                    new CanonicalReplica(
                        plan.Snapshot,
                        plan.ResumeAfterDeliverySequence,
                        false,
                        serverUtc,
                        plan.Snapshot!.DesktopDeviceId),
                    ReplicaDisposition.Applied,
                    "snapshot-applied");
            case ReconnectDisposition.UpToDate when State is not null && plan.ResumeAfterDeliverySequence == LastDeliverySequence:
                return new(
                    new CanonicalReplica(State, LastDeliverySequence, false, serverUtc, State.DesktopDeviceId),
                    ReplicaDisposition.Applied,
                    "up-to-date");
            case ReconnectDisposition.Replay when State is not null &&
                                                  plan.Replay[0].DeliverySequence.Value <= LastDeliverySequence.Value + 1:
                var replica = new CanonicalReplica(
                    State,
                    LastDeliverySequence,
                    false,
                    LastServerUtc,
                    LastAuthenticatedOriginDeviceId);
                foreach (var delivery in plan.Replay.Where(item => item.DeliverySequence.Value > LastDeliverySequence.Value))
                {
                    var observed = replica.Observe(
                        delivery.DeliverySequence,
                        delivery.ServerUtc,
                        delivery.AuthenticatedOriginDeviceId,
                        delivery.Message);
                    if (observed.Disposition is ReplicaDisposition.ResyncRequired or ReplicaDisposition.Discarded or ReplicaDisposition.Duplicate)
                    {
                        return new(
                            new CanonicalReplica(State, LastDeliverySequence, true, LastServerUtc, LastAuthenticatedOriginDeviceId),
                            ReplicaDisposition.ResyncRequired,
                            observed.Code);
                    }

                    replica = observed.Replica;
                }

                return new(
                    new CanonicalReplica(
                        replica.State,
                        replica.LastDeliverySequence,
                        false,
                        serverUtc,
                        replica.State!.DesktopDeviceId),
                    ReplicaDisposition.Applied,
                    "replay-applied");
            default:
                return new(
                    new CanonicalReplica(State, LastDeliverySequence, true, LastServerUtc, LastAuthenticatedOriginDeviceId),
                    ReplicaDisposition.ResyncRequired,
                    "reconnect-not-applicable");
        }
    }

    /// <summary>The reconnect request that describes exactly what this replica holds.</summary>
    public ReconnectRequest CreateReconnectRequest(
        CompanionProtocolVersion protocolVersion,
        DeviceSessionId sessionId,
        ReconnectRequestId requestId,
        DateTimeOffset acknowledgedUtc) =>
        State is null
            ? new ReconnectRequest(protocolVersion, sessionId, requestId, null, new GlobalRevision(0), LastDeliverySequence, [])
            : new ReconnectRequest(
                protocolVersion,
                sessionId,
                requestId,
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

    private ReplicaObservation Observe(
        DeliverySequence sequence,
        DateTimeOffset serverUtc,
        CompanionDeviceId authenticatedOriginDeviceId,
        ServerMessage message)
    {
        // An unsolicited epoch change has no ordering relationship to the cached stream. A
        // correlated reconnect response is the only safe way to adopt that authority lifetime.
        if (State is not null && EpochOf(message) is { } epoch && epoch != State.AuthorityEpoch)
        {
            return AwaitingResync
                ? new(this, ReplicaDisposition.Discarded, "awaiting-resync")
                : RequireResync("authority-epoch-changed");
        }

        if (sequence.Value <= LastDeliverySequence.Value)
        {
            return new(this, ReplicaDisposition.Duplicate, "delivery-already-applied");
        }

        if (LastServerUtc is { } previousServerUtc && serverUtc < previousServerUtc)
        {
            return RequireResync("server-time-regressed");
        }

        if (message is CanonicalUpdateMessage attributed &&
            (attributed.Update.Origin.DeviceId != authenticatedOriginDeviceId ||
             attributed.Update.ChangedUtc > serverUtc))
        {
            return RequireResync("authenticated-origin-or-time-mismatch");
        }

        if (message is CanonicalSnapshotMessage snapshot)
        {
            if (State is { } current && !SnapshotDominates(snapshot.State, current))
            {
                return RequireResync("snapshot-state-regressed");
            }

            return new(
                new CanonicalReplica(snapshot.State, sequence, false, serverUtc, authenticatedOriginDeviceId),
                ReplicaDisposition.Applied,
                "snapshot-applied");
        }

        if (AwaitingResync)
        {
            return new(this, ReplicaDisposition.Discarded, "awaiting-resync");
        }

        if (sequence.Value != LastDeliverySequence.Value + 1)
        {
            return RequireResync("delivery-sequence-gap");
        }

        if (message is CommandAcknowledgementMessage { Acknowledgement.CanonicalState: { } included })
        {
            if (SnapshotDominates(included, State!))
            {
                return included.GlobalRevision.Value > State!.GlobalRevision.Value
                    ? new(
                        new CanonicalReplica(included, sequence, false, serverUtc, authenticatedOriginDeviceId),
                        ReplicaDisposition.Applied,
                        "acknowledgement-state-applied")
                    : new(
                        new CanonicalReplica(State, sequence, false, serverUtc, authenticatedOriginDeviceId),
                        ReplicaDisposition.Applied,
                        "control-message-received");
            }

            if (!SnapshotDominates(State!, included))
            {
                return RequireResync("acknowledgement-state-diverges");
            }
        }

        if (message is CommandAcknowledgementMessage { Acknowledgement: var acknowledgement } &&
            !AcknowledgementIsReflected(acknowledgement, State!))
        {
            return RequireResync("acknowledgement-state-diverges");
        }

        switch (message)
        {
            case CanonicalUpdateMessage { Update: var update }:
                return ObserveUpdate(sequence, serverUtc, authenticatedOriginDeviceId, update);
            default:
                return new(
                    new CanonicalReplica(State, sequence, false, serverUtc, authenticatedOriginDeviceId),
                    ReplicaDisposition.Applied,
                    "control-message-received");
        }
    }

    private ReplicaObservation ObserveUpdate(
        DeliverySequence sequence,
        DateTimeOffset serverUtc,
        CompanionDeviceId authenticatedOriginDeviceId,
        CanonicalUpdate update)
    {
        var state = State!;
        if (update.AuthorityEpoch != state.AuthorityEpoch)
        {
            return RequireResync("authority-epoch-changed");
        }

        var expectedOriginKind = update.Origin.DeviceId == state.DesktopDeviceId
            ? WorkspaceOriginKind.DesktopApplication
            : WorkspaceOriginKind.PairedDevice;
        if (update.Origin.WorkspaceId != state.WorkspaceId || update.Origin.Kind != expectedOriginKind)
        {
            return RequireResync("update-attribution-mismatch");
        }

        var local = state.Cursor(update.Aggregate);
        var incoming = CursorOf(update);
        if (update.GlobalRevision.Value <= state.GlobalRevision.Value)
        {
            if (incoming.Revision.Value > local.Revision.Value ||
                (incoming.Revision == local.Revision &&
                 (incoming.LastChangeId != local.LastChangeId || !UpdateStateEqual(update, state))))
            {
                return RequireResync("revision-disagreement");
            }

            return new(
                new CanonicalReplica(state, sequence, false, serverUtc, authenticatedOriginDeviceId),
                ReplicaDisposition.AlreadyReflected,
                "update-already-reflected");
        }

        if (update.GlobalRevision.Value != state.GlobalRevision.Value + 1)
        {
            return RequireResync("global-revision-gap");
        }

        if (incoming.Revision.Value != local.Revision.Value + 1)
        {
            return RequireResync("aggregate-revision-gap");
        }

        CanonicalCompanionState next;
        try
        {
            next = update switch
            {
                DeviceModeCanonicalUpdate modes => state.With(update.GlobalRevision, deviceModes: modes.State),
                WorkspaceCanonicalUpdate workspace => state.With(update.GlobalRevision, workspace: workspace.State),
                MarksCanonicalUpdate marks => state.With(update.GlobalRevision, marks: marks.State),
                CaptureCanonicalUpdate capture => state.With(update.GlobalRevision, captureIntent: capture.State),
                ProfilePreferencesCanonicalUpdate preferences =>
                    state.With(update.GlobalRevision, profilePreferences: preferences.State),
                _ => throw new ArgumentOutOfRangeException(nameof(update)),
            };
        }
        catch (ArgumentException)
        {
            // Aggregate DTOs cannot see invariants owned by the containing canonical state, such as
            // the desktop never appearing in the paired mode table or one change ID occupying two
            // cursors. An authenticated but malformed peer delivery resynchronizes; it never crashes
            // the tablet's delivery loop.
            return RequireResync("invalid-canonical-update");
        }

        return new(
            new CanonicalReplica(next, sequence, false, serverUtc, authenticatedOriginDeviceId),
            ReplicaDisposition.Applied,
            "update-applied");
    }

    private static AuthorityEpoch? EpochOf(ServerMessage message) => message switch
    {
        CanonicalSnapshotMessage snapshot => snapshot.State.AuthorityEpoch,
        CanonicalUpdateMessage update => update.Update.AuthorityEpoch,
        CommandAcknowledgementMessage acknowledgement => acknowledgement.Acknowledgement.AuthorityEpoch,
        _ => null,
    };

    private ReplicaObservation RequireResync(string code) =>
        new(
            new CanonicalReplica(State, LastDeliverySequence, true, LastServerUtc, LastAuthenticatedOriginDeviceId),
            ReplicaDisposition.ResyncRequired,
            code);

    private bool RequestDescribesReplica(ReconnectRequest request)
    {
        if (request.LastDeliverySequence != LastDeliverySequence)
        {
            return false;
        }

        return State is null
            ? request.AuthorityEpoch is null && request.LastGlobalRevision.Value == 0 && request.AggregateAcknowledgements.Count == 0
            : request.AuthorityEpoch == State.AuthorityEpoch &&
              AggregateAcknowledgement.ExactlyMatches(State, request.LastGlobalRevision, request.AggregateAcknowledgements);
    }

    private static bool SnapshotDominates(CanonicalCompanionState candidate, CanonicalCompanionState current)
    {
        if (candidate.AuthorityEpoch != current.AuthorityEpoch ||
            candidate.WorkspaceId != current.WorkspaceId ||
            candidate.DesktopInstanceId != current.DesktopInstanceId ||
            candidate.DesktopDeviceId != current.DesktopDeviceId ||
            candidate.GlobalRevision.Value < current.GlobalRevision.Value)
        {
            return false;
        }

        return Enum.GetValues<CanonicalAggregateKind>().All(aggregate =>
        {
            var held = current.Cursor(aggregate);
            var incoming = candidate.Cursor(aggregate);
            return incoming.Revision.Value > held.Revision.Value ||
                   (incoming.Revision == held.Revision &&
                    incoming.LastChangeId == held.LastChangeId &&
                    AggregateStateEqual(candidate, current, aggregate));
        });
    }

    private static bool AggregateStateEqual(
        CanonicalCompanionState left,
        CanonicalCompanionState right,
        CanonicalAggregateKind aggregate) => aggregate switch
    {
        CanonicalAggregateKind.DeviceModes => JsonEqual(left.DeviceModes, right.DeviceModes),
        CanonicalAggregateKind.Workspace => JsonEqual(left.Workspace, right.Workspace),
        CanonicalAggregateKind.Marks => JsonEqual(left.Marks, right.Marks),
        CanonicalAggregateKind.CaptureIntent => JsonEqual(left.CaptureIntent, right.CaptureIntent),
        CanonicalAggregateKind.ProfilePreferences => JsonEqual(left.ProfilePreferences, right.ProfilePreferences),
        _ => throw new ArgumentOutOfRangeException(nameof(aggregate)),
    };

    /// <summary>
    /// An update at the exact cursor already held after a snapshot is redundant only when the
    /// cursor identity and payload both agree. Treating revision equality alone as proof would let
    /// a delayed fork advance the delivery sequence while silently preserving unrelated state.
    /// </summary>
    private static bool UpdateStateEqual(CanonicalUpdate update, CanonicalCompanionState state) => update switch
    {
        DeviceModeCanonicalUpdate modes => JsonEqual(modes.State, state.DeviceModes),
        WorkspaceCanonicalUpdate workspace => JsonEqual(workspace.State, state.Workspace),
        MarksCanonicalUpdate marks => JsonEqual(marks.State, state.Marks),
        CaptureCanonicalUpdate capture => JsonEqual(capture.State, state.CaptureIntent),
        ProfilePreferencesCanonicalUpdate preferences => JsonEqual(preferences.State, state.ProfilePreferences),
        _ => throw new ArgumentOutOfRangeException(nameof(update)),
    };

    /// <summary>
    /// An acknowledgement without a snapshot may describe only state the replica already holds.
    /// Delivery order puts the corresponding update first; accepting an acknowledgement that leads
    /// or forks the cache would consume its sequence while leaving the tablet silently stale.
    /// </summary>
    private static bool AcknowledgementIsReflected(
        CommandAcknowledgement acknowledgement,
        CanonicalCompanionState state)
    {
        if (acknowledgement.GlobalRevision.Value > state.GlobalRevision.Value)
        {
            return false;
        }

        if (acknowledgement.Disposition != CommandDisposition.Applied)
        {
            return true;
        }

        var cursor = state.Cursor(acknowledgement.Aggregate);
        return acknowledgement.AppliedRevision.Value < cursor.Revision.Value ||
               (acknowledgement.AppliedRevision == cursor.Revision &&
                acknowledgement.AppliedChangeId == cursor.LastChangeId);
    }

    private static bool JsonEqual<T>(T left, T right) =>
        JsonSerializer.SerializeToUtf8Bytes(left, CompanionProtocolJson.Options)
            .AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right, CompanionProtocolJson.Options));

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
        ProfilePreferencesCanonicalUpdate preferences => preferences.State.Cursor,
        _ => throw new ArgumentOutOfRangeException(nameof(update)),
    };
}
