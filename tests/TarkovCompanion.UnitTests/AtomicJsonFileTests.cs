using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.UnitTests;

public sealed class AtomicJsonFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "tarkov-atomic-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A write that fails leaves the previous file, not a truncated one.
    /// </summary>
    /// <remarks>
    /// File.WriteAllTextAsync truncates before it writes, so a process killed inside that
    /// window left a valid path holding nothing. The group settings reader maps the resulting
    /// JsonException to "off", so the player was silently not sharing.
    /// </remarks>
    [Fact]
    public async Task TheFileIsEitherTheOldContentsOrTheNewOnes()
    {
        var path = Path.Combine(_directory, "settings.json");
        await AtomicJsonFile.WriteAsync(path, """{"enabled":true}""", default);
        await AtomicJsonFile.WriteAsync(path, """{"enabled":false}""", default);

        Assert.Equal("""{"enabled":false}""", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".writing"), "the scratch file must not be left behind");
    }

    [Fact]
    public async Task ItCreatesTheDirectoryItIsGiven()
    {
        var path = Path.Combine(_directory, "nested", "deeper", "settings.json");

        await AtomicJsonFile.WriteAsync(path, "{}", default);

        Assert.True(File.Exists(path));
    }

    /// <summary>An unreadable file is kept, renamed, rather than thrown away.</summary>
    /// <remarks>
    /// It is the only remaining record of what somebody had configured, and "your settings
    /// were reset" is a great deal easier to believe when the old file is still sitting there.
    /// </remarks>
    [Fact]
    public async Task AnUnreadableFileIsMovedAsideAndKept()
    {
        var path = Path.Combine(_directory, "settings.json");
        await AtomicJsonFile.WriteAsync(path, "{ truncated", default);

        var aside = AtomicJsonFile.SetAside(path, new DateTimeOffset(2026, 9, 13, 20, 30, 0, TimeSpan.Zero));

        Assert.NotNull(aside);
        Assert.False(File.Exists(path));
        Assert.Equal("{ truncated", await File.ReadAllTextAsync(aside!));
        Assert.Contains("corrupt-20260913203000", aside, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingAsideNothingIsNotAnError() =>
        Assert.Null(AtomicJsonFile.SetAside(Path.Combine(_directory, "absent.json"), DateTimeOffset.UnixEpoch));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
