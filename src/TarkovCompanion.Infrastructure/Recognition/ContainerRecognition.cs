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
    public ContainerGridSpec? Detect(CapturedImage image)
    {
        CapturedImagePixels.Validate(image);
        var vertical = SelectRegularRun(FindLinePositions(image, vertical: true));
        var horizontal = SelectRegularRun(FindLinePositions(image, vertical: false));
        if (vertical.Count < 3 || horizontal.Count < 3)
        {
            return null;
        }

        return new(
            new PixelRect(
                vertical[0],
                horizontal[0],
                vertical[^1] - vertical[0],
                horizontal[^1] - horizontal[0]),
            vertical.Count - 1,
            horizontal.Count - 1);
    }

    private static IReadOnlyList<int> FindLinePositions(CapturedImage image, bool vertical)
    {
        var axisLength = vertical ? image.Width : image.Height;
        var crossLength = vertical ? image.Height : image.Width;
        var step = Math.Max(1, crossLength / 360);
        var coverage = new double[axisLength];
        for (var axis = 0; axis < axisLength; axis++)
        {
            var contrasting = 0;
            var count = 0;
            for (var cross = 0; cross < crossLength; cross += step)
            {
                var x = vertical ? axis : cross;
                var y = vertical ? cross : axis;
                var neighborAxis = Math.Clamp(
                    axis + (axis < axisLength - 2 ? 2 : -2),
                    0,
                    axisLength - 1);
                var neighborX = vertical ? neighborAxis : cross;
                var neighborY = vertical ? cross : neighborAxis;
                var value = CapturedImagePixels.GetLuminance(image, x, y);
                var neighbor = CapturedImagePixels.GetLuminance(image, neighborX, neighborY);
                if (Math.Abs(value - neighbor) >= 45)
                {
                    contrasting++;
                }

                count++;
            }

            coverage[axis] = contrasting / (double)Math.Max(1, count);
        }

        var raw = Enumerable.Range(0, axisLength)
            .Where(axis => coverage[axis] > 0.18)
            .ToArray();
        var positions = new List<int>();
        for (var index = 0; index < raw.Length;)
        {
            var start = raw[index];
            var end = start;
            while (index + 1 < raw.Length && raw[index + 1] <= end + 2)
            {
                index++;
                end = raw[index];
            }

            positions.Add((start + end) / 2);
            index++;
        }

        return positions;
    }

    private static IReadOnlyList<int> SelectRegularRun(IReadOnlyList<int> positions)
    {
        IReadOnlyList<int> best = [];
        for (var first = 0; first < positions.Count; first++)
        {
            for (var second = first + 1; second < positions.Count; second++)
            {
                var spacing = positions[second] - positions[first];
                if (spacing < 12)
                {
                    continue;
                }

                var tolerance = Math.Max(2, (int)Math.Round(spacing * 0.08));
                var run = new List<int> { positions[first] };
                var expected = positions[first] + spacing;
                while (TryFindClosest(positions, run[^1], expected, tolerance, out var match))
                {
                    run.Add(match);
                    expected += spacing;
                }

                if (run.Count > best.Count ||
                    (run.Count == best.Count &&
                     run.Count > 0 &&
                     run[^1] - run[0] > best[^1] - best[0]))
                {
                    best = run;
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
        var candidate = positions
            .Where(position => position > after)
            .Select(position => (Position: position, Distance: Math.Abs(position - expected)))
            .OrderBy(value => value.Distance)
            .FirstOrDefault();
        if (candidate.Position > after && candidate.Distance <= tolerance)
        {
            match = candidate.Position;
            return true;
        }

        match = 0;
        return false;
    }
}

public sealed class ContainerGridSegmenter
{
    public IReadOnlyList<ContainerSegment> Segment(CapturedImage image, ContainerGridSpec grid)
    {
        CapturedImagePixels.Validate(image);
        ArgumentNullException.ThrowIfNull(grid);
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
    public ContainerScanResult Analyze(
        IReadOnlyList<ContainerSegment> segments,
        IReadOnlyList<RecognitionCandidate> candidates,
        IReadOnlyList<ContainerItemValuation> valuations)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(valuations);

        var accepted = new List<CandidateCell>();
        var unresolved = new List<ContainerCellIssue>();
        var ambiguous = new List<ContainerCellIssue>();
        foreach (var cell in segments.Where(segment => segment.IsOccupied))
        {
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
    private static readonly Regex QuantitySuffix = new(
        @"(?:\s+(?:x|qty\s*:?)\s*(?<quantity>\d{1,4}))\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly IOcrEngine _ocrEngine;
    private readonly CanonicalItemResolverCache _resolverCache;
    private readonly IItemRepository _items;
    private readonly ContainerGridDetector _gridDetector;
    private readonly ContainerGridSegmenter _segmenter;
    private readonly ContainerScanAnalyzer _analyzer;

    public ContainerRecognitionService(
        IOcrEngine ocrEngine,
        CanonicalItemResolverCache resolverCache,
        IItemRepository items,
        ContainerGridDetector? gridDetector = null,
        ContainerGridSegmenter? segmenter = null,
        ContainerScanAnalyzer? analyzer = null)
    {
        _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        _resolverCache = resolverCache ?? throw new ArgumentNullException(nameof(resolverCache));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _gridDetector = gridDetector ?? new();
        _segmenter = segmenter ?? new();
        _analyzer = analyzer ?? new();
    }

    public async Task<ContainerScanResult> RecognizeAsync(
        CapturedImage image,
        CancellationToken cancellationToken)
    {
        CapturedImagePixels.Validate(image);
        var grid = _gridDetector.Detect(image);
        if (grid is null)
        {
            return Empty("container_grid_not_detected");
        }

        var ocr = await _ocrEngine
            .RecognizeAsync(image, new OcrRequest(ScanContext.Container, grid.Bounds), cancellationToken)
            .ConfigureAwait(false);
        if (!ocr.IsAvailable)
        {
            return Empty(ocr.DiagnosticCode ?? "ocr_provider_unavailable");
        }

        var resolver = await _resolverCache.GetAsync(cancellationToken).ConfigureAwait(false);
        var candidates = ocr.Lines
            .SelectMany(line => ResolveLine(resolver, line))
            .ToArray();
        var valuations = new List<ContainerItemValuation>();
        foreach (var itemId in candidates.Select(candidate => candidate.CanonicalId).Distinct(StringComparer.Ordinal))
        {
            var price = await _items.GetPriceAsync(itemId, cancellationToken).ConfigureAwait(false);
            if (price is not null && price.BestEconomicValue > 0)
            {
                valuations.Add(new(itemId, price.BestEconomicValue));
            }
        }

        return _analyzer.Analyze(_segmenter.Segment(image, grid), candidates, valuations);
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
