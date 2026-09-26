using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.FormatGuards;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Domain.Situations;
using TarkovCompanion.UnitTests.Situations;

namespace TarkovCompanion.UnitTests.FormatGuards;

/// <summary>[#712 0-3] The format-health signal: degrades on a changed shape, recovers, and says so.</summary>
/// <remarks>
/// The "changed" shapes here are invented: nobody knows what Unity 6 will write. What is pinned is
/// that anything the parsers do not know, arriving in bulk, is noticed.
/// </remarks>
public sealed class FormatHealthMonitorTests
{
    private const string OldBuild = "1.1.5.1.47510";
    private const string NewBuild = "1.1.6.0.49000";

    private static string Path(string build, string file = "application") =>
        $"C:/EFT/Logs/log_2026.01.01_20-00-00_{build}/2026.01.01_20-00-00_{build} {file}_000.log";

    private static string KnownLine(string build, int n) =>
        $"2026-01-01 20:00:{n % 60:00}.000|{build}|Info|application|LocationLoaded:18.4 real:24.73 diff:6.33";

    /// <summary>A plausible reshaped header: ISO time, bracketed level. Invented.</summary>
    private static string ReshapedLine(int n) =>
        $"2026-01-01T20:00:{n % 60:00}.000Z [Info] application: LocationLoaded 18.4";

    private static void Feed(FormatHealthMonitor monitor, string path, Func<int, string> line, int count)
    {
        for (var n = 0; n < count; n++)
        {
            monitor.ObserveLogLine(path, line(n));
        }
    }

    [Fact]
    public void KnownLinesAndNamesReadAsOk()
    {
        var monitor = new FormatHealthMonitor();
        Feed(monitor, Path(OldBuild), n => KnownLine(OldBuild, n), 60);
        for (var n = 0; n < 3; n++)
        {
            monitor.ObserveScreenshotName($"2026-01-01[20-1{n}]_1.0, 2.0, 3.0_0.0, 0.0, 0.0, 1.0_10.5 (0).png");
        }

        var report = monitor.Current;
        Assert.Equal(FormatHealthStatus.Ok, report.Logs);
        Assert.Equal(FormatHealthStatus.Ok, report.Screenshots);
        Assert.Equal(OldBuild, report.GameVersion);
        Assert.False(report.IsDegraded);
    }

    [Fact]
    public void TooFewLinesAreNotAVerdict()
    {
        var monitor = new FormatHealthMonitor();
        Feed(monitor, Path(OldBuild), ReshapedLine, FormatHealthMonitor.LogWindow.Minimum - 1);

        Assert.Equal(FormatHealthStatus.Unknown, monitor.Current.Logs);
    }

    [Fact]
    public void AReshapedLogAfterAnUpdateIsDegradedAndSaysWhichUpdate()
    {
        var monitor = new FormatHealthMonitor();
        var changes = 0;
        monitor.Changed += (_, _) => changes++;
        Feed(monitor, Path(OldBuild), n => KnownLine(OldBuild, n), 60);

        Feed(monitor, Path(NewBuild), ReshapedLine, 60);

        var logs = monitor.Current.For(FormatSource.GameLog);
        Assert.Equal(FormatHealthStatus.Degraded, logs.Status);
        Assert.True(logs.ChangedAfterUpdate);
        Assert.Equal(NewBuild, logs.GameVersion);
        Assert.Equal(OldBuild, logs.LastHealthyVersion);
        // The first build, OK, the new build, degraded: never once per line.
        Assert.Equal(4, changes);
    }

    [Fact]
    public void ADegradedLogRecoversWhenTheLinesAreKnownAgain()
    {
        var monitor = new FormatHealthMonitor();
        Feed(monitor, Path(OldBuild), n => KnownLine(OldBuild, n), 60);
        Feed(monitor, Path(NewBuild), ReshapedLine, 60);
        Assert.Equal(FormatHealthStatus.Degraded, monitor.Current.Logs);

        // Half known is not enough to call it fixed: the window must mostly be known shapes.
        Feed(monitor, Path(NewBuild), n => n % 3 == 0 ? ReshapedLine(n) : KnownLine(NewBuild, n), 60);
        Assert.Equal(FormatHealthStatus.Degraded, monitor.Current.Logs);

        Feed(monitor, Path(NewBuild), n => KnownLine(NewBuild, n), FormatHealthMonitor.LogWindow.Window);
        Assert.Equal(FormatHealthStatus.Ok, monitor.Current.Logs);
        Assert.Equal(NewBuild, monitor.Current.For(FormatSource.GameLog).LastHealthyVersion);
    }

    [Fact]
    public void UnitysStackFramesInOutputAreNotCountedAgainstTheFormat()
    {
        var monitor = new FormatHealthMonitor();
        Feed(monitor, Path(OldBuild, "output"), n => n % 10 == 0 ? KnownLine(OldBuild, n) : "EFT.CameraControl.OpticRetrice.UpdateTransform ()", 600);

        Assert.Equal(FormatHealthStatus.Ok, monitor.Current.Logs);
    }

    [Fact]
    public void AChangedNotificationEnvelopeDegradesTheLogsEvenWithGoodHeaders()
    {
        var monitor = new FormatHealthMonitor();
        var header = $"2026-01-01 20:00:00.000|{OldBuild}|Info|backend|WebSocketSharp - message received: ";
        Feed(monitor, Path(OldBuild, "backend"), _ => header + """NOTIFICATION e1 userConfirmed [{"type":"userConfirmed"}]""", 40);
        Assert.Equal(FormatHealthStatus.Ok, monitor.Current.For(FormatSource.Notification).Status);

        // Invented: the payload no longer an array.
        Feed(monitor, Path(OldBuild, "backend"), _ => header + """NOTIFICATION e1 userConfirmed {"type":"userConfirmed"}""", 20);

        Assert.Equal(FormatHealthStatus.Ok, monitor.Current.For(FormatSource.GameLog).Status);
        Assert.Equal(FormatHealthStatus.Degraded, monitor.Current.For(FormatSource.Notification).Status);
        Assert.Equal(FormatHealthStatus.Degraded, monitor.Current.Logs);
    }

    [Fact]
    public void RenamedScreenshotsDegradeAndRecover()
    {
        var monitor = new FormatHealthMonitor();
        Feed(monitor, Path(OldBuild), n => KnownLine(OldBuild, n), 60);
        for (var n = 0; n < 5; n++)
        {
            monitor.ObserveScreenshotName($"2026-01-01[20-1{n}]_1.0, 2.0, 3.0_0.0, 0.0, 0.0, 1.0_10.5 (0).png");
        }

        Assert.Equal(FormatHealthStatus.Ok, monitor.Current.Screenshots);
        for (var n = 0; n < 10; n++)
        {
            // Invented: position as x;y;z.
            monitor.ObserveScreenshotName($"2026-01-01 20.1{n} pos=1.0;2.0;3.0.png");
        }

        Assert.Equal(FormatHealthStatus.Degraded, monitor.Current.Screenshots);
        for (var n = 0; n < 10; n++)
        {
            monitor.ObserveScreenshotName($"2026-01-01[21-0{n}]_19.67 (0).png");
        }

        Assert.Equal(FormatHealthStatus.Ok, monitor.Current.Screenshots);
    }

    [Fact]
    public void TheSituationCarriesTheReasonAndDoesNotTickWithEveryLine()
    {
        var clock = new SituationTimeline.ManualClock(new DateTimeOffset(2026, 1, 1, 20, 0, 0, TimeSpan.Zero));
        var monitor = new FormatHealthMonitor(clock);
        using var situation = new SituationService(null, clock, refresh: TimeSpan.Zero, formatHealth: monitor);
        Assert.Null(situation.Current.FormatHealth);

        Feed(monitor, Path(OldBuild), n => KnownLine(OldBuild, n), 60);
        Assert.Equal(FormatHealthStatus.Ok, situation.Current.FormatHealth?.Value);

        Feed(monitor, Path(NewBuild), ReshapedLine, 60);
        var fact = situation.Current.FormatHealth;
        Assert.NotNull(fact);
        Assert.Equal(FormatHealthStatus.Degraded, fact.Value);
        Assert.Contains($"since update {NewBuild}", fact.Because, StringComparison.Ordinal);

        var version = situation.Current.Version;
        Feed(monitor, Path(NewBuild), ReshapedLine, 50);
        situation.Refresh();
        Assert.Equal(version, situation.Current.Version);
    }

    [Fact]
    public void TheReadinessRowSaysOkWaitingOrWhatChanged()
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        Assert.Equal("Game logs: not read yet · Screenshots: not read yet", SetupText.FormatHealthRow(FormatHealthReport.Empty));

        var monitor = new FormatHealthMonitor();
        Feed(monitor, Path(OldBuild), n => KnownLine(OldBuild, n), 60);
        for (var n = 0; n < 3; n++)
        {
            monitor.ObserveScreenshotName($"2026-01-01[21-0{n}]_19.67 (0).png");
        }

        Assert.Equal("Game logs: OK · Screenshots: OK", SetupText.FormatHealthRow(monitor.Current));

        Feed(monitor, Path(NewBuild), ReshapedLine, 60);
        Assert.Equal(
            $"Game logs: format changed after update {NewBuild} — raid detection may be wrong · Screenshots: OK",
            SetupText.FormatHealthRow(monitor.Current));
    }

    [Fact]
    public void AFormatNeverSeenWorkingSaysNotRecognisedRatherThanAfterAnUpdate()
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        var monitor = new FormatHealthMonitor();
        Feed(monitor, Path(NewBuild), ReshapedLine, 60);

        Assert.StartsWith("Game logs: format not recognised — raid detection may be wrong", SetupText.FormatHealthRow(monitor.Current), StringComparison.Ordinal);
    }
}
