using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>#309: turning tidying on is agreed to as a list, turning it off is one press, and the last runs are shown.</summary>
public sealed class SetupCleanupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 20, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Directory.CreateTempSubdirectory("tarkov-cleanup").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task StartingShowsWhatWouldMoveAndNothingIsTurnedOnUntilItIsConfirmed()
    {
        Write("2026-09-10[14-05]_1_a.png", Now.AddDays(-5), 2_048);
        Write("2026-09-11[10-00]_2_a.png", Now.AddDays(-4), 2_048);
        Write("2026-09-19[19-59]_4_a.png", Now.AddMinutes(-1), 10);
        var toggle = new CountingCommand();
        var view = Build(toggle, enabled: false);

        view.RequestToggleCommand.Execute(null);
        await UntilAsync(() => view.HasPreview);

        Assert.True(view.IsConfirming);
        Assert.Equal(0, toggle.Runs);
        Assert.Equal(Path.GetFullPath(_folder), view.PreviewFolder);
        Assert.Equal("Older than 24 h · the newest is always kept · only files the game named", view.PreviewPolicy);
        Assert.Equal("2 screenshots (4 KB) would move to the recycle bin.", view.PreviewSummary);
        Assert.Equal(["2026-09-10[14-05]_1_a.png", "2026-09-11[10-00]_2_a.png"], view.PreviewFiles);
        Assert.Equal(3, Directory.GetFiles(_folder).Length);

        view.ConfirmEnableCommand.Execute(null);

        Assert.Equal(1, toggle.Runs);
        Assert.False(view.IsConfirming);
        Assert.Equal("Stop tidying", view.ToggleLabel);
    }

    [Fact]
    public async Task CancellingTurnsNothingOn()
    {
        Write("2026-09-10[14-05]_1_a.png", Now.AddDays(-5), 10);
        Write("2026-09-19[19-59]_4_a.png", Now.AddMinutes(-1), 10);
        var toggle = new CountingCommand();
        var view = Build(toggle, enabled: false);
        view.RequestToggleCommand.Execute(null);
        await UntilAsync(() => view.IsConfirming);

        view.CancelCommand.Execute(null);

        Assert.False(view.IsConfirming);
        Assert.False(view.HasPreview);
        Assert.Equal(0, toggle.Runs);
    }

    [Fact]
    public async Task StoppingIsOnePressWithNoPreviewInTheWay()
    {
        var toggle = new CountingCommand();
        var view = Build(toggle, enabled: true);

        view.RequestToggleCommand.Execute(null);
        await UntilAsync(() => toggle.Runs == 1);

        Assert.False(view.IsConfirming);
        Assert.False(view.HasPreview);
        Assert.Equal("Start tidying", view.ToggleLabel);
    }

    [Fact]
    public async Task ADryRunPreviewsWithoutOfferingToTurnAnythingOn()
    {
        Write("2026-09-10[14-05]_1_a.png", Now.AddDays(-5), 10);
        Write("2026-09-19[19-59]_4_a.png", Now.AddMinutes(-1), 10);
        var toggle = new CountingCommand();
        var view = Build(toggle, enabled: true);

        view.PreviewCommand.Execute(null);
        await UntilAsync(() => view.HasPreview);

        Assert.False(view.IsConfirming);
        Assert.Equal("1 screenshot (10 B) would move to the recycle bin.", view.PreviewSummary);
        Assert.Equal(0, toggle.Runs);
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public async Task AFolderThatCannotBeTidiedIsRefusedAndNeverOfferedForConfirmation()
    {
        var view = Build(new CountingCommand(), enabled: false, root: Path.GetPathRoot(_folder));

        view.RequestToggleCommand.Execute(null);
        await UntilAsync(() => view.HasPreview);

        Assert.True(view.IsRefused);
        Assert.False(view.IsConfirming);
        Assert.Contains("drive root", view.PreviewRefusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastRunsAreShownNewestFirstWithWhyAnyFailed()
    {
        var ledger = new FixedLedger(
            new(Now.AddDays(-2), 24, 14, 40_054_812, 0, new Dictionary<string, int>()),
            new(Now.AddHours(-3), 24, 5, 10_240, 2, new Dictionary<string, int> { ["The recycle bin would not take it."] = 2 }));
        var view = Build(new CountingCommand(), enabled: true, ledger: ledger);

        Assert.True(view.HasLedger);
        Assert.Equal(
            [
                "3 h ago · moved 5 (10 KB) · 2 could not move",
                "   2 × The recycle bin would not take it.",
                "2 d ago · moved 14 (38.2 MB) · 0 could not move",
            ],
            view.LastRuns);
    }

    [Fact]
    public async Task TheRealCompositionRoutesTheSetupTidyButtonThroughThePreview()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-cleanup-{Guid.NewGuid():N}");
        try
        {
            await using var services = AppComposition.Build(
                new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                new(DataRoot: root, Offline: true));
            var legacy = services.GetRequiredService<MainWindowViewModel>();
            var shell = services.GetRequiredService<V2ShellViewModel>();
            legacy.PreviewShell = shell;
            await legacy.InitializeAsync();

            var setup = shell.SetupWorkspace!;

            Assert.True(setup.HasCleanup);
            Assert.False(setup.NoCleanup);
            Assert.IsType<SetupCleanupViewModel>(setup.Cleanup);
            Assert.NotNull(services.GetRequiredService<IScreenshotTidyLedger>());
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

    private SetupCleanupViewModel Build(ICommand toggle, bool enabled, string? root = null, IScreenshotTidyLedger? ledger = null) => new(
        new ScreenshotRetentionService(new RecordingBin(), new FixedClock(Now), ledger),
        new FixedStore(ScreenshotRetentionSettings.Default with { IsEnabled = enabled }),
        ledger,
        () => root ?? _folder,
        () => toggle,
        () => true,
        new FixedClock(Now));

    private void Write(string name, DateTimeOffset writtenUtc, int bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, new string('x', bytes));
        File.SetLastWriteTimeUtc(path, writtenUtc.UtcDateTime);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the cleanup panel did not settle");
    }

    private sealed class CountingCommand : ICommand
    {
        public int Runs { get; private set; }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Runs++;
    }

    private sealed class FixedStore(ScreenshotRetentionSettings settings) : IScreenshotRetentionStore
    {
        public Task<ScreenshotRetentionSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task SaveAsync(ScreenshotRetentionSettings updated, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedLedger(params TidyLedgerEntry[] entries) : IScreenshotTidyLedger
    {
        public IReadOnlyList<TidyLedgerEntry> Read() => entries;

        public void Append(TidyLedgerEntry entry) => throw new NotSupportedException();
    }

    private sealed class RecordingBin : IRecycleBin
    {
        public bool IsAvailable => true;

        public bool Recycle(string path)
        {
            File.Delete(path);
            return true;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
