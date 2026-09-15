using System.Text.Json;
using TarkovCompanion.Core.Abstractions.V2;
using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class DeliveryAndReconnectTests
{
    private static readonly WorkspaceOrigin TabletOrigin = new(Workspace, TabletDevice, WorkspaceOriginKind.PairedDevice, TabletInstance);

    [Fact]
    public void EveryEnvelopeToADeviceConsumesOneSequenceAcrossChannels()
    {
        var flow = Flow.Create();
        var pending = flow.Ledger.PendingFor(TabletDevice);

        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, pending.Select(item => item.Sequence.Value));
        Assert.Equal(
            new[] { DeliveryChannel.Control, DeliveryChannel.DeviceModes, DeliveryChannel.Control, DeliveryChannel.Marks, DeliveryChannel.Marks },
            pending.Select(item => item.Channel));
    }

    [Fact]
    public void AFullChannelCoalescesWithoutBlockingOtherChannelsOrDevices()
    {
        var ledger = DeliveryLedger.Empty;
        for (var index = 1; index <= ProtocolBounds.MaxDeliveryItemsPerChannel + 1; index++)
        {
            ledger = ledger.Enqueue(
                TabletDevice,
                TabletDevice,
                new CanonicalUpdateMessage(MarksUpdate(index, index)),
                Now).Ledger;
        }

        var marker = Assert.Single(ledger.PendingFor(TabletDevice), item => item.Channel == DeliveryChannel.Marks);
        var workspace = ledger.Enqueue(
            TabletDevice,
            TabletDevice,
            new CanonicalUpdateMessage(WorkspaceUpdate(70, 1)),
            Now);
        var other = workspace.Ledger.Enqueue(
            OtherDevice,
            TabletDevice,
            new CanonicalUpdateMessage(MarksUpdate(1, 1)),
            Now);

        Assert.True(marker.SnapshotRequired);
        Assert.Equal(ProtocolBounds.MaxDeliveryItemsPerChannel + 1, marker.Sequence.Value);
        Assert.IsType<CanonicalSnapshotMessage>(marker.Resolve(InitialState()));
        Assert.False(workspace.Item.SnapshotRequired);
        Assert.Equal(ProtocolBounds.MaxDeliveryItemsPerChannel + 2, workspace.Item.Sequence.Value);
        Assert.False(other.Item.SnapshotRequired);
        Assert.Equal(1, other.Item.Sequence.Value);
        Assert.Single(other.Ledger.PendingFor(OtherDevice));
    }

    [Fact]
    public void AuthenticatedAcknowledgementCoversTheDeviceStreamAndRejectsUnassignedSequences()
    {
        var flow = Flow.Create();
        var held = flow.ReplicaThrough(5).CreateDeliveryAcknowledgement(CompanionProtocolVersion.Current, TabletSession, Now)!;
        var throughThree = new ClientDeliveryAcknowledgement(
            held.ProtocolVersion,
            held.SessionId,
            held.AuthorityEpoch,
            new DeliverySequence(3),
            held.GlobalRevision,
            held.AggregateAcknowledgements);
        var throughTwo = new ClientDeliveryAcknowledgement(
            held.ProtocolVersion,
            held.SessionId,
            held.AuthorityEpoch,
            new DeliverySequence(2),
            held.GlobalRevision,
            held.AggregateAcknowledgements);
        var unassigned = new ClientDeliveryAcknowledgement(
            held.ProtocolVersion,
            held.SessionId,
            held.AuthorityEpoch,
            new DeliverySequence(99),
            held.GlobalRevision,
            held.AggregateAcknowledgements);

        var acknowledged = flow.Ledger.Acknowledge(
            TabletDevice, TabletSession, CompanionProtocolVersion.Current, throughThree, flow.Final);
        var replayedOld = acknowledged.Ledger.Acknowledge(
            TabletDevice, TabletSession, CompanionProtocolVersion.Current, throughTwo, flow.Final);
        var beyond = acknowledged.Ledger.Acknowledge(
            TabletDevice, TabletSession, CompanionProtocolVersion.Current, unassigned, flow.Final);

        Assert.True(acknowledged.Accepted);
        Assert.Equal(new long[] { 4, 5 }, acknowledged.Ledger.PendingFor(TabletDevice).Select(item => item.Sequence.Value));
        Assert.True(replayedOld.Accepted);
        Assert.Same(acknowledged.Ledger, replayedOld.Ledger);
        Assert.False(beyond.Accepted);
        Assert.Equal("delivery-sequence-not-assigned", beyond.Code);
        Assert.Equal(3, beyond.Ledger.For(TabletDevice)!.LastAcknowledged.Value);
    }

    [Fact]
    public void AClientAcknowledgementFromAnotherEpochOrDivergentStateIsRefused()
    {
        var flow = Flow.Create();
        var replica = flow.ReplicaThrough(5);
        var acknowledgement = replica.CreateDeliveryAcknowledgement(CompanionProtocolVersion.Current, TabletSession, Now)!;
        var accepted = flow.Ledger.Acknowledge(
            TabletDevice, TabletSession, CompanionProtocolVersion.Current, acknowledgement, flow.Final);
        var otherEpoch = flow.Ledger.Acknowledge(
            TabletDevice,
            TabletSession,
            CompanionProtocolVersion.Current,
            new ClientDeliveryAcknowledgement(
                CompanionProtocolVersion.Current,
                TabletSession,
                new AuthorityEpoch(Guid.Parse("30000000-0000-0000-0000-000000000099")),
                new DeliverySequence(5),
                flow.Final.GlobalRevision,
                acknowledgement.AggregateAcknowledgements),
            flow.Final);
        var divergent = flow.Ledger.Acknowledge(
            TabletDevice,
            TabletSession,
            CompanionProtocolVersion.Current,
            new ClientDeliveryAcknowledgement(
                CompanionProtocolVersion.Current,
                TabletSession,
                Epoch,
                new DeliverySequence(5),
                flow.Final.GlobalRevision,
                acknowledgement.AggregateAcknowledgements.Select(item =>
                    item.Aggregate == CanonicalAggregateKind.Marks
                        ? new AggregateAcknowledgement(item.Aggregate, item.Revision, Command(777), item.AcknowledgedUtc)
                        : item).ToArray()),
            flow.Final);

        Assert.True(accepted.Accepted);
        Assert.Empty(accepted.Ledger.PendingFor(TabletDevice));
        Assert.False(otherEpoch.Accepted);
        Assert.Equal("authority-epoch-mismatch", otherEpoch.Code);
        Assert.False(divergent.Accepted);
        Assert.Same(flow.Ledger, divergent.Ledger);
    }

    [Fact]
    public void DeliveryAcknowledgementsRequireTheAuthenticatedSessionNegotiatedVersionAndExactCurrentCursors()
    {
        var flow = Flow.Create();
        var current = flow.ReplicaThrough(5).CreateDeliveryAcknowledgement(
            CompanionProtocolVersion.Current, TabletSession, Now)!;
        var stale = flow.ReplicaThrough(2).CreateDeliveryAcknowledgement(
            CompanionProtocolVersion.Current, TabletSession, Now)!;
        var readableButUnnegotiated = new ClientDeliveryAcknowledgement(
            new CompanionProtocolVersion(2, 1),
            TabletSession,
            current.AuthorityEpoch,
            current.ThroughDeliverySequence,
            current.GlobalRevision,
            current.AggregateAcknowledgements);

        var wrongSession = flow.Ledger.Acknowledge(
            TabletDevice, OtherSession, CompanionProtocolVersion.Current, current, flow.Final);
        var wrongVersion = flow.Ledger.Acknowledge(
            TabletDevice, TabletSession, CompanionProtocolVersion.Current, readableButUnnegotiated, flow.Final);
        var staleState = flow.Ledger.Acknowledge(
            TabletDevice, TabletSession, CompanionProtocolVersion.Current, stale, flow.Final);

        Assert.Equal("authenticated-session-mismatch", wrongSession.Code);
        Assert.Equal("protocol-version-mismatch", wrongVersion.Code);
        Assert.Equal("acknowledged-state-diverges", staleState.Code);
        Assert.All(new[] { wrongSession, wrongVersion, staleState }, result =>
        {
            Assert.False(result.Accepted);
            Assert.Same(flow.Ledger, result.Ledger);
        });
        Assert.Throws<ArgumentException>(() => new ClientDeliveryAcknowledgement(
            CompanionProtocolVersion.Current,
            TabletSession,
            Epoch,
            new DeliverySequence(5),
            flow.Final.GlobalRevision,
            current.AggregateAcknowledgements.Take(4).ToArray()));
    }

    [Fact]
    public void TheReplicaAppliesContiguousDeltasAndDemandsResyncAfterAGap()
    {
        var flow = Flow.Create();
        var replica = flow.ReplicaThrough(2);

        var gap = Observe(replica, flow.Envelope(4));
        var discarded = Observe(gap.Replica, flow.Envelope(5));
        var duplicate = Observe(replica, flow.Envelope(2));
        var resynced = Observe(discarded.Replica, Delivered(6, new CanonicalSnapshotMessage(flow.Final)));
        var alreadyReflected = Observe(resynced.Replica, Delivered(7, new CanonicalUpdateMessage(flow.Updates[^1]), TabletDevice));

        Assert.Equal(ReplicaDisposition.ResyncRequired, gap.Disposition);
        Assert.Equal("delivery-sequence-gap", gap.Code);
        Assert.True(gap.Replica.AwaitingResync);
        Assert.Equal(ReplicaDisposition.Discarded, discarded.Disposition);
        Assert.Equal(ReplicaDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(ReplicaDisposition.Applied, resynced.Disposition);
        Assert.False(resynced.Replica.AwaitingResync);
        Assert.Equal(ReplicaDisposition.AlreadyReflected, alreadyReflected.Disposition);
        AssertStateEqual(flow.Final, flow.ReplicaThrough(5).State!);
    }

    [Fact]
    public void TheReplicaRejectsDeltasFromAnotherEpochOrWithRevisionGaps()
    {
        var flow = Flow.Create();
        var replica = flow.ReplicaThrough(1);
        var otherEpoch = Observe(replica, Delivered(2, new CanonicalUpdateMessage(MarksUpdate(1, 1, new AuthorityEpoch(Guid.Parse("30000000-0000-0000-0000-000000000099")))), TabletDevice));
        var globalGap = Observe(replica, Delivered(2, new CanonicalUpdateMessage(MarksUpdate(3, 1)), TabletDevice));
        var aggregateGap = Observe(replica, Delivered(2, new CanonicalUpdateMessage(MarksUpdate(1, 2)), TabletDevice));

        Assert.Equal("authority-epoch-changed", otherEpoch.Code);
        Assert.Equal("global-revision-gap", globalGap.Code);
        Assert.Equal("aggregate-revision-gap", aggregateGap.Code);
        Assert.All(new[] { otherEpoch, globalGap, aggregateGap }, observation => Assert.Equal(ReplicaDisposition.ResyncRequired, observation.Disposition));
    }

    [Fact]
    public void TheReplicaRejectsWrongSessionVersionOriginAndRegressedServerTime()
    {
        var flow = Flow.Create();
        var replica = flow.ReplicaThrough(1);
        var valid = flow.Envelope(2);
        var wrongSession = new ServerEnvelope(
            valid.ProtocolVersion,
            OtherSession,
            valid.AuthenticatedOriginDeviceId,
            valid.ServerUtc,
            valid.DeliverySequence,
            valid.Message);
        var wrongVersion = new ServerEnvelope(
            new CompanionProtocolVersion(2, 1),
            valid.SessionId,
            valid.AuthenticatedOriginDeviceId,
            valid.ServerUtc,
            valid.DeliverySequence,
            valid.Message);
        var wrongOrigin = new ServerEnvelope(
            valid.ProtocolVersion,
            valid.SessionId,
            DesktopDevice,
            valid.ServerUtc,
            valid.DeliverySequence,
            valid.Message);
        var afterUpdate = Observe(replica, valid).Replica;
        var regressedTime = new ServerEnvelope(
            CompanionProtocolVersion.Current,
            TabletSession,
            TabletDevice,
            Now.AddMilliseconds(-1),
            new DeliverySequence(3),
            new CommandAcknowledgementMessage(
                Apply(flow.Initial, SetMode(99, 1, Now, CompanionInteractionMode.Independent), TabletContext()).Acknowledgement));

        var sessionResult = replica.Observe(wrongSession, TabletSession, CompanionProtocolVersion.Current);
        var versionResult = replica.Observe(wrongVersion, TabletSession, CompanionProtocolVersion.Current);
        var originResult = replica.Observe(wrongOrigin, TabletSession, CompanionProtocolVersion.Current);
        var timeResult = afterUpdate.Observe(regressedTime, TabletSession, CompanionProtocolVersion.Current);

        Assert.Equal("authenticated-session-mismatch", sessionResult.Code);
        Assert.Equal("protocol-version-mismatch", versionResult.Code);
        Assert.Equal("authenticated-origin-mismatch", originResult.Code);
        Assert.Equal("server-time-regressed", timeResult.Code);
        Assert.All(new[] { sessionResult, versionResult }, result =>
        {
            Assert.Equal(ReplicaDisposition.Discarded, result.Disposition);
            Assert.Same(replica, result.Replica);
        });
        Assert.Equal(ReplicaDisposition.ResyncRequired, originResult.Disposition);
        Assert.Equal(ReplicaDisposition.ResyncRequired, timeResult.Disposition);
    }

    [Fact]
    public void ReconnectReplaysOnlyAProvablyContiguousStream()
    {
        var flow = Flow.Create();
        var replica = flow.ReplicaThrough(2);
        var request = replica.CreateReconnectRequest(CompanionProtocolVersion.Current, TabletSession, DefaultReconnectRequestId, Now);

        var replay = ReconnectPlanner.Plan(flow.Final, request, flow.Ledger, TabletDevice, TabletSession, CompanionProtocolVersion.Current);
        var wirePlan = CompanionProtocolJson.Deserialize<ReconnectPlan>(CompanionProtocolJson.Serialize(replay.Plan));
        var applied = replica.ApplyReconnectPlan(wirePlan, request, TabletSession, CompanionProtocolVersion.Current);

        Assert.Equal(ReconnectDisposition.Replay, replay.Plan.Disposition);
        Assert.Equal(CompanionProtocolVersion.Current, replay.Plan.ProtocolVersion);
        Assert.Equal(new long[] { 3, 4, 5 }, replay.Plan.Replay.Select(item => item.DeliverySequence.Value));
        Assert.Equal(5, replay.Plan.ResumeAfterDeliverySequence.Value);
        Assert.Equal(5, replay.Ledger.PendingFor(TabletDevice).Count);
        Assert.Equal(ReplicaDisposition.Applied, applied.Disposition);
        AssertStateEqual(flow.Final, applied.Replica.State!);

        var throughFour = new ClientDeliveryAcknowledgement(
            CompanionProtocolVersion.Current,
            TabletSession,
            Epoch,
            new DeliverySequence(4),
            flow.Final.GlobalRevision,
            AcknowledgementsFor(flow.Final));
        var trimmed = flow.Ledger.Acknowledge(
            TabletDevice, TabletSession, CompanionProtocolVersion.Current, throughFour, flow.Final).Ledger;
        var history = ReconnectPlanner.Plan(flow.Final, request, trimmed, TabletDevice, TabletSession, CompanionProtocolVersion.Current);
        var epoch = ReconnectPlanner.Plan(
            flow.Final,
            new ReconnectRequest(
                CompanionProtocolVersion.Current,
                TabletSession,
                DefaultReconnectRequestId,
                new AuthorityEpoch(Guid.Parse("30000000-0000-0000-0000-000000000099")),
                request.LastGlobalRevision,
                request.LastDeliverySequence,
                request.AggregateAcknowledgements),
            flow.Ledger,
            TabletDevice,
            TabletSession,
            CompanionProtocolVersion.Current);
        var currentRequest = flow.ReplicaThrough(5).CreateReconnectRequest(
            CompanionProtocolVersion.Current, TabletSession, DefaultReconnectRequestId, Now);
        var current = ReconnectPlanner.Plan(
            flow.Final, currentRequest, flow.Ledger, TabletDevice, TabletSession, CompanionProtocolVersion.Current);
        var unsupported = ReconnectPlanner.Plan(
            flow.Final,
            new ReconnectRequest(
                new CompanionProtocolVersion(3, 0),
                TabletSession,
                DefaultReconnectRequestId,
                request.AuthorityEpoch,
                request.LastGlobalRevision,
                request.LastDeliverySequence,
                request.AggregateAcknowledgements),
            flow.Ledger,
            TabletDevice,
            TabletSession,
            CompanionProtocolVersion.Current);
        var reloadRequest = CanonicalReplica.Empty.CreateReconnectRequest(
            CompanionProtocolVersion.Current, TabletSession, DefaultReconnectRequestId, Now);
        var reloaded = ReconnectPlanner.Plan(
            flow.Final,
            reloadRequest,
            flow.Ledger,
            TabletDevice,
            TabletSession,
            CompanionProtocolVersion.Current);

        Assert.Equal("delivery-history-unavailable", history.Plan.Reason);
        Assert.Equal(ReconnectDisposition.FullSnapshot, history.Plan.Disposition);
        Assert.Equal(ReconnectDisposition.FullSnapshot, epoch.Plan.Disposition);
        Assert.Equal(ReconnectDisposition.UpToDate, current.Plan.Disposition);
        Assert.Equal(5, current.Ledger.PendingFor(TabletDevice).Count);
        Assert.Equal(ReconnectDisposition.UnsupportedVersion, unsupported.Plan.Disposition);
        Assert.Same(flow.Ledger, unsupported.Ledger);
        Assert.Equal(ReconnectDisposition.FullSnapshot, reloaded.Plan.Disposition);
        var fromEmpty = CanonicalReplica.Empty.ApplyReconnectPlan(
            reloaded.Plan, reloadRequest, TabletSession, CompanionProtocolVersion.Current);
        Assert.Equal(5, fromEmpty.Replica.LastDeliverySequence.Value);
        AssertStateEqual(flow.Final, fromEmpty.Replica.State!);
    }

    [Fact]
    public void ReconnectIsBoundToAuthenticatedSessionVersionRequestAndCompleteCursors()
    {
        var flow = Flow.Create();
        var replica = flow.ReplicaThrough(2);
        var request = replica.CreateReconnectRequest(
            CompanionProtocolVersion.Current, TabletSession, DefaultReconnectRequestId, Now);
        var planning = ReconnectPlanner.Plan(
            flow.Final,
            request,
            flow.Ledger,
            TabletDevice,
            TabletSession,
            CompanionProtocolVersion.Current);
        var anotherRequest = new ReconnectRequestId(Guid.Parse("90000000-0000-4000-8000-000000000099"));
        var wrongCorrelation = new ReconnectPlan(
            planning.Plan.ProtocolVersion,
            planning.Plan.SessionId,
            anotherRequest,
            planning.Plan.Disposition,
            planning.Plan.Replay,
            planning.Plan.Snapshot,
            planning.Plan.ResumeAfterDeliverySequence,
            planning.Plan.Reason);
        var wrongSession = new ReconnectPlan(
            planning.Plan.ProtocolVersion,
            OtherSession,
            planning.Plan.RequestId,
            planning.Plan.Disposition,
            planning.Plan.Replay,
            planning.Plan.Snapshot,
            planning.Plan.ResumeAfterDeliverySequence,
            planning.Plan.Reason);
        var wrongVersion = new ReconnectPlan(
            new CompanionProtocolVersion(2, 1),
            planning.Plan.SessionId,
            planning.Plan.RequestId,
            planning.Plan.Disposition,
            planning.Plan.Replay,
            planning.Plan.Snapshot,
            planning.Plan.ResumeAfterDeliverySequence,
            planning.Plan.Reason);

        var correlationResult = replica.ApplyReconnectPlan(
            wrongCorrelation, request, TabletSession, CompanionProtocolVersion.Current);
        var sessionResult = replica.ApplyReconnectPlan(
            wrongSession, request, TabletSession, CompanionProtocolVersion.Current);
        var versionResult = replica.ApplyReconnectPlan(
            wrongVersion, request, TabletSession, CompanionProtocolVersion.Current);

        Assert.Equal("reconnect-request-mismatch", correlationResult.Code);
        Assert.Equal("authenticated-session-mismatch", sessionResult.Code);
        Assert.Equal("protocol-version-mismatch", versionResult.Code);
        Assert.All(new[] { correlationResult, sessionResult, versionResult }, result =>
        {
            Assert.Equal(ReplicaDisposition.Discarded, result.Disposition);
            Assert.Same(replica, result.Replica);
        });
        Assert.Throws<UnauthorizedAccessException>(() => ReconnectPlanner.Plan(
            flow.Final,
            request,
            flow.Ledger,
            TabletDevice,
            OtherSession,
            CompanionProtocolVersion.Current));
        Assert.Throws<ArgumentException>(() => new ReconnectRequest(
            CompanionProtocolVersion.Current,
            TabletSession,
            DefaultReconnectRequestId,
            request.AuthorityEpoch,
            request.LastGlobalRevision,
            request.LastDeliverySequence,
            request.AggregateAcknowledgements.Take(4).ToArray()));
    }

    [Fact]
    public void ADelayedCrossEpochPlanCannotRollBackAReplicaThatAdvancedAfterItsRequest()
    {
        var flow = Flow.Create();
        var requestedFrom = flow.ReplicaThrough(2);
        var request = requestedFrom.CreateReconnectRequest(
            CompanionProtocolVersion.Current, TabletSession, DefaultReconnectRequestId, Now);
        var initial = InitialState();
        var replacement = new CanonicalCompanionState(
            new AuthorityEpoch(Guid.Parse("30000000-0000-0000-0000-000000000099")),
            initial.WorkspaceId,
            initial.DesktopInstanceId,
            initial.GlobalRevision,
            initial.DesktopDeviceId,
            initial.DeviceModes,
            initial.Workspace,
            initial.Marks,
            initial.CaptureIntent,
            initial.ProfilePreferences);
        var delayed = new ReconnectPlan(
            CompanionProtocolVersion.Current,
            TabletSession,
            DefaultReconnectRequestId,
            ReconnectDisposition.FullSnapshot,
            [],
            replacement,
            new DeliverySequence(0),
            "authority-or-cursor-mismatch");
        var advanced = flow.ReplicaThrough(5);

        var result = advanced.ApplyReconnectPlan(
            delayed, request, TabletSession, CompanionProtocolVersion.Current);

        Assert.Equal(ReplicaDisposition.Discarded, result.Disposition);
        Assert.Equal("stale-reconnect-plan", result.Code);
        Assert.Same(advanced, result.Replica);
    }

    [Fact]
    public void ACoalescedMarkerInTheReplayRangeForcesASnapshot()
    {
        var ledger = DeliveryLedger.Empty;
        for (var index = 1; index <= ProtocolBounds.MaxDeliveryItemsPerChannel + 1; index++)
        {
            ledger = ledger.Enqueue(
                TabletDevice,
                TabletDevice,
                new CanonicalUpdateMessage(MarksUpdate(index, index)),
                Now).Ledger;
        }

        var state = StateWithMarksAt(ProtocolBounds.MaxDeliveryItemsPerChannel + 1);
        var emptyState = InitialState();
        var plan = ReconnectPlanner.Plan(
            state,
            new ReconnectRequest(
                CompanionProtocolVersion.Current,
                TabletSession,
                DefaultReconnectRequestId,
                Epoch,
                new GlobalRevision(0),
                new DeliverySequence(0),
                AcknowledgementsFor(emptyState)),
            ledger,
            TabletDevice,
            TabletSession,
            CompanionProtocolVersion.Current);

        Assert.Equal(ReconnectDisposition.FullSnapshot, plan.Plan.Disposition);
        Assert.Equal("delivery-history-unavailable", plan.Plan.Reason);
    }

    [Fact]
    public void AReplayThatWouldExceedThePayloadBoundFallsBackToASnapshot()
    {
        var state = InitialState();
        var updates = new List<CanonicalUpdate>();
        for (var index = 0; index < 40; index++)
        {
            var at = Now.AddMilliseconds(index);
            var command = new UpsertMarkCommand(
                Command(3_000 + index),
                new AggregateRevision(index + 1),
                at,
                at.AddMinutes(1),
                Mark(3_000 + index),
                0,
                new MapMarkDraft(
                    MapMarkKind.Note,
                    MapMarkScope.PairedDevice,
                    new MapMarkState(new string('m', ProtocolBounds.MaxShortStringBytes), new string('f', ProtocolBounds.MaxShortStringBytes), index, 1, new string('y', MapMarkState.MaxLabelLength), null),
                    CoordinateSpaceKind.World,
                    new string('v', ProtocolBounds.MaxShortStringBytes),
                    null,
                    "#00AACC"));
            var reduction = Apply(state, command, TabletContext(at));
            Assert.Equal(CommandDisposition.Applied, reduction.Acknowledgement.Disposition);
            state = reduction.State;
            updates.Add(reduction.Update!);
        }

        var ledger = DeliveryLedger.Empty;
        foreach (var update in updates.TakeLast(3))
        {
            ledger = ledger.Enqueue(TabletDevice, TabletDevice, new CanonicalUpdateMessage(update), Now).Ledger;
        }

        var heldState = StateWithMarksAt(state.GlobalRevision.Value - 3);
        var plan = ReconnectPlanner.Plan(
            state,
            new ReconnectRequest(
                CompanionProtocolVersion.Current,
                TabletSession,
                DefaultReconnectRequestId,
                Epoch,
                heldState.GlobalRevision,
                new DeliverySequence(0),
                AcknowledgementsFor(heldState)),
            ledger,
            TabletDevice,
            TabletSession,
            CompanionProtocolVersion.Current);

        Assert.Equal(ReconnectDisposition.FullSnapshot, plan.Plan.Disposition);
        Assert.Equal("replay-exceeds-payload-bound", plan.Plan.Reason);
        Assert.InRange(CompanionProtocolJson.Serialize(plan.Plan).Length, 1, ProtocolBounds.MaxPayloadBytes);
    }

    [Fact]
    public void ALateOrNonContinuingReconnectPlanIsNeverApplied()
    {
        var flow = Flow.Create();
        var request = flow.ReplicaThrough(2).CreateReconnectRequest(
            CompanionProtocolVersion.Current, TabletSession, DefaultReconnectRequestId, Now);
        var replay = ReconnectPlanner.Plan(
            flow.Final,
            request,
            flow.Ledger,
            TabletDevice,
            TabletSession,
            CompanionProtocolVersion.Current).Plan;
        var advanced = flow.ReplicaThrough(5);
        var staleSnapshot = new ReconnectPlan(
            CompanionProtocolVersion.Current,
            TabletSession,
            DefaultReconnectRequestId,
            ReconnectDisposition.FullSnapshot,
            [],
            flow.Initial,
            new DeliverySequence(2),
            "authority-or-cursor-mismatch");
        var staleSameEpochSnapshot = new ReconnectPlan(
            CompanionProtocolVersion.Current,
            TabletSession,
            DefaultReconnectRequestId,
            ReconnectDisposition.FullSnapshot,
            [],
            flow.Initial,
            new DeliverySequence(5),
            "authority-or-cursor-mismatch");

        var late = advanced.ApplyReconnectPlan(staleSnapshot, request, TabletSession, CompanionProtocolVersion.Current);
        var sameEpochRollback = flow.ReplicaThrough(4).ApplyReconnectPlan(
            staleSameEpochSnapshot,
            request,
            TabletSession,
            CompanionProtocolVersion.Current);
        var overlapping = flow.ReplicaThrough(4).ApplyReconnectPlan(replay, request, TabletSession, CompanionProtocolVersion.Current);
        var afterGap = flow.ReplicaThrough(1).ApplyReconnectPlan(replay, request, TabletSession, CompanionProtocolVersion.Current);
        var upToDateElsewhere = flow.ReplicaThrough(2).ApplyReconnectPlan(
            new ReconnectPlan(
                CompanionProtocolVersion.Current,
                TabletSession,
                DefaultReconnectRequestId,
                ReconnectDisposition.UpToDate,
                [],
                null,
                new DeliverySequence(5),
                "already-current"),
            request,
            TabletSession,
            CompanionProtocolVersion.Current);

        Assert.Equal(ReplicaDisposition.Discarded, late.Disposition);
        Assert.Equal("stale-reconnect-plan", late.Code);
        Assert.Same(advanced, late.Replica);
        Assert.Equal(ReplicaDisposition.Discarded, sameEpochRollback.Disposition);
        Assert.Equal("stale-reconnect-plan", sameEpochRollback.Code);
        Assert.Equal(4, sameEpochRollback.Replica.LastDeliverySequence.Value);
        Assert.Equal(ReplicaDisposition.Applied, overlapping.Disposition);
        AssertStateEqual(flow.Final, overlapping.Replica.State!);
        Assert.Equal(5, overlapping.Replica.LastDeliverySequence.Value);
        Assert.Equal(ReplicaDisposition.ResyncRequired, afterGap.Disposition);
        Assert.Equal(1, afterGap.Replica.LastDeliverySequence.Value);
        Assert.Equal(ReplicaDisposition.ResyncRequired, upToDateElsewhere.Disposition);
    }

    [Fact]
    public void AReplicaAdoptsANewAuthorityLifetimeWhoseDeliveryStreamRestarted()
    {
        var flow = Flow.Create();
        var cached = flow.ReplicaThrough(5);
        var restartedEpoch = new AuthorityEpoch(Guid.Parse("30000000-0000-0000-0000-000000000077"));
        var restarted = new CanonicalCompanionState(
            restartedEpoch,
            flow.Final.WorkspaceId,
            flow.Final.DesktopInstanceId,
            new GlobalRevision(0),
            flow.Final.DesktopDeviceId,
            flow.Initial.DeviceModes,
            new WorkspaceAggregate(AggregateCursor.Empty, Projection()),
            new MarkAggregate(AggregateCursor.Empty, []),
            new CaptureIntentAggregate(AggregateCursor.Empty, null),
            ProfilePreferencesAggregate.Empty);

        // After a desktop restart the new ledger has assigned nothing, so the plan resumes at zero.
        var plan = ReconnectPlanner.Plan(
            restarted,
            cached.CreateReconnectRequest(CompanionProtocolVersion.Current, TabletSession, DefaultReconnectRequestId, Now),
            DeliveryLedger.Empty,
            TabletDevice,
            TabletSession,
            CompanionProtocolVersion.Current).Plan;
        var restartRequest = cached.CreateReconnectRequest(
            CompanionProtocolVersion.Current, TabletSession, DefaultReconnectRequestId, Now);
        var adopted = cached.ApplyReconnectPlan(plan, restartRequest, TabletSession, CompanionProtocolVersion.Current);
        var nextLive = Observe(adopted.Replica, Delivered(1, new CanonicalUpdateMessage(RestartedMarksUpdate(restartedEpoch)), TabletDevice));
        var liveSnapshot = Observe(cached, Delivered(1, new CanonicalSnapshotMessage(restarted)));
        var liveUpdate = Observe(cached, Delivered(1, new CanonicalUpdateMessage(RestartedMarksUpdate(restartedEpoch)), TabletDevice));

        Assert.Equal(ReconnectDisposition.FullSnapshot, plan.Disposition);
        Assert.Equal(0, plan.ResumeAfterDeliverySequence.Value);
        Assert.Equal(ReplicaDisposition.Applied, adopted.Disposition);
        Assert.Equal(restartedEpoch, adopted.Replica.State!.AuthorityEpoch);
        Assert.Equal(0, adopted.Replica.LastDeliverySequence.Value);
        Assert.Equal(ReplicaDisposition.Applied, nextLive.Disposition);
        Assert.Equal(ReplicaDisposition.ResyncRequired, liveSnapshot.Disposition);
        Assert.Equal(5, liveSnapshot.Replica.LastDeliverySequence.Value);
        Assert.Equal(ReplicaDisposition.ResyncRequired, liveUpdate.Disposition);
        Assert.Equal("authority-epoch-changed", liveUpdate.Code);
    }

    private static MarksCanonicalUpdate RestartedMarksUpdate(AuthorityEpoch epoch) => new(
        epoch,
        new GlobalRevision(1),
        Command(900),
        Now,
        TabletOrigin,
        V2ContractVersion.Current,
        new MarkAggregate(new AggregateCursor(new AggregateRevision(1), Command(900)), []));

    private static void AssertStateEqual(CanonicalCompanionState expected, CanonicalCompanionState actual) =>
        AssertJsonEqual(
            JsonSerializer.SerializeToUtf8Bytes(expected, CompanionProtocolJson.Options),
            JsonSerializer.SerializeToUtf8Bytes(actual, CompanionProtocolJson.Options));

    private static ReplicaObservation Observe(CanonicalReplica replica, ServerEnvelope envelope) =>
        replica.Observe(envelope, TabletSession, CompanionProtocolVersion.Current);

    private static ServerEnvelope Delivered(
        long sequence,
        ServerMessage message,
        CompanionDeviceId? authenticatedOriginDeviceId = null) =>
        new(
            CompanionProtocolVersion.Current,
            TabletSession,
            authenticatedOriginDeviceId ?? DesktopDevice,
            Now,
            new DeliverySequence(sequence),
            message);

    private static MarksCanonicalUpdate MarksUpdate(long global, long aggregate, AuthorityEpoch? authorityEpoch = null) => new(
        authorityEpoch ?? Epoch,
        new GlobalRevision(global),
        Command((int)(100 + global)),
        Now,
        TabletOrigin,
        V2ContractVersion.Current,
        new MarkAggregate(new AggregateCursor(new AggregateRevision(aggregate), Command((int)(100 + global))), []));

    private static WorkspaceCanonicalUpdate WorkspaceUpdate(long global, long aggregate) => new(
        Epoch,
        new GlobalRevision(global),
        Command((int)(200 + global)),
        Now,
        TabletOrigin,
        V2ContractVersion.Current,
        new WorkspaceAggregate(new AggregateCursor(new AggregateRevision(aggregate), Command((int)(200 + global))), Projection()));

    private static CanonicalCompanionState StateWithMarksAt(long revision)
    {
        var initial = InitialState();
        return new CanonicalCompanionState(
            initial.AuthorityEpoch,
            initial.WorkspaceId,
            initial.DesktopInstanceId,
            new GlobalRevision(revision),
            initial.DesktopDeviceId,
            initial.DeviceModes,
            initial.Workspace,
            new MarkAggregate(new AggregateCursor(new AggregateRevision(revision), Command((int)(100 + revision))), []),
            initial.CaptureIntent,
            initial.ProfilePreferences);
    }

    /// <summary>
    /// A real reducer flow delivered to the tablet: snapshot (1), mode update (2), its
    /// acknowledgement (3), and two mark updates (4, 5).
    /// </summary>
    private sealed record Flow(
        CanonicalCompanionState Initial,
        CanonicalCompanionState Final,
        IReadOnlyList<CanonicalUpdate> Updates,
        DeliveryLedger Ledger)
    {
        public static Flow Create()
        {
            var initial = InitialState();
            var mode = Apply(initial, SetMode(1, 1, Now, CompanionInteractionMode.Independent), TabletContext());
            var create = Apply(mode.State, Upsert(2, 1, 0, Now), TabletContext());
            var edit = Apply(create.State, Upsert(3, 2, 1, Now, x: 5), TabletContext());
            var ledger = DeliveryLedger.Empty;
            foreach (var message in new ServerMessage[]
                     {
                         new CanonicalSnapshotMessage(initial),
                         new CanonicalUpdateMessage(mode.Update!),
                         new CommandAcknowledgementMessage(mode.Acknowledgement),
                         new CanonicalUpdateMessage(create.Update!),
                         new CanonicalUpdateMessage(edit.Update!),
                     })
            {
                var origin = message switch
                {
                    CanonicalUpdateMessage update => update.Update.Origin.DeviceId,
                    CanonicalSnapshotMessage => DesktopDevice,
                    CommandAcknowledgementMessage => TabletDevice,
                    _ => DesktopDevice,
                };
                ledger = ledger.Enqueue(TabletDevice, origin, message, Now).Ledger;
            }

            return new Flow(initial, edit.State, [mode.Update!, create.Update!, edit.Update!], ledger);
        }

        public ServerEnvelope Envelope(long sequence)
        {
            var item = Ledger.PendingFor(TabletDevice).Single(delivery => delivery.Sequence.Value == sequence);
            return Delivered(sequence, item.Resolve(Final), item.AuthenticatedOriginDeviceId);
        }

        public CanonicalReplica ReplicaThrough(long sequence)
        {
            var replica = CanonicalReplica.Empty;
            for (var index = 1; index <= sequence; index++)
            {
                var observed = Observe(replica, Envelope(index));
                Assert.Equal(ReplicaDisposition.Applied, observed.Disposition);
                replica = observed.Replica;
            }

            return replica;
        }
    }
}
