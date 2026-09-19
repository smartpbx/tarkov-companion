using System.Diagnostics;
using System.Net;
using TarkovCompanion.GroupServer;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.TabletSurface;

/// <summary>
/// The tablet says what the desktop said about a command, instead of dropping the answer (#290).
/// </summary>
/// <remarks>
/// A paired tablet sent a command and never looked at the reply: <c>applyServerMessage</c> handled a
/// snapshot and an update and let a <c>commandAcknowledgement</c> fall through, so a mark the desktop
/// refused simply did not appear, a stale command was invisible, and a command sent to an absent
/// desktop looked the same as one that worked. The wording and the bookkeeping are plain JavaScript in
/// <c>Tablet/command-acknowledgement.js</c>, run under Node here for real behaviour; the page tests
/// below only prove the page is wired to it, which is the half a Node test cannot see.
/// </remarks>
public sealed class TabletAcknowledgementTests
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

    [Fact]
    public void ThePageLoadsTheModuleAndHandlesTheAcknowledgementAndDeprecationMessages()
    {
        Assert.Contains("<script src=\"/tablet/command-acknowledgement.js\"></script>", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("message?.type === \"commandAcknowledgement\"", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("message?.type === \"deprecation\"", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("id=\"commandNotice\"", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCommandIsTrackedUntilAnsweredAndAFailedSendIsNotSwallowed()
    {
        // sendCommand awaited a bare relayCall whose result nobody read, so a 401 or a dropped
        // connection looked like a mark that worked.
        Assert.Contains("pending.track(commandId", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("if (!response.ok)", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("failedToSend(command, response.status)", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("pending.expire(Date.now())", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void ARevokedTabletSaysSoInsteadOfPollingOnInSilence()
    {
        Assert.Contains("response.status === 401 || response.status === 403", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("The desktop removed this tablet.", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedModeRequestPutsTheButtonsBackToWhatTheDesktopLastSaid()
    {
        // requestMode moves the highlighted mode the moment a button is pressed, before any answer.
        Assert.Contains("mode = confirmedMode", Tablet.Page, StringComparison.Ordinal);
        Assert.Contains("confirmedMode = next", Tablet.Page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRunningRelayServesTheModuleTheTabletLoadsUnderAPolicyThatAllowsIt()
    {
        await using var relay = await RunningRelay.StartAsync();

        var script = await relay.Client.GetAsync("tablet/command-acknowledgement.js");
        var page = await relay.Client.GetAsync("tablet");

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", script.Content.Headers.ContentType?.ToString());
        Assert.Equal("nosniff", script.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Tablet.CommandAcknowledgementScript, await script.Content.ReadAsStringAsync());
        // Same-origin script is what 'self' allows; nothing inline was added to make room for it.
        var policy = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self' 'sha256-", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);
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
