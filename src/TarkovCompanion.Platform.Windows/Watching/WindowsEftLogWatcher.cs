using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Platform.Windows.Watching;

public sealed partial class WindowsEftLogWatcher(
    EftLogParser parser,
    IEftLogObserver? observer = null,
    TimeProvider? timeProvider = null,
    ILogger<WindowsEftLogWatcher>? logger = null) : IEftLogWatcher
{
    /// <summary>How often file lengths are re-checked when no change notification arrives.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>The last listing failure reported, so a folder that stays unreadable says so once.</summary>
    private string? _lastEnumerationProblem;

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

        // Start every existing file at its end, so the tail below delivers only what is
        // appended from now on and a restart does not re-tail hundreds of old raids. What was
        // already written is not simply discarded: the current session is replayed once, just
        // below, and that is where a quest handed in before the companion started is recovered.
        // The positions are taken before that replay, so each line is read exactly once.
        var lines = new AppendedLineReader(_timeProvider);
        foreach (var existing in EnumerateWatched(logRoot))
        {
            lines.StartAtEnd(existing.Path, SafeLength(existing.Path));
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
            foreach (var (path, mode) in EnumerateWatched(logRoot))
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
                    if (mode == LogReadMode.Full && parser.ParseLine(line, observedUtc) is { } evidence)
                    {
                        yield return evidence;
                    }

                    // The party and the flea arrive on the same lines as the raid but describe
                    // something else, so they go to their own observer rather than through
                    // raid evidence. Each parser rejects lines that are not its own on a
                    // single substring scan, so this costs almost nothing on the vast
                    // majority of lines, which are neither.
                    Notify(line, observedUtc, mode);
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

        // Every file, output included, and bounded by EftLogFiles.MaximumReplayBytes rather than by name.
        // output used to be skipped here on the grounds that its notifications are duplicated
        // into backend. They are not, on 1.1.5.x, and skipping it meant a quest handed in before
        // the companion's first poll was never seen by anything.
        var files = EnumerateWatched(folder)
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var recovery = new RaidStateService();
        foreach (var (path, mode) in files)
        {
            await ReplayAsync(path, mode, recovery, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Replays one already-written file, into the raid state machine and into the observer.
    /// </summary>
    /// <remarks>
    /// The observer is the fix for what this method used to be. It parsed each replayed line
    /// for raid evidence and threw the line away, so the quest, flea, party and queue-time
    /// parsers never saw a single line that was already in the file when watching started --
    /// and every line already in the file is also skipped by the tail, which starts at the end.
    /// The result was an app that recovered "a raid on Reserve is running" from a session and
    /// recorded not one of the quests handed in during it, which is exactly what a player who
    /// mapped a whole raid and saw no quest update was looking at. Raid lifecycle survived the
    /// gap and everything else fell into it.
    ///
    /// Replaying observations is safe because every one of them is idempotent by design: quest
    /// notifications carry their own event id and are deduplicated, a re-recorded state reports
    /// itself unchanged, flea sales are keyed by offer id, and the party is a collapsed
    /// snapshot rather than a log. Re-reading costs a second pass, not a double count.
    ///
    /// Bounded by <see cref="EftLogFiles.MaximumReplayBytes"/> from the end of the file. A session's
    /// notifications are at its end, and output can be hundreds of megabytes of keepalives.
    /// </remarks>
    private async Task ReplayAsync(
        string path,
        LogReadMode mode,
        RaidStateService recovery,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var truncated = stream.Length > EftLogFiles.MaximumReplayBytes;
            if (truncated)
            {
                stream.Seek(stream.Length - EftLogFiles.MaximumReplayBytes, SeekOrigin.Begin);
            }

            // Same splitting as the tail, for the same reason: a replay that cut a
            // multi-kilobyte userMatchOver in half would recover the wrong raid state. The
            // whole file is present, so nothing is left unterminated and the flush timer
            // never comes into it.
            var replay = new AppendedLineReader(_timeProvider, TimeSpan.Zero);
            var read = await replay.ReadAsync(path, stream, cancellationToken).ConfigureAwait(false);
            // A seek into the middle of the file lands mid-line, and half a line is worse than
            // no line: it would be offered to the JSON parsers as though it were whole.
            var first = truncated ? 1 : 0;
            for (var index = first; index < read.Count; index++)
            {
                var line = read[index];
                var observedUtc = _timeProvider.GetUtcNow();
                // Parsed for two side effects: the parser learns the profile id, and the
                // private state machine works out what the player is in the middle of.
                if (mode == LogReadMode.Full && parser.ParseLine(line, observedUtc) is { } evidence)
                {
                    recovery.Apply(evidence);
                }

                Notify(line, observedUtc, mode);
            }

            logger?.LogInformation(
                "Replayed {Lines} line(s) of {File} at startup ({Mode}).",
                read.Count - first,
                Path.GetFileName(path),
                mode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not fatal -- the tail still delivers whatever is appended next -- but no longer
            // silent. A permission or locking problem on the game's own logs looks exactly like
            // a session in which nothing happened, and that is what made this hard to find.
            logger?.LogWarning(
                exception,
                "Could not replay {File}; anything already written to it is lost to this session.",
                Path.GetFileName(path));
        }
    }

    /// <summary>
    /// Hands one line to the parsers that describe something other than the raid.
    /// </summary>
    /// <remarks>
    /// One place, called from both the tail and the startup replay, because those two having
    /// separate copies of this is the whole bug: the tail had it and the replay did not.
    ///
    /// <see cref="LogReadMode.ChatOnly"/> is the narrow reading of a file that is otherwise not
    /// opened. Such a line has to carry a quest or flea marker to be looked at at all, and it
    /// is offered to those two parsers only -- never the party parser, whose payloads are the
    /// reason the file is treated this way.
    /// </remarks>
    private void Notify(string line, DateTimeOffset observedUtc, LogReadMode mode)
    {
        if (observer is null)
        {
            return;
        }

        if (mode == LogReadMode.ChatOnly && !EftLogFiles.IsChatNotification(line))
        {
            return;
        }

        if (mode == LogReadMode.Full && GroupNotificationParser.ParseLine(line, observedUtc) is { } group)
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

        if (mode == LogReadMode.Full && LoadTimeParser.ParseLine(line, observedUtc) is { } loadTime)
        {
            observer.Observe(loadTime);
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

    /// <summary>
    /// The game's log files this watcher will open, and how much of each it will read.
    /// </summary>
    /// <remarks>
    /// This used to swallow an unreadable log root and return nothing, which is
    /// indistinguishable from a game that has written nothing: "watching logs: True, nothing
    /// read yet" forever, with no way to tell a permission problem from an idle game. The
    /// enumeration is still non-fatal, because the folder may legitimately appear later, but it
    /// now says so once per distinct problem.
    /// </remarks>
    private IEnumerable<WatchedLogFile> EnumerateWatched(string logRoot)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(logRoot, "*.log", SearchOption.AllDirectories);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (_lastEnumerationProblem != exception.Message)
            {
                _lastEnumerationProblem = exception.Message;
                logger?.LogWarning(
                    exception,
                    "Could not list the game's log files under {Root}; nothing will be read from it.",
                    logRoot);
            }

            yield break;
        }

        _lastEnumerationProblem = null;
        foreach (var path in files)
        {
            if (EftLogFiles.ReadMode(path) is { } mode)
            {
                yield return new(path, mode);
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

    /// <summary>How much of a log file is read.</summary>
    /// <param name="Path">The file.</param>
    /// <param name="Mode">Everything in it, or only its chat notifications.</param>
    private readonly record struct WatchedLogFile(string Path, LogReadMode Mode);

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
