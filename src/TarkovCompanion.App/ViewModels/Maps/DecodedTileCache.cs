using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>
/// Keeps the most recently used values inside a byte budget, and never drops one that is in use.
/// </summary>
/// <remarks>
/// Written for decoded map tiles, which is why "in use" exists: a tile on screen is a bitmap the
/// renderer is reading, and evicting it means disposing it under the renderer. The values in use
/// are named by whoever is loading, and the budget is allowed to be exceeded by them alone; one
/// map's tiles are about 60 MB, so that is a bounded excess and not a leak. Thread-safe, because
/// tiles are fetched and decoded on worker threads.
/// </remarks>
internal sealed class BoundedLruCache<TKey, TValue>(long capacityBytes, Func<TValue, long> sizeOf, Action<TValue> evicted)
    where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recency = new();
    private readonly object _lock = new();
    private long _bytes;

    private sealed record Entry(TKey Key, TValue Value, long Bytes);

    public long Bytes
    {
        get
        {
            lock (_lock)
            {
                return _bytes;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Stores a value unless the key already has one, and returns whichever the cache now holds.
    /// </summary>
    /// <remarks>
    /// Two loads of the same map can overlap (one cancelled and still finishing, one new), and both
    /// decode the same tile. The first one in wins and the second is handed to
    /// <paramref name="evicted"/>-style disposal at once, because nobody else has seen it; replacing
    /// the stored one would dispose a bitmap the first load is already showing.
    /// </remarks>
    public TValue GetOrAdd(TKey key, TValue candidate, IReadOnlySet<TKey> inUse)
    {
        List<TValue>? dropped = null;
        TValue kept;
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _recency.Remove(existing);
                _recency.AddFirst(existing);
                kept = existing.Value.Value;
                if (!ReferenceEquals(kept, candidate))
                {
                    dropped = [candidate];
                }
            }
            else
            {
                var entry = new Entry(key, candidate, Math.Max(0, sizeOf(candidate)));
                _entries[key] = _recency.AddFirst(entry);
                _bytes += entry.Bytes;
                kept = candidate;
                dropped = TrimLocked(inUse);
            }
        }

        if (dropped is not null)
        {
            foreach (var value in dropped)
            {
                evicted(value);
            }
        }

        return kept;
    }

    public void Clear()
    {
        TValue[] all;
        lock (_lock)
        {
            all = [.. _recency.Select(entry => entry.Value)];
            _entries.Clear();
            _recency.Clear();
            _bytes = 0;
        }

        foreach (var value in all)
        {
            evicted(value);
        }
    }

    private List<TValue>? TrimLocked(IReadOnlySet<TKey> inUse)
    {
        List<TValue>? dropped = null;
        var node = _recency.Last;
        while (_bytes > capacityBytes && node is not null)
        {
            var previous = node.Previous;
            if (!inUse.Contains(node.Value.Key))
            {
                _recency.Remove(node);
                _entries.Remove(node.Value.Key);
                _bytes -= node.Value.Bytes;
                (dropped ??= []).Add(node.Value.Value);
            }

            node = previous;
        }

        return dropped;
    }
}

/// <summary>
/// Where a coarse tile lands on the canvas of a sharper level of the same pyramid.
/// </summary>
/// <remarks>
/// A photographed map is some two hundred tiles, and a first visit downloads every one. The level
/// a step or two down the pyramid is the same picture in a couple of dozen tiles or fewer, so it is
/// fetched first and drawn underneath: the whole map is on screen in a fraction of a second, soft,
/// and sharpens as the real tiles arrive. Each level doubles the last, so a coarse tile covers a
/// square 2^(levels) times as wide on the sharp level's canvas.
/// </remarks>
internal static class MapTileUnderlay
{
    /// <summary>The most tiles an underlay may be; above this it is no longer quick.</summary>
    /// <remarks>
    /// Thirty-two, because pyramids start at level 2 and the coarsest level is already twenty
    /// tiles on Customs and twenty-five on Woods; a bound of sixteen left the two widest maps with
    /// nothing to show first. It is still a third of the sharp level or less.
    /// </remarks>
    public const int MaximumTiles = 32;

    public readonly record struct Placement(MapTilePlanItem Tile, double Left, double Top, int Size);

    /// <summary>The sharpest level below <paramref name="sharpZoom"/> that is few enough tiles, or null.</summary>
    public static MapTilePlan? Plan(MapVariant variant, int sharpZoom)
    {
        ArgumentNullException.ThrowIfNull(variant);
        var minimum = variant.MinimumZoom ?? sharpZoom;
        for (var zoom = sharpZoom - 1; zoom >= minimum; zoom--)
        {
            var plan = MapTilePlanner.Plan(variant, zoom, MaximumTiles);
            if (plan.IsValid)
            {
                return plan;
            }
        }

        return null;
    }

    public static IReadOnlyList<Placement> Place(MapTilePlan coarse, MapTilePlan sharp)
    {
        ArgumentNullException.ThrowIfNull(coarse);
        ArgumentNullException.ThrowIfNull(sharp);
        if (!coarse.IsValid || !sharp.IsValid)
        {
            return [];
        }

        var levels = sharp.Tiles[0].Zoom - coarse.Tiles[0].Zoom;
        if (levels <= 0 || levels > 8)
        {
            return [];
        }

        var factor = 1 << levels;
        return
        [
            .. coarse.Tiles.Select(tile => new Placement(
                tile,
                ((coarse.OriginPixelX + tile.Left) * factor) - sharp.OriginPixelX,
                ((coarse.OriginPixelY + tile.Top) * factor) - sharp.OriginPixelY,
                tile.Size * factor)),
        ];
    }
}
