using System.Globalization;
using System.Text.RegularExpressions;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Raids;

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
/// Read-only and bounded. The file is opened with full sharing because the game is holding it
/// open and appending, only the last <see cref="MaximumBytes"/> are read, and the line budget
/// stops a pathological file rather than trusting it to end.
/// </remarks>
public sealed partial class SelfTestLogReader(TimeProvider? timeProvider = null)
{
    /// <summary>How much of the newest file is read. A full session is smaller than this.</summary>
    public const long MaximumBytes = 8L * 1024 * 1024;

    public const int MaximumLines = 200_000;

    /// <summary>The game's own per-launch files. "output" is skipped: it is huge and duplicated.</summary>
    private static readonly string[] ReadablePrefixes = ["application", "backend", "notifications"];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<SelfTestLogs> ReadAsync(string? logRoot, CancellationToken cancellationToken)
    {
        var nowUtc = _clock.GetUtcNow();
        if (string.IsNullOrWhiteSpace(logRoot))
        {
            return Nothing(nowUtc, "no log folder has been chosen, so there was no session to read");
        }

        if (!Directory.Exists(logRoot))
        {
            return Nothing(nowUtc, $"the log folder {logRoot} does not exist");
        }

        var session = NewestSession(logRoot);
        if (session is null)
        {
            return Nothing(nowUtc, $"{logRoot} holds no session folder the game would have written");
        }

        var file = NewestReadableFile(session.Value.Path);
        if (file is null)
        {
            return Nothing(
                nowUtc,
                $"session {Path.GetFileName(session.Value.Path)} holds no application or backend log yet");
        }

        try
        {
            return await ReplayAsync(session.Value, file, nowUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Nothing(nowUtc, $"{Path.GetFileName(file)} could not be read: {exception.Message}");
        }
    }

    private async Task<SelfTestLogs> ReplayAsync(
        (string Path, DateTimeOffset? StartedUtc) session,
        string file,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(file);
        await using var stream = new FileStream(
            file,
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

        var parser = new EftLogParser();
        var raids = new RaidStateService();
        var observedUtc = session.StartedUtc ?? nowUtc;
        var lines = 0;
        var raidsSeen = 0;
        var quests = 0;
        var sales = 0;
        TimeSpan? queue = null;
        string? lastMap = null;
        string? lastState = null;
        DateTimeOffset? lastRaidAt = null;
        var wasInRaid = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (++lines > MaximumLines)
            {
                break;
            }

            if (parser.ParseLine(line, observedUtc) is { } evidence)
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
                quests++;
            }

            if (FleaSaleParser.ParseLine(line, observedUtc) is not null)
            {
                sales++;
            }

            if (LoadTimeParser.ParseLine(line, observedUtc) is { } load)
            {
                queue = TimeSpan.FromSeconds(load.RealSeconds);
            }
        }

        return new(
            Path.GetFileName(session.Path),
            session.StartedUtc,
            info.Name,
            info.Length,
            lines,
            raidsSeen,
            lastMap,
            lastState ?? "no raid",
            lastRaidAt,
            queue,
            quests,
            sales,
            nowUtc);
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
            return NewestReadableFile(logRoot) is null ? null : (logRoot, null);
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

    private static string? NewestReadableFile(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.log", SearchOption.TopDirectoryOnly)
                .Where(IsReadable)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The game names each file "&lt;stamp&gt; &lt;prefix&gt;_000.log", so the prefix begins a word.</summary>
    private static bool IsReadable(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path.AsSpan());
        if (name.Contains("output", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var prefix in ReadablePrefixes)
        {
            var index = name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (index == 0 || name[index - 1] is ' ' or '_' or '-' or '.'))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"^log_(?<stamp>\d{4}\.\d{2}\.\d{2}_\d{2}-\d{2}-\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex SessionFolderPattern();
}
