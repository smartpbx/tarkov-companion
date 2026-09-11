using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Platform.Windows.Watching;

public sealed class WindowsEftLogWatcher(EftLogParser parser, TimeProvider? timeProvider = null) : IEftLogWatcher
{
    /// <summary>
    /// The log files this watcher will read.
    /// </summary>
    /// <remarks>
    /// Reading every *.log in the folder was a privacy problem, not just wasted work. The
    /// game's backend and push-notification logs carry large JSON blobs containing real
    /// personal data for the player and for anyone they grouped with: nicknames, account and
    /// profile ids, full inventories, health state, and looted dogtags naming a killer and a
    /// victim. None of that is needed to tell which map a raid is on, so none of it is opened.
    ///
    /// application carries the map and lifecycle markers. output is the only file still
    /// written throughout a raid, so it is what can say the player is still in one. backend
    /// carries the userConfirmed and userMatchOver notifications that give an exact raid
    /// start, end and duration for the player; it is opened for those and nothing else, and
    /// the parser attributes a notification to the player only when its profile id matches
    /// theirs, so a teammate's record is never read as the player's own.
    ///
    /// push-notifications stays closed. Its group blobs carry teammates' full inventories,
    /// health and looted dogtags, and nothing here needs them.
    /// </remarks>
    private static readonly string[] WatchedPrefixes = ["application", "output", "backend"];

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
            .Where(IsWatched)
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
        watcher.Created += (_, args) => Offer(args.FullPath);
        watcher.Changed += (_, args) => Offer(args.FullPath);
        watcher.Renamed += (_, args) => Offer(args.FullPath);

        void Offer(string path)
        {
            if (IsWatched(path))
            {
                changedPaths.Writer.TryWrite(path);
            }
        }
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

    /// <summary>Whether a log file is one the companion has any reason to open.</summary>
    /// <remarks>
    /// The game names each file "&lt;session stamp&gt; &lt;prefix&gt;_000.log", so the prefix is matched
    /// within the name rather than at its start.
    /// </remarks>
    private static bool IsWatched(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path.AsSpan());
        foreach (var prefix in WatchedPrefixes)
        {
            var index = name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            // "backend" must not match on "end", and "push-notifications" must not match at
            // all, so the prefix has to begin a word.
            if (index == 0 || name[index - 1] is ' ' or '_' or '-' or '.')
            {
                return true;
            }
        }

        return false;
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
