using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>
/// Deterministically merges the same OCR line observed by overlapping passes. Text and source
/// geometry both have to agree, so two legitimate repeated labels elsewhere in a frame remain
/// separate.
/// </summary>
internal static class OcrLineDeduplicator
{
    public static IReadOnlyList<OcrLine> Merge(params IEnumerable<OcrLine>[] sources)
    {
        var selected = new List<(OcrLine Line, int SourceOrdinal, int LineOrdinal)>();
        foreach (var candidate in sources
                     .SelectMany((source, sourceOrdinal) => source.Select(
                         (line, lineOrdinal) => (Line: line, SourceOrdinal: sourceOrdinal, LineOrdinal: lineOrdinal)))
                     .OrderBy(candidate => candidate.Line.Bounds.Y)
                     .ThenBy(candidate => candidate.Line.Bounds.X)
                     .ThenBy(candidate => Normalize(candidate.Line.Text), StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.SourceOrdinal)
                     .ThenBy(candidate => candidate.LineOrdinal))
        {
            var duplicate = selected.FindIndex(current => IsDuplicate(current.Line, candidate.Line));
            if (duplicate < 0)
            {
                selected.Add(candidate);
            }
            else if (IsBetter(candidate, selected[duplicate]))
            {
                selected[duplicate] = candidate;
            }
        }

        return selected
            .Select(entry => entry.Line)
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .ThenBy(line => line.Text, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsDuplicate(OcrLine left, OcrLine right)
    {
        var leftText = Normalize(left.Text);
        var rightText = Normalize(right.Text);
        if (leftText.Length == 0 || rightText.Length == 0)
        {
            return false;
        }

        var textMatches = string.Equals(leftText, rightText, StringComparison.Ordinal) ||
            (Math.Min(leftText.Length, rightText.Length) * 10 >= Math.Max(leftText.Length, rightText.Length) * 6 &&
             (leftText.Contains(rightText, StringComparison.Ordinal) ||
              rightText.Contains(leftText, StringComparison.Ordinal)));
        if (!textMatches)
        {
            return false;
        }

        var intersectionWidth = Math.Max(
            0,
            Math.Min(left.Bounds.X + left.Bounds.Width, right.Bounds.X + right.Bounds.Width) -
            Math.Max(left.Bounds.X, right.Bounds.X));
        var intersectionHeight = Math.Max(
            0,
            Math.Min(left.Bounds.Y + left.Bounds.Height, right.Bounds.Y + right.Bounds.Height) -
            Math.Max(left.Bounds.Y, right.Bounds.Y));
        var intersection = (long)intersectionWidth * intersectionHeight;
        var smaller = Math.Min(
            (long)left.Bounds.Width * left.Bounds.Height,
            (long)right.Bounds.Width * right.Bounds.Height);
        return smaller > 0 && intersection * 2 >= smaller;
    }

    private static bool IsBetter(
        (OcrLine Line, int SourceOrdinal, int LineOrdinal) candidate,
        (OcrLine Line, int SourceOrdinal, int LineOrdinal) current)
    {
        var candidateText = Normalize(candidate.Line.Text);
        var currentText = Normalize(current.Line.Text);
        if (candidateText.Length != currentText.Length)
        {
            return candidateText.Length > currentText.Length;
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
}
