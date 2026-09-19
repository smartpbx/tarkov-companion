using System.Buffers.Text;
using System.Security.Cryptography;
using TarkovCompanion.App.Services.V2.Team;
using TarkovCompanion.App.ViewModels.V2.Tablet;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.UnitTests.V2Team;

/// <summary>
/// The pairing panel's code image, its countdown, and what revoking asks first (#289).
/// </summary>
/// <remarks>
/// The panel showed the QR payload as nothing at all, showed no expiry although the offer has
/// always carried one, and revoked a device on a single press.
/// </remarks>
public sealed class PairingPanelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThePairingPayloadBecomesASymbolAViewCanDraw()
    {
        var payload = PairedTransportBinding.QrPayloadPrefix + "K7M29QRT4B/" + new string('a', 43);

        var code = QrCode.Encode(payload);
        var path = QrGeometry.PathData(code);

        Assert.StartsWith("M", path, StringComparison.Ordinal);
        // The quiet zone is part of the path, so a stretched Path cannot scale the margin away.
        Assert.Equal(code.Size + (QrGeometry.QuietZoneModules * 2), QrGeometry.Extent(code));
        Assert.EndsWith($"M0,0M{QrGeometry.Extent(code)},{QrGeometry.Extent(code)}", path, StringComparison.Ordinal);
        // A dark run becomes one rectangle, so the figure count is well under the module count.
        Assert.InRange(path.Split('z').Length - 1, 1, code.Size * code.Size);
    }

    [Theory]
    [InlineData(300, "Expires in 5m 00s")]
    [InlineData(276, "Expires in 4m 36s")]
    [InlineData(60, "Expires in 1m 00s")]
    [InlineData(45, "Expires in 45s")]
    [InlineData(1, "Expires in 1s")]
    public void TheCountdownReadsInMinutesUntilThereAreNone(int seconds, string expected) =>
        Assert.Equal(expected, CompanionPairingViewModel.DescribeExpiry(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void AnExpiredCodeSaysToStartAgainRatherThanCountingBackwards(int seconds)
    {
        var said = CompanionPairingViewModel.DescribeExpiry(TimeSpan.FromSeconds(seconds));

        Assert.Contains("expired", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("again", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokingTakesTwoPressesAndSaysSoOnTheButton()
    {
        var revoked = 0;
        var row = new PairedDeviceRowViewModel(Tablet(), _ =>
        {
            revoked++;
            return Task.CompletedTask;
        });

        Assert.Equal("Revoke", row.RevokeLabel);
        Assert.Equal(string.Empty, row.RevokeWarning);

        row.RevokeCommand.Execute(null);
        await Task.Yield();

        Assert.Equal(0, revoked);
        Assert.Equal("Confirm revoke", row.RevokeLabel);
        Assert.Contains("cannot be undone", row.RevokeWarning, StringComparison.Ordinal);
        Assert.Contains("Raid tablet", row.RevokeWarning, StringComparison.Ordinal);

        row.RevokeCommand.Execute(null);
        await Task.Yield();

        Assert.Equal(1, revoked);
        Assert.Equal("Revoke", row.RevokeLabel);
    }

    [Fact]
    public void AskingTwiceCanBeTakenBack()
    {
        var row = new PairedDeviceRowViewModel(Tablet(), _ => Task.CompletedTask);

        row.RevokeCommand.Execute(null);
        row.CancelConfirmation();

        Assert.Equal("Revoke", row.RevokeLabel);
        Assert.Equal(string.Empty, row.RevokeWarning);
    }

    private static PairedDevice Tablet() => new(
        new CompanionDeviceId(Guid.Parse("40000000-0000-4000-8000-000000000289")),
        "Raid tablet",
        DeviceKey(),
        DeviceAuthorizationRole.Member,
        [DeviceCapability.FollowDesktop],
        DeviceLifecycleStatus.Active,
        Now,
        Now,
        1,
        Now.AddDays(30),
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
            Base64Url.EncodeToString(SHA256.HashData("pairing-panel-test/credential"u8)[..16]),
            Base64Url.EncodeToString(cose));
    }
}
