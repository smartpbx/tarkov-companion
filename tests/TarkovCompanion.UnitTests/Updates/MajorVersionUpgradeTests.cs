using System.Security.Cryptography;
using TarkovCompanion.App.Services.Updates;

namespace TarkovCompanion.UnitTests.Updates;

/// <summary>
/// The rough builds went from 1.0.&lt;run&gt; to 2.0.&lt;run&gt;. Machines that installed a 1.0 build have to
/// follow, with their data, and must never be walked back.
/// </summary>
/// <remarks>
/// The updater library compares semantic versions, not run numbers, so the jump needs nothing
/// special: 2.0.anything is newer than 1.0.anything. These tests exist because that sentence is
/// exactly the kind that is true until the day it is not checked. They run the real library
/// over a feed in a temporary folder, with no network.
/// </remarks>
public sealed class MajorVersionUpgradeTests
{
    private static VelopackUpdateGateway GatewayOver(RoughChannelHarness harness) =>
        VelopackUpdateGateway.Create(
            UpdateChannel.Rough,
            new HashVerifiedUpdateSource(new DirectoryUpdateFeedTransport(harness.Feed), harness.Log),
            harness.Locator,
            harness.Log);

    private static string ArgumentAfter(IReadOnlyList<string> arguments, string name) =>
        arguments[arguments.ToList().IndexOf(name) + 1];

    [Fact]
    public async Task AnInstalledVersionOneTakesVersionTwoAndItsDataFolderIsLeftAlone()
    {
        using var harness = new RoughChannelHarness(installedVersion: "1.0.1121");
        var (marker, contents) = harness.SeedData();
        var (fileName, sha256) = harness.Publish("2.0.1140");
        var gateway = GatewayOver(harness);
        Assert.Equal("Version 1.0.1121", gateway.InstalledBuild);

        var found = await gateway.CheckAsync(CancellationToken.None);
        Assert.True(found.CanDownload, found.Status);
        Assert.Equal("2.0.1140", found.Available);

        var fetched = await gateway.DownloadAsync(CancellationToken.None);
        Assert.True(fetched.CanApply, fetched.Status);
        var staged = Path.Combine(harness.Packages, fileName);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(staged))));

        gateway.ApplyAndRestart();

        var apply = Assert.Single(harness.Locator.Recorded.Started);
        Assert.Equal(staged, ArgumentAfter(apply.Arguments, "--package"));

        // The swap program is told one folder it may replace. The data is not in it, so nothing
        // the update does can reach it, whatever the two versions are called.
        var replaced = ArgumentAfter(apply.Arguments, "--rootDir");
        Assert.Equal(harness.InstallRoot, replaced);
        Assert.False(UpdateDataFolderText.IsInside(harness.Data, replaced));
        Assert.True(UpdateDataFolderText.IsInside(ArgumentAfter(apply.Arguments, "--packageDir"), replaced));
        Assert.Equal(contents, await File.ReadAllTextAsync(marker));
        Assert.EndsWith(
            "kept across updates",
            UpdateDataFolderText.Describe(harness.Data, Path.Combine(replaced, "current")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A higher run number under the old major is still the older build.
    /// </summary>
    /// <remarks>
    /// The case that would bite: the relay's feed folder is not tidied, a 1.0 package with a
    /// larger last part than the first 2.0 build is still listed, and it is listed first. Ordering
    /// is by semantic version, not by position in the feed and not by the last number.
    /// </remarks>
    [Theory]
    [InlineData("1.0.9999", "2.0.1140")]
    [InlineData("2.0.1140", "1.0.9999")]
    public async Task TheNewerMajorWinsWhateverOrderTheFeedListsThemIn(string first, string second)
    {
        using var harness = new RoughChannelHarness(installedVersion: "1.0.1121");
        harness.PublishAll(first, second);

        var found = await GatewayOver(harness).CheckAsync(CancellationToken.None);

        Assert.Equal("2.0.1140", found.Available);
    }

    /// <summary>Once on 2.0, a leftover or republished 1.0 feed offers nothing, however large its run number.</summary>
    [Fact]
    public async Task AVersionTwoInstallIsNeverWalkedBackToVersionOne()
    {
        using var harness = new RoughChannelHarness(installedVersion: "2.0.1140");
        harness.Publish("1.0.9999");

        var result = await GatewayOver(harness).CheckAsync(CancellationToken.None);

        Assert.Equal("Up to date", result.Status);
        Assert.False(result.CanDownload);
    }

    [Fact]
    public async Task WithinVersionTwoALaterRunIsStillANewerBuild()
    {
        using var harness = new RoughChannelHarness(installedVersion: "2.0.1140");
        harness.Publish("2.0.1141");

        Assert.Equal("2.0.1141", (await GatewayOver(harness).CheckAsync(CancellationToken.None)).Available);
    }
}
