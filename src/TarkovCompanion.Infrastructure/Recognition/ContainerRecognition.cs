using System.Text.RegularExpressions;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed record ContainerGridSpec(PixelRect Bounds, int Columns, int Rows)
{
    public static ContainerGridSpec FromNormalized(
        CapturedImage image,
        double x,
        double y,
        double width,
        double height,
        int columns,
        int rows) => new(
            CapturedImagePixels.FromNormalized(image, x, y, width, height),
            columns,
            rows);
}

public sealed record ContainerSegment(
    int Row,
    int Column,
    PixelRect Bounds,
    Confidence Occupancy,
    bool IsOccupied);

public sealed class ContainerGridDetector
{
    /// <summary>How much brighter or darker than both of its sides a grid line has to be.</summary>
    /// <remarks>
    /// json.tarkov.dev draws a cell border at luminance 79 over item backgrounds between 15 and
    /// 51, so the weakest real border stands 28 above its surroundings. The old one-sided test
    /// asked for 45 and lost every border next to a light item.
    /// </remarks>
    internal const int RidgeContrast = 18;

    /// <summary>Pixels of a line that may be hidden, by art or a badge, without ending its run.</summary>
    private const int GapTolerancePixels = 4;

    /// <summary>Finds a regular grid, checking cancellation in bounded chunks of work.</summary>
    /// <remarks>
    /// <para>
    /// The algorithm is #273's. The token is #299's: this walks every column and every row of the
    /// frame and then searches every pair of line positions, and it used to run before any OCR
    /// deadline existed and without a way to stop it.
    /// </para>
    /// <para>
    /// Package 37 changed what counts as a line. It used to be a column whose pixels contrasted
    /// with a neighbour over 18% of the whole frame, which a ten-wide stash satisfies and a loot
    /// container cannot: four cells at 1080p are 13% of the frame's width, so no jacket, toolbox
    /// or safe was ever found. A line is now a thin ridge that runs unbroken for about a cell,
    /// wherever in the frame it is, and the rows are looked for only between the columns found,
    /// so a second panel drawn at the same pitch does not lend this one its lines.
    /// </para>
    /// </remarks>
    public ContainerGridSpec? Detect(CapturedImage image, CancellationToken cancellationToken = default)
    {
        // Refused over the pixel ceiling before the first read. Providers already refused such a
        // frame, but this walks it before any provider is asked.
        CapturedImagePixels.Validate(image, CapturedImagePixels.MaximumPixels);
        cancellationToken.ThrowIfCancellationRequested();
        var check = new PixelCancellationCheck(cancellationToken);
        var luminance = ReadLuminance(image, ref check);

        // No screen draws a cell this small: 1280x720 draws them 42 pixels across.
        var minimumPitch = Math.Max(12, Math.Min(image.Width, image.Height) / 40);
        var columns = SelectRegularRun(
            FindLinePositions(luminance, image.Width, image.Height, vertical: true, 0, image.Height, ref check),
            minimumPitch,
            cancellationToken);
        if (columns.Lines.Count < 3)
        {
            return null;
        }

        var rows = FindRows(luminance, image, columns, minimumPitch, null, ref check, cancellationToken);
        if (rows.Lines.Count < 3)
        {
            return null;
        }

        var refined = SelectRegularRun(
            FindLinePositions(luminance, image.Width, image.Height, vertical: true, rows.Lines[0], rows.Lines[^1] + 1, ref check),
            minimumPitch,
            cancellationToken);
        if (refined.Lines.Count >= 3 && (refined.Lines[0] != columns.Lines[0] || refined.Lines[^1] != columns.Lines[^1]))
        {
            columns = refined;
            var again = FindRows(luminance, image, columns, minimumPitch, null, ref check, cancellationToken);
            if (again.Lines.Count >= 3)
            {
                rows = again;
            }
        }

        // Cells are square. When the two axes disagree, one of them had a line hidden often
        // enough to be read at a multiple of the pitch; the axis with more found lines is
        // believed and the other is read again at its pitch.
        if (Math.Abs(columns.Pitch - rows.Pitch) > Math.Max(2, Math.Min(columns.Pitch, rows.Pitch) * 0.08))
        {
            if (columns.Seen >= rows.Seen)
            {
                var squared = FindRows(luminance, image, columns, minimumPitch, columns.Pitch, ref check, cancellationToken);
                rows = squared.Lines.Count >= 3 ? squared : rows;
            }
            else
            {
                var squared = SelectRegularRun(
                    FindLinePositions(luminance, image.Width, image.Height, vertical: true, rows.Lines[0], rows.Lines[^1] + 1, ref check),
                    minimumPitch,
                    cancellationToken,
                    rows.Pitch);
                columns = squared.Lines.Count >= 3 ? squared : columns;
            }
        }

        var vertical = columns.Lines;
        var horizontal = rows.Lines;
        return new(
            new PixelRect(
                vertical[0],
                horizontal[0],
                vertical[^1] - vertical[0],
                horizontal[^1] - horizontal[0]),
            vertical.Count - 1,
            horizontal.Count - 1);
    }

    private static RegularRun FindRows(
        byte[] luminance,
        CapturedImage image,
        RegularRun columns,
        int minimumPitch,
        double? requiredPitch,
        ref PixelCancellationCheck check,
        CancellationToken cancellationToken) => SelectRegularRun(
            FindLinePositions(luminance, image.Width, image.Height, vertical: false, columns.Lines[0], columns.Lines[^1] + 1, ref check),
            minimumPitch,
            cancellationToken,
            requiredPitch);

    private static byte[] ReadLuminance(CapturedImage image, ref PixelCancellationCheck check)
    {
        var luminance = new byte[checked(image.Width * image.Height)];
        for (var y = 0; y < image.Height; y++)
        {
            var offset = y * image.Width;
            for (var x = 0; x < image.Width; x++)
            {
                // Counted a pixel at a time: the cancellation contract is "within one check
                // interval of reads", and a row at a time overshoots it by a row.
                check.Read();
                luminance[offset + x] = CapturedImagePixels.GetLuminance(image, x, y);
            }
        }

        return luminance;
    }

    /// <summary>
    /// Positions along one axis where a thin line runs unbroken for about a cell, looking only
    /// at the stretch of the other axis between <paramref name="crossFrom"/> and
    /// <paramref name="crossTo"/>.
    /// </summary>
    /// <summary>A found line and how many pixels of it were seen.</summary>
    private readonly record struct GridLine(int Position, int Strength);

    private static IReadOnlyList<GridLine> FindLinePositions(
        byte[] luminance,
        int width,
        int height,
        bool vertical,
        int crossFrom,
        int crossTo,
        ref PixelCancellationCheck check)
    {
        var axisLength = vertical ? width : height;
        var crossLength = vertical ? height : width;
        crossFrom = Math.Clamp(crossFrom, 0, crossLength);
        crossTo = Math.Clamp(crossTo, crossFrom, crossLength);
        var minimumRun = Math.Max(24, Math.Min(width, height) / 20);
        var strength = new int[axisLength];
        for (var axis = 2; axis < axisLength - 2; axis++)
        {
            check.Read(crossTo - crossFrom);
            var runStart = -1;
            var lastRidge = -1;
            var total = 0;
            for (var cross = crossFrom; cross < crossTo; cross++)
            {
                int value;
                int before;
                int after;
                if (vertical)
                {
                    var row = cross * width;
                    value = luminance[row + axis];
                    before = luminance[row + axis - 2];
                    after = luminance[row + axis + 2];
                }
                else
                {
                    value = luminance[(axis * width) + cross];
                    before = luminance[((axis - 2) * width) + cross];
                    after = luminance[((axis + 2) * width) + cross];
                }

                var fromBefore = value - before;
                var fromAfter = value - after;
                var isRidge = (fromBefore >= RidgeContrast && fromAfter >= RidgeContrast) ||
                              (fromBefore <= -RidgeContrast && fromAfter <= -RidgeContrast);
                if (!isRidge)
                {
                    continue;
                }

                if (runStart < 0 || cross - lastRidge > GapTolerancePixels + 1)
                {
                    if (runStart >= 0 && lastRidge - runStart + 1 >= minimumRun)
                    {
                        total += lastRidge - runStart + 1;
                    }

                    runStart = cross;
                }

                lastRidge = cross;
            }

            if (runStart >= 0 && lastRidge - runStart + 1 >= minimumRun)
            {
                total += lastRidge - runStart + 1;
            }

            strength[axis] = total;
        }

        // A two-pixel line qualifies twice. Keep whichever of a touching group ran furthest,
        // not their midpoint: the midpoint of "the line and the pixel beside it" put every
        // lattice one pixel off, which is all it takes to change an icon's fingerprint.
        var positions = new List<GridLine>();
        for (var axis = 0; axis < axisLength;)
        {
            if (strength[axis] == 0)
            {
                axis++;
                continue;
            }

            var best = axis;
            var end = axis;
            while (end + 1 < axisLength && (strength[end + 1] > 0 || (end + 2 < axisLength && strength[end + 2] > 0)))
            {
                end++;
                if (strength[end] > strength[best])
                {
                    best = end;
                }
            }

            positions.Add(new(best, strength[best]));
            axis = end + 1;
        }

        return positions;
    }

    /// <summary>Lines in a row that may be missing before a run is taken to have ended.</summary>
    /// <remarks>
    /// A line between two columns is drawn only where some row has a border there. Pack a
    /// container with items two and three cells wide and a whole interior line can be covered
    /// from top to bottom; the first composed 4x5 container this was rendered from did exactly
    /// that, and the run "every line" lost to "every second line" and halved the grid.
    /// </remarks>
    private const int MaximumMissingLines = 2;

    private sealed record RegularRun(IReadOnlyList<int> Lines, int Matched, long Seen)
    {
        public static RegularRun None { get; } = new([], 0, 0);

        public double Pitch => Lines.Count < 2 ? 0 : (Lines[^1] - Lines[0]) / (double)(Lines.Count - 1);
    }

    /// <summary>
    /// The evenly spaced run that accounts for the most line seen, with the lines it had to
    /// assume filled in where they belong.
    /// </summary>
    /// <remarks>
    /// Scored by pixels of long line rather than by how many lines, and never at a pitch no
    /// screen draws a cell at. Counting lines let the ribs of a handguard's rail, a dozen short
    /// ridges twelve pixels apart, outvote the five real rows they sat inside.
    /// </remarks>
    private static RegularRun SelectRegularRun(
        IReadOnlyList<GridLine> lines,
        int minimumPitch,
        CancellationToken cancellationToken,
        double? requiredPitch = null)
    {
        var positions = lines.Select(line => line.Position).ToArray();
        var best = RegularRun.None;
        var bestSpacing = 0;
        for (var first = 0; first < positions.Length; first++)
        {
            for (var second = first + 1; second < positions.Length; second++)
            {
                // No pixels here, but a noisy frame yields hundreds of positions and every pair
                // extends a run by scanning all of them again.
                cancellationToken.ThrowIfCancellationRequested();
                var spacing = positions[second] - positions[first];
                if (spacing < minimumPitch)
                {
                    continue;
                }

                var tolerance = Math.Max(2, (int)Math.Round(spacing * 0.08));
                if (requiredPitch is { } pitch && Math.Abs(spacing - pitch) > Math.Max(2, pitch * 0.08))
                {
                    continue;
                }

                var run = new List<int> { positions[first] };
                var assumed = new List<int>();
                var matched = 1;
                var strengths = new List<int> { lines[first].Strength };
                var expected = positions[first] + spacing;
                while (assumed.Count <= MaximumMissingLines && expected <= positions[^1] + tolerance)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryFindClosest(positions, expected - tolerance - 1, expected, tolerance, out var match))
                    {
                        run.AddRange(assumed);
                        assumed.Clear();
                        run.Add(positions[match]);
                        matched++;
                        strengths.Add(lines[match].Strength);
                        expected = positions[match] + spacing;
                    }
                    else
                    {
                        assumed.Add(expected);
                        expected += spacing;
                    }
                }

                // And backwards from the first line, under the same allowance. A run only ever
                // grew forwards, so when the second line of a grid was the hidden one, no pair
                // began at the outer border and the grid lost its first columns.
                assumed.Clear();
                expected = positions[first] - spacing;
                while (assumed.Count <= MaximumMissingLines && expected >= positions[0] - tolerance)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryFindClosest(positions, expected - tolerance - 1, expected, tolerance, out var match))
                    {
                        run.InsertRange(0, assumed);
                        assumed.Clear();
                        run.Insert(0, positions[match]);
                        matched++;
                        strengths.Add(lines[match].Strength);
                        expected = positions[match] - spacing;
                    }
                    else
                    {
                        assumed.Insert(0, expected);
                        expected -= spacing;
                    }
                }

                // Only lines at least half as long as the run's longest count towards it. A grid
                // line runs the length of its panel; the edge of a barrel that happens to sit
                // mid-cell runs the length of a barrel, and counting it let half the true pitch
                // outscore the true one. Between runs that then explain the same lines, the
                // wider spacing assumes the fewest it did not see.
                var longest = strengths.Max();
                var seen = strengths.Where(strength => strength * 2 >= longest).Sum(strength => (long)strength);
                if (matched >= 3 && (seen > best.Seen || (seen == best.Seen && spacing > bestSpacing)))
                {
                    best = new(run, matched, seen);
                    bestSpacing = spacing;
                }
            }
        }

        return best;
    }

    private static bool TryFindClosest(
        IReadOnlyList<int> positions,
        int after,
        int expected,
        int tolerance,
        out int match)
    {
        match = -1;
        var bestDistance = int.MaxValue;
        for (var index = 0; index < positions.Count; index++)
        {
            var distance = Math.Abs(positions[index] - expected);
            if (positions[index] > after && distance <= tolerance && distance < bestDistance)
            {
                match = index;
                bestDistance = distance;
            }
        }

        return match >= 0;
    }
}

public sealed class ContainerGridSegmenter
{
    public IReadOnlyList<ContainerSegment> Segment(
        CapturedImage image,
        ContainerGridSpec grid,
        CancellationToken cancellationToken = default)
    {
        CapturedImagePixels.Validate(image, CapturedImagePixels.MaximumPixels);
        ArgumentNullException.ThrowIfNull(grid);
        cancellationToken.ThrowIfCancellationRequested();
        if (grid.Columns <= 0 || grid.Rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(grid), "Grid dimensions must be positive.");
        }

        var frame = new PixelRect(0, 0, image.Width, image.Height);
        if (!CapturedImagePixels.Intersects(frame, grid.Bounds) ||
            grid.Bounds.X < 0 ||
            grid.Bounds.Y < 0 ||
            grid.Bounds.X + grid.Bounds.Width > image.Width ||
            grid.Bounds.Y + grid.Bounds.Height > image.Height ||
            grid.Bounds.Width < grid.Columns ||
            grid.Bounds.Height < grid.Rows)
        {
            throw new ArgumentOutOfRangeException(nameof(grid), "Grid cells must fit completely inside the image.");
        }

        var measurements = new List<CellMeasurement>(checked(grid.Columns * grid.Rows));
        for (var row = 0; row < grid.Rows; row++)
        {
            for (var column = 0; column < grid.Columns; column++)
            {
                // A cell samples at most thirteen by thirteen pixels; a detected grid can still
                // have a thousand columns.
                cancellationToken.ThrowIfCancellationRequested();
                var bounds = GetCellBounds(image, grid, row, column);
                measurements.Add(Measure(image, row, column, bounds));
            }
        }

        var baseline = measurements.Select(cell => cell.Mean).Order().ElementAt(measurements.Count / 4);
        return measurements.Select(cell =>
        {
            var meanDelta = Math.Abs(cell.Mean - baseline) / 80d;
            var variation = cell.StandardDeviation / 55d;
            var occupancy = Math.Clamp(Math.Max(meanDelta, variation), 0, 1);
            return new ContainerSegment(
                cell.Row,
                cell.Column,
                cell.Bounds,
                new Confidence(occupancy),
                occupancy >= 0.25);
        }).ToArray();
    }

    private static PixelRect GetCellBounds(CapturedImage image, ContainerGridSpec grid, int row, int column)
    {
        var left = grid.Bounds.X + ((grid.Bounds.Width * column) / grid.Columns);
        var right = grid.Bounds.X + ((grid.Bounds.Width * (column + 1)) / grid.Columns);
        var top = grid.Bounds.Y + ((grid.Bounds.Height * row) / grid.Rows);
        var bottom = grid.Bounds.Y + ((grid.Bounds.Height * (row + 1)) / grid.Rows);
        left = Math.Clamp(left, 0, image.Width);
        right = Math.Clamp(right, left, image.Width);
        top = Math.Clamp(top, 0, image.Height);
        bottom = Math.Clamp(bottom, top, image.Height);
        return new(left, top, right - left, bottom - top);
    }

    private static CellMeasurement Measure(CapturedImage image, int row, int column, PixelRect bounds)
    {
        var horizontalMargin = Math.Max(1, bounds.Width / 8);
        var verticalMargin = Math.Max(1, bounds.Height / 8);
        var left = Math.Min(bounds.X + horizontalMargin, bounds.X + bounds.Width - 1);
        var top = Math.Min(bounds.Y + verticalMargin, bounds.Y + bounds.Height - 1);
        var right = Math.Max(left + 1, bounds.X + bounds.Width - horizontalMargin);
        var bottom = Math.Max(top + 1, bounds.Y + bounds.Height - verticalMargin);
        var stepX = Math.Max(1, (right - left) / 12);
        var stepY = Math.Max(1, (bottom - top) / 12);
        var count = 0;
        var sum = 0d;
        var squareSum = 0d;

        for (var y = top; y < bottom; y += stepY)
        {
            for (var x = left; x < right; x += stepX)
            {
                var luminance = CapturedImagePixels.GetLuminance(image, x, y);
                sum += luminance;
                squareSum += luminance * luminance;
                count++;
            }
        }

        var mean = count == 0 ? 0 : sum / count;
        var variance = count == 0 ? 0 : Math.Max(0, (squareSum / count) - (mean * mean));
        return new(row, column, bounds, mean, Math.Sqrt(variance));
    }

    private sealed record CellMeasurement(
        int Row,
        int Column,
        PixelRect Bounds,
        double Mean,
        double StandardDeviation);
}

public sealed record ContainerItemValuation(string CanonicalId, long ValueRoubles);

public sealed class ContainerScanAnalyzer
{
    /// <summary>Matches candidates to occupied cells and totals them, checking cancellation per cell.</summary>
    /// <remarks>
    /// The analysis is #273's. The token is #299's: every occupied cell searches every candidate,
    /// and both counts are bounded only by the grid and the lines a provider returned.
    /// </remarks>
    public ContainerScanResult Analyze(
        IReadOnlyList<ContainerSegment> segments,
        IReadOnlyList<RecognitionCandidate> candidates,
        IReadOnlyList<ContainerItemValuation> valuations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(valuations);

        var accepted = new List<CandidateCell>();
        var unresolved = new List<ContainerCellIssue>();
        var ambiguous = new List<ContainerCellIssue>();
        foreach (var cell in segments.Where(segment => segment.IsOccupied))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ranked = candidates
                .Where(candidate => candidate.Bounds is not null && Overlaps(candidate.Bounds, cell.Bounds))
                .GroupBy(candidate => candidate.CanonicalId, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(candidate => candidate.Confidence.Value).First())
                .OrderByDescending(candidate => candidate.Confidence.Value)
                .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
                .ToArray();
            if (ranked.Length == 0)
            {
                unresolved.Add(new(cell.Row, cell.Column, cell.Bounds, "occupied_without_candidate", []));
                continue;
            }

            if (ranked[0].Confidence.Value < RecognitionThresholds.Ambiguous ||
                (ranked.Length > 1 &&
                 ranked[0].Confidence.Value - ranked[1].Confidence.Value < RecognitionThresholds.MinimumRunnerUpLead))
            {
                ambiguous.Add(new(
                    cell.Row,
                    cell.Column,
                    cell.Bounds,
                    "low_or_near_tie_candidate",
                    ranked.Take(3).ToArray()));
                continue;
            }

            accepted.Add(new(ranked[0], cell));
        }

        var values = valuations
            .GroupBy(valuation => valuation.CanonicalId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().ValueRoubles, StringComparer.Ordinal);
        var aggregate = accepted
            .GroupBy(match => match.Candidate.CanonicalId, StringComparer.Ordinal)
            .Select(group => Aggregate(group.Key, group.ToArray()))
            .OrderByDescending(candidate => candidate.Confidence.Value)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .ToArray();
        var includedCells = accepted
            .Select(match => (match.Cell.Row, match.Cell.Column))
            .Distinct()
            .ToArray();
        var missingValuations = aggregate
            .Where(candidate => !values.ContainsKey(candidate.CanonicalId))
            .Select(candidate => candidate.CanonicalId)
            .ToArray();
        var approximateValue = aggregate.Sum(candidate =>
            values.GetValueOrDefault(candidate.CanonicalId) * (long)(candidate.Quantity ?? 1));
        var dropFirst = accepted
            .GroupBy(match => match.Candidate.CanonicalId, StringComparer.Ordinal)
            .Where(group => values.ContainsKey(group.Key))
            .Select(group => new
            {
                Id = group.Key,
                ValuePerSlot = (double)values[group.Key] /
                    Math.Max(1, group.Select(match => (match.Cell.Row, match.Cell.Column)).Distinct().Count()),
            })
            .OrderBy(item => item.ValuePerSlot)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Id)
            .ToArray();
        var confidence = aggregate.Length == 0
            ? Confidence.Unknown
            : new Confidence(aggregate.Average(candidate => candidate.Confidence.Value));
        var partial = unresolved.Count > 0 || ambiguous.Count > 0 || missingValuations.Length > 0;

        return new(
            aggregate,
            approximateValue,
            includedCells.Length,
            dropFirst,
            confidence,
            unresolved,
            ambiguous,
            partial,
            partial ? "container_partial; missing-valuations=" + string.Join(',', missingValuations) : null);
    }

    private static RecognitionCandidate Aggregate(string canonicalId, IReadOnlyList<CandidateCell> matches)
    {
        var observations = matches
            .Select(match => match.Candidate)
            .DistinctBy(candidate => candidate.Bounds)
            .ToArray();
        var best = observations.OrderByDescending(candidate => candidate.Confidence.Value).First();
        var quantity = observations.Sum(candidate => candidate.Quantity ?? 1);
        var bounds = Union(matches.Select(match => match.Cell.Bounds).Distinct().ToArray());
        return best with
        {
            Bounds = bounds,
            Quantity = quantity,
            Evidence = "container-grid; observations=" + observations.Length + "; " + best.Evidence,
        };
    }

    private static PixelRect Union(IReadOnlyList<PixelRect> bounds)
    {
        var left = bounds.Min(rectangle => rectangle.X);
        var top = bounds.Min(rectangle => rectangle.Y);
        var right = bounds.Max(rectangle => rectangle.X + rectangle.Width);
        var bottom = bounds.Max(rectangle => rectangle.Y + rectangle.Height);
        return new(left, top, right - left, bottom - top);
    }

    private static bool Overlaps(PixelRect candidate, PixelRect cell)
    {
        var left = Math.Max(candidate.X, cell.X);
        var top = Math.Max(candidate.Y, cell.Y);
        var right = Math.Min(candidate.X + candidate.Width, cell.X + cell.Width);
        var bottom = Math.Min(candidate.Y + candidate.Height, cell.Y + cell.Height);
        if (right <= left || bottom <= top)
        {
            return false;
        }

        var intersection = (long)(right - left) * (bottom - top);
        var candidateArea = Math.Max(1L, (long)candidate.Width * candidate.Height);
        return (double)intersection / candidateArea >= 0.10;
    }

    private sealed record CandidateCell(RecognitionCandidate Candidate, ContainerSegment Cell);
}

public sealed class ContainerRecognitionService : IContainerRecognitionService
{
    private const int MaximumCellFallbacks = 24;
    private static readonly Regex QuantitySuffix = new(
        @"(?:\s+(?:x|qty\s*:?)\s*(?<quantity>\d{1,4}))\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly IOcrEngine _ocrEngine;
    private readonly CanonicalItemResolverCache _resolverCache;
    private readonly IItemRepository _items;
    private readonly ContainerGridDetector _gridDetector;
    private readonly ContainerGridSegmenter _segmenter;
    private readonly ContainerScanAnalyzer _analyzer;
    private readonly OcrPipelineOptions _pipeline;

    public ContainerRecognitionService(
        IOcrEngine ocrEngine,
        CanonicalItemResolverCache resolverCache,
        IItemRepository items,
        ContainerGridDetector? gridDetector = null,
        ContainerGridSegmenter? segmenter = null,
        ContainerScanAnalyzer? analyzer = null,
        OcrPipelineOptions? pipelineOptions = null)
    {
        _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        _resolverCache = resolverCache ?? throw new ArgumentNullException(nameof(resolverCache));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _gridDetector = gridDetector ?? new();
        _segmenter = segmenter ?? new();
        _analyzer = analyzer ?? new();
        _pipeline = OcrPipelineDeadline.Validate(pipelineOptions);
    }

    public async Task<ContainerScanResult> RecognizeAsync(
        CapturedImage image,
        CancellationToken cancellationToken)
    {
        CapturedImagePixels.Validate(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (CapturedImagePixels.ExceedsPixelCeiling(image))
        {
            // The answer a provider gives a frame this size, given before grid detection walks it.
            return Empty(OcrExecutionBudget.InputLimitExceeded);
        }

        // Grid detection, segmentation and analysis belong to #273. The #299 changes here are the
        // shared deadline and the diagnostics. Inside a scan the deadline is the scan's, joined
        // through its token; alone, this starts one. Either way it is running before the first
        // pixel is read: grid detection walks the whole frame, and it used to run before the
        // budget existed, so the most expensive pixel work in a container scan was the one part
        // nothing bounded. The whole-grid pass, every cell fallback, the resolver, the analyses and
        // the price lookups then spend from the same budget, and running out keeps what was
        // already read as explicit partial evidence.
        using var deadline = OcrPipelineDeadline.Start(_pipeline, cancellationToken);
        ContainerGridSpec? grid;
        IReadOnlyList<ContainerSegment> segments;
        OcrResult ocr;
        try
        {
            grid = _gridDetector.Detect(image, deadline.Token);
            if (grid is null)
            {
                return Empty("container_grid_not_detected");
            }

            segments = _segmenter.Segment(image, grid, deadline.Token);
            ocr = await _ocrEngine
                .RecognizeAsync(image, new OcrRequest(ScanContext.Container, grid.Bounds), deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            return Empty(OcrPipelineDeadline.DiagnosticCode);
        }

        if (!ocr.IsAvailable)
        {
            return Empty(ocr.DiagnosticCode ?? "ocr_provider_unavailable");
        }

        // The catalog, the resolution of every line and the analyses are work on this frame as
        // much as the passes are, and they used to run on the caller's token once the passes had
        // spent the budget: the price lookups, one per item, had no bound at all. They spend from
        // the same deadline now. The deadline running out before the first analysis finishes
        // leaves nothing to report but the code; after it, the result is the last analysis that
        // finished, and the code names what the deadline kept out of it.
        FuzzyCanonicalItemResolver resolver;
        List<RecognitionCandidate> candidates;
        ContainerScanResult preliminary;
        try
        {
            resolver = await _resolverCache.GetAsync(deadline.Token).ConfigureAwait(false);
            candidates = ResolveLines(resolver, ocr.Lines, deadline.Token);
            preliminary = _analyzer.Analyze(segments, candidates, [], deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            return Empty(OcrPipelineDeadline.DiagnosticCode);
        }

        // A grid pass that ran out of memory is not followed by more passes, as in the coordinator.
        var fallbackCells = OcrOutcome.IsMemoryExhausted(ocr)
            ? Array.Empty<ContainerCellIssue>()
            : preliminary.UnresolvedCells
                .Concat(preliminary.AmbiguousCells)
                .OrderBy(cell => cell.Row)
                .ThenBy(cell => cell.Column)
                .Take(MaximumCellFallbacks)
                .ToArray();
        // What degraded each cell's own read, in reading order, so the cell that stayed unresolved
        // says why instead of looking like a cell OCR read and found nothing in.
        var cellDegradations = new List<((int Row, int Column) Cell, string Code)>();
        string? stopped = null;
        foreach (var cell in fallbackCells)
        {
            if (stopped is not null)
            {
                cellDegradations.Add(((cell.Row, cell.Column), stopped));
                continue;
            }

            OcrResult cellOcr;
            try
            {
                cellOcr = await _ocrEngine
                    .RecognizeAsync(
                        image,
                        new OcrRequest(ScanContext.Container, Inset(cell.Bounds)),
                        deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsExpired)
            {
                stopped = OcrPipelineDeadline.DiagnosticCode;
                cellDegradations.Add(((cell.Row, cell.Column), stopped));
                continue;
            }

            if (OcrOutcome.Degradation(cellOcr) is { } degradation)
            {
                cellDegradations.Add(((cell.Row, cell.Column), degradation));
                if (OcrOutcome.IsMemoryExhausted(cellOcr))
                {
                    // The next cell would ask for the same memory again.
                    stopped = degradation;
                }
            }

            if (cellOcr.IsAvailable)
            {
                try
                {
                    candidates.AddRange(ResolveLines(resolver, cellOcr.Lines, deadline.Token));
                }
                catch (OperationCanceledException) when (deadline.IsExpired)
                {
                    stopped = OcrPipelineDeadline.DiagnosticCode;
                    cellDegradations.Add(((cell.Row, cell.Column), stopped));
                }
            }
        }

        var valuations = new List<ContainerItemValuation>();
        ContainerScanResult result;
        try
        {
            foreach (var itemId in candidates.Select(candidate => candidate.CanonicalId).Distinct(StringComparer.Ordinal))
            {
                // Checked before each lookup as well as inside it, so a repository that never
                // looks at its token still does not start a lookup the deadline has ruled out.
                deadline.Token.ThrowIfCancellationRequested();
                var price = await _items.GetPriceAsync(itemId, deadline.Token).ConfigureAwait(false);
                if (price is not null && price.BestEconomicValue > 0)
                {
                    valuations.Add(new(itemId, price.BestEconomicValue));
                }
            }

            result = _analyzer.Analyze(segments, candidates, valuations, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            stopped = OcrPipelineDeadline.DiagnosticCode;
            result = preliminary;
        }

        // The deadline explains every cell it cut off, so it names the scan. Otherwise the first
        // degraded read does: the whole-grid pass, then cells in reading order. A partial grid
        // pass that was still available used to be analyzed as though it were complete.
        var diagnostic = stopped == OcrPipelineDeadline.DiagnosticCode
            ? stopped
            : OcrOutcome.Degradation(ocr) ?? cellDegradations.Select(entry => entry.Code).FirstOrDefault();
        if (diagnostic is null)
        {
            return result;
        }

        var byCell = cellDegradations
            .GroupBy(entry => entry.Cell)
            .ToDictionary(group => group.Key, group => group.First().Code);
        return result with
        {
            UnresolvedCells = Annotate(result.UnresolvedCells, byCell),
            AmbiguousCells = Annotate(result.AmbiguousCells, byCell),
            IsPartial = true,
            DiagnosticCode = diagnostic,
        };
    }

    private static IReadOnlyList<ContainerCellIssue> Annotate(
        IReadOnlyList<ContainerCellIssue> issues,
        IReadOnlyDictionary<(int Row, int Column), string> degradations) =>
        issues
            .Select(issue => degradations.TryGetValue((issue.Row, issue.Column), out var code)
                ? issue with { Reason = issue.Reason + "; ocr=" + code }
                : issue)
            .ToArray();

    private static PixelRect Inset(PixelRect bounds)
    {
        var horizontal = Math.Min(Math.Max(2, bounds.Width / 40), Math.Max(0, (bounds.Width - 1) / 2));
        var vertical = Math.Min(Math.Max(2, bounds.Height / 40), Math.Max(0, (bounds.Height - 1) / 2));
        return new(
            bounds.X + horizontal,
            bounds.Y + vertical,
            Math.Max(1, bounds.Width - (horizontal * 2)),
            Math.Max(1, bounds.Height - (vertical * 2)));
    }

    private static List<RecognitionCandidate> ResolveLines(
        FuzzyCanonicalItemResolver resolver,
        IReadOnlyList<OcrLine> lines,
        CancellationToken cancellationToken)
    {
        var candidates = new List<RecognitionCandidate>();
        foreach (var line in lines)
        {
            // Each line is a fuzzy search of the catalog, over as many lines as a provider returned.
            cancellationToken.ThrowIfCancellationRequested();
            candidates.AddRange(ResolveLine(resolver, line));
        }

        return candidates;
    }

    private static IEnumerable<RecognitionCandidate> ResolveLine(
        FuzzyCanonicalItemResolver resolver,
        OcrLine line)
    {
        var quantityMatch = QuantitySuffix.Match(line.Text);
        var itemText = quantityMatch.Success ? line.Text[..quantityMatch.Index] : line.Text;
        int? quantity = quantityMatch.Success &&
                        int.TryParse(quantityMatch.Groups["quantity"].Value, out var parsed)
            ? parsed
            : null;
        return resolver.Resolve(itemText, line.Confidence, limit: 3, line.Bounds).Candidates
            .Select(candidate => candidate with { Quantity = quantity });
    }

    private static ContainerScanResult Empty(string diagnosticCode) => new(
        [],
        0,
        0,
        [],
        Confidence.Unknown,
        [],
        [],
        true,
        diagnosticCode);
}
