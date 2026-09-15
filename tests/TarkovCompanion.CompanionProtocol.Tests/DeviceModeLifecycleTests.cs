using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class DeviceModeLifecycleTests
{
    private static readonly ControlLeaseId Lease = new(Guid.Parse("41000000-0000-0000-0000-000000000001"));

    [Fact]
    public void DisconnectingTheLeaseHolderSessionReturnsItToFollow()
    {
        var device = PairedTablet();
        var session = ActiveSession(device, TabletSession);
        var controlled = Controlled(InitialState());
        var ended = DeviceLifecycle.EndSession(session, DeviceSessionStatus.Closed, Now.AddSeconds(5), "transport-disconnected");

        var reduction = DesktopCanonicalStateMachine.ApplySessionTermination(controlled, ended);

        Assert.Equal(CompanionInteractionMode.Follow, reduction.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Null(reduction.State.DeviceModes.ControlLease);
        var update = Assert.IsType<DeviceModeCanonicalUpdate>(Assert.Single(reduction.Updates));
        Assert.Equal(reduction.State.DeviceModes.Cursor.LastChangeId, update.ChangeId);
    }

    [Fact]
    public void DisconnectingAPendingRequesterReleasesTheRequestButAStaleSessionChangesNothing()
    {
        var request = new RequestControlCommand(Command(1), new AggregateRevision(1), Now, Now.AddMinutes(1), TimeSpan.FromMinutes(2));
        var pending = Apply(InitialState(), request, TabletContext()).State;
        var device = PairedTablet();
        var staleSession = DeviceLifecycle.EndSession(
            ActiveSession(device, new DeviceSessionId(Guid.Parse("20000000-0000-0000-0000-000000000077"))),
            DeviceSessionStatus.Expired,
            Now.AddSeconds(1),
            "session-expired");
        var liveSessionEnded = DeviceLifecycle.EndSession(
            ActiveSession(device, TabletSession),
            DeviceSessionStatus.Revoked,
            Now.AddSeconds(2),
            "user-revoked");

        var stale = DesktopCanonicalStateMachine.ApplySessionTermination(pending, staleSession);
        var released = DesktopCanonicalStateMachine.ApplySessionTermination(pending, liveSessionEnded);

        Assert.Same(pending, stale.State);
        Assert.Empty(stale.Updates);
        Assert.Equal(CompanionInteractionMode.Follow, released.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Null(released.State.DeviceModes.PendingControl);
    }

    [Fact]
    public void AnIndependentDeviceStaysIndependentAcrossDisconnect()
    {
        var independent = Apply(InitialState(), SetMode(1, 1, Now, CompanionInteractionMode.Independent), TabletContext()).State;
        var ended = DeviceLifecycle.EndSession(ActiveSession(PairedTablet(), TabletSession), DeviceSessionStatus.Closed, Now.AddSeconds(1), "closed");

        var reduction = DesktopCanonicalStateMachine.ApplySessionTermination(independent, ended);

        Assert.Same(independent, reduction.State);
        Assert.Equal(CompanionInteractionMode.Independent, reduction.State.DeviceModes.ModeOf(TabletDevice));
    }

    [Theory]
    [InlineData(DeviceLifecycleStatus.Revoked)]
    [InlineData(DeviceLifecycleStatus.Expired)]
    [InlineData(DeviceLifecycleStatus.Replaced)]
    public void ATerminatedDeviceLeavesTheModeTableAndRelinquishesControl(DeviceLifecycleStatus status)
    {
        var device = PairedTablet();
        var controlled = Controlled(InitialState());
        var at = Now.Add(ProtocolBounds.DeviceInactivityExpiry);
        var terminated = status switch
        {
            DeviceLifecycleStatus.Revoked => DeviceLifecycle.Revoke(device, at, "user-revoked"),
            DeviceLifecycleStatus.Expired => DeviceLifecycle.Expire(device, at),
            _ => DeviceLifecycle.Replace(device, OtherDevice, at, "re-paired"),
        };

        var reduction = DesktopCanonicalStateMachine.ApplyDeviceTermination(controlled, terminated);

        Assert.DoesNotContain(reduction.State.DeviceModes.Devices, entry => entry.DeviceId == TabletDevice);
        Assert.Equal(CompanionInteractionMode.Follow, reduction.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Null(reduction.State.DeviceModes.ControlLease);
        Assert.Single(reduction.Updates);
    }

    [Fact]
    public void MaintenanceEnforcesFollowWhenTheTransportNeverReportedTheDisconnect()
    {
        var device = PairedTablet();
        var other = PairedTablet(OtherDevice);
        var controlled = Controlled(InitialState());
        var otherIndependent = Apply(controlled, SetMode(9, 3, Now, CompanionInteractionMode.Independent), OtherContext()).State;
        var at = Now.AddSeconds(30);

        var sessionGone = DesktopCanonicalStateMachine.ApplyMaintenance(otherIndependent, at, [device, other], []);
        var deviceRevoked = DesktopCanonicalStateMachine.ApplyMaintenance(
            otherIndependent,
            at,
            [DeviceLifecycle.Revoke(device, at, "user-revoked"), other],
            [ActiveSession(device, TabletSession), ActiveSession(other, OtherSession)]);
        var allLive = DesktopCanonicalStateMachine.ApplyMaintenance(
            otherIndependent,
            at,
            [device, other],
            [ActiveSession(device, TabletSession), ActiveSession(other, OtherSession)]);

        Assert.Equal(CompanionInteractionMode.Follow, sessionGone.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Contains(sessionGone.State.DeviceModes.Devices, entry => entry.DeviceId == TabletDevice);
        Assert.Equal(CompanionInteractionMode.Independent, sessionGone.State.DeviceModes.ModeOf(OtherDevice));
        Assert.Null(sessionGone.State.DeviceModes.ControlLease);

        Assert.DoesNotContain(deviceRevoked.State.DeviceModes.Devices, entry => entry.DeviceId == TabletDevice);
        Assert.Null(deviceRevoked.State.DeviceModes.ControlLease);

        Assert.Equal(CompanionInteractionMode.Control, allLive.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Empty(allLive.Updates);
    }

    [Fact]
    public void AnExpiredLeaseOrRequestReturnsToFollowDuringMaintenance()
    {
        var device = PairedTablet();
        var sessions = new[] { ActiveSession(device, TabletSession) };
        var controlled = Controlled(InitialState());
        var afterLease = DesktopCanonicalStateMachine.ApplyMaintenance(controlled, Now.AddMinutes(3), [device], sessions);

        var request = new RequestControlCommand(Command(20), new AggregateRevision(1), Now, Now.AddSeconds(30), TimeSpan.FromMinutes(2));
        var pending = Apply(InitialState(), request, TabletContext()).State;
        var afterRequest = DesktopCanonicalStateMachine.ApplyMaintenance(pending, Now.AddMinutes(1), [device], sessions);

        Assert.Equal(CompanionInteractionMode.Follow, afterLease.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Null(afterLease.State.DeviceModes.ControlLease);
        Assert.Equal(CompanionInteractionMode.Follow, afterRequest.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Null(afterRequest.State.DeviceModes.PendingControl);
    }

    [Fact]
    public void AuthenticatedContextIsBuiltOnlyFromALiveKeyBoundSession()
    {
        var device = PairedTablet(capabilities: [DeviceCapability.FollowDesktop, DeviceCapability.RequestControl]);
        var session = new DeviceSession(
            ActiveSession(device, TabletSession).Establishment,
            DeviceSessionStatus.Active,
            CompanionTransportKind.DirectLan,
            CompanionSurfaceKind.TabletPortrait,
            [DeviceCapability.FollowDesktop, DeviceCapability.ManageDevices],
            Now);

        var context = AuthenticatedCommandContext.ForPairedSession(device, session, Now.AddSeconds(1));
        var ended = DeviceLifecycle.EndSession(session, DeviceSessionStatus.Closed, Now.AddSeconds(2), "closed");

        Assert.Equal(new[] { DeviceCapability.FollowDesktop }, context.Capabilities);
        Assert.False(context.IsDesktop);
        Assert.Throws<UnauthorizedAccessException>(() => AuthenticatedCommandContext.ForPairedSession(device, ended, Now.AddSeconds(3)));
        Assert.Throws<UnauthorizedAccessException>(() => AuthenticatedCommandContext.ForPairedSession(
            DeviceLifecycle.Revoke(device, Now.AddSeconds(3), "user-revoked"),
            session,
            Now.AddSeconds(3)));
    }

    [Fact]
    public void DeviceAndSessionTerminalStatesAreExplicitAndFinal()
    {
        var device = PairedTablet();
        var revoked = DeviceLifecycle.Revoke(device, Now.AddMinutes(1), "user-revoked");
        var session = ActiveSession(device, TabletSession);
        var closed = DeviceLifecycle.EndSession(session, DeviceSessionStatus.Replaced, Now.AddMinutes(1), "session-resumed");

        Assert.Equal(DeviceLifecycleStatus.Revoked, revoked.Status);
        Assert.Throws<InvalidOperationException>(() => DeviceLifecycle.Expire(revoked, Now.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => DeviceLifecycle.Expire(device, Now.AddMinutes(1)));
        Assert.Equal(DeviceLifecycleStatus.Expired, DeviceLifecycle.Expire(device, Now.Add(ProtocolBounds.DeviceInactivityExpiry)).Status);
        Assert.Throws<InvalidOperationException>(() => DeviceLifecycle.EndSession(closed, DeviceSessionStatus.Closed, Now.AddMinutes(2), "again"));
        Assert.False(DeviceLifecycle.IsLive(revoked, Now.AddMinutes(1)));
        Assert.False(DeviceLifecycle.IsLive(closed, device, Now.AddMinutes(1)));
    }

    [Fact]
    public void ModeAggregateRejectsControlOrPendingModesWithoutTheirLeaseOrRequest()
    {
        Assert.Throws<ArgumentException>(() => new DeviceModeAggregate(
            AggregateCursor.Empty,
            [new DeviceModeEntry(TabletDevice, CompanionInteractionMode.Control, Now)],
            null,
            null));
        Assert.Throws<ArgumentException>(() => new DeviceModeAggregate(
            AggregateCursor.Empty,
            [new DeviceModeEntry(TabletDevice, CompanionInteractionMode.ControlPending, Now)],
            null,
            null));
    }

    private static CanonicalCompanionState Controlled(CanonicalCompanionState state)
    {
        var request = new RequestControlCommand(Command(1), new AggregateRevision(1), Now, Now.AddMinutes(1), TimeSpan.FromMinutes(2));
        var pending = Apply(state, request, TabletContext()).State;
        var granted = DesktopCanonicalStateMachine.Apply(
            pending,
            Envelope(new ResolveControlCommand(Command(2), new AggregateRevision(2), Now, Now.AddMinutes(1), request.CommandId, true, Lease), DesktopSession),
            DesktopContext());
        Assert.Equal(CompanionInteractionMode.Control, granted.State.DeviceModes.ModeOf(TabletDevice));
        return granted.State;
    }
}
