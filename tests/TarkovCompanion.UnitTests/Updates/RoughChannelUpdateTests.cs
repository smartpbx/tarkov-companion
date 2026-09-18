using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services.Updates;

namespace TarkovCompanion.UnitTests.Updates;

public sealed class RoughChannelUpdateTests
{
    private static readonly UpdateChannel Channel = UpdateChannel.Rough;

    private static VelopackUpdateGateway GatewayOver(RoughChannelHarness harness) =>
        VelopackUpdateGateway.Create(
            Channel,
            new HashVerifiedUpdateSource(new DirectoryUpdateFeedTransport(harness.Feed), harness.Log),
            harness.Locator,
            harness.Log);

    /// <summary>
    /// The whole road: an installed build sees a newer one, fetches it, checks it, and hands
    /// exactly that package to the program that swaps the files.
    /// </summary>
    [Fact]
    public async Task AnInstalledBuildFindsANewerVersionAndAppliesIt()
    {
        using var harness = new RoughChannelHarness(installedVersion: "1.0.100");
        var (fileName, sha256) = harness.Publish("1.0.200");
        var gateway = GatewayOver(harness);

        Assert.True(gateway.IsInstalled);
        Assert.Equal("Version 1.0.100", gateway.InstalledBuild);

        var found = await gateway.CheckAsync(CancellationToken.None);
        Assert.True(found.CanDownload);
        Assert.Equal("1.0.200", found.Available);

        var reported = new List<int>();
        var fetched = await gateway.DownloadAsync(CancellationToken.None, reported.Add);
        Assert.True(fetched.CanApply, fetched.Status);
        Assert.Equal(100, reported[^1]);

        // Staged where the updater keeps packages, and byte for byte what the feed described.
        var staged = Path.Combine(harness.Packages, fileName);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(staged))));
        Assert.Contains(harness.Log.Entries, entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains("Verified", StringComparison.Ordinal)
            && entry.Message.Contains(sha256, StringComparison.Ordinal));

        gateway.ApplyAndRestart();

        var apply = Assert.Single(harness.Locator.Recorded.Started);
        Assert.EndsWith("Update.exe", apply.Executable, StringComparison.Ordinal);
        Assert.Contains("apply", apply.Arguments);
        var packageArgument = apply.Arguments[apply.Arguments.ToList().IndexOf("--package") + 1];
        Assert.Equal(staged, packageArgument);
        // The updater waits for this process to go before it touches a file, then reopens.
        Assert.Contains("--waitPid", apply.Arguments);
        Assert.DoesNotContain("--norestart", apply.Arguments);
        Assert.Equal(0, harness.Locator.Recorded.ExitCode);
    }

    /// <summary>
    /// The feed says one thing and the bytes are another: refused, loudly, with both hashes in
    /// the log, nothing staged, and nothing applied however the caller asks.
    /// </summary>
    [Fact]
    public async Task APackageThatDoesNotMatchTheFeedIsRefusedAndTheRunningBuildIsLeftAlone()
    {
        using var harness = new RoughChannelHarness(installedVersion: "1.0.100");
        var listed = new string('A', 64);
        var (fileName, _) = harness.Publish("1.0.200", listedSha256: listed);
        var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(harness.Feed, fileName))));
        var gateway = GatewayOver(harness);

        Assert.True((await gateway.CheckAsync(CancellationToken.None)).CanDownload);
        var refused = await gateway.DownloadAsync(CancellationToken.None);

        Assert.False(refused.CanApply);
        Assert.True(refused.CanDownload);
        Assert.StartsWith("Refused", refused.Status, StringComparison.Ordinal);
        Assert.Contains(harness.Log.Entries, entry =>
            entry.Level == LogLevel.Error
            && entry.Message.Contains(listed, StringComparison.Ordinal)
            && entry.Message.Contains(actual, StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(harness.Packages, "*.nupkg*"));

        gateway.ApplyAndRestart();

        Assert.Empty(harness.Locator.Recorded.Started);
        Assert.Null(harness.Locator.Recorded.ExitCode);
    }

    [Fact]
    public async Task TheSourceItselfRefusesAMismatchAndDeletesWhatItFetched()
    {
        using var harness = new RoughChannelHarness(installedVersion: "1.0.100");
        var listed = new string('B', 64);
        var (fileName, _) = harness.Publish("1.0.200", listedSha256: listed);
        var source = new HashVerifiedUpdateSource(new DirectoryUpdateFeedTransport(harness.Feed), harness.Log);
        var feed = await source.GetReleaseFeed(harness.Locator.Log, RoughChannelHarness.PackId, "win");
        var target = Path.Combine(harness.Packages, fileName + ".partial");

        var refusal = await Assert.ThrowsAsync<UpdateHashMismatchException>(() =>
            source.DownloadReleaseEntry(harness.Locator.Log, feed.Assets[0], target, _ => { }));

        Assert.Equal(listed, refusal.Expected);
        Assert.NotEqual(listed, refusal.Actual);
        Assert.Contains(refusal.Expected, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(refusal.Actual, refusal.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    /// <summary>A server that sends more than the feed promised is cut off, not written to disk without end.</summary>
    [Fact]
    public async Task ADownloadLargerThanTheFeedPromisedIsStopped()
    {
        using var harness = new RoughChannelHarness(installedVersion: "1.0.100");
        var (fileName, _) = harness.Publish("1.0.200");
        var source = new HashVerifiedUpdateSource(new DirectoryUpdateFeedTransport(harness.Feed), harness.Log);
        var feed = await source.GetReleaseFeed(harness.Locator.Log, RoughChannelHarness.PackId, "win");
        await File.AppendAllTextAsync(Path.Combine(harness.Feed, fileName), new string('x', 200_000));
        var target = Path.Combine(harness.Packages, fileName + ".partial");

        await Assert.ThrowsAsync<UpdateFeedException>(() =>
            source.DownloadReleaseEntry(harness.Locator.Log, feed.Assets[0], target, _ => { }));

        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task AnInstalledBuildOnTheNewestVersionSaysSo()
    {
        using var harness = new RoughChannelHarness(installedVersion: "1.0.200");
        harness.Publish("1.0.200");
        var gateway = GatewayOver(harness);

        var result = await gateway.CheckAsync(CancellationToken.None);

        Assert.Equal("Up to date", result.Status);
        Assert.False(result.CanDownload);
        Assert.False(result.Failed);
    }

    /// <summary>No feed, no crash, and no claim that there is nothing newer.</summary>
    [Fact]
    public async Task AFeedThatCannotBeReadIsAFailedCheckNotAnUpToDateOne()
    {
        using var harness = new RoughChannelHarness(installedVersion: "1.0.100");
        var gateway = GatewayOver(harness);

        var result = await gateway.CheckAsync(CancellationToken.None);

        Assert.True(result.Failed);
        Assert.False(result.CanDownload);
        Assert.StartsWith("Could not check", result.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// A portable zip is a build run from a folder: it says so, offers nothing it cannot do, and
    /// knows where the installer is.
    /// </summary>
    [Fact]
    public async Task ABuildRunFromAFolderReportsThatAndOffersNothing()
    {
        // The test host was not started by an installer, which is exactly the portable case.
        var gateway = new VelopackUpdateGateway();

        Assert.False(gateway.IsInstalled);
        Assert.Equal("Running from a folder, not installed", gateway.InstalledBuild);
        var result = await gateway.CheckAsync(CancellationToken.None);
        Assert.False(result.CanDownload);
        Assert.False(result.CanApply);
        Assert.Contains("folder", result.Status, StringComparison.Ordinal);
        Assert.Equal("Check for updates first.", (await gateway.DownloadAsync(CancellationToken.None)).Status);
        gateway.ApplyAndRestart();

        Assert.Equal("https", gateway.Channel.Feed.Scheme);
        Assert.EndsWith("-Setup.exe", gateway.Channel.Installer.AbsoluteUri, StringComparison.Ordinal);
        Assert.StartsWith(gateway.Channel.Feed.AbsoluteUri, gateway.Channel.Installer.AbsoluteUri, StringComparison.Ordinal);
    }
}
