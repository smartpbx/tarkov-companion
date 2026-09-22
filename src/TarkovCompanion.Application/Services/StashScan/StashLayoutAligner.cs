using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Application.Services.StashScan;

/// <summary>
/// Places a scrolled stash frame by the shape of what is in it, when nothing in it has a name.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StashScanAssembler"/> stitches on identity: two resolved items have to appear in both
/// frames. Measured on a painted 34-row stash (package 40), with the recognizer as shipped naming
/// nothing, that placed one frame of three - the first, which is told where it is - and the rest of
/// the stash was discarded as "origin unresolved".
/// </para>
/// <para>
/// A packed stash does not need names to be recognisable: the pattern of rectangles across a few
/// shared rows is as good as a fingerprint.
/// </para>
/// <para>
/// The first version compared whole footprints and left out every rectangle touching the first or
/// last row of a screenshot, because the viewport cuts items there. That suited the painted scans
/// it was measured on, which overlapped by four rows. On a real burst (2026-09-18, seven stash
/// screens) the player scrolled nearly a full screen each time: neighbouring screens shared two or
/// three rows, all of them edge rows, and one screen of seven was placed.
/// </para>
/// <para>
/// What a viewport cut cannot corrupt is smaller than a footprint: whether a cell is occupied,
/// and whether the side it shares with the next cell is a border. A rifle cut in half still has
/// the same borders along the rows that are showing. So each frame is reduced to those bits, and
/// an offset is judged by the share of compared bits that agree. It is accepted only when at
/// least <see cref="MinimumComparedBits"/> bits were compared (about two rows), at least
/// <see cref="MinimumAgreement"/> of them agree, and every other offset is at least
/// <see cref="MinimumLeadOverRival"/> behind. The reader misjudges a few borders in a hundred on real
/// pixels, so exact agreement cannot be asked for; a run of identical one-cell items agrees with
/// itself at several offsets, which the rival test leaves unplaced rather than guesses.
/// </para>
/// <para>
/// The result is only ever an origin hint of partial completeness. Identity alignment still runs
/// first and is never overridden; this fills in the frames it could not place, and retries them
/// after later frames land, so a frame taken too far down is placed once a bridging one arrives.
/// </para>
/// </remarks>
public sealed class StashLayoutAligner
{
    /// <summary>The fewest occupancy and border bits an offset must be judged on: about two rows.</summary>
    public const int MinimumComparedBits = 40;

    /// <summary>The share of compared bits that must agree at the accepted offset.</summary>
    public const double MinimumAgreement = 0.93;

    /// <summary>How far ahead of every other offset the best one must be to be believed.</summary>
    /// <remarks>
    /// A margin and not a cap: a real stash has rows of identical one-cell items - magazines,
    /// bandages - that agree with themselves nearly as well one row off, so the same screen taken
    /// twice was refused under a cap of 0.85 while agreeing almost perfectly with itself.
    /// </remarks>
    public const double MinimumLeadOverRival = 0.07;

    private static readonly ProducerIdentity Producer = new("Tarkov Companion stash layout aligner", "stash-layout-aligner-1");

    /// <summary>
    /// Returns the frames with layout-derived origin hints added where the first assembly pass
    /// left a frame unplaced and the layout gives exactly one answer.
    /// </summary>
    public IReadOnlyList<StashScanCaptureFrame> AddLayoutOrigins(
        IReadOnlyList<StashScanCaptureFrame> frames,
        StashScanAssemblyResult firstPass,
        DateTimeOffset alignedUtc)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(firstPass);
        var stash = firstPass.Recognition.Result.Value;
        if (stash is null)
        {
            return frames;
        }

        var origins = stash.CapturedRegions
            .Where(region => region.OriginInContainer.Value is not null)
            .ToDictionary(region => region.ArtifactId, region => region.OriginInContainer.Value!.Value, StringComparer.Ordinal);
        var byArtifact = frames
            .GroupBy(frame => frame.ArtifactId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var hinted = new Dictionary<string, (GridCellAddress Origin, string Code)>(StringComparer.Ordinal);

        bool progressed;
        do
        {
            progressed = false;
            foreach (var frame in frames.OrderBy(frame => frame.CaptureOrdinal))
            {
                if (origins.ContainsKey(frame.ArtifactId) || frame.Reconstruction.Recognition is null)
                {
                    continue;
                }

                var placed = origins
                    .Where(pair => byArtifact.TryGetValue(pair.Key, out var other) &&
                                   string.Equals(other.ContainerPath, frame.ContainerPath, StringComparison.Ordinal))
                    .Select(pair => (Layout: StashFrameLayout.From(byArtifact[pair.Key]), Origin: pair.Value))
                    .Where(pair => pair.Layout is not null)
                    .Select(pair => (Layout: pair.Layout!, pair.Origin))
                    .ToArray();
                if (placed.Length == 0 || StashFrameLayout.From(frame) is not { } layout)
                {
                    continue;
                }

                if (FindOrigin(layout, placed) is { } origin)
                {
                    origins[frame.ArtifactId] = origin;
                    hinted[frame.ArtifactId] = (origin, "stash.origin.layout-overlap");
                    progressed = true;
                }
            }

            if (!progressed && AddNextScrollOrderedOrigin(frames, origins) is { } ordered)
            {
                origins[ordered.ArtifactId] = ordered.Origin;
                hinted[ordered.ArtifactId] = (ordered.Origin, "stash.origin.scroll-order");
                progressed = true;
            }
        }
        while (progressed);

        if (hinted.Count == 0)
        {
            return frames;
        }

        return frames
            .Select(frame => hinted.TryGetValue(frame.ArtifactId, out var hint)
                ? WithOrigin(frame, hint.Origin, alignedUtc, hint.Code)
                : frame)
            .ToArray();
    }

    /// <summary>
    /// A full-page scroll has no shared rows to match. Once overlap matching has stalled, place the
    /// next readable scrollbar page immediately after the nearest earlier placed page, then retry
    /// overlap matching. This preserves scroll order without guessing from capture ordinal (the
    /// player may scroll back), and a later overlapping page supplies the exact smaller offset.
    /// </summary>
    private static (string ArtifactId, GridCellAddress Origin)? AddNextScrollOrderedOrigin(
        IReadOnlyList<StashScanCaptureFrame> frames,
        IReadOnlyDictionary<string, GridCellAddress> origins)
    {
        foreach (var container in frames.GroupBy(frame => frame.ContainerPath, StringComparer.Ordinal))
        {
            var ordered = container
                .Where(frame => frame.Reconstruction.VerticalScrollPosition is not null && StashFrameLayout.From(frame) is not null)
                .OrderBy(frame => frame.Reconstruction.VerticalScrollPosition)
                .ThenBy(frame => frame.CaptureOrdinal)
                .ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var frame = ordered[index];
                if (origins.ContainsKey(frame.ArtifactId))
                {
                    continue;
                }

                for (var previousIndex = index - 1; previousIndex >= 0; previousIndex--)
                {
                    var previous = ordered[previousIndex];
                    if (!origins.TryGetValue(previous.ArtifactId, out var previousOrigin) ||
                        StashFrameLayout.From(previous) is not { } previousLayout)
                    {
                        continue;
                    }

                    var samePage = Math.Abs(
                        frame.Reconstruction.VerticalScrollPosition!.Value -
                        previous.Reconstruction.VerticalScrollPosition!.Value) <= 0.01;
                    return (
                        frame.ArtifactId,
                        new GridCellAddress(previousOrigin.Row + (samePage ? 0 : previousLayout.Rows), 0));
                }
            }
        }

        return null;
    }

    private static GridCellAddress? FindOrigin(
        StashFrameLayout layout,
        IReadOnlyList<(StashFrameLayout Layout, GridCellAddress Origin)> placed)
    {
        var absolute = new Dictionary<(int Row, int Column, StashLayoutBit Bit), bool>();
        foreach (var (other, origin) in placed)
        {
            foreach (var (key, value) in other.Bits)
            {
                absolute.TryAdd((origin.Row + key.Row, origin.Column + key.Column, key.Bit), value);
            }
        }

        if (absolute.Count == 0)
        {
            return null;
        }

        var lastRow = absolute.Keys.Max(key => key.Row);
        int? bestRow = null;
        var bestAgreement = 0d;
        var bestStanding = double.MinValue;
        var rivalStanding = double.MinValue;
        for (var row = 0; row <= lastRow && row <= GridGeometry.MaxRows - layout.Rows; row++)
        {
            var agree = 0;
            var compared = 0;
            foreach (var (key, value) in layout.Bits)
            {
                if (absolute.TryGetValue((row + key.Row, key.Column, key.Bit), out var existing))
                {
                    compared++;
                    if (existing == value)
                    {
                        agree++;
                    }
                }
            }

            if (compared < MinimumComparedBits)
            {
                continue;
            }

            // Offsets are ranked on agreement less what a sample that small could owe to chance:
            // on a real pair the same screen taken twice agreed on 347 of 367 bits, and an offset
            // eleven rows away agreed on 43 of 48.
            var agreement = agree / (double)compared;
            var standing = agreement - (1 / Math.Sqrt(compared));
            if (standing > bestStanding)
            {
                rivalStanding = bestStanding;
                bestStanding = standing;
                bestAgreement = agreement;
                bestRow = row;
            }
            else if (standing > rivalStanding)
            {
                rivalStanding = standing;
            }
        }

        return bestRow is { } found && bestAgreement >= MinimumAgreement && bestStanding - rivalStanding >= MinimumLeadOverRival
            ? new GridCellAddress(found, 0)
            : null;
    }

    private static StashScanCaptureFrame WithOrigin(
        StashScanCaptureFrame frame,
        GridCellAddress origin,
        DateTimeOffset alignedUtc,
        string code)
    {
        var observedUtc = alignedUtc < frame.Provenance.ObservedUtc ? frame.Provenance.ObservedUtc : alignedUtc;
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            code,
            observedUtc,
            EvidenceConfidence.Unscored,
            Producer,
            generatedUtc: observedUtc,
            inputs: [frame.Provenance]);
        var hint = new EvidencedValue<GridCellAddress?>(
            $"stash.origin.{frame.CaptureOrdinal}",
            origin,
            new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current, code),
            provenance);
        return new StashScanCaptureFrame(
            frame.SessionId,
            frame.ArtifactId,
            frame.CaptureOrdinal,
            frame.CorrelationId,
            frame.Context,
            frame.ContentSha256,
            frame.ContainerPath,
            frame.CapturedUtc,
            frame.DecodeRevision,
            frame.Provenance,
            frame.Reconstruction,
            frame.TotalContainerCells,
            confirmsContainerStart: false,
            originHint: hint,
            itemNetValues: frame.ItemNetValues,
            closedContainerPaths: frame.ClosedContainerPaths);
    }
}

/// <summary>The three things about a cell a viewport cut leaves intact.</summary>
internal enum StashLayoutBit
{
    Occupied,
    BorderToTheRight,
    BorderBelow,
}

/// <summary>A frame reduced to occupancy and border bits, in its own lattice coordinates.</summary>
internal sealed class StashFrameLayout
{
    private StashFrameLayout(int rows, int columns, Dictionary<(int Row, int Column, StashLayoutBit Bit), bool> bits)
    {
        Rows = rows;
        Columns = columns;
        Bits = bits;
    }

    public int Rows { get; }

    public int Columns { get; }

    public IReadOnlyDictionary<(int Row, int Column, StashLayoutBit Bit), bool> Bits { get; }

    public static StashFrameLayout? From(StashScanCaptureFrame frame)
    {
        var grid = frame.Reconstruction.Recognition;
        if (grid?.Geometry.Rows.Value is not { } rows || grid.Geometry.Columns.Value is not { } columns)
        {
            return null;
        }

        // Which footprint, if any, owns each cell.
        var owner = new int[rows, columns];
        var index = 0;
        foreach (var cell in grid.Cells)
        {
            index++;
            var (width, height) = StashFootprints.Of(grid, cell);
            for (var row = cell.Anchor.Row; row < Math.Min(rows, cell.Anchor.Row + height); row++)
            {
                for (var column = cell.Anchor.Column; column < Math.Min(columns, cell.Anchor.Column + width); column++)
                {
                    owner[row, column] = index;
                }
            }
        }

        var bits = new Dictionary<(int Row, int Column, StashLayoutBit Bit), bool>(rows * columns * 3);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                bits[(row, column, StashLayoutBit.Occupied)] = owner[row, column] != 0;
                if (column + 1 < columns)
                {
                    bits[(row, column, StashLayoutBit.BorderToTheRight)] = owner[row, column] != owner[row, column + 1];
                }

                // The side below the last row is the viewport's, not the stash's.
                if (row + 1 < rows)
                {
                    bits[(row, column, StashLayoutBit.BorderBelow)] = owner[row, column] != owner[row + 1, column];
                }
            }
        }

        return new(rows, columns, bits);
    }
}

/// <summary>The rectangle a recognized cell covers, whether or not the item was named.</summary>
public static class StashFootprints
{
    public static (int Width, int Height) Of(GridRecognition grid, GridCellRecognition cell)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(cell);
        var item = cell.Item.Value ?? cell.Item.Candidates.FirstOrDefault()?.Value;
        if (item?.WidthCells.Value is { } width && item.HeightCells.Value is { } height)
        {
            return (Math.Max(1, width), Math.Max(1, height));
        }

        // An unnamed cell still carries the pixels it was read from, and the lattice's cell size
        // turns those back into a whole number of cells.
        if (cell.Item.Bounds is { } bounds &&
            grid.Geometry.CellWidthPixels.Value is { } cellWidth and > 0 &&
            grid.Geometry.CellHeightPixels.Value is { } cellHeight and > 0)
        {
            return (
                Math.Max(1, (int)Math.Round(bounds.Width / (double)cellWidth, MidpointRounding.AwayFromZero)),
                Math.Max(1, (int)Math.Round(bounds.Height / (double)cellHeight, MidpointRounding.AwayFromZero)));
        }

        return (1, 1);
    }
}
