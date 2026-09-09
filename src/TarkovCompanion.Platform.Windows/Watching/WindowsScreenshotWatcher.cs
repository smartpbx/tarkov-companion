using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Platform.Windows.Watching;

public sealed class WindowsScreenshotWatcher(bool developerMode = false) : IScreenshotWatcher
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    public async IAsyncEnumerable<string> WatchAsync(
        string screenshotRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenshotRoot);
        if (!Directory.Exists(screenshotRoot))
        {
            throw new DirectoryNotFoundException($"EFT screenshot directory does not exist: {screenshotRoot}");
        }

        var paths = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        using var watcher = new FileSystemWatcher(screenshotRoot)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, args) => paths.Writer.TryWrite(args.FullPath);
        watcher.Renamed += (_, args) => paths.Writer.TryWrite(args.FullPath);
        using var registration = cancellationToken.Register(() => paths.Writer.TryComplete());

        await foreach (var path in paths.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (SupportedExtensions.Contains(Path.GetExtension(path))
                && (developerMode || !path.Contains("EftSimulator", StringComparison.OrdinalIgnoreCase)))
            {
                yield return path;
            }
        }
    }
}
