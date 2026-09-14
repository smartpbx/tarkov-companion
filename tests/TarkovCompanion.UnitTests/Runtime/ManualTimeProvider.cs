namespace TarkovCompanion.UnitTests.Runtime;

internal sealed class ManualTimeProvider(DateTimeOffset startUtc) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = startUtc.ToUniversalTime();

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _utcNow.UtcTicks;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_gate)
        {
            _utcNow += amount;
            foreach (var timer in _timers.ToArray())
            {
                timer.CollectCallbacksUnsafe(_utcNow, callbacks);
            }
        }

        foreach (var callback in callbacks)
        {
            callback.Callback(callback.State);
        }
    }

    private void RegisterUnsafe(ManualTimer timer)
    {
        if (!_timers.Contains(timer))
        {
            _timers.Add(timer);
        }
    }

    private void RemoveUnsafe(ManualTimer timer) => _timers.Remove(timer);

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private DateTimeOffset? _dueUtc;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ValidateTimeout(dueTime, nameof(dueTime));
            ValidateTimeout(period, nameof(period));
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                _dueUtc = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner._utcNow + dueTime;
                if (_dueUtc is null)
                {
                    owner.RemoveUnsafe(this);
                }
                else
                {
                    owner.RegisterUnsafe(this);
                }

                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                _dueUtc = null;
                owner.RemoveUnsafe(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void CollectCallbacksUnsafe(
            DateTimeOffset nowUtc,
            List<(TimerCallback Callback, object? State)> callbacks)
        {
            while (!_disposed && _dueUtc is { } due && due <= nowUtc)
            {
                callbacks.Add((callback, state));
                if (_period == Timeout.InfiniteTimeSpan)
                {
                    _dueUtc = null;
                    owner.RemoveUnsafe(this);
                    return;
                }

                _dueUtc = due + _period;
            }
        }

        private static void ValidateTimeout(TimeSpan value, string parameterName)
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }
}

internal static class RuntimeTestTasks
{
    public static async Task DrainAsync(int turns = 20)
    {
        for (var index = 0; index < turns; index++)
        {
            await Task.Yield();
        }
    }
}
