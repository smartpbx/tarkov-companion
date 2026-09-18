using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Platform.Windows.Watching;

public sealed partial class WindowsEftLogWatcher(
    EftLogParser parser,
    IEftLogObserver? observer = null,
    TimeProvider? timeProvider = null) : IEftLogWatcher
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
        var lines = new AppendedLineReader(_timeProvider);
        foreach (var existing in EnumerateWatched(logRoot))
        {
            lines.StartAtEnd(existing, SafeLength(existing));
        }

        // The player signs in before starting the companion in the ordinary case, so the line
        // naming their profile is already in the file and would never be tailed. Without it
        // no notification can be attributed to them and raid start and end never fire, so the
        // current session is read once up front to learn who they are, and to catch up on a
        // raid that is already running.
        var resumed = await ReadCurrentSessionAsync(logRoot, cancellationToken).ConfigureAwait(false);
        if (resumed is not null)
        {
            yield return resumed;
        }

        using var woken = new SemaphoreSlim(0, 1);
        using var watcher = CreateChangeSignal(logRoot, woken);

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var path in EnumerateWatched(logRoot))
            {
                // A file that has not grown is still polled while it holds a part-written
                // line, or that line would never be completed and the last entry of a rolled
                // log would be dropped.
                if (SafeLength(path) <= lines.Position(path) && !lines.HasPending(path))
                {
                    continue;
                }

                foreach (var line in await ReadAppendedLinesAsync(path, lines, cancellationToken)
                             .ConfigureAwait(false))
                {
                    var observedUtc = _timeProvider.GetUtcNow();
                    var evidence = parser.ParseLine(line, observedUtc);
                    if (evidence is not null)
                    {
                        yield return evidence;
                    }

                    // The party and the flea arrive on the same lines as the raid but describe
                    // something else, so they go to their own observer rather than through
                    // raid evidence. Each parser rejects lines that are not its own on a
                    // single substring scan, so this costs almost nothing on the vast
                    // majority of lines, which are neither.
                    if (observer is null)
                    {
                        continue;
                    }

                    if (GroupNotificationParser.ParseLine(line, observedUtc) is { } group)
                    {
                        observer.Observe(group);
                    }

                    if (FleaSaleParser.ParseLine(line, observedUtc) is { } sale)
                    {
                        observer.Observe(sale);
                    }

                    if (QuestNotificationParser.ParseLine(line, observedUtc) is { } quest)
                    {
                        observer.Observe(quest);
                    }

                    if (LoadTimeParser.ParseLine(line, observedUtc) is { } loadTime)
                    {
                        observer.Observe(loadTime);
                    }
                }
            }

            await WaitForChangeAsync(woken, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the current game session once, to learn the player and to catch up on a raid
    /// that is already running.
    /// </summary>
    /// <remarks>
    /// Two things are recovered here and neither can be got by tailing.
    ///
    /// The profile id, because the player signs in before starting the companion, so the line
    /// naming them is already in the file and would never be tailed. Without it no
    /// notification can be attributed to them.
    ///
    /// And the raid in progress. A companion started sixty-nine seconds into a raid missed the
    /// notification that carries the map, inferred the raid only from ongoing in-raid chatter,
    /// which names no map, and sat on "map none" for the rest of it. Replaying the session into
    /// a private state machine recovers what the player is actually in the middle of; it is
    /// returned only when that state machine ends in a raid, so a session whose last raid
    /// finished replays nothing.
    /// </remarks>
    private async Task<RaidEvidence?> ReadCurrentSessionAsync(string logRoot, CancellationToken cancellationToken)
    {
        var folder = CurrentSessionFolder(logRoot);
        if (folder is null)
        {
            return null;
        }

        // output is skipped. It is the largest file by far, it is mostly keepalives, and every
        // notification it carries is duplicated into backend, which is small.
        var files = EnumerateWatched(folder)
            .Where(path => !Path.GetFileNameWithoutExtension(path)
                .Contains("output", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var recovery = new RaidStateService();
        foreach (var path in files)
        {
            await ReplayAsync(path, recovery, cancellationToken).ConfigureAwait(false);
        }

        var current = recovery.Current;
        if (current.State is not (RaidLifecycleState.InRaid or RaidLifecycleState.LoadingRaid))
        {
            return null;
        }

        return new RaidEvidence(
            RaidEvidenceKind.LogLine,
            _timeProvider.GetUtcNow(),
            current.MapId,
            current.State,
            current.Confidence,
            current.MapId is null
                ? "A raid was already running when the companion started."
                : $"A raid on {current.MapId} was already running when the companion started.")
        {
            Side = current.Side,
            SideBasis = current.SideBasis,
            // Says that this raid may already have a row. Every other piece of evidence
            // arrives from a line written while this process was watching, so this is the only
            // one where the previous run of the companion could have been recording it.
            ResumesSession = true,
        };
    }

    /// <summary>
    /// Names the folder the game is writing to now, from the folder's own name.
    /// </summary>
    /// <remarks>
    /// Not by modification time. The game stamps each folder with the session's start time
    /// when it creates it, and that name cannot drift afterwards; file timestamps can and do.
    /// On a machine whose clock stepped back four hours mid-session, the previous session's
    /// files carried timestamps in the future and a newest-by-time sort chose the dead folder
    /// over the live one. Reading a finished session as though it were current is the same
    /// class of error as reading no session at all, and harder to notice.
    ///
    /// Falls back to the root itself when no folder name parses, which is what an installation
    /// writing logs directly into the root looks like.
    /// </remarks>
    private static string? CurrentSessionFolder(string logRoot)
    {
        try
        {
            var newest = Directory.EnumerateDirectories(logRoot)
                .Select(path => (Path: path, Started: SessionStart(Path.GetFileName(path))))
                .Where(entry => entry.Started is not null)
                .OrderByDescending(entry => entry.Started!.Value)
                .Select(entry => entry.Path)
                .FirstOrDefault();
            return newest ?? logRoot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return logRoot;
        }
    }

    private static DateTime? SessionStart(string? folderName)
    {
        if (folderName is null)
        {
            return null;
        }

        var match = SessionFolderPattern().Match(folderName);
        return match.Success && DateTime.TryParseExact(
            match.Groups["stamp"].Value,
            "yyyy.MM.dd_HH-mm-ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var started)
                ? started
                : null;
    }

    private async Task ReplayAsync(string path, RaidStateService recovery, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            // Same splitting as the tail, for the same reason: a replay that cut a
            // multi-kilobyte userMatchOver in half would recover the wrong raid state. The
            // whole file is present, so nothing is left unterminated and the flush timer
            // never comes into it.
            var replay = new AppendedLineReader(_timeProvider, TimeSpan.Zero);
            foreach (var line in await replay.ReadAsync(path, stream, cancellationToken).ConfigureAwait(false))
            {
                // Parsed for two side effects: the parser learns the profile id, and the
                // private state machine works out what the player is in the middle of.
                if (parser.ParseLine(line, _timeProvider.GetUtcNow()) is { } evidence)
                {
                    recovery.Apply(evidence);
                }
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

    /// <summary>The session start the game writes into each log folder's name.</summary>
    [GeneratedRegex(@"^log_(?<stamp>\d{4}\.\d{2}\.\d{2}_\d{2}-\d{2}-\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex SessionFolderPattern();

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

    /// <summary>
    /// The lines this file has finished writing since the last poll.
    /// </summary>
    /// <remarks>
    /// The splitting lives in <see cref="AppendedLineReader"/>, in Application, so the Linux
    /// suite can pin it. This is only the file handling: opening the log without disturbing
    /// the game's own writes, and treating a momentary lock as a skip.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> ReadAppendedLinesAsync(
        string path,
        AppendedLineReader lines,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        }
        catch (IOException)
        {
            // The game may hold the file exclusively for an instant while it rolls. The next
            // poll picks it up, so this is a skip rather than a failure.
            return [];
        }

        await using (stream.ConfigureAwait(false))
        {
            return await lines.ReadAsync(path, stream, cancellationToken).ConfigureAwait(false);
        }
    }
}
