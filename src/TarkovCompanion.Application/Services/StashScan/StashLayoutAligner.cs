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
/// A packed stash does not need names to be recognisable. The pattern of rectangles across a few
/// shared rows is as good as a fingerprint, so this compares footprints cell by cell at every
/// candidate offset and accepts an offset only when every compared cell agrees, at least
/// <see cref="MinimumSharedFootprints"/> whole items are shared, and no other offset does as well.
/// A tie is left unplaced: the assembler's own guidance then asks for more overlap.
/// </para>
/// <para>
/// A rectangle touching the first or last row of either frame is not compared. The viewport cuts
/// items there, and a cut item read as a smaller one is a disagreement that is not real.
/// </para>
/// <para>
/// The result is only ever an origin hint of partial completeness. Identity alignment still runs
/// first and is never overridden; this fills in the frames it could not place, and retries them
/// after later frames land, so a frame taken too far down is placed once a bridging one arrives.
/// </para>
/// </remarks>
public sealed class StashLayoutAligner
{
    public const int MinimumSharedFootprints = 3;

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
        var hinted = new Dictionary<string, GridCellAddress>(StringComparer.Ordinal);

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
                    hinted[frame.ArtifactId] = origin;
                    progressed = true;
                }
            }
        }
        while (progressed);

        if (hinted.Count == 0)
        {
            return frames;
        }

        return frames
            .Select(frame => hinted.TryGetValue(frame.ArtifactId, out var origin)
                ? WithOrigin(frame, origin, alignedUtc)
                : frame)
            .ToArray();
    }

    private static GridCellAddress? FindOrigin(
        StashFrameLayout layout,
        IReadOnlyList<(StashFrameLayout Layout, GridCellAddress Origin)> placed)
    {
        var absolute = new Dictionary<(int Row, int Column), StashLayoutCell>();
        foreach (var (other, origin) in placed)
        {
            foreach (var (cell, value) in other.Cells)
            {
                absolute.TryAdd((origin.Row + cell.Row, origin.Column + cell.Column), value);
            }
        }

        if (absolute.Count == 0)
        {
            return null;
        }

        var lastRow = absolute.Keys.Max(cell => cell.Row);
        GridCellAddress? best = null;
        var bestShared = 0;
        var tied = false;
        for (var row = 0; row <= lastRow && row <= GridGeometry.MaxRows - layout.Rows; row++)
        {
            var shared = SharedFootprints(layout, absolute, row);
            if (shared < MinimumSharedFootprints)
            {
                continue;
            }

            if (shared > bestShared)
            {
                best = new GridCellAddress(row, 0);
                bestShared = shared;
                tied = false;
            }
            else if (shared == bestShared)
            {
                tied = true;
            }
        }

        return tied ? null : best;
    }

    /// <summary>Whole items the two layouts agree on at this offset, or -1 when any cell disagrees.</summary>
    private static int SharedFootprints(
        StashFrameLayout layout,
        IReadOnlyDictionary<(int Row, int Column), StashLayoutCell> absolute,
        int originRow)
    {
        var sharedAnchors = 0;
        foreach (var (cell, value) in layout.Cells)
        {
            if (!absolute.TryGetValue((originRow + cell.Row, cell.Column), out var existing))
            {
                continue;
            }

            if (value.Uncertain || existing.Uncertain)
            {
                continue;
            }

            if (value != existing)
            {
                return -1;
            }

            if (value is { Occupied: true, OffsetRow: 0, OffsetColumn: 0 })
            {
                sharedAnchors++;
            }
        }

        return sharedAnchors;
    }

    private static StashScanCaptureFrame WithOrigin(StashScanCaptureFrame frame, GridCellAddress origin, DateTimeOffset alignedUtc)
    {
        var observedUtc = alignedUtc < frame.Provenance.ObservedUtc ? frame.Provenance.ObservedUtc : alignedUtc;
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            "stash.origin.layout-overlap",
            observedUtc,
            EvidenceConfidence.Unscored,
            Producer,
            generatedUtc: observedUtc,
            inputs: [frame.Provenance]);
        var hint = new EvidencedValue<GridCellAddress?>(
            $"stash.origin.{frame.CaptureOrdinal}",
            origin,
            new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current, "stash.origin.layout-overlap"),
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

/// <summary>What one cell of a frame holds, as far as its shape goes.</summary>
internal readonly record struct StashLayoutCell(bool Occupied, int Width, int Height, int OffsetRow, int OffsetColumn, bool Uncertain);

/// <summary>A frame reduced to rectangles: every cell is empty or a known part of one footprint.</summary>
internal sealed class StashFrameLayout
{
    private StashFrameLayout(int rows, int columns, Dictionary<(int Row, int Column), StashLayoutCell> cells)
    {
        Rows = rows;
        Columns = columns;
        Cells = cells;
    }

    public int Rows { get; }

    public int Columns { get; }

    public IReadOnlyDictionary<(int Row, int Column), StashLayoutCell> Cells { get; }

    public static StashFrameLayout? From(StashScanCaptureFrame frame)
    {
        var grid = frame.Reconstruction.Recognition;
        if (grid?.Geometry.Rows.Value is not { } rows || grid.Geometry.Columns.Value is not { } columns)
        {
            return null;
        }

        var cells = new Dictionary<(int Row, int Column), StashLayoutCell>(rows * columns);
        foreach (var cell in grid.Cells)
        {
            var (width, height) = StashFootprints.Of(grid, cell);
            var uncertain = cell.Anchor.Row == 0 || cell.Anchor.Row + height >= rows;
            for (var row = 0; row < height; row++)
            {
                for (var column = 0; column < width; column++)
                {
                    cells[(cell.Anchor.Row + row, cell.Anchor.Column + column)] =
                        new(true, width, height, row, column, uncertain);
                }
            }
        }

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                // An empty cell on an edge row is as unreliable as an item there: a cut item's
                // remainder can read as flat.
                cells.TryAdd((row, column), new(false, 0, 0, 0, 0, row == 0 || row == rows - 1));
            }
        }

        return new(rows, columns, cells);
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
