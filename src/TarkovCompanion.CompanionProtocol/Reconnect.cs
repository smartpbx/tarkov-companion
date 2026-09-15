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

    /// <summary>True when a complete cursor vector is no newer than the desktop and agrees at every equal cursor.</summary>
    public static bool CanAdvanceTo(CanonicalCompanionState canonical, IReadOnlyList<AggregateAcknowledgement> acknowledgements)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(acknowledgements);
        return IsComplete(acknowledgements) && acknowledgements.All(acknowledgement =>
        {
            var cursor = canonical.Cursor(acknowledgement.Aggregate);
            return acknowledgement.Revision.Value < cursor.Revision.Value ||
                   (acknowledgement.Revision == cursor.Revision && acknowledgement.AppliedChangeId == cursor.LastChangeId);
        });
    }

    /// <summary>True only when the vector is the exact current canonical state.</summary>
    public static bool ExactlyMatches(
        CanonicalCompanionState canonical,
        GlobalRevision globalRevision,
        IReadOnlyList<AggregateAcknowledgement> acknowledgements) =>
        globalRevision == canonical.GlobalRevision &&
        IsComplete(acknowledgements) &&
        acknowledgements.All(acknowledgement =>
        {
            var cursor = canonical.Cursor(acknowledgement.Aggregate);
            return acknowledgement.Revision == cursor.Revision && acknowledgement.AppliedChangeId == cursor.LastChangeId;
        });

    internal static IReadOnlyList<AggregateAcknowledgement> RequireComplete(
        IReadOnlyList<AggregateAcknowledgement> acknowledgements,
        GlobalRevision globalRevision,
        string parameterName)
    {
        var list = ProtocolGuard.List(acknowledgements, parameterName, Enum.GetValues<CanonicalAggregateKind>().Length);
        if (!IsComplete(list) || list.Sum(item => item.Revision.Value) != globalRevision.Value)
        {
            throw new ArgumentException(
                "A cached state acknowledges every aggregate exactly once and its revisions sum to the global revision.",
                parameterName);
        }

        return list;
    }

    private static bool IsComplete(IReadOnlyList<AggregateAcknowledgement> acknowledgements) =>
        acknowledgements.Count == Enum.GetValues<CanonicalAggregateKind>().Length &&
        acknowledgements.Select(item => item.Aggregate).Distinct().Count() == acknowledgements.Count;
}

public sealed record ReconnectRequest
{
    public ReconnectRequest(
        CompanionProtocolVersion protocolVersion,
        DeviceSessionId sessionId,
        ReconnectRequestId requestId,
        AuthorityEpoch? authorityEpoch,
        GlobalRevision lastGlobalRevision,
        DeliverySequence lastDeliverySequence,
        IReadOnlyList<AggregateAcknowledgement> aggregateAcknowledgements)
    {
        ProtocolVersion = ProtocolGuard.Version(protocolVersion, nameof(protocolVersion));
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("A device session id is required.", nameof(sessionId))
            : sessionId;
        RequestId = requestId.Value == Guid.Empty
            ? throw new ArgumentException("A reconnect request id is required.", nameof(requestId))
            : requestId;
        AuthorityEpoch = authorityEpoch is { } epoch && epoch.Value == Guid.Empty
            ? throw new ArgumentException("An authority epoch is required when present.", nameof(authorityEpoch))
            : authorityEpoch;
        LastGlobalRevision = lastGlobalRevision;
        LastDeliverySequence = lastDeliverySequence;
        if (authorityEpoch is null)
        {
            AggregateAcknowledgements = ProtocolGuard.List(aggregateAcknowledgements, nameof(aggregateAcknowledgements), 0);
            if (lastGlobalRevision.Value != 0)
            {
                throw new ArgumentException("A client without a cached authority epoch has no revisions to acknowledge.", nameof(authorityEpoch));
            }
        }
        else
        {
            AggregateAcknowledgements = AggregateAcknowledgement.RequireComplete(
                aggregateAcknowledgements,
                lastGlobalRevision,
                nameof(aggregateAcknowledgements));
        }
    }

    public CompanionProtocolVersion ProtocolVersion { get; }

    public DeviceSessionId SessionId { get; }

    public ReconnectRequestId RequestId { get; }

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
    public DeliveredServerMessage(
        DeliverySequence deliverySequence,
        CompanionDeviceId authenticatedOriginDeviceId,
        DateTimeOffset serverUtc,
        ServerMessage message)
    {
        DeliverySequence = deliverySequence.Value > 0
            ? deliverySequence
            : throw new ArgumentOutOfRangeException(nameof(deliverySequence));
        AuthenticatedOriginDeviceId = authenticatedOriginDeviceId.Value == Guid.Empty
            ? throw new ArgumentException("An authenticated origin device is required.", nameof(authenticatedOriginDeviceId))
            : authenticatedOriginDeviceId;
        ServerUtc = ProtocolGuard.Utc(serverUtc, nameof(serverUtc));
        Message = ProtocolGuard.NotNull(message, nameof(message));
    }

    public DeliverySequence DeliverySequence { get; }

    public CompanionDeviceId AuthenticatedOriginDeviceId { get; }

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
        DeviceSessionId sessionId,
        ReconnectRequestId requestId,
        ReconnectDisposition disposition,
        IReadOnlyList<DeliveredServerMessage> replay,
        CanonicalCompanionState? snapshot,
        DeliverySequence resumeAfterDeliverySequence,
        string reason)
    {
        ProtocolVersion = ProtocolGuard.Version(protocolVersion, nameof(protocolVersion));
        SessionId = sessionId.Value == Guid.Empty
            ? throw new ArgumentException("A device session id is required.", nameof(sessionId))
            : sessionId;
        RequestId = requestId.Value == Guid.Empty
            ? throw new ArgumentException("A reconnect request id is required.", nameof(requestId))
            : requestId;
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

    public DeviceSessionId SessionId { get; }

    public ReconnectRequestId RequestId { get; }

    public ReconnectDisposition Disposition { get; }

    public IReadOnlyList<DeliveredServerMessage> Replay { get; }

    public CanonicalCompanionState? Snapshot { get; }

    public DeliverySequence ResumeAfterDeliverySequence { get; }

    public string Reason { get; }
}

/// <summary>A reconnect plan and the unchanged ledger; only an authenticated client acknowledgement trims history.</summary>
public sealed record ReconnectPlanning(ReconnectPlan Plan, DeliveryLedger Ledger);

public static class ReconnectPlanner
{
    /// <summary>
    /// Replays only when the retained stream provably covers every device sequence and every global
    /// revision after the client's position, in order, without a coalesced marker, and within the
    /// replay and payload bounds. Anything else is an authoritative snapshot. Every plan is stamped
    /// with the session's negotiated version, the only version the client is guaranteed to read.
    /// </summary>
    public static ReconnectPlanning Plan(
        CanonicalCompanionState canonical,
        ReconnectRequest request,
        DeliveryLedger ledger,
        CompanionDeviceId deviceId,
        DeviceSessionId authenticatedSessionId,
        CompanionProtocolVersion negotiatedVersion)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ledger);
        var version = ProtocolGuard.Version(negotiatedVersion, nameof(negotiatedVersion));
        if (authenticatedSessionId.Value == Guid.Empty || request.SessionId != authenticatedSessionId)
        {
            throw new UnauthorizedAccessException("The reconnect request does not belong to the authenticated session.");
        }

        var lastAssigned = ledger.For(deviceId)?.LastAssigned ?? new DeliverySequence(0);
        if (!CompanionProtocolVersion.Current.CanRead(request.ProtocolVersion) || request.ProtocolVersion != version)
        {
            return new ReconnectPlanning(
                new ReconnectPlan(
                    version,
                    authenticatedSessionId,
                    request.RequestId,
                    ReconnectDisposition.UnsupportedVersion,
                    [],
                    null,
                    request.LastDeliverySequence,
                    CompanionProtocolVersion.Current.CanRead(request.ProtocolVersion) ? "version-not-negotiated" : "unsupported-version"),
                ledger);
        }

        if (request.AuthorityEpoch != canonical.AuthorityEpoch ||
            request.LastGlobalRevision.Value > canonical.GlobalRevision.Value ||
            request.LastDeliverySequence.Value > lastAssigned.Value ||
            !AggregateAcknowledgement.CanAdvanceTo(canonical, request.AggregateAcknowledgements))
        {
            return Snapshot(canonical, ledger, lastAssigned, version, authenticatedSessionId, request.RequestId, "authority-or-cursor-mismatch");
        }

        if (request.LastDeliverySequence == lastAssigned)
        {
            return AggregateAcknowledgement.ExactlyMatches(canonical, request.LastGlobalRevision, request.AggregateAcknowledgements)
                ? new ReconnectPlanning(
                    new ReconnectPlan(
                        version,
                        authenticatedSessionId,
                        request.RequestId,
                        ReconnectDisposition.UpToDate,
                        [],
                        null,
                        lastAssigned,
                        "already-current"),
                    ledger)
                : Snapshot(canonical, ledger, lastAssigned, version, authenticatedSessionId, request.RequestId, "revision-not-in-delivery-stream");
        }

        var retained = ledger.PendingFor(deviceId)
            .Where(item => item.Sequence.Value > request.LastDeliverySequence.Value)
            .ToArray();
        var expectedCount = lastAssigned.Value - request.LastDeliverySequence.Value;
        if (expectedCount > ProtocolBounds.MaxReplayItems ||
            retained.LongLength != expectedCount ||
            retained.Any(item => item.SnapshotRequired))
        {
            return Snapshot(canonical, ledger, lastAssigned, version, authenticatedSessionId, request.RequestId, "delivery-history-unavailable");
        }

        var nextSequence = request.LastDeliverySequence.Value + 1;
        var nextGlobal = request.LastGlobalRevision.Value + 1;
        var cursors = request.AggregateAcknowledgements.ToDictionary(item => item.Aggregate);
        foreach (var item in retained)
        {
            if (item.Sequence.Value != nextSequence)
            {
                return Snapshot(canonical, ledger, lastAssigned, version, authenticatedSessionId, request.RequestId, "delivery-sequence-gap");
            }

            nextSequence++;
            if (item.Message is CanonicalUpdateMessage { Update: var update })
            {
                var incoming = CursorOf(update);
                var prior = cursors[update.Aggregate];
                if (update.AuthorityEpoch != canonical.AuthorityEpoch ||
                    update.GlobalRevision.Value != nextGlobal ||
                    incoming.Revision.Value != prior.Revision.Value + 1)
                {
                    return Snapshot(canonical, ledger, lastAssigned, version, authenticatedSessionId, request.RequestId, "global-or-aggregate-revision-gap");
                }

                cursors[update.Aggregate] = new AggregateAcknowledgement(
                    update.Aggregate,
                    incoming.Revision,
                    incoming.LastChangeId,
                    update.ChangedUtc);
                nextGlobal++;
            }
        }

        if (nextGlobal - 1 != canonical.GlobalRevision.Value ||
            !AggregateAcknowledgement.ExactlyMatches(canonical, canonical.GlobalRevision, cursors.Values.ToArray()))
        {
            return Snapshot(canonical, ledger, lastAssigned, version, authenticatedSessionId, request.RequestId, "global-or-aggregate-revision-gap");
        }

        var plan = new ReconnectPlan(
            version,
            authenticatedSessionId,
            request.RequestId,
            ReconnectDisposition.Replay,
            retained.Select(item => new DeliveredServerMessage(
                item.Sequence,
                item.AuthenticatedOriginDeviceId,
                item.EnqueuedUtc,
                item.Message!)).ToArray(),
            null,
            lastAssigned,
            "bounded-replay");
        try
        {
            _ = CompanionProtocolJson.Serialize(plan);
        }
        catch (JsonException)
        {
            return Snapshot(canonical, ledger, lastAssigned, version, authenticatedSessionId, request.RequestId, "replay-exceeds-payload-bound");
        }

        return new ReconnectPlanning(plan, ledger);
    }

    private static ReconnectPlanning Snapshot(
        CanonicalCompanionState canonical,
        DeliveryLedger ledger,
        DeliverySequence lastAssigned,
        CompanionProtocolVersion version,
        DeviceSessionId sessionId,
        ReconnectRequestId requestId,
        string reason) =>
        new(
            new ReconnectPlan(
                version,
                sessionId,
                requestId,
                ReconnectDisposition.FullSnapshot,
                [],
                canonical,
                lastAssigned,
                reason),
            ledger);

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
