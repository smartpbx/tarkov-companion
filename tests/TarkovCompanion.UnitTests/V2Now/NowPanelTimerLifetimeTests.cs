using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Now;

/// <summary>
/// [#963] The Now panel ticks once a second through the interface thread. The cockpit that built it
/// did not stop it on Dispose, so every composed app a test built kept posting to the dispatcher
/// for the rest of the run, long after its services were disposed.
/// </summary>
public sealed class NowPanelTimerLifetimeTests
{
    [Fact]
    public async Task Disposing_the_composed_app_stops_the_Now_panel_clock()
    {
        var clock = new RecordingClock();
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-now-timer-{Guid.NewGuid():N}");
        try
        {
            RaidCockpitViewModel cockpit;
            await using (var services = AppComposition.Build(
                new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                new(DataRoot: root, Offline: true, TimeProvider: clock)))
            {
                _ = services.GetRequiredService<V2ShellViewModel>();
                cockpit = services.GetRequiredService<RaidCockpitViewModel>();
                Assert.NotNull(cockpit.NowHost.Panel);
                Assert.Contains(clock.Timers, timer => timer.Period == TimeSpan.FromSeconds(1) && !timer.IsDisposed);
            }

            Assert.All(clock.Timers.Where(timer => timer.Period == TimeSpan.FromSeconds(1)), timer => Assert.True(timer.IsDisposed));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class RecordingClock : TimeProvider
    {
        private readonly List<RecordedTimer> _timers = [];

        public IReadOnlyList<RecordedTimer> Timers
        {
            get
            {
                lock (_timers)
                {
                    return [.. _timers];
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Never fires: the test is about who stops the clock, not what it does.
            var timer = new RecordedTimer(period);
            lock (_timers)
            {
                _timers.Add(timer);
            }

            return timer;
        }
    }

    private sealed class RecordedTimer(TimeSpan period) : ITimer
    {
        public TimeSpan Period { get; private set; } = period;

        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Period = period;
            return !IsDisposed;
        }

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
