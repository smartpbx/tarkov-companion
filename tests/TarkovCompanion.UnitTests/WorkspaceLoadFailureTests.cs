using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What happens to the rest of the application when one pane's load fails.
/// </summary>
/// <remarks>
/// Reported on 2026-09-19, alongside the crash: "freezes, crashes, panes/tabs not rendering at all
/// until a restart", with no exception in the log. Shares
/// <see cref="CrashLogCollection"/> because the destination it asserts on is static for the whole
/// process.
/// </remarks>
[Collection(CrashLogCollection.Name)]
public sealed class WorkspaceLoadFailureTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "tarkov-workspace-fault-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A workspace load that throws is recorded, and does not become an unobserved task.
    /// </summary>
    /// <remarks>
    /// The shell started all six workspace loads as <c>_ = workspace.LoadAsync();</c>. A load that
    /// faulted outside the workspace's own catch had its exception reach only
    /// <see cref="TaskScheduler.UnobservedTaskException"/>, whenever the collector got round to
    /// finalising the task — if ever. The pane stayed blank and nothing said why.
    /// </remarks>
    [Fact]
    public async Task AWorkspaceLoadThatThrowsIsRecordedRatherThanDiscarded()
    {
        CrashLog.Install(_directory);

        await V2ShellViewModel.ObserveWorkspaceLoad(
            "team",
            () => throw new IOException("The group settings file is in use by another process."));

        var log = ReadShared(CrashLog.FilePath!);
        Assert.Contains("workspace-fault/team", log, StringComparison.Ordinal);
        Assert.Contains("in use by another process", log, StringComparison.Ordinal);
    }

    /// <summary>A load that was cancelled is not a failure, and is not reported as one.</summary>
    /// <remarks>
    /// One navigation supersedes another every time the player moves, so this is the common case
    /// and not the exception. Reported, it would fill the log with the shell working correctly.
    /// </remarks>
    [Fact]
    public async Task AWorkspaceLoadThatWasCancelledIsNotReported()
    {
        CrashLog.Install(_directory);
        CrashLog.Write("marker", "before");

        await V2ShellViewModel.ObserveWorkspaceLoad("plan", () => throw new OperationCanceledException());

        Assert.DoesNotContain("workspace-fault", ReadShared(CrashLog.FilePath!), StringComparison.Ordinal);
    }

    /// <summary>A load that succeeds says nothing.</summary>
    [Fact]
    public async Task AWorkspaceLoadThatSucceedsSaysNothing()
    {
        CrashLog.Install(_directory);

        await V2ShellViewModel.ObserveWorkspaceLoad("hideout", () => Task.CompletedTask);

        Assert.DoesNotContain("workspace-fault", ReadShared(CrashLog.FilePath!), StringComparison.Ordinal);
    }

    /// <summary>
    /// One page failing at startup does not take the pages after it.
    /// </summary>
    /// <remarks>
    /// Ten page loads were a single await chain inside one try. A failure in any of them skipped
    /// every step after it for the rest of the session — a failure in Hideout meant Ammo, Keys,
    /// Events, the background refresh, Group and the map were never initialised at all, and the
    /// player saw several pages that simply never filled in. They are siblings, not a chain.
    /// </remarks>
    [Fact]
    public async Task OnePageFailingAtStartupIsNamedAndDoesNotStopTheOthers()
    {
        CrashLog.Install(_directory);
        using var factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new TestFileLoggerProvider()));
        var logger = factory.CreateLogger("startup");
        var loaded = new List<string>();

        var faults = new List<string>();
        foreach (var (surface, load) in new (string, Func<Task>)[]
                 {
                     ("items", () => { loaded.Add("items"); return Task.CompletedTask; }),
                     ("hideout", () => throw new InvalidOperationException("no such table: hideout_stations")),
                     ("ammo", () => { loaded.Add("ammo"); return Task.CompletedTask; }),
                     ("map", () => { loaded.Add("map"); return Task.CompletedTask; }),
                 })
        {
            if (await MainWindowViewModel.LoadSurfaceAsync(surface, load, logger) is { } failed)
            {
                faults.Add(failed);
            }
        }

        Assert.Equal(["items", "ammo", "map"], loaded);
        Assert.Equal(["hideout"], faults);
        var log = ReadShared(CrashLog.FilePath!);
        Assert.Contains("hideout", log, StringComparison.Ordinal);
        Assert.Contains("no such table: hideout_stations", log, StringComparison.Ordinal);
    }

    /// <summary>A cancelled startup stops, because that is the application shutting down.</summary>
    [Fact]
    public async Task ACancelledStartupIsNotTreatedAsAPageThatFailed()
    {
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.None));

        await Assert.ThrowsAsync<OperationCanceledException>(() => MainWindowViewModel.LoadSurfaceAsync(
            "quests",
            () => throw new OperationCanceledException(),
            factory.CreateLogger("startup")));
    }

    /// <summary>The application's own logger reaches the file a player is asked to send.</summary>
    private sealed class TestFileLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Sink(categoryName);

        public void Dispose()
        {
        }

        private sealed class Sink(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                CrashLog.Write($"{logLevel}/{category}", $"{formatter(state, exception)} :: {exception}");
        }
    }

    /// <summary>
    /// Retry on the shell's banner reloads the pages that failed, and only keeps naming the ones
    /// that failed again (#453).
    /// </summary>
    [Fact]
    public async Task RetryingStartupFaultsDropsThePagesThatNowLoadAndKeepsTheOnesThatDoNot()
    {
        var hideoutAttempts = 0;
        var mapAttempts = 0;
        var loaders = new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
        {
            ["hideout"] = () =>
            {
                hideoutAttempts++;
                return Task.CompletedTask;
            },
            ["map"] = () =>
            {
                mapAttempts++;
                throw new InvalidOperationException("The map catalog is still unreadable.");
            },
            ["ammo"] = () => throw new InvalidOperationException("Ammo loaded at startup and must not be run again."),
        };

        var stillFailed = await MainWindowViewModel.RetryFailedSurfacesAsync(
            ["hideout", "map", "scanner"],
            loaders,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        // Hideout now loads; the map failed again; the scanner has no loader and cannot be claimed.
        Assert.Equal(["map", "scanner"], stillFailed);
        Assert.Equal(1, hideoutAttempts);
        Assert.Equal(1, mapAttempts);
    }

    [Theory]
    [InlineData(new[] { "hideout" }, "Hideout did not load")]
    [InlineData(new[] { "hideout", "map" }, "Hideout and Map did not load")]
    [InlineData(new[] { "hideout", "ammo", "map" }, "Hideout, Ammo and Map did not load")]
    public void TheBannerNamesThePagesThatDidNotLoad(string[] faults, string expected) =>
        Assert.Equal(expected, V2ShellViewModel.DescribeStartupFaults(faults));

    public void Dispose()
    {
        CrashLog.Detach();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
