using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.UnitTests.Runtime;

namespace TarkovCompanion.UnitTests.RelayLink;

public sealed class RelayRegistrationRetryLoopTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FailuresBackOffFromThirtySecondsAndCapAtFiveMinutes()
    {
        var clock = new ManualTimeProvider(Now);
        var attempts = new List<DateTimeOffset>();
        using var retry = new RelayRegistrationRetryLoop(
            _ =>
            {
                attempts.Add(clock.GetUtcNow());
                return Task.FromResult(attempts.Count >= 7);
            },
            clock);

        await retry.StartAsync();
        Assert.Equal([Now], attempts);

        foreach (var elapsed in new[] { 30, 60, 120, 240, 300, 300 })
        {
            clock.Advance(TimeSpan.FromSeconds(elapsed - 1));
            await DrainAsync();
            var before = attempts.Count;
            clock.Advance(TimeSpan.FromSeconds(1));
            await UntilAsync(() => attempts.Count == before + 1);
        }

        Assert.Equal(
            [0, 30, 90, 210, 450, 750, 1050],
            attempts.Select(attempt => (int)(attempt - Now).TotalSeconds));
    }

    [Fact]
    public async Task AClockCorrectionInterruptsTheCurrentDelay()
    {
        var clock = new ManualTimeProvider(Now);
        var offset = new RelayClockOffsetTracker();
        offset.ObserveOffsetSeconds(-14_400);
        var attempts = new List<DateTimeOffset>();
        using var retry = new RelayRegistrationRetryLoop(
            _ =>
            {
                attempts.Add(clock.GetUtcNow());
                return Task.FromResult(attempts.Count == 2);
            },
            clock,
            offset);

        await retry.StartAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        offset.ObserveOffsetSeconds(0);
        await UntilAsync(() => attempts.Count == 2);

        Assert.Equal([Now, Now.AddSeconds(10)], attempts);
    }

    [Fact]
    public async Task AClockCorrectionDuringTheFailedAttemptRetriesWithoutStartingADelay()
    {
        var clock = new ManualTimeProvider(Now);
        var offset = new RelayClockOffsetTracker();
        offset.ObserveOffsetSeconds(-14_400);
        var attempts = 0;
        using var retry = new RelayRegistrationRetryLoop(
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    offset.ObserveOffsetSeconds(0);
                }

                return Task.FromResult(attempts == 2);
            },
            clock,
            offset);

        await retry.StartAsync();
        await UntilAsync(() => attempts == 2);

        Assert.Equal(2, attempts);
        Assert.Equal(Now, clock.GetUtcNow());
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        for (var turn = 0; turn < 100 && !predicate(); turn++)
        {
            await Task.Yield();
        }

        Assert.True(predicate());
    }

    private static async Task DrainAsync()
    {
        for (var turn = 0; turn < 10; turn++)
        {
            await Task.Yield();
        }
    }
}
