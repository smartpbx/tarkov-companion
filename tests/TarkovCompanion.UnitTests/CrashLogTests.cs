using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.UnitTests;

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

        var text = File.ReadAllText(CrashLog.FilePath!);
        Assert.Single(
            text.Split('\n'),
            line => line.Contains("the relay did not answer", StringComparison.Ordinal));
        Assert.Contains("still failing (120x) since", text, StringComparison.Ordinal);
    }

    /// <summary>The log says which build wrote it, before anything else.</summary>
    /// <remarks>
    /// Every assembly reported 1.0.0.0 until the packaging script started stamping a version,
    /// so "which build produced this" had no answer. A log that cannot name its own build is a
    /// log somebody has to guess about.
    /// </remarks>
    [Fact]
    public void TheFirstLineNamesTheBuild()
    {
        CrashLog.Install(_directory);

        var first = File.ReadAllLines(CrashLog.FilePath!)[0];

        Assert.Contains("[started]", first, StringComparison.Ordinal);
        Assert.Contains("build ", first, StringComparison.Ordinal);
    }

    /// <summary>A log that grows without bound is rolled, keeping one previous.</summary>
    [Fact]
    public void ThePreviousLogIsKeptOnceTheCurrentOneIsLargeEnough()
    {
        CrashLog.Install(_directory);
        var path = CrashLog.FilePath!;

        // Past the two-megabyte mark, then one more distinct entry to trigger the roll.
        File.WriteAllText(path, new string('x', 3 * 1024 * 1024));
        CrashLog.Write("sync", "something new");

        Assert.True(File.Exists(path + ".1"), "the oversized log should have been rolled aside");
        Assert.True(new FileInfo(path).Length < 1024, "the current log should start again");
    }

    /// <summary>Diagnostics never become the reason the application fails.</summary>
    [Fact]
    public void WritingWithNowhereToWriteIsNotAnError()
    {
        CrashLog.Write("group", "before Install was ever called");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
