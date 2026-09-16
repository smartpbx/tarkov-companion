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
    /// <summary>Finds a regular grid, checking cancellation in bounded chunks of work.</summary>
    /// <remarks>
    /// The algorithm is #273's. The token is #299's: this walks every column and every row of the
    /// frame and then searches every pair of line positions, and it used to run before any OCR
    /// deadline existed and without a way to stop it.
    /// </remarks>
    public ContainerGridSpec? Detect(CapturedImage image, CancellationToken cancellationToken = default)
    {
        // Refused over the pixel ceiling before the first read. Providers already refused such a
        // frame, but this walks it before any provider is asked.
        CapturedImagePixels.Validate(image, CapturedImagePixels.MaximumPixels);
        cancellationToken.ThrowIfCancellationRequested();
        var check = new PixelCancellationCheck(cancellationToken);
        var vertical = SelectRegularRun(FindLinePositions(image, vertical: true, ref check), cancellationToken);
        var horizontal = SelectRegularRun(FindLinePositions(image, vertical: false, ref check), cancellationToken);
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

    private static IReadOnlyList<int> FindLinePositions(
        CapturedImage image,
        bool vertical,
        ref PixelCancellationCheck check)
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
                check.Read(2);
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

    private static IReadOnlyList<int> SelectRegularRun(
        IReadOnlyList<int> positions,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<int> best = [];
        for (var first = 0; first < positions.Count; first++)
        {
            for (var second = first + 1; second < positions.Count; second++)
            {
                // No pixels here, but a noisy frame yields hundreds of positions and every pair
                // extends a run by scanning all of them again.
                cancellationToken.ThrowIfCancellationRequested();
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
                    cancellationToken.ThrowIfCancellationRequested();
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
