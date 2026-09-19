using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.UnitTests.Raids;

public sealed class EftLogFolderGameVersionSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tarkov-logroot-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("log_2026.09.10_12-00-00_1.1.5.0.47242", "1.1.5.0.47242")]
    [InlineData("log_2026.09.10_12-00-00_0.16.9.0.39000", "0.16.9.0.39000")]
    [InlineData("log_2026.09.10_12-00-00", null)]
    [InlineData("crashes", null)]
    [InlineData("log_2026.09.10_12-00-00_1.1", null)]
    [InlineData(null, null)]
    public void The_version_is_the_dotted_build_after_the_stamp(string? folder, string? expected) =>
        Assert.Equal(expected, EftLogFolderGameVersionSource.ParseFolderName(folder));

    [Fact]
    public async Task The_newest_log_folder_names_the_running_build_and_no_folder_means_unknown()
    {
        var older = Directory.CreateDirectory(Path.Combine(_root, "log_2026.09.01_10-00-00_1.1.4.0.46000"));
        var newer = Directory.CreateDirectory(Path.Combine(_root, "log_2026.09.10_12-00-00_1.1.5.0.47242"));
        older.LastWriteTimeUtc = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        newer.LastWriteTimeUtc = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

        var source = new EftLogFolderGameVersionSource(_ => Task.FromResult<string?>(_root));
        Assert.Equal("1.1.5.0.47242", await source.GetAsync(CancellationToken.None));

        var absent = new EftLogFolderGameVersionSource(_ => Task.FromResult<string?>(Path.Combine(_root, "nope")));
        Assert.Null(await absent.GetAsync(CancellationToken.None));
        var unlocated = new EftLogFolderGameVersionSource(_ => Task.FromResult<string?>(null));
        Assert.Null(await unlocated.GetAsync(CancellationToken.None));
    }
}
