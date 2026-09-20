using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The record a run leaves when it is killed rather than closed.
/// </summary>
/// <remarks>
/// Shares <see cref="CrashLogCollection"/> because the thing under test ends up in
/// <see cref="CrashLog"/>, whose destination is static for the whole process; running beside a
/// test that installs its own destination would send this one's replay somewhere else. The
/// remarks on that collection explain why it is serialised at all.
/// </remarks>
[Collection(CrashLogCollection.Name)]
public sealed class CrashBreadcrumbTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "tarkov-breadcrumbs-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A run that is killed leaves its last moments behind for the next one to report.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the file. On 2026-09-19 a native access violation inside Skia
    /// killed the application while a map was being rasterised; nothing in the process ran
    /// afterwards, so the log held two and a half minutes of silence and then the next launch.
    /// Simulated here by dropping breadcrumbs and never calling MarkCleanExit, which is exactly
    /// what being shot in the head looks like from the next launch's point of view.
    /// </remarks>
    [Fact]
    public void ARunThatNeverReachedShutdownIsReportedToTheNextLaunchWithItsLastBreadcrumbs()
    {
        CrashLog.Install(_directory);
        CrashBreadcrumbs.Install(_directory);
        CrashBreadcrumbs.Drop("navigate", "v2://plan");
        CrashBreadcrumbs.Drop("map", "loading reserve/reserve");
        CrashBreadcrumbs.Detach();

        var previous = CrashBreadcrumbs.Install(_directory);

        Assert.Contains("[navigate] v2://plan", string.Join("\n", previous), StringComparison.Ordinal);
        Assert.Contains("[map] loading reserve/reserve", string.Join("\n", previous), StringComparison.Ordinal);
        var log = ReadShared(CrashLog.FilePath!);
        Assert.Contains("previous-run-died", log, StringComparison.Ordinal);
        Assert.Contains("loading reserve/reserve", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run that closed normally is not reported as a crash.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is the one that would make the feature useless: a report
    /// on every single launch trains the reader to ignore it.
    /// </remarks>
    [Fact]
    public void ARunThatExitedCleanlyLeavesNothingForTheNextLaunchToReport()
    {
        CrashLog.Install(_directory);
        CrashBreadcrumbs.Install(_directory);
        CrashBreadcrumbs.Drop("navigate", "v2://setup");
        CrashBreadcrumbs.MarkCleanExit();
        CrashBreadcrumbs.Detach();

        var previous = CrashBreadcrumbs.Install(_directory);

        Assert.Empty(previous);
        Assert.DoesNotContain("previous-run-died", ReadShared(CrashLog.FilePath!), StringComparison.Ordinal);
    }

    /// <summary>
    /// A breadcrumb is on disk before the next line of code runs.
    /// </summary>
    /// <remarks>
    /// The one property that matters. A buffered writer would lose precisely the last line —
    /// the one naming what killed the process — so this reads the file back with no flush,
    /// dispose or shutdown in between.
    /// </remarks>
    [Fact]
    public void EachBreadcrumbIsOnDiskImmediately()
    {
        CrashBreadcrumbs.Install(_directory);

        CrashBreadcrumbs.Drop("map-asset", "reading svg reserve::base");

        Assert.Contains(
            "[map-asset] reading svg reserve::base",
            ReadShared(CrashBreadcrumbs.FilePath!),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A workspace that absorbs a failure still puts it where a player's log will show it.
    /// </summary>
    /// <remarks>
    /// The second half of "panes not rendering, and no exception in the log". Every one of these
    /// went to <see cref="System.Diagnostics.Trace"/>, which has no listener in an installed
    /// build, so the page said "Quest data isn't available yet." and the cause was destroyed.
    /// </remarks>
    [Fact]
    public void AWorkspaceFaultIsWrittenToTheLogFileAndLeavesABreadcrumb()
    {
        CrashLog.Install(_directory);
        CrashBreadcrumbs.Install(_directory);

        WorkspaceFault.Record("plan", "refresh", new InvalidOperationException("no such table: quests"));

        var log = ReadShared(CrashLog.FilePath!);
        Assert.Contains("workspace-fault/plan", log, StringComparison.Ordinal);
        Assert.Contains("no such table: quests", log, StringComparison.Ordinal);
        Assert.Contains("[workspace-fault] plan refresh", ReadShared(CrashBreadcrumbs.FilePath!), StringComparison.Ordinal);
    }

    /// <summary>Nothing is written once the destination is gone.</summary>
    [Fact]
    public void DroppingWithNoDestinationInstalledIsSilent()
    {
        CrashBreadcrumbs.Detach();

        CrashBreadcrumbs.Drop("navigate", "v2://raid");

        Assert.Null(CrashBreadcrumbs.FilePath);
        Assert.False(Directory.Exists(_directory));
    }

    public void Dispose()
    {
        CrashBreadcrumbs.Detach();
        CrashLog.Detach();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
