using TarkovCompanion.Infrastructure.Processes;

namespace TarkovCompanion.UnitTests.Updates;

public sealed class OutsideInstallFolderTests
{
    private static readonly string Root = Path.GetPathRoot(Path.GetTempPath())!;
    private static readonly string Install = Path.Combine(Root, "Users", "p", "AppData", "Local", "TarkovCompanionDesktop", "current");

    [Fact]
    public void ALaunchNeverStandsInTheInstallFolder()
    {
        foreach (var start in new[]
                 {
                     OutsideInstallFolder.ShellOpen("https://escapefromtarkov.fandom.com/wiki/Salewa"),
                     OutsideInstallFolder.Program("TarkovCompanion"),
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(start.WorkingDirectory));
            Assert.True(Directory.Exists(start.WorkingDirectory), start.WorkingDirectory);
            Assert.False(OutsideInstallFolder.IsUnder(start.WorkingDirectory, AppContext.BaseDirectory), start.WorkingDirectory);
        }

        Assert.True(OutsideInstallFolder.ShellOpen("https://example.invalid/").UseShellExecute);
        Assert.False(OutsideInstallFolder.Program("TarkovCompanion").UseShellExecute);
    }

    [Fact]
    public void TheDataFolderBesideTheInstallIsNotMistakenForItsChild()
    {
        var install = Path.Combine(Root, "Users", "p", "AppData", "Local", "TarkovCompanion");
        var beside = Path.Combine(Root, "Users", "p", "AppData", "Local", "TarkovCompanionDesktop");

        Assert.False(OutsideInstallFolder.IsUnder(beside, install));
        Assert.True(OutsideInstallFolder.IsUnder(install, install));
        Assert.True(OutsideInstallFolder.IsUnder(install + Path.DirectorySeparatorChar, install));
        Assert.True(OutsideInstallFolder.IsUnder(Path.Combine(install, "Logs"), install));
        Assert.False(OutsideInstallFolder.IsUnder(Path.GetDirectoryName(install)!, install));
    }

    [Fact]
    public void ACandidateInsideTheInstallFolderOrMissingIsPassedOver()
    {
        var profile = Path.Combine(Root, "Users", "p");
        var chosen = OutsideInstallFolder.Choose(
            Install,
            [null, " ", Path.Combine(Install, "Data"), Path.Combine(Root, "missing"), profile],
            exists: candidate => candidate != Path.Combine(Root, "missing"));

        Assert.Equal(profile, chosen);
        // With nothing usable, still an answer, and still not the install folder.
        var fallback = OutsideInstallFolder.Choose(Install, [Install], exists: _ => true);
        Assert.False(OutsideInstallFolder.IsUnder(fallback, Install));
    }

    /// <summary>What <c>Main</c> does first: started from a shortcut, it is no longer in <c>current\</c>.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("runtimes")]
    public void StartedInTheInstallFolderTheProcessLeavesIt(string below)
    {
        var current = Path.Combine(Install, below);
        var destination = Path.Combine(Root, "Users", "p");

        var moved = OutsideInstallFolder.LeaveInstallFolder(Install, () => current, to => current = to, destination);

        Assert.Equal(destination, moved);
        Assert.False(OutsideInstallFolder.IsUnder(current, Install));
    }

    [Fact]
    public void StartedFromAWorkFolderTheProcessStaysThereSoRelativeArgumentsStillResolve()
    {
        var work = Path.Combine(Root, "work");
        var current = work;

        Assert.Null(OutsideInstallFolder.LeaveInstallFolder(Install, () => current, to => current = to));
        Assert.Equal(work, current);
    }

    [Fact]
    public void AWorkingDirectoryThatCannotBeReadDoesNotStopTheStart() =>
        Assert.Null(OutsideInstallFolder.LeaveInstallFolder(
            Install,
            () => throw new UnauthorizedAccessException(),
            _ => Assert.Fail("nothing to move from")));

    [Fact]
    public void ThisProcessIsNotLeftStandingInItsOwnBaseDirectory()
    {
        // The real prologue over the real process, without moving the test host: it reports
        // where it would go, and that place is outside the base directory.
        var current = AppContext.BaseDirectory;
        var moved = OutsideInstallFolder.LeaveInstallFolder(AppContext.BaseDirectory, () => current, to => current = to);

        Assert.NotNull(moved);
        Assert.False(OutsideInstallFolder.IsUnder(current, AppContext.BaseDirectory));
        Assert.True(Directory.Exists(current));
    }
}
