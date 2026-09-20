using TarkovCompanion.App.ViewModels;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// The pairing QR, over a real in-process relay (#289): a phone camera can only open a link, not
/// run the ceremony, so the symbol has to be a URL to the relay's own tablet page with the pairing
/// payload in its fragment — never sent to any server — and that same URL is shown as text.
/// </summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class RelayLinkPairingQrTests
{
    [Fact]
    public async Task TheQrIsTheRelaysTabletPageWithThePayloadInTheFragment()
    {
        var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
        await using var relay = await LinkRelay.StartAsync(clock);
        using var disk = new DesktopDisk();
        await using var desktop = await DesktopRun.StartAsync(disk, relay.Origin, clock);
        await desktop.ClaimAsync();

        await ((AsyncDelegateCommand)desktop.Panel.StartPairingCommand).ExecuteAsync();
        Assert.True(desktop.Panel.IsAwaitingTablet, desktop.Panel.StatusMessage);

        var expectedUrl = relay.Origin.GetLeftPart(UriPartial.Authority) + PairedTransportBinding.TabletPagePath;
        Assert.Equal(expectedUrl, desktop.Panel.RelayTabletUrl);
        Assert.True(desktop.Panel.HasRelayTabletUrl);

        var qr = desktop.Panel.QrPayload;
        Assert.NotNull(qr);
        Assert.StartsWith(expectedUrl + "#", qr, StringComparison.Ordinal);

        // The fragment alone is the same payload a scanner would have read before #289: still
        // parseable by the server-side format, and it carries this same pairing code.
        var fragment = qr![(expectedUrl.Length + 1)..];
        Assert.True(PairedTransportBinding.TryParseQrPayload(fragment, out var code, out _));
        Assert.Equal(desktop.Panel.PairingCode!.Replace("-", string.Empty, StringComparison.Ordinal), code);
    }
}
