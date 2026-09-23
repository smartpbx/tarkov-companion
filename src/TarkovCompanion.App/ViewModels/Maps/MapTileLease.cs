using Avalonia.Threading;

namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>
/// Held while decoded tiles are read off the interface thread, so none of them is disposed under
/// the reader.
/// </summary>
/// <remarks>
/// [#678] The Raid map stitches the loaded tiles into one picture, 160-270 ms for Customs, and did
/// it on the interface thread. It now draws on a worker. A tile's bitmap is disposed on the
/// interface thread when the decoded-tile cache evicts it (<see cref="MapViewModel"/>), and a map
/// switch during a stitch can evict exactly the tiles being drawn: a disposed bitmap drawn by
/// the rasteriser is a native fault, not an exception. So disposal waits while this is held.
/// </remarks>
internal static class MapTileLease
{
    private static int _holders;

    /// <summary>True while any worker is reading tiles.</summary>
    public static bool IsHeld => Volatile.Read(ref _holders) > 0;

    /// <summary>Held until the returned object is disposed; take it on the interface thread, before the worker starts.</summary>
    public static IDisposable Take()
    {
        Interlocked.Increment(ref _holders);
        return new Holder();
    }

    /// <summary>Runs <paramref name="dispose"/> on the interface thread once no worker is reading tiles.</summary>
    public static void DisposeWhenFree(Action dispose)
    {
        if (!IsHeld)
        {
            dispose();
            return;
        }

        DispatcherTimer.RunOnce(() => DisposeWhenFree(dispose), TimeSpan.FromMilliseconds(100), DispatcherPriority.Background);
    }

    private sealed class Holder : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Decrement(ref _holders);
            }
        }
    }
}
