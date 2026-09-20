using System.Text.RegularExpressions;
using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The budget shutdown is given, and the way it is spent.
/// </summary>
/// <remarks>
/// The Windows page gallery closes the main window and allows the process twenty seconds to be
/// gone. Teardown's budget was fifteen — and fifteen was also exactly what the two things it waits
/// on add up to, so a shutdown that used its budget used all of it and left five seconds to cover
/// the window closing and the process ending. That is what "The packaged app required forced
/// termination after '&lt;page&gt;'" was, and why it moved from page to page between runs rather
/// than naming one broken route.
/// </remarks>
public sealed class ShutdownStagesTests
{
    /// <summary>A step that will not finish is abandoned at its allowance, not waited out.</summary>
    [Fact]
    public async Task AStepThatOverrunsIsAbandonedAndSaysSo()
    {
        // No stopwatch. The step is a task that is never completed, so this method returning at
        // all is the proof that it was not waited out; a regression to an unbounded wait hangs the
        // test rather than failing an assertion about how busy the machine was.
        var stages = new ShutdownStages(TimeSpan.FromSeconds(5));

        await stages.RunAsync("stuck", () => new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(120));

        Assert.False(stages.WithinBudget);
        Assert.Contains("stuck", stages.Report(), StringComparison.Ordinal);
        Assert.Contains("(abandoned)", stages.Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The steps share one budget rather than each having their own.
    /// </summary>
    /// <remarks>
    /// This is the defect, stated as a test. The old shutdown nested its bounds: ten seconds for
    /// the feature lifecycle beside ten for the supervisor, then five more for an initialisation
    /// drain, inside a fifteen-second total — so the parts could add up to more than the whole and
    /// the whole was decided by whichever wait happened to be slowest.
    /// </remarks>
    [Fact]
    public async Task AStepThatSpendsTheBudgetLeavesNoneForTheNext()
    {
        var stages = new ShutdownStages(TimeSpan.FromMilliseconds(150));

        await stages.RunAsync("first", () => new TaskCompletionSource().Task, TimeSpan.FromSeconds(30));
        var remainingAfterFirst = stages.Remaining;
        await stages.RunAsync("second", () => new TaskCompletionSource().Task, TimeSpan.FromSeconds(30));

        // Both abandoned, neither given its own thirty seconds: that is the whole claim, and it
        // is a fact about the report rather than about how fast the machine was.
        var report = stages.Report();
        Assert.False(stages.WithinBudget);
        Assert.Equal(2, Regex.Matches(report, @"\(abandoned\)").Count);
        Assert.Contains("first", report, StringComparison.Ordinal);
        Assert.Contains("second", report, StringComparison.Ordinal);

        // An upper bound, which is the safe direction: load only makes the elapsed time larger and
        // so the remainder smaller. Not an equality — this failed on a CI runner at 0.15 ms left
        // of a 150 ms budget, because the timer that ends the wait runs on its own clock and can
        // be a fraction ahead of the stopwatch measuring it. Twenty milliseconds covers Windows's
        // ~15.6 ms timer granularity; what it proves is that the first step spent the shared
        // budget rather than having one of its own.
        Assert.True(
            remainingAfterFirst < TimeSpan.FromMilliseconds(20),
            $"A step that overran a 150 ms budget left {remainingAfterFirst.TotalMilliseconds:0.###} ms of it.");
    }

    /// <summary>A step that fails is recorded and the rest of shutdown carries on.</summary>
    /// <remarks>
    /// A dependency throwing on the way out must not be the reason the process stays alive, which
    /// is the same rule that applies to a pane that fails while the application is running.
    /// </remarks>
    [Fact]
    public async Task AStepThatThrowsDoesNotStopTheOnesAfterIt()
    {
        var stages = new ShutdownStages(TimeSpan.FromSeconds(5));
        var ran = false;

        await stages.RunAsync("angry", () => throw new InvalidOperationException("the relay went away"), TimeSpan.FromSeconds(1));
        await stages.RunAsync("calm", () => { ran = true; return Task.CompletedTask; }, TimeSpan.FromSeconds(1));

        Assert.True(ran);
        Assert.True(stages.WithinBudget);
        Assert.Contains("calm", stages.Report(), StringComparison.Ordinal);
    }

    /// <summary>An ordinary shutdown reports every step and its cost, in order.</summary>
    /// <remarks>
    /// The numbers are the point. Before this the log recorded only that a close had been slow,
    /// which is why nobody could say which of the six things teardown waits on was the slow one.
    /// </remarks>
    [Fact]
    public async Task TheReportNamesEachStepAndWhatItCost()
    {
        var stages = new ShutdownStages(TimeSpan.FromSeconds(5));

        await stages.RunAsync("diagnostic-channel", () => Task.CompletedTask, TimeSpan.FromSeconds(1));
        await stages.RunAsync("interface", () => Task.Delay(30), TimeSpan.FromSeconds(1));
        await stages.RunAsync("services", () => Task.CompletedTask, TimeSpan.FromSeconds(1));

        var report = stages.Report();

        Assert.True(stages.WithinBudget);
        Assert.Matches(
            @"^\d+ms total · diagnostic-channel \d+ms · interface \d+ms · services \d+ms$",
            report);
    }

    /// <summary>
    /// The shutdown budget stays inside what the Windows gallery allows a launch on the way out.
    /// </summary>
    /// <remarks>
    /// A ratchet, and the one that matters. The two numbers live in different languages in
    /// different files and drifted apart without anybody noticing: teardown was given fifteen
    /// seconds and the harness allows twenty, which sounds like a margin until the twenty also has
    /// to cover the window closing and the process ending. Read from the script rather than
    /// repeated here, so raising one and not the other fails rather than passes.
    /// </remarks>
    [Fact]
    public void TheShutdownBudgetLeavesTheGalleryRoomToSeeTheProcessGo()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "windows-page-gallery.ps1"));
        var allowance = Regex.Match(script, @"WaitForExit\((?<ms>\d{4,})\)");
        Assert.True(allowance.Success, "The gallery's close-and-wait allowance was not found in windows-page-gallery.ps1.");
        var galleryAllowance = TimeSpan.FromMilliseconds(int.Parse(allowance.Groups["ms"].Value, System.Globalization.CultureInfo.InvariantCulture));

        var budget = ShutdownBudget();

        Assert.True(
            budget <= galleryAllowance / 2,
            $"Teardown may take {budget.TotalSeconds:0} seconds of the {galleryAllowance.TotalSeconds:0} "
            + "the gallery allows for closing the window, tearing down and the process ending. Half is the "
            + "most that leaves room for the other two.");
    }

    /// <summary>The budget as the application really holds it, read rather than repeated.</summary>
    private static TimeSpan ShutdownBudget()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "TarkovCompanion.App", "Program.cs"));
        var match = Regex.Match(source, @"ShutdownTimeout = TimeSpan\.FromSeconds\((?<seconds>\d+)\)");
        Assert.True(match.Success, "Program.ShutdownTimeout was not found.");
        return TimeSpan.FromSeconds(int.Parse(match.Groups["seconds"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test output.");
    }
}
