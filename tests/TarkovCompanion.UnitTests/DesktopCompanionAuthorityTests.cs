using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Infrastructure.Devices;

namespace TarkovCompanion.UnitTests;

public sealed class DesktopCompanionAuthorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 1, 0, 0, TimeSpan.Zero);
    private static readonly CompanionDeviceId DesktopDevice = new(Guid.Parse("10000000-0000-4000-8000-000000000001"));
    private static readonly CompanionDeviceId TabletDevice = new(Guid.Parse("10000000-0000-4000-8000-000000000002"));
    private static readonly DeviceSessionId TabletSession = new(Guid.Parse("20000000-0000-4000-8000-000000000002"));
    private static readonly AuthorityEpoch Epoch = new(Guid.Parse("30000000-0000-4000-8000-000000000001"));
    private static readonly WorkspaceId Workspace = new(Guid.Parse("80000000-0000-4000-8000-000000000001"));
    private static readonly IReadOnlyList<DeviceCapability> Capabilities =
    [
        DeviceCapability.FollowDesktop,
        DeviceCapability.RequestControl,
        DeviceCapability.ShowOnDesktop,
        DeviceCapability.ManageOwnMarks,
        DeviceCapability.RequestCaptureIntent,
    ];

    [Fact]
    public async Task AuthenticatedCommandCommitsStateThenSequencesUpdateAndAcknowledgement()
    {
        var seeded = SeededState();
        var store = new MemoryAuthorityStore(seeded);
        using var authority = await DesktopCompanionAuthority.OpenAsync(store, seeded.CanonicalState);
        var command = new SetInteractionModeCommand(
            Command(1),
            new AggregateRevision(1),
            Now.AddMinutes(1),
            Now.AddMinutes(2),
            CompanionInteractionMode.Independent);

        var result = await authority.ApplyCommandAsync(
            Frame(Now.AddMinutes(1)),
            Envelope(command));

        Assert.Equal(CommandDisposition.Applied, result.Acknowledgement.Disposition);
        Assert.Equal(CompanionInteractionMode.Independent, result.State.CanonicalState.DeviceModes.ModeOf(TabletDevice));
        Assert.Equal(new long[] { 1, 2 }, result.Deliveries.Select(delivery => delivery.Item.Sequence.Value));
        Assert.IsType<CanonicalUpdateMessage>(result.Deliveries[0].Item.Message);
        Assert.IsType<CommandAcknowledgementMessage>(result.Deliveries[1].Item.Message);
        Assert.Equal(Now.AddMinutes(1), Assert.Single(result.State.Devices).LastUsedUtc);
        Assert.Equal(Now.AddMinutes(1), Assert.Single(result.State.Sessions).LastUsedUtc);
        Assert.Single(result.State.CanonicalState.RecentCommands);
        Assert.Equal(1, store.SaveCalls);
        Assert.Same(result.State, authority.Snapshot);
    }

    [Fact]
    public async Task PersistenceFailureLeavesPublishedAuthorityStateUnchanged()
    {
        var seeded = SeededState();
        var store = new MemoryAuthorityStore(seeded) { FailNextSave = true };
        using var authority = await DesktopCompanionAuthority.OpenAsync(store, seeded.CanonicalState);
        var command = new SetInteractionModeCommand(
            Command(2),
            new AggregateRevision(1),
            Now.AddMinutes(1),
            Now.AddMinutes(2),
            CompanionInteractionMode.Independent);

        await Assert.ThrowsAsync<IOException>(async () => await authority.ApplyCommandAsync(
            Frame(Now.AddMinutes(1)),
            Envelope(command)));

        Assert.Same(seeded, authority.Snapshot);
        Assert.Equal(CompanionInteractionMode.Follow, authority.Snapshot.CanonicalState.DeviceModes.ModeOf(TabletDevice));
        Assert.Empty(authority.Snapshot.DeliveryLedger.PendingFor(TabletDevice));
    }

    [Fact]
    public async Task FrameKeyEpochMustMatchThePersistedAuthenticatedSession()
    {
        var seeded = SeededState();
        var store = new MemoryAuthorityStore(seeded);
        using var authority = await DesktopCompanionAuthority.OpenAsync(store, seeded.CanonicalState);
        var command = new SetInteractionModeCommand(
            Command(3),
            new AggregateRevision(1),
            Now.AddMinutes(1),
            Now.AddMinutes(2),
            CompanionInteractionMode.Independent);
        var wrongEpoch = new AuthenticatedPairedFrame(
            TabletSession,
            2,
            "tablet-tab-1",
            Now.AddMinutes(1));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await authority.ApplyCommandAsync(
            wrongEpoch,
            Envelope(command)));

        Assert.Equal(0, store.SaveCalls);
        Assert.Same(seeded, authority.Snapshot);
    }

    [Fact]
    public async Task RevocationEndsSessionsReleasesModesAndDropsTheDeviceStream()
    {
        var seeded = SeededState();
        var store = new MemoryAuthorityStore(seeded);
        using var authority = await DesktopCompanionAuthority.OpenAsync(store, seeded.CanonicalState);
        var independent = new SetInteractionModeCommand(
            Command(4),
            new AggregateRevision(1),
            Now.AddMinutes(1),
            Now.AddMinutes(2),
            CompanionInteractionMode.Independent);
        _ = await authority.ApplyCommandAsync(Frame(Now.AddMinutes(1)), Envelope(independent));

        var revoked = await authority.RevokeDeviceAsync(
            TabletDevice,
            Now.AddMinutes(2),
            "stolen-device");

        Assert.Equal(DeviceLifecycleStatus.Revoked, Assert.Single(revoked.State.Devices).Status);
        Assert.Equal(DeviceSessionStatus.Revoked, Assert.Single(revoked.State.Sessions).Status);
        Assert.Equal(CompanionInteractionMode.Follow, revoked.State.CanonicalState.DeviceModes.ModeOf(TabletDevice));
        Assert.Null(revoked.State.DeliveryLedger.For(TabletDevice));
        Assert.Empty(revoked.Deliveries);
    }

    [Fact]
    public void AuthorityStateRejectsTwoActiveTabsForOneDevice()
    {
        var seeded = SeededState();
        var second = ActiveSession(seeded.Devices[0], new DeviceSessionId(Guid.Parse("20000000-0000-4000-8000-000000000003")));

        var exception = Assert.Throws<ArgumentException>(() => new DesktopCompanionAuthorityState(
            seeded.CanonicalState,
            seeded.Devices,
            seeded.Sessions.Append(second).ToArray(),
            seeded.DeliveryLedger));

        Assert.Contains("at most one active session", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonStoreRoundTripsReceiptsAndDeliveryHistoryThatWireSnapshotsOmit()
    {
        var seeded = SeededState();
        var memory = new MemoryAuthorityStore(seeded);
        using var authority = await DesktopCompanionAuthority.OpenAsync(memory, seeded.CanonicalState);
        var command = new SetInteractionModeCommand(
            Command(5),
            new AggregateRevision(1),
            Now.AddMinutes(1),
            Now.AddMinutes(2),
            CompanionInteractionMode.Independent);
        var applied = await authority.ApplyCommandAsync(Frame(Now.AddMinutes(1)), Envelope(command));
        var directory = Directory.CreateTempSubdirectory("tarkov-companion-device-authority-");
        try
        {
            var path = Path.Combine(directory.FullName, "authority.json");
            var store = new JsonFileDesktopCompanionAuthorityStore(path);
            await store.SaveAsync(applied.State, CancellationToken.None);

            var loaded = Assert.IsType<DesktopCompanionAuthorityState>(
                await store.LoadAsync(CancellationToken.None));

            Assert.Equal(command.CommandId, Assert.Single(loaded.CanonicalState.RecentCommands).CommandId);
            Assert.Equal(2, loaded.DeliveryLedger.For(TabletDevice)!.LastAssigned.Value);
            Assert.Equal(DeviceSessionStatus.Active, Assert.Single(loaded.Sessions).Status);
            var persisted = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("trafficKey", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("privateKey", persisted, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JsonAuthorityLeaseRejectsASecondAuthorityWhileTheFirstMutates()
    {
        var directory = Directory.CreateTempSubdirectory("tarkov-companion-device-authority-");
        try
        {
            var path = Path.Combine(directory.FullName, "authority.json");
            var seeded = SeededState();
            var seededStore = new JsonFileDesktopCompanionAuthorityStore(path);
            await seededStore.SaveAsync(seeded, CancellationToken.None);
            using (var first = await DesktopCompanionAuthority.OpenAsync(
                       seededStore,
                       seeded.CanonicalState))
            {
                var command = new SetInteractionModeCommand(
                    Command(6),
                    new AggregateRevision(1),
                    Now.AddMinutes(1),
                    Now.AddMinutes(2),
                    CompanionInteractionMode.Independent);
                var mutation = first.ApplyCommandAsync(Frame(Now.AddMinutes(1)), Envelope(command)).AsTask();
                await Assert.ThrowsAsync<IOException>(async () => await DesktopCompanionAuthority.OpenAsync(
                    new JsonFileDesktopCompanionAuthorityStore(path),
                    seeded.CanonicalState));
                var committed = await mutation;

                Assert.Equal(new GlobalRevision(1), committed.State.CanonicalState.GlobalRevision);
                Assert.Equal(new DeliverySequence(2), committed.State.DeliveryLedger.For(TabletDevice)!.LastAssigned);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JsonStoreRejectsDocumentsPastTheBoundedReadLimit()
    {
        var directory = Directory.CreateTempSubdirectory("tarkov-companion-device-authority-");
        try
        {
            var path = Path.Combine(directory.FullName, "authority.json");
            await File.WriteAllBytesAsync(
                path,
                new byte[JsonFileDesktopCompanionAuthorityStore.MaximumDocumentBytes + 1]);

            var store = new JsonFileDesktopCompanionAuthorityStore(path);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.LoadAsync(CancellationToken.None));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // [#601] A tablet paired before #558 was stored with a session grant that lacked
    // RequestControl, so its Control request was refused before the desktop could show Allow.
    [Fact]
    public async Task AStoredPairingFromBeforeTheControlFixIsUpgradedAndItsControlRequestReachesTheDesktop()
    {
        var device = PairedTablet();
        var oldSession = ActiveSession(device, TabletSession);
        oldSession = new DeviceSession(
            oldSession.Establishment,
            oldSession.Status,
            oldSession.Transport,
            oldSession.Surface,
            [.. device.Capabilities.Where(capability => capability != DeviceCapability.RequestControl)],
            oldSession.LastUsedUtc);
        var seeded = new DesktopCompanionAuthorityState(InitialState(), [device], [oldSession], DeliveryLedger.Empty);
        var store = new MemoryAuthorityStore(seeded);

        using var authority = await DesktopCompanionAuthority.OpenAsync(store, seeded.CanonicalState);

        Assert.Contains(DeviceCapability.RequestControl, Assert.Single(authority.Snapshot.Sessions).Capabilities);
        Assert.Equal(1, store.SaveCalls); // kept, so the next start does not have to do it again
        var request = new RequestControlCommand(
            Command(20),
            new AggregateRevision(1),
            Now.AddMinutes(1),
            Now.AddMinutes(2),
            TimeSpan.FromMinutes(2));
        var result = await authority.ApplyCommandAsync(Frame(Now.AddMinutes(1)), Envelope(request));

        Assert.Equal(CommandDisposition.Applied, result.Acknowledgement.Disposition);
        // PendingControl is what the desktop's Allow prompt is raised from.
        Assert.NotNull(result.State.CanonicalState.DeviceModes.PendingControl);
    }

    [Fact]
    public async Task TheGrantUpgradeNeverGoesBeyondATabletGrantOrTouchesAnotherRole()
    {
        var member = PairedTablet();
        var observer = new PairedDevice(
            new CompanionDeviceId(Guid.Parse("10000000-0000-4000-8000-000000000003")),
            "Watcher",
            DeviceKey(),
            DeviceAuthorizationRole.Observer,
            [DeviceCapability.FollowDesktop],
            DeviceLifecycleStatus.Active,
            Now,
            Now,
            1,
            Now.AddDays(30),
            Now);
        var narrowMember = new PairedDevice(
            member.DeviceId, member.DisplayName, member.DeviceKey, member.Role,
            [DeviceCapability.FollowDesktop, DeviceCapability.ManageProfilePreferences],
            member.Status, member.CreatedUtc, member.LastUsedUtc, member.LastKeyEpoch, member.ExpiresUtc, member.StatusChangedUtc);
        var seeded = new DesktopCompanionAuthorityState(
            InitialState(),
            [narrowMember, observer],
            [ActiveSession(narrowMember, TabletSession)],
            DeliveryLedger.Empty);

        var upgraded = PairedTabletGrantUpgrade.Apply(seeded, Now);

        var tablet = upgraded.Devices.Single(item => item.DeviceId == member.DeviceId);
        Assert.Equal(
            PairedTabletGrant.Create(Now).DeviceCapabilities.Append(DeviceCapability.ManageProfilePreferences).Order(),
            tablet.Capabilities.Order());
        Assert.Equal([DeviceCapability.FollowDesktop], upgraded.Devices.Single(item => item.DeviceId == observer.DeviceId).Capabilities);
        Assert.Equal(
            PairedTabletGrant.Create(Now).SessionCapabilities.Order(),
            Assert.Single(upgraded.Sessions).Capabilities.Where(item => item != DeviceCapability.ManageProfilePreferences).Order());
        Assert.Same(upgraded, PairedTabletGrantUpgrade.Apply(upgraded, Now));
    }

    // [#601] The tablet says "This pairing is out of date · pair again" when its Control is refused
    // for a capability its grant lacks. The desktop has to say it too, on that device's row.
    [Fact]
    public async Task AControlRequestRefusedForAMissingCapabilityMarksThatPairingOutOfDateOnTheDesktop()
    {
        var member = PairedTablet();
        // An Observer is not upgraded on load, so its grant still lacks RequestControl.
        var observer = new PairedDevice(
            member.DeviceId, member.DisplayName, member.DeviceKey, DeviceAuthorizationRole.Observer,
            [DeviceCapability.FollowDesktop],
            member.Status, member.CreatedUtc, member.LastUsedUtc, member.LastKeyEpoch, member.ExpiresUtc, member.StatusChangedUtc);
        var seeded = new DesktopCompanionAuthorityState(
            InitialState(), [observer], [ActiveSession(observer, TabletSession)], DeliveryLedger.Empty);
        using var authority = await DesktopCompanionAuthority.OpenAsync(new MemoryAuthorityStore(seeded), seeded.CanonicalState);
        var raised = new List<CompanionDeviceId>();
        authority.PairingOutOfDateChanged += raised.Add;
        Assert.False(authority.IsPairingOutOfDate(TabletDevice));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var request = new RequestControlCommand(
                Command(20 + attempt), new AggregateRevision(1), Now.AddMinutes(1), Now.AddMinutes(2), TimeSpan.FromMinutes(2));
            var refused = await authority.ApplyCommandAsync(Frame(Now.AddMinutes(1)), Envelope(request));
            Assert.Equal(CommandDisposition.RejectedUnauthorized, refused.Acknowledgement.Disposition);
        }

        Assert.True(authority.IsPairingOutOfDate(TabletDevice));
        Assert.Equal([TabletDevice], raised); // once, not once per refusal

        using var pairing = new TarkovCompanion.App.ViewModels.V2.Tablet.CompanionPairingViewModel(
            authority,
            TarkovCompanion.App.ViewModels.V2.Tablet.CompanionPairingAvailability.Unavailable,
            TimeProvider.System);
        var row = Assert.Single(pairing.Devices);
        Assert.True(row.IsPairingOutOfDate);
        Assert.Equal("Out of date · pair again", row.PairingOutOfDateLabel);
    }

    private static DesktopCompanionAuthorityState SeededState()
    {
        var device = PairedTablet();
        return new DesktopCompanionAuthorityState(
            InitialState(),
            [device],
            [ActiveSession(device, TabletSession)],
            DeliveryLedger.Empty);
    }

    private static CanonicalCompanionState InitialState() => new(
        Epoch,
        Workspace,
        "desktop-install-1",
        new GlobalRevision(0),
        DesktopDevice,
        new DeviceModeAggregate(
            AggregateCursor.Empty,
            [new DeviceModeEntry(TabletDevice, CompanionInteractionMode.Follow, Now)],
            null,
            null),
        new WorkspaceAggregate(AggregateCursor.Empty, Projection()),
        new MarkAggregate(AggregateCursor.Empty, []),
        new CaptureIntentAggregate(AggregateCursor.Empty, null),
        ProfilePreferencesAggregate.Empty);

    private static WorkspaceProjection Projection() => new(
        WorkspaceKind.Raid,
        "customs",
        "ground",
        new WorkspaceViewport(
            new MapCoordinate("customs", "ground", CoordinateSpaceKind.World, "tarkov-dev-1", 10, 2, 20),
            1),
        null,
        [],
        [],
        null,
        [],
        ["extracts"],
        [],
        null);

    private static PairedDevice PairedTablet()
    {
        var key = DeviceKey();
        return new PairedDevice(
            TabletDevice,
            "Raid tablet",
            key,
            DeviceAuthorizationRole.Member,
            Capabilities,
            DeviceLifecycleStatus.Active,
            Now,
            Now,
            1,
            Now.AddDays(30),
            Now);
    }

    private static DeviceSession ActiveSession(PairedDevice device, DeviceSessionId sessionId)
    {
        var establishment = new SessionEstablished(
            new HandshakeChallengeId(Guid.Parse("5a1d3c2e-0000-4000-8000-000000000099")),
            HandshakePurpose.SessionResume,
            device.DeviceKey.KeyId,
            new SessionAssignment(
                CompanionProtocolVersion.Current,
                device.DeviceId,
                sessionId,
                new RelayChannelId(Guid.Parse("90000000-0000-4000-8000-000000000001")),
                1,
                RelayCipherSuite.P256HkdfSha256Aes256Gcm,
                Now.AddHours(12)),
            Base64Url.EncodeToString(SHA256.HashData("authority-test/session"u8)),
            Now);
        return new DeviceSession(
            establishment,
            DeviceSessionStatus.Active,
            CompanionTransportKind.EndToEndRelay,
            CompanionSurfaceKind.TabletLandscape,
            device.Capabilities,
            Now);
    }

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
            Base64Url.EncodeToString(SHA256.HashData("authority-test/credential"u8)[..16]),
            Base64Url.EncodeToString(cose));
    }

    private static AuthenticatedPairedFrame Frame(DateTimeOffset receivedUtc) =>
        new(TabletSession, 1, "tablet-tab-1", receivedUtc);

    private static ClientCommandEnvelope Envelope(CompanionCommand command) =>
        new(CompanionProtocolVersion.Current, TabletSession, Epoch, command.IssuedUtc, command);

    private static CommandId Command(int number) =>
        new(Guid.Parse($"40000000-0000-4000-8000-{number:000000000000}"));

    private sealed class MemoryAuthorityStore(DesktopCompanionAuthorityState? state) : IDesktopCompanionAuthorityStore
    {
        private DesktopCompanionAuthorityState? _state = state;
        private readonly SemaphoreSlim _lease = new(1, 1);

        public int SaveCalls { get; private set; }

        public bool FailNextSave { get; set; }

        public ValueTask<IDisposable> AcquireExclusiveLeaseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_lease.Wait(0))
            {
                throw new IOException("fixture authority is already leased");
            }

            return ValueTask.FromResult<IDisposable>(new Lease(_lease));
        }

        public ValueTask<DesktopCompanionAuthorityState?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_state);
        }

        public ValueTask SaveAsync(DesktopCompanionAuthorityState next, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCalls++;
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("fixture persistence failure");
            }

            _state = next;
            return ValueTask.CompletedTask;
        }

        private sealed class Lease(SemaphoreSlim gate) : IDisposable
        {
            private SemaphoreSlim? _gate = gate;

            public void Dispose()
            {
                Interlocked.Exchange(ref _gate, null)?.Release();
            }
        }
    }
}
