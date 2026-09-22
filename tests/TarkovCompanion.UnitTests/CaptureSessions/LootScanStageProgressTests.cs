using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.UnitTests.CaptureSessions;

/// <summary>
/// #572: a loot scan says which stage it is on while it runs, Setup remembers the auto-return
/// countdown, and Diagnostics shows the last scan's stages.
/// </summary>
public sealed class LootScanStageProgressTests
{
    private static readonly DateTimeOffset FileSeen = new(2026, 9, 22, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheProgressLineNamesTheStageAfterTheOneThatFinished()
    {
        var id = CaptureCorrelationId.New();

        Assert.Equal("Scanning · Waiting for the file", LootScanStageText.Progress(new(id, null, null)));
        Assert.Equal("Scanning · Reading the screen", LootScanStageText.Progress(new(id, "settle_wait", null)));
        Assert.Equal("Scanning · Matching icons", LootScanStageText.Progress(new(id, "context_ocr", null)));
        Assert.Equal("Scanning · Showing the result", LootScanStageText.Progress(new(id, "decide", null)));
    }

    [Fact]
    public void TheTimelineReportsEveryStepAndTheFinishedSummary()
    {
        var timeline = new CaptureStageTimeline();
        var seen = new List<CaptureStageStep>();
        timeline.Progressed += seen.Add;
        var id = CaptureCorrelationId.New();

        timeline.Begin(id, FileSeen);
        timeline.Mark(id, "context_ocr", TimeSpan.FromMilliseconds(140));
        timeline.Complete(id, FileSeen.AddMilliseconds(900));

        Assert.Equal([null, "context_ocr", "context_ocr"], seen.Select(step => step.LastStage));
        Assert.Equal(900, seen[^1].Summary!.TotalMilliseconds);
        Assert.Equal("Scanned in 0.9 s", LootScanStageText.Progress(seen[^1]));
    }

    [Fact]
    public void TheProgressLineHidesWhenAScanGoesQuietAndAfterItFinishes()
    {
        var clock = new ManualClock(FileSeen);
        var timeline = new CaptureStageTimeline();
        var progress = new LootScanProgressViewModel(timeline, action => action(), clock);
        var id = CaptureCorrelationId.New();

        timeline.Begin(id, FileSeen);
        timeline.Mark(id, "context_ocr", TimeSpan.FromMilliseconds(100));
        Assert.Equal("Scanning · Matching icons", progress.Text);
        Assert.True(progress.IsScanning);

        // A screenshot that was not a loot container never completes; the line stops claiming it runs.
        clock.Advance(LootScanProgressViewModel.QuietAfter);
        Assert.False(progress.IsVisible);

        var next = CaptureCorrelationId.New();
        timeline.Begin(next, FileSeen);
        timeline.Complete(next, FileSeen.AddMilliseconds(1200));
        Assert.Equal("Scanned in 1.2 s", progress.Text);
        Assert.False(progress.IsScanning);
        clock.Advance(LootScanProgressViewModel.DoneShownFor);
        Assert.False(progress.IsVisible);
    }

    [Fact]
    public void ChoosingACountdownIsRememberedAndHandedToTheShell()
    {
        var layout = new MemoryLayout();
        var setup = new SetupLootScanViewModel(layout, new CaptureStageTimeline());
        TimeSpan? handed = TimeSpan.MaxValue;
        setup.TimeoutChanged += value => handed = value;

        Assert.Equal(TimeSpan.FromSeconds(LootAutoReturnPolicy.DefaultSeconds), setup.Timeout);
        Assert.True(setup.Choices.Single(choice => choice.Seconds == LootAutoReturnPolicy.DefaultSeconds).IsCurrent);

        setup.Choices.Single(choice => choice.Seconds is null).ChooseCommand.Execute(null);
        Assert.Null(handed);
        Assert.Equal("off", layout.Get(WorkspaceLayoutKeys.LootAutoReturnSeconds));
        Assert.Null(new SetupLootScanViewModel(layout, null).Timeout);

        setup.Choices.Single(choice => choice.Seconds == 30).ChooseCommand.Execute(null);
        Assert.Equal(TimeSpan.FromSeconds(30), handed);
        Assert.Equal(TimeSpan.FromSeconds(30), new SetupLootScanViewModel(layout, null).Timeout);
    }

    // #572: "Show loot results on the tablet only" is kept like the countdown is.
    [Fact]
    public void TabletOnlyIsRememberedAcrossARestart()
    {
        var layout = new MemoryLayout();
        var setup = new SetupLootScanViewModel(layout, null);
        Assert.False(setup.TabletOnly);

        setup.ToggleTabletOnlyCommand.Execute(null);

        Assert.True(setup.TabletOnly);
        Assert.Equal("on", layout.Get(WorkspaceLayoutKeys.LootOnTabletOnly));
        Assert.True(new SetupLootScanViewModel(layout, null).TabletOnly);
        setup.ToggleTabletOnlyCommand.Execute(null);
        Assert.False(new SetupLootScanViewModel(layout, null).TabletOnly);
    }

    [Theory]
    [InlineData("2", 5)]
    [InlineData("600", 60)]
    [InlineData("junk", 15)]
    public void AStoredCountdownIsClampedToThePolicyRange(string stored, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SetupLootScanViewModel.Parse(stored));

    [Fact]
    public void DiagnosticsListsTheLastScansStagesInOrder()
    {
        var timeline = new CaptureStageTimeline();
        var setup = new SetupLootScanViewModel(new MemoryLayout(), timeline);
        Assert.False(setup.HasLastScan);
        var id = CaptureCorrelationId.New();

        timeline.Begin(id, FileSeen);
        timeline.Mark(id, "context_ocr", TimeSpan.FromMilliseconds(152.4));
        timeline.Mark(id, "grid_and_icon_matching", TimeSpan.FromMilliseconds(1250));
        timeline.Complete(id, FileSeen.AddMilliseconds(1700));

        Assert.True(setup.HasLastScan);
        Assert.StartsWith("1.7 s from file to result", setup.LastScanSummary, StringComparison.Ordinal);
        Assert.Equal(["Reading the screen · 152 ms", "Matching icons · 1.3 s"], setup.LastScanStages);
    }

    /// <summary>A clock whose timers fire only when a test moves time.</summary>
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _now + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var timer in _timers.Where(timer => !timer.Disposed && timer.Due <= _now).ToArray())
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public DateTimeOffset Due { get; } = due;

            public bool Disposed { get; private set; }

            public void Fire()
            {
                Disposed = true;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class MemoryLayout : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
