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
        if (!CapturedImagePixels.Intersects(frame, grid.Bounds))
        {
            throw new ArgumentOutOfRangeException(nameof(grid), "Grid must intersect the image.");
        }

        if (grid.Bounds.X < 0 ||
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

    private static PixelRect GetCellBounds(
        CapturedImage image,
        ContainerGridSpec grid,
        int row,
        int column)
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

        var occupied = segments.Where(segment => segment.IsOccupied).ToArray();
        var included = candidates
            .Where(candidate =>
                candidate.Confidence.Value >= RecognitionPolicy.AmbiguityThreshold &&
                candidate.Bounds is not null)
            .Select(candidate => new CandidateCells(
                candidate,
                occupied.Where(segment => Overlaps(candidate.Bounds!, segment.Bounds)).ToArray()))
            .Where(candidate => candidate.Cells.Count > 0)
            .ToArray();
        var values = valuations
            .GroupBy(valuation => valuation.CanonicalId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().ValueRoubles, StringComparer.Ordinal);

        var aggregate = included
            .GroupBy(item => item.Candidate.CanonicalId, StringComparer.Ordinal)
            .Select(group => Aggregate(group.Key, group.ToArray()))
            .OrderByDescending(candidate => candidate.Confidence.Value)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .ToArray();
        var occupiedKeys = included
            .SelectMany(item => item.Cells)
            .Select(cell => (cell.Row, cell.Column))
            .Distinct()
            .ToArray();
        var approximateValue = aggregate.Sum(candidate =>
            values.GetValueOrDefault(candidate.CanonicalId) * (long)(candidate.Quantity ?? 1));
        var dropFirst = included
            .GroupBy(item => item.Candidate.CanonicalId, StringComparer.Ordinal)
            .Where(group => values.ContainsKey(group.Key))
            .Select(group => new
            {
                Id = group.Key,
                ValuePerSlot = (double)values[group.Key] / Math.Max(1, group.SelectMany(item => item.Cells).DistinctBy(cell => (cell.Row, cell.Column)).Count()),
            })
            .OrderBy(item => item.ValuePerSlot)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Id)
            .ToArray();
        var confidence = aggregate.Length == 0
            ? Confidence.Unknown
            : new Confidence(aggregate.Average(candidate => candidate.Confidence.Value));

        return new(aggregate, approximateValue, occupiedKeys.Length, dropFirst, confidence);
    }

    private static RecognitionCandidate Aggregate(string canonicalId, IReadOnlyList<CandidateCells> matches)
    {
        var best = matches
            .Select(match => match.Candidate)
            .OrderByDescending(candidate => candidate.Confidence.Value)
            .First();
        var quantity = matches.Sum(match => match.Candidate.Quantity ?? 1);
        var bounds = Union(matches.Select(match => match.Candidate.Bounds!).ToArray());
        return best with
        {
            Bounds = bounds,
            Quantity = quantity,
            Evidence = $"container-beta; observations={matches.Count}; {best.Evidence}",
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

    private sealed record CandidateCells(
        RecognitionCandidate Candidate,
        IReadOnlyList<ContainerSegment> Cells);
}
