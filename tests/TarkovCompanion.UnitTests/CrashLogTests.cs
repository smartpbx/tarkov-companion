using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The startup log, which is the only sink a failure before the window has.
/// </summary>
/// <remarks>
/// Every file here is opened sharing read and write, which is not fussiness. Install subscribes
/// to <see cref="TaskScheduler.UnobservedTaskException"/>, and that fires on the finalizer
/// thread whenever any faulted task anywhere in the run is collected — including in the middle
/// of one of these tests, which then finds the log being appended to underneath it. On Linux
/// the two calls simply interleave; on Windows the plain File helpers deny each other and the
/// test fails with a sharing violation that has nothing to do with what it was checking.
/// </remarks>
public sealed class CrashLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "tarkov-crashlog-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The same failure repeated is one fact, not two hundred.
    /// </summary>
    /// <remarks>
    /// While the relay is unreachable the group loop reports every five seconds, with a full
    /// stack trace. Appended verbatim, that buries everything else that happened that evening
    /// under the same paragraph a thousand times.
    /// </remarks>
    [Fact]
    public void RepeatsAreCollapsedIntoOneLineWithACount()
    {
        CrashLog.Install(_directory);
        for (var attempt = 0; attempt < 120; attempt++)
        {
            CrashLog.Write("group", "the relay did not answer");
        }

        // Something else breaks the run, which is what flushes the count.
        CrashLog.Write("sync", "refreshed");

        var text = ReadShared(CrashLog.FilePath!);
        Assert.Single(
            text.Split('\n'),
            line => line.Contains("the relay did not answer", StringComparison.Ordinal));
        Assert.Contains("still failing (120x) since", text, StringComparison.Ordinal);
    }

    /// <summary>The log says which build wrote it.</summary>
    /// <remarks>
    /// Every assembly reported 1.0.0.0 until the packaging script started stamping a version,
    /// so "which build produced this" had no answer. A log that cannot name its own build is a
    /// log somebody has to guess about.
    ///
    /// It asserts that the line is there rather than that it is first, and that is not a
    /// weakening of the test — it is the same claim stated without a false premise. CrashLog's
    /// destination is a static, so any test running in parallel that logs anything writes to
    /// whichever directory was installed last, and a line landing between Install setting the
    /// directory and Install writing its own first line puts that line ahead of it. That became
    /// frequent the moment the migration runner started logging what it applied, because the
    /// tests that build a whole application run migrations.
    ///
    /// In the application Install runs before anything else has a logger, so the started line
    /// is genuinely first there. What matters to somebody reading a log is that the build is
    /// named in it, which is what this now says.
    /// </remarks>
    [Fact]
    public void TheLogNamesTheBuildThatWroteIt()
    {
        CrashLog.Install(_directory);

        var started = Assert.Single(
            ReadShared(CrashLog.FilePath!).Split('\n'),
            line => line.Contains("[started]", StringComparison.Ordinal));

        Assert.Contains("build ", started, StringComparison.Ordinal);
    }

    /// <summary>A log that grows without bound is rolled, keeping one previous.</summary>
    [Fact]
    public void ThePreviousLogIsKeptOnceTheCurrentOneIsLargeEnough()
    {
        CrashLog.Install(_directory);
        var path = CrashLog.FilePath!;

        // Past the two-megabyte mark, then one more distinct entry to trigger the roll.
        // SetLength rather than three megabytes of 'x': the size is the whole of what the roll
        // looks at, and a sparse grow does not put three megabytes through memory to say so.
        using (var grow = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            grow.SetLength(3 * 1024 * 1024);
        }

        CrashLog.Write("sync", "something new");

        Assert.True(File.Exists(path + ".1"), "the oversized log should have been rolled aside");
        Assert.True(new FileInfo(path).Length < 1024, "the current log should start again");
    }

    /// <summary>A line is still written while something else holds the log open.</summary>
    /// <remarks>
    /// The log matters most when something else is looking at it — a support bundle reading it
    /// to send on, or the user with it open. File.AppendAllText shares the file read-only for
    /// as long as it is open, and Windows reads that from both ends, so an append and any other
    /// handle wanting write access refuse each other and the line is dropped silently.
    /// </remarks>
    [Fact]
    public void ALineIsStillWrittenWhileSomethingElseHoldsTheLog()
    {
        CrashLog.Install(_directory);
        var path = CrashLog.FilePath!;

        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            CrashLog.Write("sync", "written while the file was held open");
        }

        Assert.Contains("written while the file was held open", ReadShared(path), StringComparison.Ordinal);
    }

    /// <summary>Pointing the log somewhere new starts that log with its own first line.</summary>
    /// <remarks>
    /// The repeat collapse compares against whatever was written last, which after an Install
    /// belongs to a different file. The started line is identical from one Install to the next,
    /// so it matched, and the new log's first line was counted as a repeat of the old log's
    /// last one and never written — leaving a log with no build stamp, which is the one thing
    /// the first line exists to carry.
    /// </remarks>
    [Fact]
    public void ANewDestinationGetsItsOwnStartedLine()
    {
        CrashLog.Install(_directory);
        var second = Path.Combine(_directory, "again");
        CrashLog.Install(second);

        Assert.Contains("[started]", ReadShared(Path.Combine(second, "startup.log")), StringComparison.Ordinal);
    }

    /// <summary>Diagnostics never become the reason the application fails.</summary>
    [Fact]
    public void WritingWithNowhereToWriteIsNotAnError()
    {
        CrashLog.Write("group", "before Install was ever called");
    }

    /// <summary>Reads the log without locking out the writer that may be appending to it.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
