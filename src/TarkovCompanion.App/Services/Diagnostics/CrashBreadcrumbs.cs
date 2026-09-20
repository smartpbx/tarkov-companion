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

    private static bool IsExpectedFileFailure(Exception exception) => exception
        is IOException
        or UnauthorizedAccessException
        or NotSupportedException
        or ArgumentException;
}
