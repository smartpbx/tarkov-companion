using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.UnitTests;

public sealed class QuestScreenshotImageSourceTests
{
    [Fact]
    public async Task RecentScreenshotsAreFilteredAndLoadedOldestFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), $"quest-screenshot-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var old = Touch(root, "old.png", DateTime.UtcNow.AddMinutes(-20));
            var second = Touch(root, "second.jpg", DateTime.UtcNow.AddMinutes(-8));
            var third = Touch(root, "third.png", DateTime.UtcNow.AddMinutes(-2));
            Touch(root, "notes.txt", DateTime.UtcNow.AddMinutes(-1));
            var loader = new RecordingLoader();
            var source = new QuestScreenshotImageSource(loader);

            var result = await source.LoadRecentAsync(
                root,
                DateTimeOffset.UtcNow.AddMinutes(-10),
                CancellationToken.None);

            Assert.Equal(2, result.Images.Count);
            Assert.Equal([second, third], loader.Paths);
            Assert.DoesNotContain(old, loader.Paths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecentReadIsBoundedToTwelveNewestImages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"quest-screenshot-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < 15; index++)
            {
                Touch(root, $"{index:D2}.png", DateTime.UtcNow.AddSeconds(index));
            }

            var loader = new RecordingLoader();
            var source = new QuestScreenshotImageSource(loader);

            var result = await source.LoadRecentAsync(root, DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

            Assert.Equal(12, result.Images.Count);
            Assert.DoesNotContain(loader.Paths, path => Path.GetFileName(path) is "00.png" or "01.png" or "02.png");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Touch(string root, string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, [0]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private sealed class RecordingLoader : IScreenshotImageLoader
    {
        public List<string> Paths { get; } = [];

        public Task<CapturedImage?> LoadAsync(string path, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult<CapturedImage?>(new(
                new byte[4],
                1,
                1,
                4,
                PixelFormat.Bgra8888,
                DateTimeOffset.UnixEpoch,
                path));
        }
    }
}
