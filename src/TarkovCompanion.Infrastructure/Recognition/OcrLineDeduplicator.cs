using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>The lines left after merging overlapping OCR passes.</summary>
/// <param name="Lines">Deduplicated lines in reading order.</param>
/// <param name="IsExhaustive">
/// False when the comparison budget ran out and the remaining lines were kept without being
/// compared. Evidence is never dropped to stay inside the budget; the shortfall is reported.
/// </param>
internal sealed record OcrLineMerge(IReadOnlyList<OcrLine> Lines, bool IsExhaustive);

/// <summary>
/// Deterministically merges the same OCR line observed by overlapping passes. Text and source
/// geometry both have to agree, so two legitimate repeated labels elsewhere in a frame remain
/// separate.
/// </summary>
/// <remarks>
/// The first version compared every candidate with every line already kept. That is quadratic
/// in whatever a provider returns, and provider output is not ours to bound. A duplicate always
/// intersects the line it duplicates, so a candidate is now compared only with kept lines that
/// share a 64-pixel grid cell with it. A line spanning more cells than a caption would goes on a
/// short list every candidate checks instead of into hundreds of cells. Each visited entry
/// spends from a budget linear in the input, so stacked boxes from a misbehaving provider cost
/// bounded work and an explicit incomplete merge rather than a stall.
///
/// This file is also compiled into TarkovCompanion.Platform.Windows.Ocr, so tiled Windows reads
/// and coordinated full/contextual passes cannot drift onto two different duplicate rules.
/// </remarks>
internal static class OcrLineDeduplicator
{
    private const int CellSize = 64;
    private const long MaximumIndexedCells = 64;
    private const long VisitsPerLine = 64;
    private const long MinimumVisitBudget = 4_096;

    public static OcrLineMerge Merge(params IReadOnlyList<OcrLine>[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var ordered = sources
            .SelectMany((source, sourceOrdinal) => source.Select((line, lineOrdinal) =>
                new Candidate(line, Normalize(line.Text), sourceOrdinal, lineOrdinal)))
            .OrderBy(candidate => candidate.Line.Bounds.Y)
            .ThenBy(candidate => candidate.Line.Bounds.X)
            .ThenBy(candidate => candidate.Text, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SourceOrdinal)
            .ThenBy(candidate => candidate.LineOrdinal)
            .ToArray();
        var selection = new Selection(ordered.Length);
        foreach (var candidate in ordered)
        {
            selection.Add(candidate);
        }

        return new(
            selection.Kept
                .Select(candidate => candidate.Line)
                .OrderBy(line => line.Bounds.Y)
                .ThenBy(line => line.Bounds.X)
                .ThenBy(line => line.Text, StringComparer.Ordinal)
                .ToArray(),
            selection.IsExhaustive);
    }

    /// <summary>Whether a line can ever be a duplicate: it has text and a positive area.</summary>
    private static bool IsComparable(Candidate candidate) =>
        candidate.Text.Length > 0 &&
        candidate.Line.Bounds.Width > 0 &&
        candidate.Line.Bounds.Height > 0;

    private static bool IsDuplicate(Candidate left, Candidate right)
    {
        var a = left.Line.Bounds;
        var b = right.Line.Bounds;
        var intersectionWidth = Math.Max(
            0L,
            Math.Min((long)a.X + a.Width, (long)b.X + b.Width) - Math.Max(a.X, b.X));
        var intersectionHeight = Math.Max(
            0L,
            Math.Min((long)a.Y + a.Height, (long)b.Y + b.Height) - Math.Max(a.Y, b.Y));
        var smaller = Math.Min((long)a.Width * a.Height, (long)b.Width * b.Height);
        if (smaller <= 0 || intersectionWidth * intersectionHeight * 2 < smaller)
        {
            return false;
        }

        return string.Equals(left.Text, right.Text, StringComparison.Ordinal) ||
            (Math.Min(left.Text.Length, right.Text.Length) * 10L >= Math.Max(left.Text.Length, right.Text.Length) * 6L &&
             (left.Text.Contains(right.Text, StringComparison.Ordinal) ||
              right.Text.Contains(left.Text, StringComparison.Ordinal)));
    }

    private static bool IsBetter(Candidate candidate, Candidate current)
    {
        if (candidate.Text.Length != current.Text.Length)
        {
            return candidate.Text.Length > current.Text.Length;
        }

        if (candidate.Line.Confidence?.Value != current.Line.Confidence?.Value)
        {
            // Unknown does not become zero. A reported confidence can break a tie between two
            // otherwise equal duplicate reads, but an unscored line is not demoted below a
            // provider-scored zero by substituting a number for its absence.
            if (candidate.Line.Confidence is null || current.Line.Confidence is null)
            {
                return candidate.Line.Confidence is null;
            }

            return candidate.Line.Confidence.Value.Value > current.Line.Confidence.Value.Value;
        }

        var candidateArea = (long)candidate.Line.Bounds.Width * candidate.Line.Bounds.Height;
        var currentArea = (long)current.Line.Bounds.Width * current.Line.Bounds.Height;
        return candidateArea != currentArea
            ? candidateArea > currentArea
            : candidate.SourceOrdinal != current.SourceOrdinal
                ? candidate.SourceOrdinal < current.SourceOrdinal
                : candidate.LineOrdinal < current.LineOrdinal;
    }

    private static string Normalize(string text) => new(
        text.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    /// <summary>The grid cells a positive-area rectangle covers, or false when it covers too many to index.</summary>
    private static bool TryCells(PixelRect bounds, out long left, out long top, out long right, out long bottom)
    {
        left = FloorCell(bounds.X);
        top = FloorCell(bounds.Y);
        right = FloorCell((long)bounds.X + bounds.Width - 1);
        bottom = FloorCell((long)bounds.Y + bounds.Height - 1);
        return (right - left + 1) * (bottom - top + 1) <= MaximumIndexedCells;
    }

    private static long FloorCell(long coordinate) => coordinate >= 0
        ? coordinate / CellSize
        : ((coordinate + 1) / CellSize) - 1;

    private sealed record Candidate(OcrLine Line, string Text, int SourceOrdinal, int LineOrdinal);

    private readonly record struct Entry(int Index, int Version);

    /// <summary>
    /// The lines kept so far and a grid over their bounds. A replaced line gets a new version, so
    /// its old grid entries are skipped rather than searched for and removed.
    /// </summary>
    private sealed class Selection(int capacity)
    {
        private readonly List<Candidate> _kept = new(capacity);
        private readonly List<int> _versions = new(capacity);
        private readonly int[] _visited = new int[capacity];
        private readonly Dictionary<(long X, long Y), List<Entry>> _cells = new();
        private readonly List<Entry> _wide = new();
        private long _budget = Math.Max(MinimumVisitBudget, capacity * VisitsPerLine);
        private int _stamp;

        public IReadOnlyList<Candidate> Kept => _kept;

        public bool IsExhaustive { get; private set; } = true;

        public void Add(Candidate candidate)
        {
            if (!IsExhaustive || !IsComparable(candidate))
            {
                _kept.Add(candidate);
                _versions.Add(0);
                return;
            }

            _stamp++;
            var duplicate = -1;
            if (!TryFindFirstDuplicate(candidate, ref duplicate))
            {
                IsExhaustive = false;
                _kept.Add(candidate);
                _versions.Add(0);
                return;
            }

            if (duplicate < 0)
            {
                _kept.Add(candidate);
                _versions.Add(0);
                Index(_kept.Count - 1);
            }
            else if (IsBetter(candidate, _kept[duplicate]))
            {
                _kept[duplicate] = candidate;
                _versions[duplicate]++;
                Index(duplicate);
            }
        }

        /// <summary>
        /// Finds the earliest kept duplicate, matching the order a full scan would have chosen.
        /// Returns false when the budget ran out before the search finished.
        /// </summary>
        private bool TryFindFirstDuplicate(Candidate candidate, ref int duplicate)
        {
            if (!TryCells(candidate.Line.Bounds, out var left, out var top, out var right, out var bottom))
            {
                for (var index = 0; index < _kept.Count; index++)
                {
                    if (!Visit(new Entry(index, _versions[index]), candidate, ref duplicate))
                    {
                        return false;
                    }
                }

                return true;
            }

            foreach (var entry in _wide)
            {
                if (!Visit(entry, candidate, ref duplicate))
                {
                    return false;
                }
            }

            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    if (!_cells.TryGetValue((x, y), out var entries))
                    {
                        continue;
                    }

                    foreach (var entry in entries)
                    {
                        if (!Visit(entry, candidate, ref duplicate))
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        private bool Visit(Entry entry, Candidate candidate, ref int duplicate)
        {
            if (--_budget < 0)
            {
                return false;
            }

            if (_versions[entry.Index] != entry.Version || _visited[entry.Index] == _stamp)
            {
                return true;
            }

            _visited[entry.Index] = _stamp;
            if ((duplicate < 0 || entry.Index < duplicate) && IsDuplicate(_kept[entry.Index], candidate))
            {
                duplicate = entry.Index;
            }

            return true;
        }

        private void Index(int index)
        {
            var entry = new Entry(index, _versions[index]);
            if (!TryCells(_kept[index].Line.Bounds, out var left, out var top, out var right, out var bottom))
            {
                _wide.Add(entry);
                return;
            }

            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    if (!_cells.TryGetValue((x, y), out var entries))
                    {
                        entries = new List<Entry>();
                        _cells.Add((x, y), entries);
                    }

                    entries.Add(entry);
                }
            }
        }
    }
}
