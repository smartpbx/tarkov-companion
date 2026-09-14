namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class DeliveryAndReconnectTests
{
    [Fact]
    public void SlowAggregateAndDeviceDoNotBlockOtherChannels()
    {
        var ledger = DeliveryLedger.Empty;
        for (var index = 1; index <= ProtocolBounds.MaxDeliveryItemsPerAggregate + 1; index++)
        {
            ledger = ledger.Enqueue(
                ProtocolTestData.TabletDevice,
                MarksUpdate(index, index)).Ledger;
        }

        var slow = Assert.Single(
            ledger.Channels,
            channel => channel.Key.DeviceId == ProtocolTestData.TabletDevice &&
                       channel.Key.Aggregate == CanonicalAggregateKind.Marks);
        Assert.Single(slow.Pending);
        Assert.True(slow.Pending[0].SnapshotRequired);

        var workspace = ledger.Enqueue(
            ProtocolTestData.TabletDevice,
            WorkspaceUpdate(ProtocolBounds.MaxDeliveryItemsPerAggregate + 2, 1));
        var other = workspace.Ledger.Enqueue(ProtocolTestData.OtherDevice, MarksUpdate(1, 1));

        Assert.False(workspace.Item.SnapshotRequired);
        Assert.False(other.Item.SnapshotRequired);
        Assert.Single(other.Ledger.PendingFor(ProtocolTestData.OtherDevice));
    }

    [Fact]
    public void AcknowledgementIsChannelLocalAndStaleAcknowledgementIsIgnored()
    {
        var first = DeliveryLedger.Empty.Enqueue(ProtocolTestData.TabletDevice, MarksUpdate(1, 1));
        var second = first.Ledger.Enqueue(ProtocolTestData.TabletDevice, WorkspaceUpdate(2, 1));
        var acknowledged = second.Ledger.Acknowledge(
            ProtocolTestData.TabletDevice,
            CanonicalAggregateKind.Marks,
            first.Item.Sequence);
        var replayedStale = acknowledged.Acknowledge(
            ProtocolTestData.TabletDevice,
            CanonicalAggregateKind.Marks,
            new DeliverySequence(0));

        Assert.Single(acknowledged.PendingFor(ProtocolTestData.TabletDevice));
        Assert.Same(acknowledged, replayedStale);
        Assert.Equal(CanonicalAggregateKind.Workspace, acknowledged.PendingFor(ProtocolTestData.TabletDevice)[0].Update!.Aggregate);
    }

    [Fact]
    public void ReconnectReplaysOnlyWhenEpochDeliveryAndGlobalSequenceAreProvable()
    {
        var initial = ProtocolTestData.InitialState();
        var firstCommand = new UpsertMarkCommand(
            ProtocolTestData.Command(70),
            new AggregateRevision(1),
            ProtocolTestData.Now,
            ProtocolTestData.Now.AddMinutes(1),
            ProtocolTestData.Mark(70),
            0,
            new MapMarkDraft(
                MapMarkKind.Waypoint,
                MapMarkScope.PairedDevice,
                new MapCoordinate("customs", null, CoordinateSpaceKind.World, "v1", 1, 2, 3),
                null,
                "#00AACC",
                null));
        var first = DesktopCanonicalStateMachine.Apply(
            initial,
            ProtocolTestData.Envelope(firstCommand),
            ProtocolTestData.TabletContext());
        var modeAt = ProtocolTestData.Now.AddSeconds(1);
        var secondCommand = new SetInteractionModeCommand(
            ProtocolTestData.Command(71),
            new AggregateRevision(1),
            modeAt,
            modeAt.AddMinutes(1),
            CompanionInteractionMode.Independent);
        var second = DesktopCanonicalStateMachine.Apply(
            first.State,
            ProtocolTestData.Envelope(secondCommand),
            ProtocolTestData.TabletContext(modeAt));

        var deliveries = new[]
        {
            new DeliveredCanonicalUpdate(new DeliverySequence(1), first.Update!),
            new DeliveredCanonicalUpdate(new DeliverySequence(2), second.Update!),
        };
        var request = new ReconnectRequest(
            CompanionProtocolVersion.Current,
            ProtocolTestData.TabletSession,
            initial.AuthorityEpoch,
            initial.GlobalRevision,
            new DeliverySequence(0),
            []);

        var replay = ReconnectPlanner.Plan(second.State, request, deliveries);
        var missing = ReconnectPlanner.Plan(second.State, request, deliveries.Skip(1).ToArray());
        var outOfOrder = ReconnectPlanner.Plan(
            second.State,
            request,
            [
                new DeliveredCanonicalUpdate(new DeliverySequence(1), second.Update!),
                new DeliveredCanonicalUpdate(new DeliverySequence(2), first.Update!),
            ]);
        var staleEpoch = ReconnectPlanner.Plan(
            first.State,
            request,
            [
                new DeliveredCanonicalUpdate(
                    new DeliverySequence(1),
                    MarksUpdate(
                        1,
                        1,
                        new AuthorityEpoch(Guid.Parse("30000000-0000-0000-0000-000000000099")))),
            ]);
        var wrongEpoch = ReconnectPlanner.Plan(
            second.State,
            new ReconnectRequest(
                CompanionProtocolVersion.Current,
                ProtocolTestData.TabletSession,
                new AuthorityEpoch(Guid.NewGuid()),
                initial.GlobalRevision,
                new DeliverySequence(0),
                []),
            deliveries);

        Assert.Equal(ReconnectDisposition.Replay, replay.Disposition);
        Assert.Equal(2, replay.Replay.Count);
        Assert.Equal(ReconnectDisposition.FullSnapshot, missing.Disposition);
        Assert.Equal(ReconnectDisposition.FullSnapshot, outOfOrder.Disposition);
        Assert.Equal(ReconnectDisposition.FullSnapshot, staleEpoch.Disposition);
        Assert.Equal(ReconnectDisposition.FullSnapshot, wrongEpoch.Disposition);
    }

    private static MarksCanonicalUpdate MarksUpdate(
        long global,
        long aggregate,
        AuthorityEpoch? authorityEpoch = null) => new(
        authorityEpoch ?? ProtocolTestData.Epoch,
        new GlobalRevision(global),
        ProtocolTestData.Command((int)(100 + global)),
        ProtocolTestData.Now,
        new MarkAggregate(
            new AggregateCursor(
                new AggregateRevision(aggregate),
                ProtocolTestData.Command((int)(100 + global))),
            []));

    private static WorkspaceCanonicalUpdate WorkspaceUpdate(long global, long aggregate) => new(
        ProtocolTestData.Epoch,
        new GlobalRevision(global),
        ProtocolTestData.Command((int)(200 + global)),
        ProtocolTestData.Now,
        new WorkspaceAggregate(
            new AggregateCursor(
                new AggregateRevision(aggregate),
                ProtocolTestData.Command((int)(200 + global))),
            ProtocolTestData.Projection()));
}
