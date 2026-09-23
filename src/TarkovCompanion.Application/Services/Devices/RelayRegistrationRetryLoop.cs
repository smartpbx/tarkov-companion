namespace TarkovCompanion.Application.Services.Devices;

/// <summary>Retries desktop registration without turning a transient refusal into a restart.</summary>
/// <remarks>
/// Registration used to run once, two seconds after launch. A relay outage or bad PC clock then
/// left the desktop off the relay for the rest of the process. This loop has one owner, one
/// injected clock, and a bounded schedule; a clock correction interrupts the wait immediately.
/// </remarks>
public sealed class RelayRegistrationRetryLoop : IDisposable
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
        TimeSpan.FromMinutes(5),
    ];

    private readonly Func<CancellationToken, Task<bool>> _attempt;
    private readonly TimeProvider _clock;
    private readonly RelayClockOffsetTracker? _clockOffset;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _waitCancellation;
    private Task _run = Task.CompletedTask;
    private TaskCompletionSource _firstAttempt = CompletedAttempt();
    private bool _resetBackoff;
    private bool _disposed;

    public RelayRegistrationRetryLoop(
        Func<CancellationToken, Task<bool>> attempt,
        TimeProvider clock,
        RelayClockOffsetTracker? clockOffset = null)
    {
        _attempt = attempt ?? throw new ArgumentNullException(nameof(attempt));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _clockOffset = clockOffset;
        if (_clockOffset is not null)
        {
            _clockOffset.ClockCorrected += RetryNow;
        }
    }

    /// <summary>Starts one run if none is active and completes after that run's first attempt.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_run.IsCompleted)
            {
                return _firstAttempt.Task;
            }

            _runCancellation?.Dispose();
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _resetBackoff = false;
            _run = RunAsync(_runCancellation.Token, _firstAttempt);
            return _firstAttempt.Task;
        }
    }

    /// <summary>Interrupts the current delay. Used when a relay response proves the clock is fixed.</summary>
    public void RetryNow()
    {
        CancellationTokenSource? wait;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _resetBackoff = true;
            wait = _waitCancellation;
        }

        wait?.Cancel();
    }

    private async Task RunAsync(CancellationToken cancellationToken, TaskCompletionSource firstAttempt)
    {
        var failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Preserve the caller's context: the desktop callback updates its bound state.
                var succeeded = await _attempt(cancellationToken);
                firstAttempt.TrySetResult();
                if (succeeded)
                {
                    return;
                }

                TimeSpan delay = default;
                CancellationTokenSource? wait = null;
                var retryImmediately = false;
                lock (_gate)
                {
                    if (_resetBackoff)
                    {
                        _resetBackoff = false;
                        failures = 0;
                        retryImmediately = true;
                    }
                    else
                    {
                        delay = Delays[Math.Min(failures, Delays.Length - 1)];
                        failures++;
                        wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        _waitCancellation = wait;
                    }
                }

                if (retryImmediately)
                {
                    continue;
                }

                using (wait!)
                {
                    try
                    {
                        await Task.Delay(delay, _clock, wait!.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // A clock correction is a request to try now, not a stopped run.
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_waitCancellation, wait))
                            {
                                _waitCancellation = null;
                            }

                            if (_resetBackoff)
                            {
                                failures = 0;
                                _resetBackoff = false;
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            firstAttempt.TrySetCanceled(cancellationToken);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? run;
        CancellationTokenSource? wait;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            run = _runCancellation;
            wait = _waitCancellation;
        }

        if (_clockOffset is not null)
        {
            _clockOffset.ClockCorrected -= RetryNow;
        }

        run?.Cancel();
        wait?.Cancel();
        run?.Dispose();
    }

    private static TaskCompletionSource CompletedAttempt()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult();
        return completed;
    }
}
