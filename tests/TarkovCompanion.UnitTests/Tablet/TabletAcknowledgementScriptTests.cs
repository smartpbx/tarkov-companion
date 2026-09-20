using System.Diagnostics;

namespace TarkovCompanion.UnitTests.TabletSurface;

/// <summary>
/// The tablet says what the desktop said about a command, instead of dropping the answer (#290).
/// </summary>
/// <remarks>
/// A paired tablet sent a command and never looked at the reply: <c>applyServerMessage</c> handled
/// a snapshot and an update and let a <c>commandAcknowledgement</c> fall through, so a mark the
/// desktop refused simply did not appear, and a request the desktop was still busy with looked
/// exactly like one that worked. The wording and the bookkeeping are plain JavaScript in
/// <c>Tablet/command-acknowledgement.js</c>, run under Node here for real behaviour — a refused
/// request actually reaching the page and showing this same wording is proven separately, in a
/// real headless browser, by <c>RelayLink.TabletScreenshotHarness</c>.
/// </remarks>
public sealed class TabletAcknowledgementScriptTests
{
    [Fact]
    public async Task TheNodeChecksForEveryDispositionAndForCommandsNeverAnswered()
    {
        var script = Path.Combine(RepositoryRoot(), "scripts", "test-command-acknowledgement.mjs");
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("node did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(process.ExitCode == 0, await output + await errors);
        Assert.Contains("all checks passed", await output, StringComparison.Ordinal);
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

        throw new DirectoryNotFoundException("The repository root was not found above the test binaries.");
    }
}
