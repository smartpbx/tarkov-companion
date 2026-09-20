using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>
/// #292/#309: a problem report is read, then sent as the text that was read. Agreeing to a report is agreeing
/// to its contents, so the send must be of what was shown and must need a fresh look every time.
/// </summary>
public sealed class SetupReportTests
{
    [Fact]
    public async Task SendSendsExactlyTheTextThatWasShownEvenIfTheStateChangedAfterwards()
    {
        var builds = 0;
        var sent = new List<string>();
        var view = Build(() => $"report v{++builds}", sent);

        view.PreviewCommand.Execute(null);
        Assert.Equal("report v1", view.ReportText);
        await ((AsyncDelegateCommand)view.SendCommand).ExecuteAsync();

        Assert.Equal(["report v1"], sent);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task LookingSendsNothingAndSendWithNothingShownDoesNothing()
    {
        var sent = new List<string>();
        var view = Build(() => "a report", sent);

        await ((AsyncDelegateCommand)view.SendCommand).ExecuteAsync();
        Assert.Empty(sent);
        Assert.False(view.HasPreview);

        view.PreviewCommand.Execute(null);
        Assert.True(view.HasPreview);
        Assert.Empty(sent);
        Assert.Equal("8 characters", view.Size);
    }

    [Fact]
    public async Task AfterASendTheNextOneNeedsAFreshLookAndDiscardSendsNothing()
    {
        var sent = new List<string>();
        var view = Build(() => "a report", sent);
        var send = (AsyncDelegateCommand)view.SendCommand;

        view.PreviewCommand.Execute(null);
        await send.ExecuteAsync();
        await send.ExecuteAsync();

        Assert.Single(sent);
        Assert.False(view.HasPreview);

        view.PreviewCommand.Execute(null);
        view.DiscardCommand.Execute(null);
        await send.ExecuteAsync();

        Assert.Single(sent);
        Assert.False(view.HasPreview);
    }

    [Fact]
    public async Task NothingToDescribeIsSaidAndNothingCanBeSent()
    {
        var sent = new List<string>();
        var view = Build(() => null, sent);

        view.PreviewCommand.Execute(null);
        await ((AsyncDelegateCommand)view.SendCommand).ExecuteAsync();

        Assert.False(view.HasPreview);
        Assert.Contains("Nothing to describe", view.Status, StringComparison.Ordinal);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task ASendThatThrowsIsReportedAndPointsAtCopyDiagnostics()
    {
        var view = new SetupReportViewModel(() => "a report", (_, _) => throw new InvalidOperationException("relay unreachable"));

        view.PreviewCommand.Execute(null);
        await ((AsyncDelegateCommand)view.SendCommand).ExecuteAsync();

        Assert.Equal("Could not send: relay unreachable. Use Copy diagnostics instead.", view.Status);
        Assert.False(view.HasPreview);
    }

    [Fact]
    public async Task TheRealCompositionShowsTheClosedReportAndSendsNothingUntilAsked()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-report-{Guid.NewGuid():N}");
        try
        {
            await using var services = AppComposition.Build(
                new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                new(DataRoot: root, Offline: true));
            var legacy = services.GetRequiredService<MainWindowViewModel>();
            var shell = services.GetRequiredService<V2ShellViewModel>();
            legacy.PreviewShell = shell;
            await legacy.InitializeAsync();

            var report = shell.SetupWorkspace!.Admin!.Report;

            Assert.NotNull(report);
            report.PreviewCommand.Execute(null);
            Assert.True(report.HasPreview);
            // The closed projection: it ends by saying what it excludes, and it names no path on this machine.
            Assert.EndsWith(SupportBundle.Footer, report.ReportText.TrimEnd(), StringComparison.Ordinal);
            Assert.DoesNotContain(root, report.ReportText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetTempPath(), report.ReportText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static SetupReportViewModel Build(Func<string?> build, List<string> sent) => new(
        build,
        (report, _) =>
        {
            sent.Add(report);
            return Task.FromResult("Sent.");
        });
}
