using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>Which cell of which read a crop was cut from: the read's time and the cell's region.</summary>
public readonly record struct IconCropKey(DateTimeOffset ObservedUtc, int X, int Y, int Width, int Height)
{
    public static IconCropKey For(DateTimeOffset observedUtc, EvidenceRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return new(observedUtc.ToUniversalTime(), region.X, region.Y, region.Width, region.Height);
    }
}

/// <summary>
/// The icon squares of the cells the last few reads could not name, held in memory only, so a
/// player who then says what one was can have that crop kept as a reference (#712 1-12).
/// </summary>
/// <remarks>
/// A read is pixel-free once it leaves the recognizer, so the crop has to be put aside while
/// the pixels are still there. Only refused cells are held, only their own squares (never the
/// frame), and only the newest <see cref="Capacity"/>: about 20 KB a one-cell crop at 1080p,
/// a few megabytes in all. Nothing here is written anywhere; persisting one is the correction's
/// decision, and the player can turn it off in Setup.
/// </remarks>
public sealed class RecentIconCrops
{
    public const int Capacity = 400;

    private readonly Lock _gate = new();
    private readonly Dictionary<IconCropKey, CapturedImage> _crops = [];
    private readonly Queue<IconCropKey> _order = new();

    public void Put(IconCropKey key, CapturedImage crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        lock (_gate)
        {
            if (_crops.TryAdd(key, crop))
            {
                _order.Enqueue(key);
            }
            else
            {
                _crops[key] = crop;
            }

            while (_order.Count > Capacity)
            {
                _crops.Remove(_order.Dequeue());
            }
        }
    }

    public CapturedImage? Find(IconCropKey key)
    {
        lock (_gate)
        {
            return _crops.GetValueOrDefault(key);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _crops.Count;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _crops.Clear();
            _order.Clear();
        }
    }
}
