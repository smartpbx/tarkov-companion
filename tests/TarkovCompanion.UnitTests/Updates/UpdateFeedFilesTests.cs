using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests.Updates;

/// <summary>What the relay will and will not serve from its update folder.</summary>
public sealed class UpdateFeedFilesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "tarkov-updates");

    [Theory]
    [InlineData("rough", "releases.win.json")]
    [InlineData("rough", "TarkovCompanionDesktop-1.0.1301-full.nupkg")]
    [InlineData("rough", "TarkovCompanionDesktop-win-Setup.exe")]
    [InlineData("rough", "RELEASES")]
    public void APublishedFileResolvesInsideItsChannel(string channel, string file)
    {
        Assert.Equal(Path.Combine(Root, channel, file), UpdateFeedFiles.Resolve(Root, channel, file));
    }

    [Theory]
    [InlineData("rough", "../rooms.json")]
    [InlineData("rough", "..")]
    [InlineData("rough", "a/../../b")]
    [InlineData("rough", "sub/file.nupkg")]
    [InlineData("rough", "sub\\file.nupkg")]
    [InlineData("rough", ".hidden")]
    [InlineData("rough", "a..b")]
    [InlineData("rough", "")]
    [InlineData("rough", null)]
    [InlineData("..", "releases.win.json")]
    [InlineData("Rough", "releases.win.json")]
    [InlineData("rough/x", "releases.win.json")]
    [InlineData("", "releases.win.json")]
    [InlineData(null, "releases.win.json")]
    public void ANameThatCouldLeaveTheFolderIsNotServed(string? channel, string? file)
    {
        Assert.Null(UpdateFeedFiles.Resolve(Root, channel, file));
    }

    /// <summary>Only a file named after its version may be cached; the feed and the installer keep their names.</summary>
    [Fact]
    public void OnlyAVersionedPackageMayBeCached()
    {
        Assert.True(UpdateFeedFiles.IsCacheable("TarkovCompanionDesktop-1.0.1301-full.nupkg"));
        Assert.False(UpdateFeedFiles.IsCacheable("releases.win.json"));
        Assert.False(UpdateFeedFiles.IsCacheable("TarkovCompanionDesktop-win-Setup.exe"));
        Assert.Equal("application/json", UpdateFeedFiles.ContentType("releases.win.json"));
        Assert.Equal("application/octet-stream", UpdateFeedFiles.ContentType("TarkovCompanionDesktop-win-Setup.exe"));
    }
}
