using System.Buffers.Text;
using System.Security.Cryptography;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.UnitTests.TabletSurface;

/// <summary>
/// Follow, Control and Independent, end to end through the one canonical authority (#407).
/// </summary>
/// <remarks>
/// The reducer has always had the desktop half of this — <c>UpdateDesktopWorkspaceCommand</c>,
/// <c>ResolveControlCommand</c> and <c>PreemptControlCommand</c> are all refused unless the caller
/// is the canonical desktop — and nothing in the running application could build a context that
/// satisfied it, so none of it was reachable: a tablet could not follow the desktop's map, and a
/// desktop could neither grant control nor take it back. These drive the whole round trip through
/// <see cref="DesktopCompanionAuthority.ApplyDesktopCommandAsync"/>, which is what closed it.
/// </remarks>
public sealed class PairedControlRoundTripTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 1, 0, 0, TimeSpan.Zero);
    private static readonly CompanionDeviceId DesktopDevice = new(Guid.Parse("10000000-0000-4000-8000-000000000011"));
    private static readonly CompanionDeviceId TabletDevice = new(Guid.Parse("10000000-0000-4000-8000-000000000012"));
    private static readonly DeviceSessionId TabletSession = new(Guid.Parse("20000000-0000-4000-8000-000000000012"));
    private static readonly AuthorityEpoch Epoch = new(Guid.Parse("30000000-0000-4000-8000-000000000011"));
    private static readonly WorkspaceId Workspace = new(Guid.Parse("80000000-0000-4000-8000-000000000011"));

    [Fact]
    public async Task ATabletTakesControlOnlyWhenTheDesktopGrantsItAndTheDesktopCanTakeItBack()
    {
        using var authority = await OpenAsync();

        // Before the lease exists, the same command the tablet will later be allowed to send is
        // refused: Control is not a mode a device can simply declare.
        var beforeLease = await authority.ApplyCommandAsync(Frame(), Envelope(Navigate(1, 640)));
        var requested = await authority.ApplyCommandAsync(Frame(), Envelope(RequestControl(2)));
        var pending = requested.State.CanonicalState.DeviceModes.PendingControl;

        var granted = await authority.ApplyDesktopCommandAsync(
            new ResolveControlCommand(
                Command(3),
                new AggregateRevision(requested.State.CanonicalState.DeviceModes.Cursor.Revision.Value + 1),
                Now,
                Now.AddMinutes(1),
                pending!.RequestCommandId,
                approved: true,
                new ControlLeaseId(Guid.Parse("60000000-0000-4000-8000-000000000001"))),
            Now);

        var drove = await authority.ApplyCommandAsync(
            Frame(),
            Envelope(Navigate(granted.State.CanonicalState.Workspace.Cursor.Revision.Value + 1, 640)));

        var takenBack = await authority.ApplyDesktopCommandAsync(
            new PreemptControlCommand(
                Command(5),
                new AggregateRevision(drove.State.CanonicalState.DeviceModes.Cursor.Revision.Value + 1),
                Now,
                Now.AddMinutes(1),
                "desktop-took-control-back"),
            Now);

        Assert.Equal(CommandDisposition.RejectedUnauthorized, beforeLease.Acknowledgement.Disposition);
        Assert.Equal(CompanionInteractionMode.ControlPending, requested.State.CanonicalState.DeviceModes.ModeOf(TabletDevice));
        Assert.Equal(CompanionInteractionMode.Control, granted.State.CanonicalState.DeviceModes.ModeOf(TabletDevice));
        Assert.Equal(CommandDisposition.Applied, drove.Acknowledgement.Disposition);
        Assert.Equal(640, drove.State.CanonicalState.Workspace.Projection.Viewport!.Center.X);
        // One action ends it, and the device is back to following rather than left in limbo.
        Assert.Null(takenBack.State.CanonicalState.DeviceModes.ControlLease);
        Assert.Equal(CompanionInteractionMode.Follow, takenBack.State.CanonicalState.DeviceModes.ModeOf(TabletDevice));
    }

    [Fact]
    public async Task AControlChangeComesBackNamingTheTabletThatCausedIt()
    {
        // This is the input to the tablet page's echo suppression: a workspace update whose origin
        // is this device must not be re-applied as a follow update, or it would fight the pan the
        // person is still making. The broadcast has to carry that attribution for it to work.
        using var authority = await OpenAsync();
        var requested = await authority.ApplyCommandAsync(Frame(), Envelope(RequestControl(11)));
        await authority.ApplyDesktopCommandAsync(
            new ResolveControlCommand(
                Command(12),
                new AggregateRevision(requested.State.CanonicalState.DeviceModes.Cursor.Revision.Value + 1),
                Now,
                Now.AddMinutes(1),
                requested.State.CanonicalState.DeviceModes.PendingControl!.RequestCommandId,
                approved: true,
                new ControlLeaseId(Guid.Parse("60000000-0000-4000-8000-000000000002"))),
            Now);

        var drove = await authority.ApplyCommandAsync(
            Frame(),
            Envelope(Navigate(authority.Snapshot.CanonicalState.Workspace.Cursor.Revision.Value + 1, 900)));

        var update = drove.Deliveries
            .Select(delivery => delivery.Item.Message)
            .OfType<CanonicalUpdateMessage>()
            .Select(message => message.Update)
            .OfType<WorkspaceCanonicalUpdate>()
            .Single();
        Assert.Equal(TabletDevice, update.Origin.DeviceId);
        Assert.Equal(WorkspaceOriginKind.PairedDevice, update.Origin.Kind);
    }

    [Fact]
    public async Task TheDesktopsOwnNavigationReachesAFollowingTablet()
    {
        // Follow is only meaningful if the desktop's map reaches canonical state. It is the same
        // revisioned command and the same delivery stream a tablet's own change uses.
        using var authority = await OpenAsync();

        var moved = await authority.ApplyDesktopCommandAsync(
            new UpdateDesktopWorkspaceCommand(
                Command(21),
                new AggregateRevision(1),
                Now,
                Now.AddMinutes(1),
                Projection(1280)),
            Now);

        var update = Assert.IsType<WorkspaceCanonicalUpdate>(
            Assert.IsType<CanonicalUpdateMessage>(Assert.Single(moved.Deliveries).Item.Message).Update);
        Assert.Equal(CommandDisposition.Applied, moved.Acknowledgement.Disposition);
        Assert.Equal(1280, update.State.Projection.Viewport!.Center.X);
        Assert.Equal(DesktopDevice, update.Origin.DeviceId);
        Assert.Equal(WorkspaceOriginKind.DesktopApplication, update.Origin.Kind);
        Assert.Equal(TabletDevice, Assert.Single(moved.Deliveries).DeviceId);
    }

    [Fact]
    public async Task ATabletCannotIssueTheDesktopsOwnCommands()
    {
        // The reverse of the above: the desktop-origin context is not something a paired session
        // can borrow by sending the same command type over the transport.
        using var authority = await OpenAsync();

        var refused = await authority.ApplyCommandAsync(
            Frame(),
            Envelope(new UpdateDesktopWorkspaceCommand(
                Command(31),
                new AggregateRevision(1),
                Now,
                Now.AddMinutes(1),
                Projection(64))));

        Assert.Equal(CommandDisposition.RejectedUnauthorized, refused.Acknowledgement.Disposition);
    }

    private static ControlWorkspaceCommand Navigate(long revision, double centerX) => new(
        Command((int)(revision + 100)),
        new AggregateRevision(revision),
        Now,
        Now.AddMinutes(1),
        new NavigateWorkspaceAction(
            WorkspaceKind.Raid,
            "customs",
            "ground",
            new WorkspaceViewport(
                new MapCoordinate("customs", "ground", CoordinateSpaceKind.World, "tarkov-dev-1", centerX, null, 20),
                2)));

    private static RequestControlCommand RequestControl(int number) => new(
        Command(number),
        new AggregateRevision(1),
        Now,
        Now.AddMinutes(1),
        TimeSpan.FromMinutes(2));

    private static async ValueTask<DesktopCompanionAuthority> OpenAsync()
    {
        var device = PairedTablet();
        var seeded = new DesktopCompanionAuthorityState(
            InitialState(),
            [device],
            [ActiveSession(device)],
            DeliveryLedger.Empty);
        return await DesktopCompanionAuthority.OpenAsync(new MemoryStore(seeded), seeded.CanonicalState);
    }

    private static CanonicalCompanionState InitialState() => new(
        Epoch,
        Workspace,
        "desktop-install-24",
        new GlobalRevision(0),
        DesktopDevice,
        new DeviceModeAggregate(
            AggregateCursor.Empty,
            [new DeviceModeEntry(TabletDevice, CompanionInteractionMode.Follow, Now)],
            null,
            null),
        new WorkspaceAggregate(AggregateCursor.Empty, Projection(10)),
        new MarkAggregate(AggregateCursor.Empty, []),
        new CaptureIntentAggregate(AggregateCursor.Empty, null),
        ProfilePreferencesAggregate.Empty);

    private static WorkspaceProjection Projection(double centerX) => new(
        WorkspaceKind.Raid,
        "customs",
        "ground",
        new WorkspaceViewport(
            new MapCoordinate("customs", "ground", CoordinateSpaceKind.World, "tarkov-dev-1", centerX, null, 20),
            1),
        null,
        [],
        [],
        null,
        [],
        ["extracts"],
        [],
        null);

    private static PairedDevice PairedTablet() => new(
        TabletDevice,
        "Raid tablet",
        DeviceKey(),
        DeviceAuthorizationRole.Member,
        [
            DeviceCapability.FollowDesktop,
            DeviceCapability.RequestControl,
            DeviceCapability.ShowOnDesktop,
            DeviceCapability.ManageOwnMarks,
        ],
        DeviceLifecycleStatus.Active,
        Now,
        Now,
        1,
        Now.AddDays(30),
        Now);

    private static DeviceSession ActiveSession(PairedDevice device) => new(
        new SessionEstablished(
            new HandshakeChallengeId(Guid.Parse("5a1d3c2e-0000-4000-8000-000000000024")),
            HandshakePurpose.SessionResume,
            device.DeviceKey.KeyId,
            new SessionAssignment(
                CompanionProtocolVersion.Current,
                device.DeviceId,
                TabletSession,
                new RelayChannelId(Guid.Parse("90000000-0000-4000-8000-000000000024")),
                1,
                RelayCipherSuite.P256HkdfSha256Aes256Gcm,
                Now.AddHours(12)),
            Base64Url.EncodeToString(SHA256.HashData("control-round-trip/session"u8)),
            Now),
        DeviceSessionStatus.Active,
        CompanionTransportKind.EndToEndRelay,
        CompanionSurfaceKind.TabletLandscape,
        device.Capabilities,
        Now);

    private static DevicePublicKey DeviceKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: false);
        byte[] cose =
        [
            0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20,
            .. parameters.Q.X!,
            0x22, 0x58, 0x20,
            .. parameters.Q.Y!,
        ];
        return new DevicePublicKey(
            new DeviceKeyId(Base64Url.EncodeToString(SHA256.HashData(cose))),
            DeviceKeyAlgorithm.WebAuthnEs256,
            Base64Url.EncodeToString(SHA256.HashData("control-round-trip/credential"u8)[..16]),
            Base64Url.EncodeToString(cose));
    }

    private static AuthenticatedPairedFrame Frame() => new(TabletSession, 1, "tablet-tab-24", Now);

    private static ClientCommandEnvelope Envelope(CompanionCommand command) =>
        new(CompanionProtocolVersion.Current, TabletSession, Epoch, command.IssuedUtc, command);

    private static CommandId Command(int number) =>
        new(Guid.Parse($"40000000-0000-4000-8000-{number:000000000000}"));

    private sealed class MemoryStore(DesktopCompanionAuthorityState? state) : IDesktopCompanionAuthorityStore
    {
        private readonly SemaphoreSlim _lease = new(1, 1);
        private DesktopCompanionAuthorityState? _state = state;

        public ValueTask<IDisposable> AcquireExclusiveLeaseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lease.Wait(0);
            return ValueTask.FromResult<IDisposable>(new Lease(_lease));
        }

        public ValueTask<DesktopCompanionAuthorityState?> LoadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(_state);

        public ValueTask SaveAsync(DesktopCompanionAuthorityState next, CancellationToken cancellationToken)
        {
            _state = next;
            return ValueTask.CompletedTask;
        }

        private sealed class Lease(SemaphoreSlim gate) : IDisposable
        {
            private SemaphoreSlim? _gate = gate;

            public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
