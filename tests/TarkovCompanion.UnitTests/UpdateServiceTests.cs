using System.Net.Http;
using TarkovCompanion.Application.Services.Updates;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Reading what the running build is, which is what an update check compares against.
/// </summary>
public sealed class UpdateServiceTests
{
    [Fact]
    public void ReadsTheStampPackagingLeavesBesideTheApplication()
    {
        var directory = Directory.CreateTempSubdirectory("tarkov-build-info");
        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "BUILD_INFO.txt"),
                "version=1.0.0\ncommit=d228c9c3cd2636bf8ed8209536037113751bf636\nbuilt_utc=2026-09-12T02:17:07Z\n");

            var installed = Service(directory.FullName).ReadInstalled();

            Assert.Equal("1.0.0", installed.Version);
            Assert.Equal("d228c9c3cd2636bf8ed8209536037113751bf636", installed.Commit);
            Assert.Equal("d228c9c", installed.ShortCommit);
            Assert.Equal(new DateTimeOffset(2026, 9, 12, 2, 17, 7, TimeSpan.Zero), installed.BuiltUtc);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A build run from source has no stamp, and says so rather than inventing one.
    /// </summary>
    /// <remarks>
    /// An unknown build must never be told it is out of date, because there is nothing to
    /// compare it against and offering to replace a developer's own build would be wrong.
    /// </remarks>
    [Fact]
    public void ReportsAnUnstampedBuildAsUnknown()
    {
        var directory = Directory.CreateTempSubdirectory("tarkov-build-info");
        try
        {
            var installed = Service(directory.FullName).ReadInstalled();

            Assert.Equal("unknown", installed.Version);
            Assert.Null(installed.Commit);
            Assert.Equal("unknown", installed.ShortCommit);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The release is addressed by tag and its assets by id, which is what the API offers.
    /// </summary>
    [Fact]
    public void AddressesTheRollingReleaseAndItsAssets()
    {
        var options = UpdateOptions.CreateDefault("C:\\app", "C:\\cache");

        Assert.Equal(
            "https://api.github.com/repos/smartpbx/tarkov-companion/releases/tags/dev",
            options.ReleaseUri.ToString());
        Assert.Equal(
            "https://api.github.com/repos/smartpbx/tarkov-companion/releases/assets/42",
            options.AssetUri(42).ToString());
    }

    private static UpdateService Service(string installDirectory) => new(
        new HttpClient(),
        UpdateOptions.CreateDefault(installDirectory, Path.GetTempPath()));
}
