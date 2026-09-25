using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Network;
using TarkovCompanion.Core.Network;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// [#292 follow-up] Under Local only the tablet panel said "Could not reach the group relay. Still
/// trying." and, on Start pairing, "Could not reach the group relay." Nothing was wrong with the
/// relay: the network policy refused before a connection was opened. It says what Data &amp;
/// Privacy says instead, and says the relay's own trouble only when sharing may connect.
/// </summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class RelayLinkLocalOnlyTests
{
    [Fact]
    public async Task UnderLocalOnlyThePairingPanelSaysLocalOnlyNotUnreachable()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        var policy = new NetworkPolicyService(new Controls(new NetworkControls { LocalOnly = true }));
        await using var desktop = await DesktopRun.StartAsync(
            disk, relay.Origin, clock, groupKey: "local-only-squad-key", network: policy);

        var off = SetupText.NetworkState(NetworkVerdict.LocalOnly);
        Assert.False(desktop.Panel.IsClaimedByThisDesktop);
        Assert.Equal(off, desktop.Panel.RelayClaimMessage);

        await ((AsyncDelegateCommand)desktop.Panel.StartPairingCommand).ExecuteAsync();
        Assert.Equal(off, desktop.Panel.StatusMessage);
        Assert.DoesNotContain("reach", desktop.Panel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithSharingAllowedTheSameDesktopRegisters()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        var policy = new NetworkPolicyService(new Controls(NetworkControls.Default));
        await using var desktop = await DesktopRun.StartAsync(
            disk, relay.Origin, clock, groupKey: "local-only-squad-key", network: policy);

        Assert.True(desktop.Panel.IsClaimedByThisDesktop, desktop.Panel.RelayClaimMessage);
        Assert.Null(desktop.Panel.NetworkOff());
    }

    private sealed class Controls(NetworkControls controls) : INetworkControlsStore
    {
        private NetworkControls _controls = controls;

        public NetworkControls Read() => _controls;

        public void Save(NetworkControls value) => _controls = value;
    }
}
