namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// Keeps a picture alive for as long as somebody away from the interface thread is reading it.
/// </summary>
/// <remarks>
/// Every build from 2.0.1278 died a few seconds after its first map began to load, with nothing
/// in the crash log: exit code 0xC0000005. Windows' own event log named it on 2026-09-20:
/// <c>TabletMapSurfacePublisher.ArtworkFor</c>, on a pool thread, inside Skia's PNG encoder. The
/// publisher was encoding the plan's picture, which takes a few hundred milliseconds for eight
/// million pixels, while the cockpit replaced that picture and disposed the old one from the
/// interface thread. The encoder was reading memory that had been freed.
///
/// That race was always there and almost never lost, because a map's picture was replaced once
/// per map. Tiles that fill in replace it twice a second for the whole load, and the app now
/// opens a map by itself, so it was lost on every launch.
///
/// The cure is ownership rather than a lock around the encoder: whoever made a picture retires
/// it when it is replaced, a reader holds a lease while it reads, and the picture is released
/// when it is retired and the last lease on it has ended, whichever comes second. A reader that
/// asks for a picture already retired is told so and reads nothing.
/// </remarks>
internal sealed class PictureLeases<TPicture>(Action<TPicture> release)
    where TPicture : class
{
    private readonly Dictionary<TPicture, State> _pictures = new(ReferenceEqualityComparer.Instance);
    private readonly object _lock = new();

    private sealed class State
    {
        public int Readers;
        public bool Retired;
    }

    /// <summary>How many pictures are tracked: in use, or retired and still being read.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _pictures.Count;
            }
        }
    }

    /// <summary>Says this picture exists and may be read. Called by whoever made it, before anybody can see it.</summary>
    public void Track(TPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        lock (_lock)
        {
            _pictures.TryAdd(picture, new());
        }
    }

    /// <summary>
    /// A lease on the picture, or null when it has been retired (or was never tracked) and must not be read.
    /// </summary>
    public IDisposable? TryRead(TPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        lock (_lock)
        {
            if (!_pictures.TryGetValue(picture, out var state) || state.Retired)
            {
                return null;
            }

            state.Readers++;
            return new Lease(this, picture);
        }
    }

    /// <summary>The owner is finished with the picture: released now, or when its last reader is.</summary>
    public void Retire(TPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        lock (_lock)
        {
            if (!_pictures.TryGetValue(picture, out var state))
            {
                // Never tracked: nobody can hold a lease on it, so it is the owner's alone.
            }
            else if (state.Retired)
            {
                return;
            }
            else if (state.Readers > 0)
            {
                state.Retired = true;
                return;
            }
            else
            {
                _pictures.Remove(picture);
            }
        }

        release(picture);
    }

    private void EndRead(TPicture picture)
    {
        lock (_lock)
        {
            if (!_pictures.TryGetValue(picture, out var state))
            {
                return;
            }

            state.Readers--;
            if (state.Readers > 0 || !state.Retired)
            {
                return;
            }

            _pictures.Remove(picture);
        }

        release(picture);
    }

    private sealed class Lease(PictureLeases<TPicture> owner, TPicture picture) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                owner.EndRead(picture);
            }
        }
    }
}
