using System.Runtime.CompilerServices;
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

    /// <summary>How often file lengths are re-checked when no change notification arrives.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Tails the game's logs and turns appended lines into raid evidence.
    /// </summary>
    /// <remarks>
    /// This polls file lengths rather than trusting change notifications. Windows does not
    /// reliably update a directory entry while a process holds the file open and appends to
    /// it, which is exactly what the game does, so a notification-only watcher can sit silent
    /// through an entire raid. A change notification is still used, but only to wake the loop
    /// early; the poll is what guarantees the lines arrive.
    ///
    /// The game also creates a new folder per launch, usually after the companion has already
    /// started, so the scan is re-run each cycle rather than fixed at the first one.
    /// </remarks>
    public async IAsyncEnumerable<RaidEvidence> WatchAsync(
        string logRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logRoot);
        if (!Directory.Exists(logRoot))
        {
            throw new DirectoryNotFoundException($"EFT log directory does not exist: {logRoot}");
        }

        // Files present when watching starts are already-finished sessions: start at their end
        // so a restart does not replay hundreds of old raids.
        var offsets = EnumerateWatched(logRoot)
            .ToDictionary(path => path, SafeLength, StringComparer.OrdinalIgnoreCase);

        // The player signs in before starting the companion in the ordinary case, so the line
        // naming their profile is already in the file and would never be tailed. Without it
        // no notification can be attributed to them and raid start and end never fire, so the
        // newest session is read once up front purely to learn who they are.
        await LearnIdentityAsync(logRoot, cancellationToken).ConfigureAwait(false);

        using var woken = new SemaphoreSlim(0, 1);
        using var watcher = CreateChangeSignal(logRoot, woken);

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var path in EnumerateWatched(logRoot))
            {
                if (offsets.TryGetValue(path, out var seen) && SafeLength(path) <= seen)
                {
                    continue;
                }

                await foreach (var line in ReadAppendedLinesAsync(path, offsets, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    var evidence = parser.ParseLine(line, _timeProvider.GetUtcNow());
                    if (evidence is not null)
                    {
                        yield return evidence;
                    }
                }
            }

            await WaitForChangeAsync(woken, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LearnIdentityAsync(string logRoot, CancellationToken cancellationToken)
    {
        var newest = EnumerateWatched(logRoot)
            .Where(path => Path.GetFileNameWithoutExtension(path)
                .Contains("application", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (newest is null)
        {
            return;
        }

        try
        {
            await using var stream = new FileStream(
                newest,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                // Parsed for the side effect of learning the profile id. Any evidence this
                // produces describes a finished session and is deliberately discarded.
                _ = parser.ParseLine(line, _timeProvider.GetUtcNow());
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static FileSystemWatcher? CreateChangeSignal(string logRoot, SemaphoreSlim woken)
    {
        try
        {
            var watcher = new FileSystemWatcher(logRoot, "*.log")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            void Wake(object? sender, FileSystemEventArgs arguments)
            {
                if (woken.CurrentCount == 0)
                {
                    try
                    {
                        woken.Release();
                    }
                    catch (SemaphoreFullException)
                    {
                    }
                }
            }

            watcher.Created += Wake;
            watcher.Changed += Wake;
            watcher.Renamed += (sender, arguments) => Wake(sender, arguments);
            return watcher;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the early wake-up only costs latency; the poll still delivers the lines.
            return null;
        }
    }

    private static async Task WaitForChangeAsync(SemaphoreSlim woken, CancellationToken cancellationToken)
    {
        try
        {
            await woken.WaitAsync(PollInterval, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static IEnumerable<string> EnumerateWatched(string logRoot)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(logRoot, "*.log", SearchOption.AllDirectories);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var path in files)
        {
            if (IsWatched(path))
            {
                yield return path;
            }
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
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
