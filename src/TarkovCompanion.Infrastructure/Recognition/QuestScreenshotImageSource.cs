using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>Loads only pictures the player selected or the game recently wrote.</summary>
/// <remarks>
/// The recent-file path is bounded before pixels are decoded. Screenshot folders can hold years
/// of captures, and onboarding should never turn that history into an unbounded memory read.
/// </remarks>
public sealed class QuestScreenshotImageSource(IScreenshotImageLoader loader) : IQuestScreenshotImageSource
{
    private const int RecentLimit = 12;
    private static readonly HashSet<string> ImageExtensions = new(
        [".png", ".jpg", ".jpeg", ".bmp", ".webp"],
        StringComparer.OrdinalIgnoreCase);

    public Task<QuestScreenshotImageLoad> LoadFilesAsync(
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken) =>
        LoadAsync(paths.Where(IsImage).Distinct(StringComparer.OrdinalIgnoreCase), cancellationToken);

    public async Task<QuestScreenshotImageLoad> LoadRecentAsync(
        string? screenshotRoot,
        DateTimeOffset takenSinceUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(screenshotRoot) || !Directory.Exists(screenshotRoot))
        {
            return new([], 0, "The screenshot folder is not available.");
        }

        try
        {
            var recent = new DirectoryInfo(screenshotRoot)
                .EnumerateFiles()
                .Where(file => IsImage(file.FullName) && file.LastWriteTimeUtc >= takenSinceUtc.UtcDateTime)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(RecentLimit)
                .OrderBy(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .ToArray();
            return await LoadAsync(recent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new([], 0, $"The screenshot folder could not be read: {exception.Message}");
        }
    }

    private async Task<QuestScreenshotImageLoad> LoadAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var images = new List<CapturedImage>();
        var skipped = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var image = await loader.LoadAsync(path, cancellationToken).ConfigureAwait(false);
                if (image is null)
                {
                    skipped++;
                }
                else
                {
                    images.Add(image);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        return new(images, skipped);
    }

    private static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
}
