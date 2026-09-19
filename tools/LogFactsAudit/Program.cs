using System.Globalization;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.LogFactsAudit;

/// <summary>
/// V2 rough package 26 (log facts): re-measures docs/research/EFT_LOG_FACTS.md's claims against
/// a folder of real EFT log sessions, using the same parsers the companion runs in production
/// rather than re-deriving the JSON shapes by hand. Point it at a copy of the game's own
/// <c>Logs</c> folder (or any folder containing <c>log_&lt;stamp&gt;_&lt;version&gt;</c>
/// subfolders) and it reports counts for each fact the note discusses: raid outcome,
/// run-through, scav cooldown, quest completion, flea sales, and queue/load time.
///
/// This tool reads local files only. It never touches the game process, never generates input,
/// and is not wired into scripts/build.sh or scripts/test.sh, the same way tools/V2RenderPreview
/// is not — both are developer aids, not part of the shipped app or its gates.
/// </summary>
internal static class Program
{

    public static int Main(string[] args)
    {
        var root = args.FirstOrDefault(argument => !argument.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(root))
        {
            Console.WriteLine("Usage: dotnet run --project tools/LogFactsAudit -- <folder of EFT log sessions>");
            Console.WriteLine("No folder supplied; skipping.");
            return 0;
        }

        if (!Directory.Exists(root))
        {
            Console.WriteLine($"'{root}' does not exist; skipping.");
            return 0;
        }

        var files = EnumerateWatchedFiles(root)
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            Console.WriteLine(
                $"No log file this companion reads was found under '{root}'; skipping. " +
                "Point this at a folder holding log_<stamp>_<version> session folders.");
            return 0;
        }

        Console.WriteLine($"Reading {files.Length} log file(s) under {root}.");
        Console.WriteLine();

        var report = Measure(files);
        Print(report);
        return 0;
    }

    private static Report Measure(IReadOnlyList<(string Path, LogReadMode Mode)> files)
    {
        // One parser and one state machine for the whole run, exactly as the watcher uses them:
        // the parser learns the player's own profile id from a SelectedProfile line, and the
        // state machine is what turns a raw suggestion into a raid boundary without flapping on
        // every loading-ish line mid-raid (RaidStateService.Apply's own remarks explain why a
        // raw suggestion cannot be counted directly).
        var parser = new EftLogParser();
        var state = new RaidStateService();

        var report = new Report();
        foreach (var (path, mode) in files)
        {
            var isApplication = Path.GetFileName(path).Contains("application", StringComparison.OrdinalIgnoreCase);
            var quests = report.QuestEventCount;
            var lines = 0;
            foreach (var line in SafeReadLines(path))
            {
                lines++;
                // Narrowed exactly as the watcher narrows it, so a count here cannot promise
                // something the shipped code would not have seen.
                if (mode == LogReadMode.ChatOnly && !EftLogFiles.IsChatNotification(line))
                {
                    continue;
                }

                MeasureLine(line, mode == LogReadMode.Full && isApplication, parser, state, report);
            }

            // Per file, because "which file had the quests in it" is the question this tool was
            // unable to answer when the note it checks was written.
            Console.WriteLine(
                $"  {Path.GetFileName(path)}: {lines} line(s), {mode}, " +
                $"{report.QuestEventCount - quests} quest event(s)");
        }

        Console.WriteLine();
        return report;
    }

    private static void MeasureLine(
        string line,
        bool isApplication,
        EftLogParser parser,
        RaidStateService state,
        Report report)
    {
        // The timestamp only labels the resulting evidence; nothing counted here compares it,
        // so a constant is as good as the line's own and far cheaper than parsing it back out.
        var observedUtc = DateTimeOffset.UnixEpoch;

        if (parser.ParseLine(line, observedUtc) is { } evidence)
        {
            var previous = state.Current;
            var current = state.Apply(evidence);
            if (current.RaidId is not null && current.RaidId != previous.RaidId)
            {
                report.RaidsStarted++;
            }

            if (previous.State == RaidLifecycleState.InRaid && current.State != RaidLifecycleState.InRaid)
            {
                report.RaidsEnded++;
                // EftLogParser's own summary names the status in words when it applies; see its
                // "userMatchOver when transferred" case. Matching the same word here means this
                // count moves in lockstep with what the parser itself considers a transfer.
                if (evidence.Summary.Contains("Transfer", StringComparison.Ordinal))
                {
                    report.RunThroughEnds++;
                }
            }
        }

        if (FleaSaleParser.ParseLine(line, observedUtc) is { } sale)
        {
            report.SaleOfferIds.Add(sale.OfferId);
            if (sale.HandbookItemId is { Length: > 0 } itemId)
            {
                report.SoldItemIds.Add(itemId);
            }
        }

        if (QuestNotificationParser.ParseLine(line, observedUtc) is { } quest)
        {
            report.QuestEventsByState(quest.State).Add(quest.EventId);
        }

        // docs/research/EFT_LOG_FACTS.md: "Queue time | application | MatchingCompleted:...".
        // Restricted to application, the one file the note names, so a coincidental match
        // elsewhere cannot inflate the count.
        if (isApplication && LoadTimeParser.ParseLine(line, observedUtc) is { } loadTime)
        {
            report.LoadTimesSeconds.Add(loadTime.RealSeconds);
        }

        // Outcome: the note's own method was to search for the field and then check whether
        // every hit was actually a stack frame for a method signature that also carries a
        // TimeSpan. Both counts are reported so a reader can re-run that check rather than
        // trust this tool's word for it.
        if (line.Contains("ExitStatus", StringComparison.Ordinal))
        {
            report.ExitStatusLines++;
            if (line.Contains("TimeSpan", StringComparison.Ordinal))
            {
                report.ExitStatusStackFrameLines++;
            }
        }

        // Scav cooldown: the note's finding was that SavageLockTime only ever appears inside a
        // squadmate's blob, never beside the player's own lowercase "profileid" marker — and
        // that the check has to be case-sensitive, because "ProfileId"/"KillerProfileId" inside
        // a dogtag object would otherwise look like the same marker.
        if (line.Contains("SavageLockTime", StringComparison.Ordinal))
        {
            report.ScavLockTimeLines++;
            if (line.Contains("\"profileid\"", StringComparison.Ordinal))
            {
                report.ScavLockTimeWithOwnProfileIdLines++;
            }
        }
    }

    private static void Print(Report report)
    {
        Console.WriteLine("Raid outcome (survived/died/run-through) and duration:");
        Console.WriteLine($"  Raids started: {report.RaidsStarted}");
        Console.WriteLine($"  Raids ended:   {report.RaidsEnded}");
        Console.WriteLine($"  Ended with status Transfer (the only proof of a scav run this note relies on): {report.RunThroughEnds}");
        Console.WriteLine($"  \"ExitStatus\" lines: {report.ExitStatusLines} (of which {report.ExitStatusStackFrameLines} also carry \"TimeSpan\", the stack-frame signature the note found)");
        Console.WriteLine();

        Console.WriteLine("Scav cooldown:");
        Console.WriteLine($"  \"SavageLockTime\" lines: {report.ScavLockTimeLines} (of which {report.ScavLockTimeWithOwnProfileIdLines} also carry the player's own lowercase \"profileid\" marker)");
        Console.WriteLine();

        Console.WriteLine("Quest completion (from ChatMessageReceived, distinct notifications):");
        foreach (var stateValue in Enum.GetValues<RecordedTaskState>())
        {
            if (stateValue is RecordedTaskState.Unknown or RecordedTaskState.NotStarted)
            {
                continue;
            }

            Console.WriteLine($"  {stateValue}: {report.QuestEventsByState(stateValue).Count}");
        }

        Console.WriteLine();

        Console.WriteLine("Flea sales (RagfairOfferSold, distinct offers):");
        Console.WriteLine($"  Sales: {report.SaleOfferIds.Count}");
        Console.WriteLine($"  Distinct items resolved to a handbook id: {report.SoldItemIds.Count}");
        Console.WriteLine();

        Console.WriteLine("Queue/load time (MatchingCompleted's real: figure, application only):");
        if (report.LoadTimesSeconds.Count == 0)
        {
            Console.WriteLine("  None seen.");
        }
        else
        {
            Console.WriteLine($"  Seen: {report.LoadTimesSeconds.Count}");
            Console.WriteLine($"  Min/avg/max seconds: {report.LoadTimesSeconds.Min().ToString("0.0", CultureInfo.InvariantCulture)}"
                + $" / {report.LoadTimesSeconds.Average().ToString("0.0", CultureInfo.InvariantCulture)}"
                + $" / {report.LoadTimesSeconds.Max().ToString("0.0", CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>
    /// The same files the companion opens, and how much of each.
    /// </summary>
    /// <remarks>
    /// From <see cref="EftLogFiles"/> rather than a copy of the list. This tool exists to check
    /// the note's claims against real logs, and a tool measuring a different file set from the
    /// one the watcher reads would confirm claims about nothing. That is not hypothetical: the
    /// note's "380 ChatMessageReceived lines across eight log folders" was measured against
    /// push-notifications, which the watcher does not read in full, and the parser was therefore
    /// "verified" against a file whose spelling of the announcement the shipped code never saw.
    /// </remarks>
    private static IEnumerable<(string Path, LogReadMode Mode)> EnumerateWatchedFiles(string root)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(root, "*.log", SearchOption.AllDirectories);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Could not list '{root}': {exception.Message}");
            yield break;
        }

        foreach (var path in files)
        {
            if (EftLogFiles.ReadMode(path) is { } mode)
            {
                yield return (path, mode);
            }
        }
    }

    private static IEnumerable<string> SafeReadLines(string path)
    {
        IEnumerable<string> lines;
        try
        {
            lines = File.ReadLines(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  Could not read {path}; skipped.");
            yield break;
        }

        foreach (var line in lines)
        {
            yield return line;
        }
    }

    private sealed class Report
    {
        public int RaidsStarted { get; set; }

        public int RaidsEnded { get; set; }

        public int RunThroughEnds { get; set; }

        public int ExitStatusLines { get; set; }

        public int ExitStatusStackFrameLines { get; set; }

        public int ScavLockTimeLines { get; set; }

        public int ScavLockTimeWithOwnProfileIdLines { get; set; }

        public HashSet<string> SaleOfferIds { get; } = new(StringComparer.Ordinal);

        public HashSet<string> SoldItemIds { get; } = new(StringComparer.Ordinal);

        public List<double> LoadTimesSeconds { get; } = [];

        private readonly Dictionary<RecordedTaskState, HashSet<string>> _questEventsByState = new();

        public HashSet<string> QuestEventsByState(RecordedTaskState state)
        {
            if (!_questEventsByState.TryGetValue(state, out var events))
            {
                events = new(StringComparer.Ordinal);
                _questEventsByState[state] = events;
            }

            return events;
        }

        /// <summary>Distinct quest events seen so far, for the running per-file total.</summary>
        public int QuestEventCount => _questEventsByState.Values.Sum(events => events.Count);
    }
}
