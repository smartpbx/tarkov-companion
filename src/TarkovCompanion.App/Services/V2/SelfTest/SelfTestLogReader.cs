using System.Globalization;
using System.Text.RegularExpressions;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.App.Localization;

namespace TarkovCompanion.App.Services.V2.SelfTest;

/// <summary>
/// Reads the newest game session once and says what this build understood of it.
/// </summary>
/// <remarks>
/// The watcher tails these files for a living, and that is exactly why it cannot answer this
/// question: it reports what has happened since it started, so a session it read nothing from
/// and a session in which nothing happened produce the same silence. This replays the session's
/// own lines through the same parsers, from the beginning, and counts what came back.
///
/// Every file of the session that the watcher would open, not the newest one. It used to read
/// one file -- whichever had been written to last -- and report its counts as the session's. A
/// player whose quests were announced in a file it never looked at was told "0 quest
/// notification(s)" for a raid in which he had handed several in, and that reading passed. The
/// file set now comes from <see cref="EftLogFiles"/>, which is also what the watcher uses, so
/// this describes the watcher rather than something adjacent to it.
///
/// Read-only and bounded. Each file is opened with full sharing because the game is holding it
/// open and appending, only the last <see cref="MaximumBytes"/> of each are read, and the line
/// budget stops a pathological file rather than trusting it to end.
/// </remarks>
public sealed partial class SelfTestLogReader(TimeProvider? timeProvider = null)
{
    /// <summary>How much of the newest file is read. A full session is smaller than this.</summary>
    public const long MaximumBytes = 8L * 1024 * 1024;

    public const int MaximumLines = 200_000;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<SelfTestLogs> ReadAsync(string? logRoot, CancellationToken cancellationToken)
    {
        var nowUtc = _clock.GetUtcNow();
        if (string.IsNullOrWhiteSpace(logRoot))
        {
            return Nothing(nowUtc, SetupText.ProbeLogsNoFolderChosen);
        }

        if (!Directory.Exists(logRoot))
        {
            return Nothing(nowUtc, SetupText.ProbeLogsFolderMissing(logRoot));
        }

        var session = NewestSession(logRoot);
        if (session is null)
        {
            return Nothing(nowUtc, SetupText.ProbeLogsNoSession(logRoot));
        }

        var files = ReadableFiles(session.Value.Path);
        if (files.Count == 0)
        {
            return Nothing(
                nowUtc,
                SetupText.ProbeLogsNoReadableFile(Path.GetFileName(session.Value.Path)));
        }

        return await ReplayAsync(session.Value, files, nowUtc, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replays every readable file of the session, in the order the watcher reads them.
    /// </summary>
    /// <remarks>
    /// One raid state machine and one parser across the whole session, as the watcher has, so
    /// the raid count means the same thing here as it does there. A file that cannot be read is
    /// recorded against that file and the rest are still read: one locked log is not a reason to
    /// report nothing about the session.
    /// </remarks>
    private async Task<SelfTestLogs> ReplayAsync(
        (string Path, DateTimeOffset? StartedUtc) session,
        IReadOnlyList<(string Path, LogReadMode Mode)> files,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var parser = new EftLogParser();
        var raids = new RaidStateService();
        var observedUtc = session.StartedUtc ?? nowUtc;
        var read = new List<SelfTestLogFile>(files.Count);
        var totalBytes = 0L;
        var totalLines = 0;
        var raidsSeen = 0;
        var quests = 0;
        var sales = 0;
        TimeSpan? queue = null;
        string? lastMap = null;
        string? lastState = null;
        DateTimeOffset? lastRaidAt = null;
        var wasInRaid = false;

        foreach (var (path, mode) in files)
        {
            var name = Path.GetFileName(path);
            long bytes;
            try
            {
                bytes = new FileInfo(path).Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                read.Add(new(name, mode, 0, 0, 0, 0, exception.Message));
                continue;
            }

            totalBytes += bytes;
            var fileLines = 0;
            var fileQuests = 0;
            var fileSales = 0;
            string? problem = null;
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan);
                if (stream.Length > MaximumBytes)
                {
                    stream.Seek(stream.Length - MaximumBytes, SeekOrigin.Begin);
                }

                using var reader = new StreamReader(stream);
                if (stream.Position > 0)
                {
                    // The seek lands mid-line. Dropping that fragment beats handing a parser half a line.
                    await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }

                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (++totalLines > MaximumLines)
                    {
                        break;
                    }

                    fileLines++;

                    // Exactly the narrowing the watcher applies to this file, so a count here
                    // cannot promise something the watcher would not have seen.
                    if (mode == LogReadMode.ChatOnly)
                    {
                        if (!EftLogFiles.IsChatNotification(line))
                        {
                            continue;
                        }
                    }
                    else if (parser.ParseLine(line, observedUtc) is { } evidence)
                    {
                        var snapshot = raids.Apply(evidence);
                        var inRaid = snapshot.State is RaidLifecycleState.InRaid or RaidLifecycleState.LoadingRaid;
                        if (inRaid && !wasInRaid)
                        {
                            raidsSeen++;
                        }

                        if (inRaid || snapshot.State == RaidLifecycleState.PostRaid)
                        {
                            lastMap = snapshot.MapId ?? lastMap;
                            lastState = snapshot.State.ToString();
                            lastRaidAt = observedUtc;
                        }

                        wasInRaid = inRaid;
                    }

                    if (QuestNotificationParser.ParseLine(line, observedUtc) is not null)
                    {
                        fileQuests++;
                    }

                    if (FleaSaleParser.ParseLine(line, observedUtc) is not null)
                    {
                        fileSales++;
                    }

                    if (mode == LogReadMode.Full && LoadTimeParser.ParseLine(line, observedUtc) is { } load)
                    {
                        queue = TimeSpan.FromSeconds(load.RealSeconds);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                problem = exception.Message;
            }

            quests += fileQuests;
            sales += fileSales;
            read.Add(new(name, mode, bytes, fileLines, fileQuests, fileSales, problem));
        }

        return new(
            Path.GetFileName(session.Path),
            session.StartedUtc,
            Describe(read),
            totalBytes,
            totalLines,
            raidsSeen,
            lastMap,
            lastState ?? SetupText.ProbeLogsNoRaidState,
            lastRaidAt,
            queue,
            quests,
            sales,
            nowUtc)
        {
            Files = read,
            SkippedFiles = Skipped(session.Path),
        };
    }

    /// <summary>The files read, named, because which files were opened is half the answer.</summary>
    private static string Describe(IReadOnlyList<SelfTestLogFile> files) =>
        files.Count switch
        {
            0 => SetupText.ProbeLogsNoFile,
            1 => files[0].Name,
            _ => string.Join(", ", files.Select(file => file.Name)),
        };

    /// <summary>
    /// The session's other log files, named but not opened.
    /// </summary>
    /// <remarks>
    /// Reported so a quest that is not in anything the companion reads is a visible possibility
    /// rather than an invisible one. Naming a file is not reading it.
    /// </remarks>
    private static IReadOnlyList<string> Skipped(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.log", SearchOption.TopDirectoryOnly)
                .Where(path => EftLogFiles.ReadMode(path) is null)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static SelfTestLogs Nothing(DateTimeOffset nowUtc, string problem) =>
        new(null, null, null, 0, 0, 0, null, null, null, null, 0, 0, nowUtc, problem);

    /// <summary>
    /// The session the game is writing to, by the stamp in its own folder name.
    /// </summary>
    /// <remarks>
    /// Not by modification time, for the reason the watcher documents: a clock that steps back
    /// mid-session leaves the previous session's files stamped in the future, and a
    /// newest-by-time sort then picks the dead folder. The name is written once and cannot drift.
    /// </remarks>
    private static (string Path, DateTimeOffset? StartedUtc)? NewestSession(string logRoot)
    {
        try
        {
            var newest = Directory.EnumerateDirectories(logRoot)
                .Select(path => (Path: path, Started: SessionStart(Path.GetFileName(path))))
                .Where(entry => entry.Started is not null)
                .OrderByDescending(entry => entry.Started!.Value)
                .FirstOrDefault();
            if (newest.Path is not null)
            {
                return (newest.Path, new DateTimeOffset(newest.Started!.Value, TimeSpan.Zero));
            }

            // An installation that writes its logs straight into the root looks like this.
            return ReadableFiles(logRoot).Count == 0 ? null : (logRoot, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
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
    /// Every log file of this folder the watcher would open, and how much of each it reads.
    /// </summary>
    /// <remarks>
    /// Ordered by name, which is the order the watcher replays them in, so the raid state
    /// machine sees the same sequence in both places.
    /// </remarks>
    private static IReadOnlyList<(string Path, LogReadMode Mode)> ReadableFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.log", SearchOption.TopDirectoryOnly)
                .Select(path => (Path: path, Mode: EftLogFiles.ReadMode(path)))
                .Where(entry => entry.Mode is not null)
                .Select(entry => (entry.Path, Mode: entry.Mode!.Value))
                .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^log_(?<stamp>\d{4}\.\d{2}\.\d{2}_\d{2}-\d{2}-\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex SessionFolderPattern();
}
