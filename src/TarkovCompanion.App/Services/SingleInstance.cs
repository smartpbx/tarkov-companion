namespace TarkovCompanion.App.Services;

/// <summary>
/// Keeps a second copy of the companion from starting beside the first.
/// </summary>
/// <remarks>
/// A second copy does not fail loudly, which is the problem. It opens a window, observes the
/// game, and then quietly cannot register the scan shortcut, because Windows has already given
/// that key combination to the first copy and refuses the second with error 1409. The result is
/// a companion that looks fine and whose shortcut does nothing.
///
/// This happened for real: the window sits behind a fullscreen game, so it looks like nothing
/// launched, so it gets launched again. Three copies were running before anybody noticed, and
/// only the first one worked.
///
/// The lock is per-user rather than machine-wide. Two people signed into the same machine are
/// two players with two games and two screenshot folders, and neither is a second copy of the
/// other's companion.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private bool _released;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Claims the right to be the running companion, or returns null if another copy has it.
    /// </summary>
    /// <remarks>
    /// A lock left behind by a copy that crashed is treated as free. The alternative is a
    /// companion that refuses to start until the machine is restarted, which is a far worse
    /// failure than the one this prevents.
    ///
    /// Anything unexpected also counts as free. This is a convenience, and a convenience that
    /// can stop the application starting is not one.
    /// </remarks>
    public static SingleInstance? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, $"Local\\{name}");
            if (mutex.WaitOne(TimeSpan.Zero, exitContext: false))
            {
                return new SingleInstance(mutex);
            }

            mutex.Dispose();
            return null;
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without letting go. The lock is ours.
            return mutex is null ? null : new SingleInstance(mutex);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                          or IOException
                                          or WaitHandleCannotBeOpenedException)
        {
            mutex?.Dispose();
            return new SingleInstance(new Mutex(initiallyOwned: true));
        }
    }

    public void Dispose()
    {
        if (!_released)
        {
            _released = true;
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (Exception exception) when (exception is ApplicationException or ObjectDisposedException)
            {
                // Never owned, or already gone. Either way there is nothing to release.
            }
        }

        _mutex.Dispose();
    }
}
