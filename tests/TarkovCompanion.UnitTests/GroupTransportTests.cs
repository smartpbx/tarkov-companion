using TarkovCompanion.Application.Services.Group;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Where the group key is allowed to travel.
/// </summary>
/// <remarks>
/// It rides on X-Group-Key on every request and is the only thing between a group and a
/// stranger. Plain http was accepted for any host, and the public relay answers http today
/// with no redirect, so the key was one mistyped scheme away from crossing the internet in
/// the clear.
/// </remarks>
public sealed class GroupTransportTests
{
    [Theory]
    [InlineData("https://tarkov.example.net/")]
    [InlineData("https://10.10.10.80:8090/")]
    [InlineData("https://localhost:5000/")]
    public void HttpsIsAlwaysAcceptable(string address) =>
        Assert.True(GroupSharingSettings.IsTransportAcceptable(new(address)));

    /// <summary>A relay on the same network is an ordinary way to run this.</summary>
    [Theory]
    [InlineData("http://127.0.0.1:8090/")]
    [InlineData("http://localhost:8090/")]
    [InlineData("http://10.10.10.80:8090/")]
    [InlineData("http://192.168.1.20:8090/")]
    [InlineData("http://172.16.4.4:8090/")]
    [InlineData("http://172.31.10.196:8090/")]
    [InlineData("http://100.72.1.1:8090/")]
    [InlineData("http://169.254.4.4:8090/")]
    [InlineData("http://relaybox:8090/")]
    [InlineData("http://relay.local:8090/")]
    [InlineData("http://relay.internal:8090/")]
    [InlineData("http://[::1]:8090/")]
    public void HttpIsAcceptableWhereThereIsNoInternetToCross(string address) =>
        Assert.True(GroupSharingSettings.IsTransportAcceptable(new(address)));

    /// <summary>This is the address that answers http today, with no redirect.</summary>
    [Theory]
    [InlineData("http://tarkov.mannerow.net/")]
    [InlineData("http://example.com/")]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://172.32.0.1/")]
    [InlineData("http://100.128.0.1/")]
    public void HttpIsRefusedForAnythingReachableFromOutside(string address) =>
        Assert.False(GroupSharingSettings.IsTransportAcceptable(new(address)));

    [Fact]
    public void AnotherSchemeIsNeverAcceptable() =>
        Assert.False(GroupSharingSettings.IsTransportAcceptable(new("ftp://relay.example.net/")));

    /// <summary>Somebody is told what is wrong before they try, not by a refusal afterwards.</summary>
    [Fact]
    public void TheSettingsSayWhyAPlainHttpAddressIsNotEnough()
    {
        var settings = new GroupSharingSettings(true, "http://tarkov.example.net/", "Clay", "a-key-long-enough", false, false);

        Assert.False(settings.IsUsable);
        Assert.Equal("an https address, because the group key travels with every request", settings.MissingPiece);
    }

    [Fact]
    public void ALocalAddressIsStillUsable() =>
        Assert.True(new GroupSharingSettings(true, "http://10.10.10.80:8090/", "Clay", "a-key-long-enough", false, false).IsUsable);
}
