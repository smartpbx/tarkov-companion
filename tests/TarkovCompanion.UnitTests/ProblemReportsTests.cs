using TarkovCompanion.GroupServer;
using TarkovCompanion.UnitTests.Runtime;

namespace TarkovCompanion.UnitTests;

/// <summary>Runs alone: every test points <c>TARKOV_GROUP_STATE</c> at its own directory.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProblemReportsCollection
{
    public const string Name = "problem-reports";
}

/// <summary>
/// #310: <see cref="ProblemReports.List"/> and <see cref="ProblemReports.Read"/> now share one
/// identity - the twelve-hex reference - instead of List handing back a basename Read could
/// never accept.
/// </summary>
[Collection(ProblemReportsCollection.Name)]
public sealed class ProblemReportsTests : IDisposable
{
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("tarkov-reports-state").FullName;
    private readonly string? _previousState = Environment.GetEnvironmentVariable("TARKOV_GROUP_STATE");

    public ProblemReportsTests()
    {
        Environment.SetEnvironmentVariable("TARKOV_GROUP_STATE", _stateDirectory);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TARKOV_GROUP_STATE", _previousState);
        try
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ReportsDirectory => Path.Combine(_stateDirectory, "reports");

    [Fact]
    public void AReportListedByListCanBeReadBackByRead()
    {
        var reports = new ProblemReports(Clock());
        var accepted = reports.Accept("room-a", "the report body");

        var listed = Assert.Single(reports.List());

        Assert.Equal(accepted.Reference, listed.Reference);
        Assert.Equal("the report body", reports.Read(listed.Reference));
    }

    [Fact]
    public void ARestartRelistsWhatWasAlreadyWrittenToDisk()
    {
        var clock = Clock();
        var accepted = new ProblemReports(clock).Accept("room-b", "kept across restart");

        var afterRestart = new ProblemReports(clock);
        var listed = Assert.Single(afterRestart.List());

        Assert.Equal(accepted.Reference, listed.Reference);
        Assert.Equal("kept across restart", afterRestart.Read(listed.Reference));
    }

    [Fact]
    public void EachReportKeepsItsOwnIdentityUnderRepeatedProcessing()
    {
        var clock = Clock();
        var reports = new ProblemReports(clock);
        var first = reports.Accept("room-c", "first body");
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = reports.Accept("room-c", "second body");

        Assert.NotEqual(first.Reference, second.Reference);
        Assert.Equal(2, reports.List().Count);

        // Reading the same reference twice - as the relay-watch workflow would if a run were
        // retried - returns the same body both times rather than consuming or losing it.
        Assert.Equal("first body", reports.Read(first.Reference));
        Assert.Equal("first body", reports.Read(first.Reference));
        Assert.Equal("second body", reports.Read(second.Reference));
        Assert.Equal("second body", reports.Read(second.Reference));
    }

    [Fact]
    public void HostileFilenamesInTheDirectoryAreSkippedInsteadOfListedOrRead()
    {
        var reports = new ProblemReports(Clock());
        var accepted = reports.Accept("room-d", "the only real report");

        Directory.CreateDirectory(ReportsDirectory);
        File.WriteAllText(Path.Combine(ReportsDirectory, "..-deadbeefcafe.md"), "traversal-shaped name");
        File.WriteAllText(Path.Combine(ReportsDirectory, "20260101-000000-DEADBEEFCAFE.md"), "uppercase hex");
        File.WriteAllText(Path.Combine(ReportsDirectory, "20260101-000000-tooshort.md"), "wrong length");
        File.WriteAllText(Path.Combine(ReportsDirectory, "not-a-report-at-all.md"), "no shape at all");

        var listed = Assert.Single(reports.List());

        Assert.Equal(accepted.Reference, listed.Reference);
        Assert.Null(reports.Read(".."));
        Assert.Null(reports.Read("tooshort"));
        Assert.Null(reports.Read("deadbeefcafe/../x"));
    }

    private static ManualTimeProvider Clock() => new(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
}
