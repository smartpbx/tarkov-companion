using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Covers the promises the screenshot sweep makes, because it removes the player's own files.
/// </summary>
public sealed class ScreenshotRetentionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 20, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Directory.CreateTempSubdirectory("tarkov-retention").FullName;
    private readonly RecordingRecycleBin _bin = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TidiesScreenshotsOlderThanTheRetentionWindow()
    {
        Write("2026-09-10[14-05]_1.0, 2.0, 3.0_0.0, 0.0, 0.0, 1.0_12.34 (0).png", Now.AddHours(-48));
        Write("2026-09-12[19-00]_4.0, 5.0, 6.0_0.0, 0.0, 0.0, 1.0_12.34 (0).png", Now.AddMinutes(-60));

        var tidied = Tidy();

        Assert.Equal(1, tidied);
        Assert.Contains(_bin.Recycled, path => path.Contains("2026-09-10", StringComparison.Ordinal));
    }

    [Fact]
    public void NeverTidiesTheNewestScreenshotHoweverOldItIs()
    {
        // The map may still be showing a position read from it, and a folder that empties
        // itself completely after a break from the game reads as a bug rather than as tidying.
        Write("2026-01-01[10-00]_1.0, 2.0, 3.0_0.0, 0.0, 0.0, 1.0_12.34 (0).png", Now.AddDays(-90));

        Assert.Equal(0, Tidy());
        Assert.Empty(_bin.Recycled);
    }

    [Fact]
    public void LeavesFilesTheGameDidNotName()
    {
        // The folder belongs to the player and may hold anything. A document somebody saved
        // into it is not this application's business, whatever its age.
        Write("holiday.png", Now.AddDays(-30));
        Write("notes.txt", Now.AddDays(-30));
        Write("2026-09-12[19-00]_4.0, 5.0, 6.0_0.0, 0.0, 0.0, 1.0_12.34 (0).png", Now.AddMinutes(-5));

        Assert.Equal(0, Tidy());
        Assert.Empty(_bin.Recycled);
    }

    [Fact]
    public void TidiesNothingWhenTurnedOff()
    {
        Write("2026-09-01[10-00]_1.0, 2.0, 3.0_0.0, 0.0, 0.0, 1.0_12.34 (0).png", Now.AddDays(-10));
        Write("2026-09-12[19-00]_4.0, 5.0, 6.0_0.0, 0.0, 0.0, 1.0_12.34 (0).png", Now.AddMinutes(-5));

        Assert.Equal(0, Tidy(ScreenshotRetentionSettings.Default with { IsEnabled = false }));
        Assert.Empty(_bin.Recycled);
    }

    [Fact]
    public void TidiesNothingWhereThereIsNoRecycleBin()
    {
        // Deleting outright would keep half the promise, which is worse than keeping none.
        Write("2026-09-01[10-00]_1.0, 2.0, 3.0_0.0, 0.0, 0.0, 1.0_12.34 (0).png", Now.AddDays(-10));
        Write("2026-09-12[19-00]_4.0, 5.0, 6.0_0.0, 0.0, 0.0, 1.0_12.34 (0).png", Now.AddMinutes(-5));

        var service = new ScreenshotRetentionService(new UnavailableRecycleBin(), new FixedClock(Now));

        Assert.Equal(0, service.Tidy(_folder, ScreenshotRetentionSettings.Default));
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void ReportsNothingForAFolderThatIsNotThere()
    {
        var service = new ScreenshotRetentionService(_bin, new FixedClock(Now));

        Assert.Equal(0, service.Tidy(Path.Combine(_folder, "gone"), ScreenshotRetentionSettings.Default));
    }

    [Fact]
    public void KeepsAHandEditedWindowInsideSensibleBounds()
    {
        Assert.Equal(1, (ScreenshotRetentionSettings.Default with { RetentionHours = 0 }).SafeRetentionHours);
        Assert.Equal(720, (ScreenshotRetentionSettings.Default with { RetentionHours = 100_000 }).SafeRetentionHours);
    }

    private int Tidy(ScreenshotRetentionSettings? settings = null) =>
        new ScreenshotRetentionService(_bin, new FixedClock(Now))
            .Tidy(_folder, settings ?? ScreenshotRetentionSettings.Default);

    private void Write(string name, DateTimeOffset writtenUtc)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "picture");
        File.SetLastWriteTimeUtc(path, writtenUtc.UtcDateTime);
    }

    private sealed class RecordingRecycleBin : IRecycleBin
    {
        public List<string> Recycled { get; } = [];

        public bool IsAvailable => true;

        public bool Recycle(string path)
        {
            Recycled.Add(path);
            File.Delete(path);
            return true;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
