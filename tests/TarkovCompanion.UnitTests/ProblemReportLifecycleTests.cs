using TarkovCompanion.GroupServer;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What the relay keeps of problem reports, for how long, how much, and in what state (#310).
/// </summary>
/// <remarks>
/// The relay accepted any number of any reports and kept every one until an operator deleted it, so a
/// hostile or merely looping caller could fill the disk the room registry and device registry share.
/// Each of these is a way that was possible and no longer is. They point a <see cref="ProblemReports"/>
/// at its own directory rather than setting <c>TARKOV_GROUP_STATE</c>, which is process-wide.
/// </remarks>
public sealed class ProblemReportLifecycleTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "reports-" + Guid.NewGuid().ToString("N"));
    private readonly RelayTestClock _clock = new(Start);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp.
        }
    }

    private ProblemReports Reports(ProblemReportLimits? limits = null) =>
        new(_clock, limits ?? ProblemReportLimits.Default with { MinimumFreeMegabytes = 0 }, _directory);

    [Fact]
    public void AReportIsReceivedThenProcessedAndProcessingItTwiceChangesNothing()
    {
        var reports = Reports();
        var reference = reports.Accept("room-a", "body").Reference;

        Assert.Equal(reference, Assert.Single(reports.List()).Reference);
        Assert.Equal(ReportTransition.Applied, reports.MarkProcessed(reference));
        Assert.Equal(ReportTransition.Unchanged, reports.MarkProcessed(reference));

        // No longer to be filed, still held and still readable by the operator.
        Assert.Empty(reports.List());
        var entry = Assert.Single(reports.Ledger().Reports);
        Assert.Equal("processed", entry.State);
        Assert.Equal("body", reports.Read(reference));
    }

    [Fact]
    public void AProcessedReportIsNeverDowngradedToFailed()
    {
        var reports = Reports();
        var reference = reports.Accept("room-a", "body").Reference;
        reports.MarkProcessed(reference);

        Assert.Equal(ReportTransition.Refused, reports.MarkFailed(reference));
        Assert.Equal("processed", Assert.Single(reports.Ledger().Reports).State);
    }

    [Fact]
    public void AReportThatKeepsFailingDropsOutOfTheQueueAtTheAttemptLimitButStaysVisibleToTheOperator()
    {
        var limits = ProblemReportLimits.Default with { MinimumFreeMegabytes = 0, MaximumFilingAttempts = 3 };
        var reports = Reports(limits);
        var reference = reports.Accept("room-a", "body").Reference;

        reports.MarkFailed(reference);
        reports.MarkFailed(reference);
        Assert.Single(reports.List());
        reports.MarkFailed(reference);

        Assert.Empty(reports.List());
        var entry = Assert.Single(reports.Ledger().Reports);
        Assert.Equal(("failed", 3), (entry.State, entry.Attempts));
    }

    [Fact]
    public void DeletingIsIdempotentAndMarkingWhatIsGoneOrNeverWasIsNotFound()
    {
        var reports = Reports();
        var reference = reports.Accept("room-a", "body").Reference;

        Assert.True(reports.Delete(reference));
        Assert.False(reports.Delete(reference));
        Assert.Equal(ReportTransition.NotFound, reports.MarkProcessed(reference));
        Assert.Null(reports.Read(reference));
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Equal(ReportTransition.NotFound, reports.MarkProcessed("../../etc/pas"));
        Assert.False(reports.Delete("*"));
    }

    [Fact]
    public void AStateSurvivesARestart()
    {
        var reference = Reports().Accept("room-a", "body").Reference;
        Reports().MarkFailed(reference);

        var afterRestart = Reports();

        var entry = Assert.Single(afterRestart.Ledger().Reports);
        Assert.Equal(("failed", 1), (entry.State, entry.Attempts));
    }

    [Fact]
    public void AReportOlderThanItsTimeToLiveIsSweptAndOneWithinItIsNot()
    {
        var reports = Reports(ProblemReportLimits.Default with { MinimumFreeMegabytes = 0, TimeToLive = TimeSpan.FromDays(10) });
        var old = reports.Accept("room-a", "old").Reference;
        _clock.Advance(TimeSpan.FromDays(8));
        var recent = reports.Accept("room-b", "recent").Reference;
        _clock.Advance(TimeSpan.FromDays(3));

        Assert.Equal(1, reports.Sweep());

        Assert.Null(reports.Read(old));
        Assert.Equal("recent", reports.Read(recent));
        Assert.Empty(Directory.GetFiles(_directory, "*-" + old + ".*"));
    }

    [Fact]
    public void ASweepDoesItsWorkAtMostHourlyEvenWhenAskedEveryMinute()
    {
        var reports = Reports(ProblemReportLimits.Default with { MinimumFreeMegabytes = 0, TimeToLive = TimeSpan.FromMinutes(30) });
        reports.Accept("room-a", "old");
        _clock.Advance(TimeSpan.FromHours(2));
        reports.Accept("room-b", "newer");

        Assert.Equal(1, reports.Sweep());

        // The second report is past its time to live 40 minutes later, but the last sweep was 40
        // minutes ago, so this one is not the hour's work and does nothing.
        _clock.Advance(TimeSpan.FromMinutes(40));
        Assert.Equal(0, reports.Sweep());
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(1, reports.Sweep());
    }

    [Fact]
    public void ACrashBetweenTwoWritesLeavesDebrisTheSweepRemovesAndNeverAHalfReport()
    {
        var reports = Reports();
        reports.Accept("room-a", "kept");
        var stale = Path.Combine(_directory, "20260101-000000-aaaaaaaaaaaa");
        File.WriteAllText(stale + ".state", "{}");
        File.WriteAllText(stale + ".md.tmp", "half a report");
        var fresh = Path.Combine(_directory, "20260919-115959-bbbbbbbbbbbb.state");
        File.WriteAllText(fresh, "{}");
        foreach (var path in new[] { stale + ".state", stale + ".md.tmp", fresh })
        {
            File.SetLastWriteTimeUtc(path, path == fresh ? Start.UtcDateTime : Start.UtcDateTime.AddHours(-5));
        }

        // A temporary file and a state file with no body are never listed as reports.
        Assert.Single(reports.List());
        reports.Sweep();

        Assert.False(File.Exists(stale + ".state"));
        Assert.False(File.Exists(stale + ".md.tmp"));
        // Newer than the debris age: might be a write in progress, so it is left.
        Assert.True(File.Exists(fresh));
        Assert.Single(reports.List());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("{\"state\":\"processed\",\"attempts\":-5}")]
    [InlineData("{\"state\":\"processed\",\"attempts\":99999999999}")]
    public void ADamagedStateFileMeansReceivedSoNothingIsLostAndNothingCrashes(string content)
    {
        var reports = Reports();
        var reference = reports.Accept("room-a", "body").Reference;
        var state = Directory.GetFiles(_directory, "*-" + reference + ".state").Single();
        File.WriteAllText(state, content);

        var entry = Assert.Single(reports.Ledger().Reports);

        Assert.Equal("received", entry.State);
        Assert.Equal("body", reports.Read(reference));
    }

    [Fact]
    public void AHostileStateFileCannotBeAnObjectBombOrAnOversizedRead()
    {
        var reports = Reports();
        var reference = reports.Accept("room-a", "body").Reference;
        var state = Directory.GetFiles(_directory, "*-" + reference + ".state").Single();

        File.WriteAllText(state, new string('[', 50_000) + new string(']', 50_000));
        Assert.Equal("received", Assert.Single(reports.Ledger().Reports).State);

        File.WriteAllText(state, "{\"state\":\"processed\",\"pad\":\"" + new string('x', 100_000) + "\"}");
        Assert.Equal("received", Assert.Single(reports.Ledger().Reports).State);
    }

    [Fact]
    public void TheRelayStopsAtItsReportCountAndSaysWhyWithoutKeepingTheRefusedOne()
    {
        var reports = Reports(ProblemReportLimits.Default with { MinimumFreeMegabytes = 0, MaximumHeld = 2 });
        Assert.Equal(ReportAdmission.Accepted, reports.Accept("room-a", "1").Admission);
        Assert.Equal(ReportAdmission.Accepted, reports.Accept("room-b", "2").Admission);

        var refused = reports.Accept("room-c", "3");

        Assert.Equal(ReportAdmission.RelayFull, refused.Admission);
        Assert.Equal(string.Empty, refused.Reference);
        Assert.Contains("Copy diagnostics", refused.Detail, StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(_directory, "*.md").Length);
    }

    [Fact]
    public void TheRelayStopsAtItsByteBudget()
    {
        var reports = Reports(ProblemReportLimits.Default with { MinimumFreeMegabytes = 0, MaximumHeldBytes = 100 });
        Assert.Equal(ReportAdmission.Accepted, reports.Accept("room-a", new string('x', 60)).Admission);

        Assert.Equal(ReportAdmission.RelayFull, reports.Accept("room-b", new string('y', 60)).Admission);
        Assert.Equal(ReportAdmission.Accepted, reports.Accept("room-b", new string('y', 40)).Admission);
    }

    [Fact]
    public void OneRoomCannotFillTheRelayOnItsOwn()
    {
        var reports = Reports(ProblemReportLimits.Default with { MinimumFreeMegabytes = 0, MaximumHeldPerRoom = 2 });
        reports.Accept("room-a", "1");
        _clock.Advance(TimeSpan.FromSeconds(1));
        reports.Accept("room-a", "2");
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(ReportAdmission.RoomFull, reports.Accept("room-a", "3").Admission);
        Assert.Equal(ReportAdmission.Accepted, reports.Accept("room-b", "1").Admission);
    }

    [Fact]
    public void TheWholeRelayHasAnHourlyCapAndItLiftsAsTheHourPasses()
    {
        var reports = Reports(ProblemReportLimits.Default with { MinimumFreeMegabytes = 0, MaximumPerHour = 2 });
        reports.Accept("room-a", "1");
        _clock.Advance(TimeSpan.FromSeconds(1));
        reports.Accept("room-b", "2");
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(ReportAdmission.TooManyRecently, reports.Accept("room-c", "3").Admission);
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(ReportAdmission.Accepted, reports.Accept("room-c", "3").Admission);
    }

    [Fact]
    public void ALowDiskStopsReportsBeforeItStopsTheRelay()
    {
        // No volume has this much free, which is the same as every volume being short.
        var reports = Reports(ProblemReportLimits.Default with { MinimumFreeMegabytes = 100_000_000 });

        var refused = reports.Accept("room-a", "body");

        Assert.Equal(ReportAdmission.DiskPressure, refused.Admission);
        Assert.Contains("short of space", refused.Detail, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_directory) && Directory.GetFiles(_directory).Length > 0);
    }

    [Fact]
    public void ABurstOfConcurrentReportsCannotOverfillWhatIsHeld()
    {
        var reports = Reports(ProblemReportLimits.Default with
        {
            MinimumFreeMegabytes = 0,
            MaximumHeld = 5,
            MaximumHeldPerRoom = 100,
            MaximumPerHour = 1_000,
        });

        var outcomes = Enumerable.Range(0, 40).AsParallel().WithDegreeOfParallelism(16)
            .Select(index => reports.Accept("room-" + index, "body " + index))
            .ToArray();

        Assert.Equal(5, outcomes.Count(outcome => outcome.Admission == ReportAdmission.Accepted));
        Assert.Equal(5, Directory.GetFiles(_directory, "*.md").Length);
    }

    [Fact]
    public void AnUnreadableEnvironmentValueIsTheDefaultAndAnInRangeOneIsUsed()
    {
        var limits = ProblemReportLimits.FromEnvironment(name => name switch
        {
            ProblemReportLimits.TimeToLiveDaysVariable => "7",
            ProblemReportLimits.MaximumHeldVariable => "many",
            ProblemReportLimits.MaximumMegabytesVariable => "99999",
            ProblemReportLimits.MinimumFreeMegabytesVariable => "-1",
            _ => null,
        });

        Assert.Equal(TimeSpan.FromDays(7), limits.TimeToLive);
        Assert.Equal(ProblemReportLimits.Default.MaximumHeld, limits.MaximumHeld);
        Assert.Equal(ProblemReportLimits.Default.MaximumHeldBytes, limits.MaximumHeldBytes);
        Assert.Equal(ProblemReportLimits.Default.MinimumFreeMegabytes, limits.MinimumFreeMegabytes);
    }
}
