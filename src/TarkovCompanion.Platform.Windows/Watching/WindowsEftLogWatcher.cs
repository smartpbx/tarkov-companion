using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Platform.Windows.Watching;

public sealed class WindowsEftLogWatcher(EftLogParser parser, TimeProvider? timeProvider = null) : IEftLogWatcher
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async IAsyncEnumerable<RaidEvidence> WatchAsync(
        string logRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logRoot);
        if (!Directory.Exists(logRoot))
        {
            throw new DirectoryNotFoundException($"EFT log directory does not exist: {logRoot}");
        }

        var offsets = Directory.EnumerateFiles(logRoot, "*.log", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => new FileInfo(path).Length, StringComparer.OrdinalIgnoreCase);
        var changedPaths = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        using var watcher = new FileSystemWatcher(logRoot, "*.log")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, args) => changedPaths.Writer.TryWrite(args.FullPath);
        watcher.Changed += (_, args) => changedPaths.Writer.TryWrite(args.FullPath);
        watcher.Renamed += (_, args) => changedPaths.Writer.TryWrite(args.FullPath);
        using var registration = cancellationToken.Register(() => changedPaths.Writer.TryComplete());

        await foreach (var path in changedPaths.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await foreach (var line in ReadAppendedLinesAsync(path, offsets, cancellationToken).ConfigureAwait(false))
            {
                var evidence = parser.ParseLine(line, _timeProvider.GetUtcNow());
                if (evidence is not null)
                {
                    yield return evidence;
                }
            }
        }
    }

    private static async IAsyncEnumerable<string> ReadAppendedLinesAsync(
        string path,
        IDictionary<string, long> offsets,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        }
        catch (IOException)
        {
            yield break;
        }

        await using (stream.ConfigureAwait(false))
        {
            offsets.TryGetValue(path, out var offset);
            if (stream.Length < offset)
            {
                offset = 0;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                yield return line;
            }

            offsets[path] = stream.Position;
        }
    }
}
