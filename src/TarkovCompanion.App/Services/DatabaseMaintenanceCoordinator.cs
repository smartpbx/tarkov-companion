using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.App.Services;

/// <summary>
/// Rechecks durable database maintenance while the app stays open, without competing with a raid.
/// Startup remains the first durable boundary; this loop covers long tray sessions that used to
/// miss every later due time until the process was restarted.
/// </summary>
public sealed class DatabaseMaintenanceCoordinator : IAsyncDisposable
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    internal static readonly TimeSpan RaidRetryInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(15);

    private readonly Func<CancellationToken, Task> _runMaintenance;
    private readonly IRuntimeStateStore _runtime;
    private readonly TimeProvider _clock;
    private readonly ILogger<DatabaseMaintenanceCoordinator> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private Task? _loop;

    public DatabaseMaintenanceCoordinator(
        SqliteDataPlatformMaintenance maintenance,
        IRuntimeStateStore runtime,
        TimeProvider clock,
        ILogger<DatabaseMaintenanceCoordinator>? logger = null)
        : this(
            cancellationToken => RunMaintenanceAsync(maintenance, cancellationToken),
            runtime,
            clock,
            logger)
    {
    }

    internal DatabaseMaintenanceCoordinator(
        Func<CancellationToken, Task> runMaintenance,
        IRuntimeStateStore runtime,
        TimeProvider clock,
        ILogger<DatabaseMaintenanceCoordinator>? logger = null)
    {
        _runMaintenance = runMaintenance ?? throw new ArgumentNullException(nameof(runMaintenance));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? NullLogger<DatabaseMaintenanceCoordinator>.Instance;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
            _loop ??= RunLoopAsync(_stopping.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _stopping.Cancel();
            loop = _loop;
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
        }

        _stopping.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var nextDelay = CheckInterval;
        while (true)
        {
            await Task.Delay(nextDelay, _clock, cancellationToken).ConfigureAwait(false);
            nextDelay = await RunOnceWhenIdleAsync(cancellationToken).ConfigureAwait(false)
                ? CheckInterval
                : RaidRetryInterval;
        }
    }

    private async Task<bool> RunOnceWhenIdleAsync(CancellationToken cancellationToken)
    {
        using var raidStarted = new CancellationTokenSource();
        void CancelForRaid(object? sender, EventArgs args)
        {
            if (IsRaidActive())
            {
                raidStarted.Cancel();
            }
        }

        _runtime.Changed += CancelForRaid;
        try
        {
            if (IsRaidActive())
            {
                return false;
            }

            using var timeout = new CancellationTokenSource(RunTimeout, _clock);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                raidStarted.Token,
                timeout.Token);
            try
            {
                // RunDueAsync is asynchronous I/O, but starting it on the pool also guarantees a
                // provider that performs synchronous setup cannot spend that setup on Avalonia.
                await Task.Run(() => _runMaintenance(bounded.Token), bounded.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (raidStarted.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Periodic database maintenance exceeded its bounded run window.");
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Periodic database maintenance failed; the next scheduled check will retry it.");
                return true;
            }
        }
        finally
        {
            _runtime.Changed -= CancelForRaid;
        }
    }

    private bool IsRaidActive() =>
        _runtime.Current.Raid.State is RaidLifecycleState.LoadingRaid or RaidLifecycleState.InRaid;

    private static async Task RunMaintenanceAsync(
        SqliteDataPlatformMaintenance maintenance,
        CancellationToken cancellationToken) =>
        _ = await maintenance.RunDueAsync(cancellationToken).ConfigureAwait(false);
}
