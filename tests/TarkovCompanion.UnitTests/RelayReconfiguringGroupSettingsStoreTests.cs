using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// group.json was read once at startup, and RelayMarksBridge.Configure could always be called
/// again but nothing did — so a player who edited the group relay address in the Group page had
/// to restart the desktop before a paired tablet would follow it (#289).
/// </summary>
public sealed class RelayReconfiguringGroupSettingsStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SavingAUsableRelayAddressReconfiguresTheBridgeAtOnce()
    {
        var bridge = await CreateBridgeAsync();
        var store = new RelayReconfiguringGroupSettingsStore(new InMemorySettings(), bridge);
        Assert.Null(bridge.ConfiguredOrigin);

        await store.SaveAsync(Settings("https://relay.example/some/path?x=1"), CancellationToken.None);

        // The path and query are not part of an origin, and are not what a device-key proof is
        // pinned to.
        Assert.Equal(new Uri("https://relay.example"), bridge.ConfiguredOrigin);

        await store.SaveAsync(Settings("https://second-relay.example"), CancellationToken.None);

        Assert.Equal(new Uri("https://second-relay.example"), bridge.ConfiguredOrigin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://relay.example")] // not https
    [InlineData("https://203.0.113.9")] // an IP, not a DNS name
    public async Task AnUnusableAddressLeavesTheBridgeAsItWas(string? serverUri)
    {
        var bridge = await CreateBridgeAsync();
        var store = new RelayReconfiguringGroupSettingsStore(new InMemorySettings(), bridge);
        await store.SaveAsync(Settings("https://relay.example"), CancellationToken.None);
        Assert.Equal(new Uri("https://relay.example"), bridge.ConfiguredOrigin);

        await store.SaveAsync(Settings(serverUri), CancellationToken.None);

        // Left on the last usable relay, not torn down and not silently switched to garbage.
        Assert.Equal(new Uri("https://relay.example"), bridge.ConfiguredOrigin);
    }

    [Fact]
    public async Task TheUnderlyingStoreStillReadsAndWritesTheSameSettings()
    {
        var inner = new InMemorySettings();
        var bridge = await CreateBridgeAsync();
        var store = new RelayReconfiguringGroupSettingsStore(inner, bridge);
        var settings = Settings("https://relay.example", displayName: "Riley");

        await store.SaveAsync(settings, CancellationToken.None);

        // Reached the wrapped store, not just the bridge, and reading back goes through it too.
        Assert.Equal(settings, inner.Saved);
        Assert.Equal("Riley", (await store.GetAsync(CancellationToken.None)).DisplayName);
    }

    private static GroupSharingSettings Settings(string? serverUri, string displayName = "Player") =>
        new(IsEnabled: serverUri is not null, serverUri, displayName, Key: "shared-secret", SharesLoadout: false, SharesQuests: false);

    private static async Task<RelayMarksBridge> CreateBridgeAsync()
    {
        var clock = new FakeTimeProvider(Now);
        var initial = new CanonicalCompanionState(
            new AuthorityEpoch(Guid.NewGuid()),
            new WorkspaceId(Guid.NewGuid()),
            "desktop",
            new GlobalRevision(0),
            new CompanionDeviceId(Guid.NewGuid()),
            new DeviceModeAggregate(AggregateCursor.Empty, [], null, null),
            new WorkspaceAggregate(
                AggregateCursor.Empty,
                new WorkspaceProjection(WorkspaceKind.Raid, null, null, null, null, [], [], null, [], [], [], null)),
            new MarkAggregate(AggregateCursor.Empty, []),
            new CaptureIntentAggregate(AggregateCursor.Empty, null),
            ProfilePreferencesAggregate.Empty);
        var authority = await DesktopCompanionAuthority.OpenAsync(new MemoryAuthorityStore(), initial);
        return new RelayMarksBridge(authority, new FakeRaidMarkStore(), clock);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InMemorySettings : IGroupSettingsStore
    {
        public GroupSharingSettings? Saved { get; private set; }

        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Saved ?? GroupSharingSettings.Off);

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRaidMarkStore : IRaidMarkStore
    {
        public IReadOnlyList<RaidMark> Marks { get; } = [];

        public event Action? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<RaidMark> AddAsync(
            RaidMarkKind kind,
            string mapId,
            string? floorId,
            double x,
            double y,
            string? label,
            CancellationToken cancellationToken = default)
        {
            // Never actually called by these tests (they exercise settings/relay wiring, not
            // marks), but invoked here so IRaidMarkStore.Changed is not an unused-event error.
            Changed?.Invoke();
            return Task.FromResult(new RaidMark(Guid.NewGuid(), kind, new MapMarkState(mapId, floorId, x, y, label, null), Now));
        }

        public Task MoveAsync(Guid id, double x, double y, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RenameAsync(Guid id, string? label, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<RaidMark> PlaceAsync(string mapId, string? floorId, double x, double y, string? label, RaidMarkScope scope, RaidMarkLifetime lifetime, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RaidMark(Guid.NewGuid(), RaidMarkLifetimes.KindFor(lifetime), new MapMarkState(mapId, floorId, x, y, label, null), Now));

        public Task SetOptionsAsync(Guid id, RaidMarkScope scope, RaidMarkLifetime lifetime, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EndRaidAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryAuthorityStore : IDesktopCompanionAuthorityStore
    {
        private DesktopCompanionAuthorityState? _state;

        public ValueTask<DesktopCompanionAuthorityState?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_state);

        public ValueTask SaveAsync(DesktopCompanionAuthorityState state, CancellationToken cancellationToken = default)
        {
            _state = state;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IDisposable> AcquireExclusiveLeaseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IDisposable>(new NoopLease());

        private sealed class NoopLease : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
