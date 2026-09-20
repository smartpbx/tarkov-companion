using System.Globalization;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Records what the application was doing, so a run that dies without saying anything still
/// leaves an account of its last moments behind.
/// </summary>
/// <remarks>
/// On 2026-09-19 the application was killed by a native access violation (0xc0000005) inside
/// Skia while a map was being rasterised. A native fault of that kind unwinds nothing: no
/// managed exception is raised, so <see cref="AppDomain.UnhandledException"/>,
/// <see cref="TaskScheduler.UnobservedTaskException"/> and the dispatcher's handler — all of
/// which <see cref="CrashLog"/> already subscribes to — never run. The log held two and a half
/// minutes of silence and then the next launch. There was nothing to diagnose from.
///
/// So this does not try to catch anything. It writes *before* the dangerous thing happens, to
/// its own file, closing the handle each time so the bytes are with the operating system before
/// the next instruction runs. The process can then be shot in the head and the file survives.
///
/// The file is only ever read by the launch that follows. A marker file says "a run is in
/// progress"; a clean exit removes it. Finding one at startup means the previous run was killed,
/// and the breadcrumbs it left are copied into <see cref="CrashLog"/> — the one file a player is
/// asked to send — under a category that says so. A run that exits cleanly reports nothing,
/// because nothing went wrong.
///
/// Deliberately separate from <see cref="CrashLog"/>: breadcrumbs are dropped on every
/// navigation and every map load, and folding that volume into the file somebody reads would
/// bury the lines that matter. <see cref="CrashLog"/> also collapses consecutive identical
/// entries, which is right for a repeating failure and wrong for "this happened again".
/// </remarks>
public static class CrashBreadcrumbs
{
    /// <summary>How many of the previous run's breadcrumbs are replayed into the crash log.</summary>
    /// <remarks>
    /// Enough to show the route taken into the fault, few enough that the replay does not push
    /// the rest of the log out of view. The whole file is kept beside it either way.
    /// </remarks>
    public const int ReplayedLines = 40;

    /// <summary>How large the breadcrumb file may grow before the previous one is rolled aside.</summary>
    private const long MaximumBytes = 512 * 1024;

    private const string FileName = "breadcrumbs.log";
    private const string MarkerName = "breadcrumbs.running";

    private static readonly Lock Gate = new();
    private static string? _directory;

    /// <summary>
    /// Whether the run before this one was killed rather than closed.
    /// </summary>
    /// <remarks>
    /// One boolean, which is all the outbound diagnostics report is allowed to carry and all it
    /// needs. On 2026-09-19 Clayton sent a report for a run that had died and the report could not
    /// say so: it excludes free-form text and exception bodies by design, the breadcrumbs that
    /// explain the death are free-form, and nothing projected the one fact that is not. "The run
    /// before this one did not reach its own shutdown" is the difference between a report that
    /// starts an investigation and one that ends it.
    ///
    /// False until <see cref="Install"/> has looked, so a launch that never installed one — a
    /// self-test, a page gallery — reports nothing rather than guessing.
    /// </remarks>
    public static bool PreviousRunDied { get; private set; }

    /// <summary>
    /// Where the run before this one was when it died, or null if it closed normally.
    /// </summary>
    /// <remarks>
    /// #454, "a crash that logs nothing". The breadcrumbs already survive a native fault and are
    /// already replayed into the crash log; what nobody was told was the answer they add up to.
    /// This is that answer — the last page, the last map, whether the map was still being drawn,
    /// whether the window was frozen — read from the whole of the previous run's file rather than
    /// the replayed tail, so a long session's last navigation is not lost behind forty map lines.
    /// Setup shows it once, on the launch that follows, and the problem report carries it.
    /// </remarks>
    public static PreviousRunEnd? PreviousRun { get; private set; }

    /// <summary>Where breadcrumbs are being written, or null while nothing is installed.</summary>
    public static string? FilePath
    {
        get
        {
            lock (Gate)
            {
                return _directory is null ? null : Path.Combine(_directory, FileName);
            }
        }
    }

    /// <summary>
    /// Reports the previous run's last moments if it was killed, then starts recording this one.
    /// </summary>
    /// <returns>
    /// The breadcrumbs of a previous run that did not exit cleanly, oldest first, or an empty
    /// list when the previous run ended normally or there was none.
    /// </returns>
    public static IReadOnlyList<string> Install(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        IReadOnlyList<string> previous = [];
        PreviousRunEnd? summary = null;
        lock (Gate)
        {
            _directory = logDirectory;
            var path = Path.Combine(logDirectory, FileName);
            var marker = Path.Combine(logDirectory, MarkerName);
            try
            {
                Directory.CreateDirectory(logDirectory);
                if (File.Exists(marker))
                {
                    previous = ReadTail(path, ReplayedLines);
                    summary = Summarise(ReadLines(path));
                    // Kept, not deleted. The replay below is a tail; the full account of the run
                    // that died is worth more than the disk it costs, and one previous file is
                    // the same bound CrashLog keeps.
                    if (File.Exists(path))
                    {
                        File.Move(path, path + ".1", overwrite: true);
                    }
                }
                else if (File.Exists(path))
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            }
            catch (Exception failure) when (IsExpectedFileFailure(failure))
            {
                // A diagnostic that cannot be written is not a reason to refuse to start.
            }
        }

        PreviousRunDied = previous.Count > 0;
        PreviousRun = previous.Count > 0 ? summary : null;
        if (previous.Count > 0)
        {
            CrashLog.Write(
                "previous-run-died",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The previous run did not reach its own shutdown. Its last "
                    + $"{previous.Count} breadcrumbs, oldest first:{Environment.NewLine}  "
                    + $"{string.Join(Environment.NewLine + "  ", previous)}"));
        }

        Drop("run", "started");
        return previous;
    }

    /// <summary>Records one thing the application is about to do, or has just finished.</summary>
    /// <remarks>
    /// Opened, written and closed on every call. Holding a buffered writer would be cheaper and
    /// would lose exactly the last line — the one naming what killed the process.
    /// </remarks>
    public static void Drop(string category, string detail)
    {
        var entry = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:O} [{category}] {detail}{Environment.NewLine}");
        try
        {
            lock (Gate)
            {
                if (_directory is not { } directory)
                {
                    return;
                }

                var path = Path.Combine(directory, FileName);
                Directory.CreateDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    4096,
                    FileOptions.WriteThrough);
                using var writer = new StreamWriter(stream);
                writer.Write(entry);
            }
        }
        catch (Exception failure) when (IsExpectedFileFailure(failure))
        {
            // As above: never the reason the application fails.
        }
    }

    /// <summary>Reduces a run's breadcrumbs to where it was when they stopped.</summary>
    internal static PreviousRunEnd Summarise(IEnumerable<string> breadcrumbs)
    {
        string? route = null;
        string? map = null;
        var drawing = false;
        var frozen = false;
        foreach (var line in breadcrumbs)
        {
            // "<timestamp> [category] detail"
            var open = line.IndexOf(" [", StringComparison.Ordinal);
            var close = open < 0 ? -1 : line.IndexOf("] ", open, StringComparison.Ordinal);
            if (close < 0)
            {
                continue;
            }

            var category = line[(open + 2)..close];
            var detail = line[(close + 2)..].Trim();
            switch (category)
            {
                case "navigate":
                    route = detail;
                    break;
                case "map" or "map-asset":
                    var verb = detail.IndexOf(' ', StringComparison.Ordinal);
                    // "loading customs/…", "reading svg …" against "loaded …", "drew …".
                    drawing = detail.StartsWith("loading ", StringComparison.Ordinal)
                        || detail.StartsWith("reading ", StringComparison.Ordinal);
                    map = verb < 0 ? detail : detail[(verb + 1)..].Replace("svg ", string.Empty, StringComparison.Ordinal);
                    break;
                case "ui-hang":
                    frozen = true;
                    break;
                case "ui-hang-recovered":
                    frozen = false;
                    break;
            }
        }

        return new(route, map, drawing, frozen);
    }

    /// <summary>One sentence for Setup, or empty when the previous run closed normally.</summary>
    public static string DescribePreviousRun()
    {
        if (PreviousRun is not { } end)
        {
            return string.Empty;
        }

        var page = end.LastRoute is { Length: > 0 } route ? $" Last page: {route}." : string.Empty;
        var map = end.LastMap is { Length: > 0 } last
            ? $" Last map: {last}{(end.MapWasBeingDrawn ? ", still being drawn" : string.Empty)}."
            : string.Empty;
        var frozen = end.WasFrozen ? " The window was frozen." : string.Empty;
        return $"The last session ended without closing.{page}{map}{frozen}";
    }

    /// <summary>Records that this run ended on purpose, so the next one does not report it.</summary>
    public static void MarkCleanExit()
    {
        Drop("run", "exited cleanly");
        lock (Gate)
        {
            if (_directory is not { } directory)
            {
                return;
            }

            try
            {
                File.Delete(Path.Combine(directory, MarkerName));
            }
            catch (Exception failure) when (IsExpectedFileFailure(failure))
            {
                // A marker that cannot be removed only costs one spurious report next launch.
            }
        }
    }

    /// <summary>Stops recording, until something installs a destination again.</summary>
    /// <remarks>
    /// Present for the same reason <see cref="CrashLog.Detach"/> is: the destination is a static,
    /// so a test that installed one has to be able to stop the rest of the process writing into
    /// a directory it is about to delete.
    /// </remarks>
    public static void Detach()
    {
        lock (Gate)
        {
            _directory = null;
        }
    }

    /// <summary>Every line of a breadcrumb file, read lazily; the file is capped at half a megabyte.</summary>
    private static IEnumerable<string> ReadLines(string path)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        using var reader = new StreamReader(new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite));
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static IReadOnlyList<string> ReadTail(string path, int lines)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var tail = new Queue<string>(lines);
        using var reader = new StreamReader(new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite));
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (tail.Count == lines)
            {
                tail.Dequeue();
            }

            tail.Enqueue(line);
        }

        return [.. tail];
    }

    /// <summary>Forgets what was learned about the previous run. For tests: the state is static.</summary>
    internal static void ForgetPreviousRun()
    {
        PreviousRunDied = false;
        PreviousRun = null;
    }

    private static bool IsExpectedFileFailure(Exception exception) => exception
        is IOException
        or UnauthorizedAccessException
        or NotSupportedException
        or ArgumentException;
}

/// <summary>Where a run was when it stopped without shutting down.</summary>
/// <param name="LastRoute">The last page it navigated to, as the shell addresses it.</param>
/// <param name="LastMap">The last map (and floor) handed to the map loader or the rasteriser.</param>
/// <param name="MapWasBeingDrawn">No "loaded" or "drew" followed that map: it died mid-draw.</param>
/// <param name="WasFrozen">The hang watchdog had reported a freeze that never recovered.</param>
public sealed record PreviousRunEnd(string? LastRoute, string? LastMap, bool MapWasBeingDrawn, bool WasFrozen);
