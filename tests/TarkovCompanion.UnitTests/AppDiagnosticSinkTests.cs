using System.Text.RegularExpressions;
namespace TarkovCompanion.UnitTests;

/// <summary>
/// A ratchet on where the application writes when something goes wrong.
/// </summary>
/// <remarks>
/// <see cref="System.Diagnostics.Trace"/> has no listener in an installed build. The only thing
/// that attaches one is <c>InterfaceWarningLog</c>, and only when a verification tool names a
/// file in the environment, so in a player's run every Trace warning is formatted and then
/// discarded. That is how nine separate workspace failures came to be invisible, and how
/// "panes not rendering, and no exception in the log" came to be undiagnosable on 2026-09-19.
///
/// This is a source scan rather than a behavioural test because the defect is a choice of sink,
/// which is visible in the text and not in any output. Kept narrow on purpose: it forbids the
/// error-shaped Trace helpers in the application's own code, not <c>Trace</c> itself, which
/// remains the right place for the toolkit's own logging to land.
/// </remarks>
public sealed class AppDiagnosticSinkTests
{
    private static readonly string[] ForbiddenCalls =
    [
        "Trace.TraceWarning(",
        "Trace.TraceError(",
        "Trace.Fail(",
    ];

    /// <summary>
    /// A file allowed to keep one of these, and why.
    /// </summary>
    /// <remarks>
    /// Empty, and meant to stay that way. <c>WorkspaceFault</c> exists precisely so there is
    /// somewhere better to go; if a new entry is ever needed here, the reason belongs beside it.
    /// </remarks>
    private static readonly string[] Allowed = [];

    [Fact]
    public void NothingInTheApplicationReportsAFailureToATraceListenerNobodyAttaches()
    {
        var appRoot = Path.Combine(RepositoryRoot(), "src", "TarkovCompanion.App");
        var offenders = Directory
            .EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !Allowed.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .Where(path => ForbiddenCalls.Any(call =>
                File.ReadAllText(path).Contains(call, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(appRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Every report the application builds carries the pages that did not load.
    /// </summary>
    /// <remarks>
    /// A source assertion because the alternative is constructing a thirty-three parameter view
    /// model to watch it call one method. It is the wiring, not the projection, that goes missing:
    /// <c>SupportBundle</c> gained the section and both "Copy diagnostics" and "Report a problem"
    /// have to pass it, and a third caller added later that forgets is a report that quietly stops
    /// answering the question. <c>StartupFaults</c> already spent a day with no reader but a test.
    /// </remarks>
    [Fact]
    public void EveryDiagnosticsReportCarriesThePagesThatDidNotLoad()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "TarkovCompanion.App", "ViewModels", "MainWindowViewModel.cs"));
        var calls = Regex.Matches(source, @"SupportBundle\.Describe\((?<arguments>[^;]*?)\);", RegexOptions.Singleline);

        Assert.NotEmpty(calls);
        Assert.All(
            calls,
            call => Assert.Contains("StartupFaults", call.Groups["arguments"].Value, StringComparison.Ordinal));
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
