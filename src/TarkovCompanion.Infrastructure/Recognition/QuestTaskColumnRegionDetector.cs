using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>Finds quest-name text inside a centred Character › TASKS panel.</summary>
/// <remarks>
/// Ultrawide screenshots put the game menu in a centred panel and leave scenery on both sides.
/// The panel is found from its bright top navigation strip. SIDE and OPERATIONAL are tables, so
/// their Task column is the widest span between persistent vertical separators. STORY is a
/// different screen: only the one-line chapter title is useful, not its briefing or objectives.
/// In both layouts the item grids on the right are never sent to OCR.
/// </remarks>
public sealed class QuestTaskColumnRegionDetector : IQuestTaskColumnRegionDetector
{
    public QuestScreenshotTextRegion Detect(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 || image.Stride <= 0 ||
            image.Format is not (PixelFormat.Bgra8888 or PixelFormat.Rgba8888))
        {
            throw new ArgumentException("A 32-bit screenshot is required.", nameof(image));
        }

        var pixels = image.Pixels.Span;
        if (pixels.Length < image.Stride * image.Height)
        {
            throw new ArgumentException("The screenshot pixel buffer is incomplete.", nameof(image));
        }

        var panel = FindPanel(image, pixels);
        var taskSeparators = FindTaskSeparators(image, pixels, panel);
        if (taskSeparators is not { } separators)
        {
            var storyLeft = panel.Left + (int)(panel.Width * 0.08);
            var storyRight = panel.Left + (int)(panel.Width * 0.58);
            var storyTop = Math.Clamp((int)Math.Round(image.Height * 0.152), 0, image.Height - 1);
            var storyBottom = Math.Clamp((int)Math.Round(image.Height * 0.205), storyTop + 1, image.Height);
            return new(
                QuestScreenshotLayout.StoryChapter,
                new PixelRect(storyLeft, storyTop, storyRight - storyLeft, storyBottom - storyTop));
        }

        var (left, right) = separators;
        var inset = Math.Max(3, panel.Width / 500);
        var top = Math.Clamp((int)Math.Round(image.Height * 0.135), 0, image.Height - 1);
        var bottom = Math.Clamp((int)Math.Round(image.Height * 0.965), top + 1, image.Height);
        return new(
            FindTaskListLayout(image, pixels, panel),
            new PixelRect(
                Math.Clamp(left + inset, 0, image.Width - 1),
                top,
                Math.Max(1, Math.Min(image.Width, right - inset) - (left + inset)),
                bottom - top));
    }

    private static QuestScreenshotLayout FindTaskListLayout(
        CapturedImage image,
        ReadOnlySpan<byte> pixels,
        PanelBounds panel)
    {
        var top = Math.Clamp((int)(image.Height * 0.048), 0, image.Height - 1);
        var bottom = Math.Clamp((int)(image.Height * 0.085), top + 1, image.Height);
        var side = MeanLuma(image, pixels, panel, 0.08, 0.15, top, bottom);
        var operational = MeanLuma(image, pixels, panel, 0.155, 0.27, top, bottom);
        return operational > side + 8
            ? QuestScreenshotLayout.OperationalTaskList
            : QuestScreenshotLayout.SideTaskList;
    }

    private static double MeanLuma(
        CapturedImage image,
        ReadOnlySpan<byte> pixels,
        PanelBounds panel,
        double leftRatio,
        double rightRatio,
        int top,
        int bottom)
    {
        var left = panel.Left + (int)(panel.Width * leftRatio);
        var right = panel.Left + (int)(panel.Width * rightRatio);
        double sum = 0;
        var count = 0;
        for (var y = top; y < bottom; y += 3)
        {
            for (var x = left; x < right; x += 3)
            {
                sum += Luma(image, pixels, x, y);
                count++;
            }
        }

        return sum / Math.Max(1, count);
    }

    private static PanelBounds FindPanel(CapturedImage image, ReadOnlySpan<byte> pixels)
    {
        var sampleBottom = Math.Max(8, image.Height / 10);
        var scores = new double[image.Width];
        for (var x = 0; x < image.Width; x++)
        {
            double sum = 0;
            var count = 0;
            for (var y = 0; y < sampleBottom; y += 4)
            {
                sum += Luma(image, pixels, x, y);
                count++;
            }

            scores[x] = sum / count;
        }

        var smoothRadius = Math.Max(2, image.Width / 960);
        var smoothed = Smooth(scores, smoothRadius);
        var edgeWidth = Math.Max(1, image.Width / 8);
        var outside = smoothed.Take(edgeWidth).Concat(smoothed.TakeLast(edgeWidth)).Average();
        var centerWidth = Math.Max(1, image.Width / 5);
        var centerStart = (image.Width - centerWidth) / 2;
        var center = smoothed.Skip(centerStart).Take(centerWidth).Average();
        var threshold = outside + ((center - outside) * 0.45);

        var middle = image.Width / 2;
        var left = middle;
        var misses = 0;
        while (left > 0 && misses < Math.Max(6, image.Width / 320))
        {
            misses = smoothed[left] >= threshold ? 0 : misses + 1;
            left--;
        }

        var right = middle;
        misses = 0;
        while (right < image.Width - 1 && misses < Math.Max(6, image.Width / 320))
        {
            misses = smoothed[right] >= threshold ? 0 : misses + 1;
            right++;
        }

        var tolerance = Math.Max(6, image.Width / 320);
        left += tolerance;
        right -= tolerance;
        var width = right - left;
        if (center <= outside + 2 || width < image.Width * 0.35 || width > image.Width * 0.75)
        {
            width = Math.Min(image.Width, (int)Math.Round(image.Height * (16d / 9d)));
            left = (image.Width - width) / 2;
            right = left + width;
        }

        return new(left, right);
    }

    private static (int Left, int Right)? FindTaskSeparators(
        CapturedImage image,
        ReadOnlySpan<byte> pixels,
        PanelBounds panel)
    {
        var start = panel.Left + (int)(panel.Width * 0.04);
        var end = panel.Left + (int)(panel.Width * 0.68);
        var yStart = (int)(image.Height * 0.14);
        // OPERATIONAL may have only three rows. Restrict the continuity measurement to the
        // upper rows so its short list is still recognised as a table.
        var yEnd = (int)(image.Height * 0.43);
        var edges = new double[image.Width];
        var continuity = new double[image.Width];
        for (var x = Math.Max(1, start); x < Math.Min(image.Width, end); x++)
        {
            double sum = 0;
            var count = 0;
            var edgeRows = 0;
            for (var y = yStart; y < yEnd; y += 3)
            {
                var edge = Math.Abs(Luma(image, pixels, x, y) - Luma(image, pixels, x - 1, y));
                sum += edge;
                if (edge >= 8)
                {
                    edgeRows++;
                }

                count++;
            }

            edges[x] = sum / Math.Max(1, count);
            continuity[x] = (double)edgeRows / Math.Max(1, count);
        }

        var candidates = new List<(int X, double Score)>();
        var minimumDistance = Math.Max(12, panel.Width / 55);
        for (var x = start + 1; x < end - 1; x++)
        {
            if (edges[x] < edges[x - 1] || edges[x] < edges[x + 1])
            {
                continue;
            }

            if (continuity[x] < 0.35)
            {
                continue;
            }

            if (candidates.Count > 0 && x - candidates[^1].X < minimumDistance)
            {
                if (edges[x] > candidates[^1].Score)
                {
                    candidates[^1] = (x, edges[x]);
                }

                continue;
            }

            candidates.Add((x, edges[x]));
        }

        var strongest = candidates
            .OrderByDescending(candidate => candidate.Score)
            .Take(12)
            .OrderBy(candidate => candidate.X)
            .ToArray();
        var best = strongest
            .SelectMany((left, index) => strongest.Skip(index + 1).Select(right => (Left: left, Right: right)))
            .Where(pair =>
                // The narrow Type and Class columns sit immediately before Task. Accepting
                // their separators made a visually wider span win and sent a class glyph to
                // OCR on the real 3840x1080 TASKS frames.
                pair.Left.X >= panel.Left + (panel.Width * 0.11) &&
                pair.Left.X <= panel.Left + (panel.Width * 0.20) &&
                pair.Right.X - pair.Left.X >= panel.Width * 0.18 &&
                pair.Right.X - pair.Left.X <= panel.Width * 0.34)
            .OrderByDescending(pair =>
                (pair.Right.X - pair.Left.X) + ((pair.Left.Score + pair.Right.Score) * panel.Width * 0.02))
            .FirstOrDefault();

        return best == default ? null : (best.Left.X, best.Right.X);
    }

    private static double[] Smooth(IReadOnlyList<double> values, int radius)
    {
        var result = new double[values.Count];
        double sum = 0;
        var left = 0;
        var right = 0;
        for (var index = 0; index < values.Count; index++)
        {
            while (right < values.Count && right <= index + radius)
            {
                sum += values[right++];
            }

            while (left < index - radius)
            {
                sum -= values[left++];
            }

            result[index] = sum / Math.Max(1, right - left);
        }

        return result;
    }

    private static double Luma(CapturedImage image, ReadOnlySpan<byte> pixels, int x, int y)
    {
        var offset = (y * image.Stride) + (x * 4);
        var first = pixels[offset];
        var green = pixels[offset + 1];
        var third = pixels[offset + 2];
        var red = image.Format == PixelFormat.Bgra8888 ? third : first;
        var blue = image.Format == PixelFormat.Bgra8888 ? first : third;
        return (red * 0.2126) + (green * 0.7152) + (blue * 0.0722);
    }

    private readonly record struct PanelBounds(int Left, int Right)
    {
        public int Width => Right - Left;
    }
}
