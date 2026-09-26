using System.Runtime.InteropServices;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// [#931] Which of a map's words give way first when they cannot all be read.
/// </summary>
/// <remarks>
/// Ordered so a larger value wins. <see cref="Pinned"/> is not a place name at all: a squadmate's
/// name or the distance written on a spawn line says something about this raid, and hiding it to
/// make room for "Burger Spot" would be the wrong way round.
/// </remarks>
public enum MapLabelRank
{
    /// <summary>A shop, a room, a sign: Interchange's mall shops, Streets' cafés.</summary>
    Room = 0,

    /// <summary>A building or a street: "Garage A", "Primorsky Ave.".</summary>
    Building = 1,

    /// <summary>A part of the map you navigate by: "Power Station", "Kilmov Shopping Mall".</summary>
    Area = 2,

    /// <summary>Not one of the catalog's place names; always drawn.</summary>
    Pinned = 3,
}

/// <summary>A label's rectangle on screen, in screen pixels, centred on its anchor.</summary>
public readonly record struct MapLabelBox(double CenterX, double CenterY, double Width, double Height);

/// <summary>A marker a label must not be written across, in screen pixels.</summary>
public readonly record struct MapLabelDisc(double CenterX, double CenterY, double Radius);

/// <summary>
/// [#931] Decides which place names are written, so no two of them are ever drawn over each
/// other or over an extract, the player or a squadmate.
/// </summary>
/// <remarks>
/// <para>
/// Reported from Interchange in Stack view on Windows: "Nortex", "Tarr", "Rendezvous", "Mantis",
/// "ТАРЗДРАВ" and thirty more shop names in one unreadable block over the mall. Every one of them
/// was drawn exactly where the catalog put it; nothing looked at any other.
/// </para>
/// <para>
/// A place name cannot move: a name somewhere else is a name for somewhere else. So the only
/// move is to leave one out, and which one is decided by rank (area over building over shop),
/// then by the catalog's own size, then alphabetically so the same name loses on every pass
/// rather than two of them flickering. Screen positions spread apart as the map zooms in and the
/// names keep their size, so a shop that loses at the fitted plan comes back as you zoom in on
/// the mall, which is when it is worth reading.
/// </para>
/// <para>
/// One instance per map. It keeps its grid and buffers between calls so a zoom step does not
/// allocate; accepted rectangles go into a uniform grid so each candidate is tested only against
/// what is near it rather than against every name already written.
/// </para>
/// </remarks>
public sealed class MapLabelPlacer
{
    /// <summary>The grid's cell, in screen pixels: about one short name wide.</summary>
    public const double CellSize = 64;

    /// <summary>Space kept clear between two names, in screen pixels, so they read as two.</summary>
    public const double Gap = 2;

    private readonly Dictionary<long, int> _cellHeads = [];
    private readonly List<int> _entryNext = [];
    private readonly List<int> _entryRect = [];
    private readonly List<(double Left, double Top, double Right, double Bottom)> _taken = [];

    /// <summary>
    /// The order names are considered in: the most important first. Computed once per set of
    /// names, since zooming changes where they are but not which matters more.
    /// </summary>
    public static int[] PriorityOrder(
        IReadOnlyList<MapLabelRank> ranks,
        IReadOnlyList<double> sizes,
        IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        ArgumentNullException.ThrowIfNull(sizes);
        ArgumentNullException.ThrowIfNull(texts);
        if (sizes.Count != ranks.Count || texts.Count != ranks.Count)
        {
            throw new ArgumentException("Every label needs a rank, a size and a text.");
        }

        var order = new int[ranks.Count];
        for (var index = 0; index < order.Length; index++)
        {
            order[index] = index;
        }

        Array.Sort(order, (left, right) =>
        {
            var byRank = ranks[right].CompareTo(ranks[left]);
            if (byRank != 0)
            {
                return byRank;
            }

            var bySize = sizes[right].CompareTo(sizes[left]);
            if (bySize != 0)
            {
                return bySize;
            }

            var byText = string.CompareOrdinal(texts[left], texts[right]);
            return byText != 0 ? byText : left.CompareTo(right);
        });
        return order;
    }

    /// <summary>The rank the catalog's size for a place name puts it at; null is not a place name.</summary>
    /// <remarks>
    /// Measured on the catalog of 2026-09: areas are written at 90 and 100 (a missing size is
    /// 100), buildings and streets at 80, shops and signs at 60 to 70. Interchange's "Go Kart"
    /// and "Four Camp" are 80 beside its 100 "Power Station", and rank with the garages.
    /// </remarks>
    public static MapLabelRank RankFor(double? placeNameSize) => placeNameSize switch
    {
        null => MapLabelRank.Pinned,
        >= 90 => MapLabelRank.Area,
        >= 75 => MapLabelRank.Building,
        _ => MapLabelRank.Room,
    };

    /// <summary>
    /// Writes, per label, whether it is drawn. <paramref name="order"/> comes from
    /// <see cref="PriorityOrder"/>; a pinned label is always drawn and still takes its space.
    /// </summary>
    public void Place(
        ReadOnlySpan<MapLabelBox> labels,
        ReadOnlySpan<MapLabelRank> ranks,
        ReadOnlySpan<int> order,
        ReadOnlySpan<MapLabelDisc> discs,
        Span<bool> shown)
    {
        if (ranks.Length != labels.Length || order.Length != labels.Length || shown.Length != labels.Length)
        {
            throw new ArgumentException("Every label needs a rank, an order slot and a result.");
        }

        _cellHeads.Clear();
        _entryNext.Clear();
        _entryRect.Clear();
        _taken.Clear();
        shown.Clear();

        foreach (var disc in discs)
        {
            if (double.IsFinite(disc.CenterX) && double.IsFinite(disc.CenterY) && disc.Radius > 0)
            {
                Take((disc.CenterX - disc.Radius, disc.CenterY - disc.Radius,
                    disc.CenterX + disc.Radius, disc.CenterY + disc.Radius));
            }
        }

        foreach (var index in order)
        {
            var label = labels[index];
            if (!double.IsFinite(label.CenterX) || !double.IsFinite(label.CenterY))
            {
                continue;
            }

            var halfWidth = (label.Width / 2) + (Gap / 2);
            var halfHeight = (label.Height / 2) + (Gap / 2);
            var rect = (label.CenterX - halfWidth, label.CenterY - halfHeight,
                label.CenterX + halfWidth, label.CenterY + halfHeight);
            if (ranks[index] != MapLabelRank.Pinned && Collides(rect))
            {
                continue;
            }

            shown[index] = true;
            Take(rect);
        }
    }

    private bool Collides((double Left, double Top, double Right, double Bottom) rect)
    {
        var rects = CollectionsMarshal.AsSpan(_taken);
        var next = CollectionsMarshal.AsSpan(_entryNext);
        var owner = CollectionsMarshal.AsSpan(_entryRect);
        var (firstColumn, lastColumn, firstRow, lastRow) = Cells(rect);
        for (var column = firstColumn; column <= lastColumn; column++)
        {
            for (var row = firstRow; row <= lastRow; row++)
            {
                if (!_cellHeads.TryGetValue(Key(column, row), out var entry))
                {
                    continue;
                }

                for (; entry >= 0; entry = next[entry])
                {
                    var other = rects[owner[entry]];
                    if (rect.Left < other.Right && rect.Right > other.Left &&
                        rect.Top < other.Bottom && rect.Bottom > other.Top)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private void Take((double Left, double Top, double Right, double Bottom) rect)
    {
        var id = _taken.Count;
        _taken.Add(rect);
        var (firstColumn, lastColumn, firstRow, lastRow) = Cells(rect);
        for (var column = firstColumn; column <= lastColumn; column++)
        {
            for (var row = firstRow; row <= lastRow; row++)
            {
                var key = Key(column, row);
                _entryNext.Add(_cellHeads.TryGetValue(key, out var head) ? head : -1);
                _entryRect.Add(id);
                _cellHeads[key] = _entryNext.Count - 1;
            }
        }
    }

    private static (int FirstColumn, int LastColumn, int FirstRow, int LastRow) Cells(
        (double Left, double Top, double Right, double Bottom) rect) =>
        (Cell(rect.Left), Cell(rect.Right), Cell(rect.Top), Cell(rect.Bottom));

    // Clamped so a name far off the plan at a deep zoom still lands in a finite cell.
    private static int Cell(double value) => (int)Math.Clamp(Math.Floor(value / CellSize), -1_000_000, 1_000_000);

    private static long Key(int column, int row) => ((long)column << 32) | (uint)row;
}
