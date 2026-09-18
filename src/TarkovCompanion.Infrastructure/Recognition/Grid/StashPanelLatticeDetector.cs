using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>
/// Finds the stash panel's lattice by solving for where it is, not for what it is.
/// </summary>
/// <remarks>
/// <para>
/// The general line detector looks for any regular run of contrast, and on a packed stash the most
/// regular thing on screen is the artwork. Measured on a painted 34-row stash (package 40): it
/// returned 25-pixel and 37-pixel lattices for frames whose cells are 63 pixels, and nothing
/// downstream survived that.
/// </para>
/// <para>
/// The stash is the one grid whose shape is already known. It is ten columns wide and its cells
/// are 63 pixels on a 1080-tall frame (<see cref="StashGrid.PitchInHeights"/>, measured on a real
/// screenshot), so the only unknowns are the panel's left edge, the row phase, and how many whole
/// rows are showing. Eleven lines at an exact pitch are scored together, which is what lets an
/// item hide some of them without moving the answer - the same reasoning as
/// <see cref="StashGrid.FindPhase"/>, extended to find the panel rather than be told it.
/// </para>
/// <para>
/// A line is scored as a ridge or a valley against the pixels either side of it, in either
/// polarity. The one real measurement says the lines are darker than the cells; nothing here
/// depends on that staying true.
/// </para>
/// <para>
/// A row that is cut by the top or bottom of the scroll viewport is left out: its cells are not
/// whole, and a half-drawn item read as an item is a wrong answer an overlap would then have to
/// argue with.
/// </para>
/// </remarks>
public sealed class StashPanelLatticeDetector
{
    /// <summary>The stash is ten cells wide in every edition of the game.</summary>
    public const int Columns = 10;

    /// <summary>How much stronger the lines must be than whatever else is in the panel.</summary>
    private const double MinimumCombRatio = 2.5;

    /// <summary>The weakest mean ridge, in luminance levels, that still counts as a drawn line.</summary>
    private const double MinimumLineStrength = 2.0;

    public ContainerGridSpec? Detect(CapturedImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        CapturedImagePixels.Validate(image, CapturedImagePixels.MaximumPixels);
        cancellationToken.ThrowIfCancellationRequested();

        var pitch = StashGrid.Pitch(image);
        var reach = Math.Max(2, pitch / 21);
        var panelWidth = pitch * Columns;
        if (image.Width < panelWidth + (2 * reach) + 1 || image.Height < pitch * 3)
        {
            return null;
        }

        var check = new PixelCancellationCheck(cancellationToken);
        var columnStrength = LineStrengths(
            Profile(image, 0, image.Width, 0, image.Height, vertical: true, ref check),
            reach);
        if (FindPanelLeft(columnStrength, pitch, reach) is not { } panel)
        {
            return null;
        }

        var (left, combStrength) = panel;

        var rowStrength = LineStrengths(
            Profile(image, left, panelWidth + 1, 0, image.Height, vertical: false, ref check),
            reach);
        if (FindPhase(rowStrength, pitch, reach) is not { } phase)
        {
            return null;
        }

        // Whole rows only, and only where the panel's two outer edges are both drawn: those are
        // the two an item can never cover.
        var required = Math.Max(MinimumLineStrength, combStrength * 0.4);
        var bestStart = -1;
        var bestLength = 0;
        var runStart = -1;
        var bandCount = (image.Height - phase) / pitch;
        for (var band = 0; band <= bandCount; band++)
        {
            var top = phase + (band * pitch);
            var inside = band < bandCount && IsWholeRow(image, left, panelWidth, top, pitch, reach, required, ref check);
            if (inside)
            {
                runStart = runStart < 0 ? band : runStart;
                continue;
            }

            if (runStart >= 0 && band - runStart > bestLength)
            {
                bestStart = runStart;
                bestLength = band - runStart;
            }

            runStart = -1;
        }

        return bestLength < 2
            ? null
            : new(new PixelRect(left, phase + (bestStart * pitch), panelWidth, bestLength * pitch), Columns, bestLength);
    }

    private static (int Left, double CombStrength)? FindPanelLeft(double[] strength, int pitch, int reach)
    {
        var span = pitch * Columns;
        var best = -1;
        var bestScore = 0d;
        for (var left = reach; left + span < strength.Length - reach; left++)
        {
            var score = 0d;
            for (var line = 0; line <= Columns; line++)
            {
                score += strength[left + (line * pitch)];
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = left;
            }
        }

        if (best < 0)
        {
            return null;
        }

        var comb = bestScore / (Columns + 1);
        var background = MeanOffComb(strength, best, best + span, best, pitch, reach);
        return comb >= MinimumLineStrength && comb >= background * MinimumCombRatio ? (best, comb) : null;
    }

    private static int? FindPhase(double[] strength, int pitch, int reach)
    {
        var best = -1;
        var bestScore = 0d;
        for (var phase = 0; phase < pitch; phase++)
        {
            var score = 0d;
            var count = 0;
            for (var at = phase; at < strength.Length; at += pitch)
            {
                score += strength[at];
                count++;
            }

            score /= Math.Max(1, count);
            if (score > bestScore)
            {
                bestScore = score;
                best = phase;
            }
        }

        if (best < 0)
        {
            return null;
        }

        var background = MeanOffComb(strength, 0, strength.Length - 1, best, pitch, reach);
        return bestScore >= MinimumLineStrength && bestScore >= background * MinimumCombRatio ? best : null;
    }

    private static double MeanOffComb(double[] strength, int from, int to, int phase, int pitch, int reach)
    {
        var total = 0d;
        var count = 0;
        for (var at = from; at <= to && at < strength.Length; at++)
        {
            var distance = (((at - phase) % pitch) + pitch) % pitch;
            if (Math.Min(distance, pitch - distance) <= reach)
            {
                continue;
            }

            total += strength[at];
            count++;
        }

        return count == 0 ? 0 : total / count;
    }

    private static bool IsWholeRow(
        CapturedImage image,
        int left,
        int panelWidth,
        int top,
        int pitch,
        int reach,
        double required,
        ref PixelCancellationCheck check)
    {
        if (top + pitch > image.Height)
        {
            return false;
        }

        var half = pitch / 2;
        return OuterLinesDrawn(image, left, panelWidth, top + reach, half - reach, reach, required, ref check) &&
               OuterLinesDrawn(image, left, panelWidth, top + half, half - reach, reach, required, ref check);
    }

    private static bool OuterLinesDrawn(
        CapturedImage image,
        int left,
        int panelWidth,
        int top,
        int height,
        int reach,
        double required,
        ref PixelCancellationCheck check)
    {
        foreach (var x in (ReadOnlySpan<int>)[left, left + panelWidth])
        {
            if (x - reach < 0 || x + reach >= image.Width)
            {
                return false;
            }

            double line = 0;
            double before = 0;
            double after = 0;
            for (var y = top; y < top + height; y++)
            {
                check.Read(3);
                line += CapturedImagePixels.GetLuminance(image, x, y);
                before += CapturedImagePixels.GetLuminance(image, x - reach, y);
                after += CapturedImagePixels.GetLuminance(image, x + reach, y);
            }

            if (LineStrength(line / height, before / height, after / height) < required)
            {
                return false;
            }
        }

        return true;
    }

    private static double[] Profile(
        CapturedImage image,
        int x,
        int width,
        int y,
        int height,
        bool vertical,
        ref PixelCancellationCheck check)
    {
        var length = vertical ? width : height;
        var profile = new double[length];
        for (var at = 0; at < length; at++)
        {
            double total = 0;
            var count = 0;
            if (vertical)
            {
                for (var row = y; row < y + height; row += 2)
                {
                    check.Read();
                    total += CapturedImagePixels.GetLuminance(image, x + at, row);
                    count++;
                }
            }
            else
            {
                for (var column = x; column < x + width && column < image.Width; column += 2)
                {
                    check.Read();
                    total += CapturedImagePixels.GetLuminance(image, column, y + at);
                    count++;
                }
            }

            profile[at] = count == 0 ? 0 : total / count;
        }

        return profile;
    }

    private static double[] LineStrengths(double[] profile, int reach)
    {
        var strength = new double[profile.Length];
        for (var at = reach; at < profile.Length - reach; at++)
        {
            strength[at] = LineStrength(profile[at], profile[at - reach], profile[at + reach]);
        }

        return strength;
    }

    /// <summary>
    /// How far a value stands out from both neighbours, or half the step across it.
    /// </summary>
    /// <remarks>
    /// An interior line is a ridge or a valley. The panel's own outer edge need not be: a dark
    /// line with the darker screen beyond it is a step, and scoring only ridges made the comb one
    /// pitch to the right tie with the true one.
    /// </remarks>
    private static double LineStrength(double value, double before, double after)
    {
        var fromBefore = value - before;
        var fromAfter = value - after;
        var ridge = Math.Sign(fromBefore) == Math.Sign(fromAfter)
            ? Math.Min(Math.Abs(fromBefore), Math.Abs(fromAfter))
            : 0;
        return Math.Max(ridge, Math.Abs(before - after) * 0.5);
    }
}
