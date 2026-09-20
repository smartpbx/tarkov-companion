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
    /// The next launch can say where the run that died was: the page, the map, and that the map
    /// was still being drawn (#454).
    /// </summary>
    /// <remarks>
    /// The route is the first thing dropped and sixty map lines follow it, because the replayed
    /// tail is forty lines and the summary must not be: a long session's last navigation is
    /// exactly what falls off the end of a tail.
    /// </remarks>
    [Fact]
    public void TheNextLaunchKnowsTheLastPageAndThatTheMapWasStillBeingDrawn()
    {
        CrashLog.Install(_directory);
        CrashBreadcrumbs.Install(_directory);
        CrashBreadcrumbs.Drop("navigate", "#/plan");
        for (var index = 0; index < 60; index++)
        {
            CrashBreadcrumbs.Drop("map-asset", "reading svg customs::base");
            CrashBreadcrumbs.Drop("map-asset", "drew customs::base");
        }

        CrashBreadcrumbs.Drop("map", "loading reserve/reserve-2d");
        CrashBreadcrumbs.Detach();

        CrashBreadcrumbs.Install(_directory);

        var end = CrashBreadcrumbs.PreviousRun;
        Assert.NotNull(end);
        Assert.Equal("#/plan", end.LastRoute);
        Assert.Equal("reserve/reserve-2d", end.LastMap);
        Assert.True(end.MapWasBeingDrawn);
        Assert.False(end.WasFrozen);
        Assert.Equal(
            "The last session ended without closing. Last page: #/plan. Last map: reserve/reserve-2d, still being drawn.",
            CrashBreadcrumbs.DescribePreviousRun());
    }

    /// <summary>A map that finished drawing, and a freeze that never ended, read as what they were.</summary>
    [Fact]
    public void AFreezeThatNeverRecoveredIsToldApartFromOneThatDid()
    {
        var recovered = CrashBreadcrumbs.Summarise(
        [
            "2026-09-20T10:00:00.0000000+00:00 [navigate] #/raid",
            "2026-09-20T10:00:01.0000000+00:00 [map-asset] reading svg reserve::bunkers",
            "2026-09-20T10:00:03.0000000+00:00 [map-asset] drew reserve::bunkers",
            "2026-09-20T10:00:04.0000000+00:00 [ui-hang] The interface has not answered for 5.0 s.",
            "2026-09-20T10:00:09.0000000+00:00 [ui-hang-recovered] The interface answered again after 9.8 s.",
        ]);
        var frozen = CrashBreadcrumbs.Summarise(
        [
            "2026-09-20T10:00:00.0000000+00:00 [navigate] #/raid",
            "2026-09-20T10:00:04.0000000+00:00 [ui-hang] The interface has not answered for 5.0 s.",
            "not a breadcrumb at all",
        ]);

        Assert.Equal(new PreviousRunEnd("#/raid", "reserve::bunkers", MapWasBeingDrawn: false, WasFrozen: false), recovered);
        Assert.Equal(new PreviousRunEnd("#/raid", null, MapWasBeingDrawn: false, WasFrozen: true), frozen);
    }

    /// <summary>A run that closed normally says nothing in Setup either.</summary>
    [Fact]
    public void ACleanExitLeavesNoPreviousRunToDescribe()
    {
        CrashLog.Install(_directory);
        CrashBreadcrumbs.Install(_directory);
        CrashBreadcrumbs.Drop("navigate", "#/plan");
        CrashBreadcrumbs.MarkCleanExit();
        CrashBreadcrumbs.Detach();

        CrashBreadcrumbs.Install(_directory);

        Assert.Null(CrashBreadcrumbs.PreviousRun);
        Assert.Equal(string.Empty, CrashBreadcrumbs.DescribePreviousRun());
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
        CrashBreadcrumbs.ForgetPreviousRun();
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
