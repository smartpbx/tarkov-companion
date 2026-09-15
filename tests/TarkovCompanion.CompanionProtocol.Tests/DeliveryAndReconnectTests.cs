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
            ledger = ledger.Enqueue(TabletDevice, new CanonicalUpdateMessage(MarksUpdate(index, index)), Now).Ledger;
        }

        var marker = Assert.Single(ledger.PendingFor(TabletDevice), item => item.Channel == DeliveryChannel.Marks);
        var workspace = ledger.Enqueue(TabletDevice, new CanonicalUpdateMessage(WorkspaceUpdate(70, 1)), Now);
        var other = workspace.Ledger.Enqueue(OtherDevice, new CanonicalUpdateMessage(MarksUpdate(1, 1)), Now);

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
    public void AcknowledgementCoversTheDeviceStreamAndOldAcknowledgementsAreIgnored()
    {
        var flow = Flow.Create();
        var acknowledged = flow.Ledger.Acknowledge(TabletDevice, new DeliverySequence(3));
        var replayedOld = acknowledged.Acknowledge(TabletDevice, new DeliverySequence(2));
        var beyond = acknowledged.Acknowledge(TabletDevice, new DeliverySequence(99));

        Assert.Equal(new long[] { 4, 5 }, acknowledged.PendingFor(TabletDevice).Select(item => item.Sequence.Value));
        Assert.Same(acknowledged, replayedOld);
        Assert.Empty(beyond.PendingFor(TabletDevice));
        Assert.Equal(5, beyond.For(TabletDevice)!.LastAcknowledged.Value);
    }

    [Fact]
    public void AClientAcknowledgementFromAnotherEpochOrDivergentStateIsRefused()
    {
        var flow = Flow.Create();
        var replica = flow.ReplicaThrough(5);
        var acknowledgement = replica.CreateDeliveryAcknowledgement(CompanionProtocolVersion.Current, TabletSession, Now)!;
        var accepted = flow.Ledger.Acknowledge(TabletDevice, acknowledgement, flow.Final);
        var otherEpoch = flow.Ledger.Acknowledge(
            TabletDevice,
            new ClientDeliveryAcknowledgement(
                CompanionProtocolVersion.Current,
                TabletSession,
                new AuthorityEpoch(Guid.Parse("30000000-0000-0000-0000-000000000099")),
                new DeliverySequence(5),
                flow.Final.GlobalRevision,
                []),
            flow.Final);
        var divergent = flow.Ledger.Acknowledge(
            TabletDevice,
            new ClientDeliveryAcknowledgement(
                CompanionProtocolVersion.Current,
                TabletSession,
                Epoch,
                new DeliverySequence(5),
                flow.Final.GlobalRevision,
                [new AggregateAcknowledgement(CanonicalAggregateKind.Marks, flow.Final.Marks.Cursor.Revision, Command(777), Now)]),
            flow.Final);

        Assert.True(accepted.Accepted);
        Assert.Empty(accepted.Ledger.PendingFor(TabletDevice));
        Assert.False(otherEpoch.Accepted);
        Assert.Equal("authority-epoch-mismatch", otherEpoch.Code);
        Assert.False(divergent.Accepted);
        Assert.Same(flow.Ledger, divergent.Ledger);
    }

    [Fact]
    public void TheReplicaAppliesContiguousDeltasAndDemandsResyncAfterAGap()
    {
        var flow = Flow.Create();
        var replica = flow.ReplicaThrough(2);

        var gap = replica.Observe(flow.Envelope(4));
        var discarded = gap.Replica.Observe(flow.Envelope(5));
        var duplicate = replica.Observe(flow.Envelope(2));
        var resynced = discarded.Replica.Observe(Delivered(6, new CanonicalSnapshotMessage(flow.Final)));
        var alreadyReflected = resynced.Replica.Observe(Delivered(7, new CanonicalUpdateMessage(flow.Updates[^1])));

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
        var otherEpoch = replica.Observe(Delivered(2, new CanonicalUpdateMessage(MarksUpdate(1, 1, new AuthorityEpoch(Guid.Parse("30000000-0000-0000-0000-000000000099"))))));
        var globalGap = replica.Observe(Delivered(2, new CanonicalUpdateMessage(MarksUpdate(3, 1))));
        var aggregateGap = replica.Observe(Delivered(2, new CanonicalUpdateMessage(MarksUpdate(1, 2))));

        Assert.Equal("authority-epoch-changed", otherEpoch.Code);
        Assert.Equal("global-revision-gap", globalGap.Code);
        Assert.Equal("aggregate-revision-gap", aggregateGap.Code);
        Assert.All(new[] { otherEpoch, globalGap, aggregateGap }, observation => Assert.Equal(ReplicaDisposition.ResyncRequired, observation.Disposition));
    }

    [Fact]
    public void ReconnectReplaysOnlyAProvablyContiguousStream()
    {
        var flow = Flow.Create();
        var replica = flow.ReplicaThrough(2);
        var request = replica.CreateReconnectRequest(CompanionProtocolVersion.Current, TabletSession, Now);

        var replay = ReconnectPlanner.Plan(flow.Final, request, flow.Ledger, TabletDevice, CompanionProtocolVersion.Current);
        var wirePlan = CompanionProtocolJson.Deserialize<ReconnectPlan>(CompanionProtocolJson.Serialize(replay.Plan));
        var applied = replica.ApplyReconnectPlan(wirePlan);

        Assert.Equal(ReconnectDisposition.Replay, replay.Plan.Disposition);
        Assert.Equal(CompanionProtocolVersion.Current, replay.Plan.ProtocolVersion);
        Assert.Equal(new long[] { 3, 4, 5 }, replay.Plan.Replay.Select(item => item.DeliverySequence.Value));
        Assert.Equal(5, replay.Plan.ResumeAfterDeliverySequence.Value);
        Assert.Empty(replay.Ledger.PendingFor(TabletDevice));
        Assert.Equal(ReplicaDisposition.Applied, applied.Disposition);
        AssertStateEqual(flow.Final, applied.Replica.State!);

        var history = ReconnectPlanner.Plan(flow.Final, request, flow.Ledger.Acknowledge(TabletDevice, new DeliverySequence(4)), TabletDevice, CompanionProtocolVersion.Current);
        var epoch = ReconnectPlanner.Plan(
            flow.Final,
            new ReconnectRequest(CompanionProtocolVersion.Current, TabletSession, new AuthorityEpoch(Guid.Parse("30000000-0000-0000-0000-000000000099")), new GlobalRevision(1), new DeliverySequence(2), []),
            flow.Ledger,
            TabletDevice,
            CompanionProtocolVersion.Current);
        var current = ReconnectPlanner.Plan(flow.Final, flow.ReplicaThrough(5).CreateReconnectRequest(CompanionProtocolVersion.Current, TabletSession, Now), flow.Ledger, TabletDevice, CompanionProtocolVersion.Current);
        var unsupported = ReconnectPlanner.Plan(
            flow.Final,
            new ReconnectRequest(new CompanionProtocolVersion(3, 0), TabletSession, Epoch, new GlobalRevision(1), new DeliverySequence(2), []),
            flow.Ledger,
            TabletDevice,
            CompanionProtocolVersion.Current);
        var reloaded = ReconnectPlanner.Plan(
            flow.Final,
            CanonicalReplica.Empty.CreateReconnectRequest(CompanionProtocolVersion.Current, TabletSession, Now),
            flow.Ledger,
            TabletDevice,
            CompanionProtocolVersion.Current);

        Assert.Equal("delivery-history-unavailable", history.Plan.Reason);
        Assert.Equal(ReconnectDisposition.FullSnapshot, history.Plan.Disposition);
        Assert.Equal(ReconnectDisposition.FullSnapshot, epoch.Plan.Disposition);
        Assert.Equal(ReconnectDisposition.UpToDate, current.Plan.Disposition);
        Assert.Empty(current.Ledger.PendingFor(TabletDevice));
        Assert.Equal(ReconnectDisposition.UnsupportedVersion, unsupported.Plan.Disposition);
        Assert.Same(flow.Ledger, unsupported.Ledger);
        Assert.Equal(ReconnectDisposition.FullSnapshot, reloaded.Plan.Disposition);
        var fromEmpty = CanonicalReplica.Empty.ApplyReconnectPlan(reloaded.Plan);
        Assert.Equal(5, fromEmpty.Replica.LastDeliverySequence.Value);
        AssertStateEqual(flow.Final, fromEmpty.Replica.State!);
    }

    [Fact]
    public void ACoalescedMarkerInTheReplayRangeForcesASnapshot()
    {
        var ledger = DeliveryLedger.Empty;
        for (var index = 1; index <= ProtocolBounds.MaxDeliveryItemsPerChannel + 1; index++)
        {
            ledger = ledger.Enqueue(TabletDevice, new CanonicalUpdateMessage(MarksUpdate(index, index)), Now).Ledger;
        }

        var state = StateWithMarksAt(ProtocolBounds.MaxDeliveryItemsPerChannel + 1);
        var plan = ReconnectPlanner.Plan(
            state,
            new ReconnectRequest(CompanionProtocolVersion.Current, TabletSession, Epoch, new GlobalRevision(0), new DeliverySequence(0), []),
            ledger,
            TabletDevice,
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
            ledger = ledger.Enqueue(TabletDevice, new CanonicalUpdateMessage(update), Now).Ledger;
        }

        var plan = ReconnectPlanner.Plan(
            state,
            new ReconnectRequest(CompanionProtocolVersion.Current, TabletSession, Epoch, new GlobalRevision(state.GlobalRevision.Value - 3), new DeliverySequence(0), []),
            ledger,
            TabletDevice,
            CompanionProtocolVersion.Current);

        Assert.Equal(ReconnectDisposition.FullSnapshot, plan.Plan.Disposition);
        Assert.Equal("replay-exceeds-payload-bound", plan.Plan.Reason);
        Assert.InRange(CompanionProtocolJson.Serialize(plan.Plan).Length, 1, ProtocolBounds.MaxPayloadBytes);
    }

    [Fact]
    public void ALateOrNonContinuingReconnectPlanIsNeverApplied()
    {
        var flow = Flow.Create();
        var replay = ReconnectPlanner.Plan(
            flow.Final,
            flow.ReplicaThrough(2).CreateReconnectRequest(CompanionProtocolVersion.Current, TabletSession, Now),
            flow.Ledger,
            TabletDevice,
            CompanionProtocolVersion.Current).Plan;
        var advanced = flow.ReplicaThrough(5);
        var staleSnapshot = new ReconnectPlan(CompanionProtocolVersion.Current, ReconnectDisposition.FullSnapshot, [], flow.Initial, new DeliverySequence(2), "authority-or-cursor-mismatch");

        var late = advanced.ApplyReconnectPlan(staleSnapshot);
        var overlapping = flow.ReplicaThrough(4).ApplyReconnectPlan(replay);
        var afterGap = flow.ReplicaThrough(1).ApplyReconnectPlan(replay);
        var upToDateElsewhere = flow.ReplicaThrough(2).ApplyReconnectPlan(
            new ReconnectPlan(CompanionProtocolVersion.Current, ReconnectDisposition.UpToDate, [], null, new DeliverySequence(5), "already-current"));

        Assert.Equal(ReplicaDisposition.Discarded, late.Disposition);
        Assert.Equal("stale-reconnect-plan", late.Code);
        Assert.Same(advanced, late.Replica);
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
            flow.Final.DeviceModes,
            new WorkspaceAggregate(AggregateCursor.Empty, Projection()),
            new MarkAggregate(AggregateCursor.Empty, []),
            new CaptureIntentAggregate(AggregateCursor.Empty, null));

        // After a desktop restart the new ledger has assigned nothing, so the plan resumes at zero.
        var plan = ReconnectPlanner.Plan(
            restarted,
            cached.CreateReconnectRequest(CompanionProtocolVersion.Current, TabletSession, Now),
            DeliveryLedger.Empty,
            TabletDevice,
            CompanionProtocolVersion.Current).Plan;
        var adopted = cached.ApplyReconnectPlan(plan);
        var nextLive = adopted.Replica.Observe(Delivered(1, new CanonicalUpdateMessage(RestartedMarksUpdate(restartedEpoch))));
        var liveSnapshot = cached.Observe(Delivered(1, new CanonicalSnapshotMessage(restarted)));
        var liveUpdate = cached.Observe(Delivered(1, new CanonicalUpdateMessage(RestartedMarksUpdate(restartedEpoch))));

        Assert.Equal(ReconnectDisposition.FullSnapshot, plan.Disposition);
        Assert.Equal(0, plan.ResumeAfterDeliverySequence.Value);
        Assert.Equal(ReplicaDisposition.Applied, adopted.Disposition);
        Assert.Equal(restartedEpoch, adopted.Replica.State!.AuthorityEpoch);
        Assert.Equal(0, adopted.Replica.LastDeliverySequence.Value);
        Assert.Equal(ReplicaDisposition.Applied, nextLive.Disposition);
        Assert.Equal(ReplicaDisposition.Applied, liveSnapshot.Disposition);
        Assert.Equal(1, liveSnapshot.Replica.LastDeliverySequence.Value);
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

    private static ServerEnvelope Delivered(long sequence, ServerMessage message) =>
        new(CompanionProtocolVersion.Current, TabletSession, DesktopDevice, Now, new DeliverySequence(sequence), message);

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
            initial.CaptureIntent);
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
                ledger = ledger.Enqueue(TabletDevice, message, Now).Ledger;
            }

            return new Flow(initial, edit.State, [mode.Update!, create.Update!, edit.Update!], ledger);
        }

        public ServerEnvelope Envelope(long sequence)
        {
            var item = Ledger.PendingFor(TabletDevice).Single(delivery => delivery.Sequence.Value == sequence);
            return Delivered(sequence, item.Resolve(Final));
        }

        public CanonicalReplica ReplicaThrough(long sequence)
        {
            var replica = CanonicalReplica.Empty;
            for (var index = 1; index <= sequence; index++)
            {
                var observed = replica.Observe(Envelope(index));
                Assert.Equal(ReplicaDisposition.Applied, observed.Disposition);
                replica = observed.Replica;
            }

            return replica;
        }
    }
}
