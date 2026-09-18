using System.Runtime.InteropServices;
using SkiaSharp;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests.LootScanMeasurement;

internal sealed record PlacedItem(CorpusItem Item, int Row, int Column)
{
    public int Width => Item.Width;

    public int Height => Item.Height;
}

internal enum FrameVariant
{
    /// <summary>1920x1080, icons placed pixel for pixel. The best case nothing real can beat.</summary>
    Pristine1080,

    /// <summary>The same frame darkened by a tenth with a little per-pixel noise.</summary>
    Dimmed1080,

    /// <summary>The 1080p frame resampled to 2560x1440, the way the game scales its interface.</summary>
    Scaled1440,

    /// <summary>The same panel in a 3840x1080 frame, which is what Clayton's game writes.</summary>
    Ultrawide1080,
}

internal sealed record ComposedFrame(
    CapturedImage Image,
    FrameVariant Variant,
    int Rows,
    int Columns,
    double OriginX,
    double OriginY,
    double PitchPixels,
    IReadOnlyList<PlacedItem> Items);

/// <summary>
/// Composes a loot container out of json.tarkov.dev's own grid images, with the truth known by
/// construction.
/// </summary>
/// <remarks>
/// This is a stand-in for the container screenshots nobody has supplied yet, and it flatters the
/// recognizer in every way that matters: the art is the very bytes the reference index is built
/// from, the lattice is exact, nothing is hovered, selected, found-in-raid ticked or half covered
/// by a tooltip, and no scene shows through the panel. A number measured here is a ceiling. What
/// it can still show honestly is every failure that happens even on a perfect picture.
/// The cell pitch is the grid image's own: 63 pixels a cell with shared one-pixel borders.
/// </remarks>
internal static class LootFrameComposer
{
    public const int Pitch = 63;
    private static readonly SKColor Backdrop = new(14, 14, 14);
    private static readonly SKColor EmptyCell = new(24, 25, 25);

    public static ComposedFrame Compose(
        IconCorpus corpus,
        IReadOnlyList<CorpusItem> pool,
        int rows,
        int columns,
        double fill,
        int seed,
        FrameVariant variant)
    {
        var random = new Random(seed);
        var placed = Pack(pool, rows, columns, fill, random);
        var frameWidth = variant == FrameVariant.Ultrawide1080 ? 3840 : 1920;
        const int frameHeight = 1080;
        var originX = (frameWidth / 2) + 300;
        const int originY = 180;

        using var bitmap = new SKBitmap(new SKImageInfo(frameWidth, frameHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(Backdrop);
            using var cellPaint = new SKPaint { Color = EmptyCell, IsAntialias = false };
            using var linePaint = new SKPaint { Color = LineColor(corpus, pool), IsAntialias = false };
            canvas.DrawRect(originX, originY, (columns * Pitch) + 1, (rows * Pitch) + 1, cellPaint);
            for (var column = 0; column <= columns; column++)
            {
                canvas.DrawRect(originX + (column * Pitch), originY, 1, (rows * Pitch) + 1, linePaint);
            }

            for (var row = 0; row <= rows; row++)
            {
                canvas.DrawRect(originX, originY + (row * Pitch), (columns * Pitch) + 1, 1, linePaint);
            }

            foreach (var item in placed)
            {
                using var icon = SKBitmap.Decode(corpus.ReadIcon(item.Item.Id));
                if (icon is null)
                {
                    continue;
                }

                canvas.DrawBitmap(icon, originX + (item.Column * Pitch), originY + (item.Row * Pitch));
            }
        }

        using var final = variant == FrameVariant.Scaled1440
            ? bitmap.Resize(new SKImageInfo(2560, 1440, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
            : bitmap.Copy();
        var pixels = new byte[checked(final.RowBytes * final.Height)];
        Marshal.Copy(final.GetPixels(), pixels, 0, pixels.Length);
        if (variant == FrameVariant.Dimmed1080)
        {
            Dim(pixels, random);
        }

        var image = new CapturedImage(
            pixels,
            final.Width,
            final.Height,
            final.RowBytes,
            PixelFormat.Bgra8888,
            new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero),
            "composed-loot-frame");
        var scale = variant == FrameVariant.Scaled1440 ? 1440d / 1080d : 1d;
        return new(image, variant, rows, columns, originX * scale, originY * scale, Pitch * scale, placed);
    }

    /// <summary>Writes a frame as a PNG, the format the game writes, for the render preview to scan.</summary>
    public static void SavePng(CapturedImage image, string path)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(image.Pixels.ToArray(), 0, bitmap.GetPixels(), image.Pixels.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    /// <summary>The border colour the grid images themselves are drawn with.</summary>
    private static SKColor LineColor(IconCorpus corpus, IReadOnlyList<CorpusItem> pool)
    {
        using var icon = SKBitmap.Decode(corpus.ReadIcon(pool[0].Id));
        return icon?.GetPixel(0, 0) ?? new SKColor(73, 81, 84);
    }

    private static List<PlacedItem> Pack(IReadOnlyList<CorpusItem> pool, int rows, int columns, double fill, Random random)
    {
        var taken = new bool[rows, columns];
        var placed = new List<PlacedItem>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                if (taken[row, column] || random.NextDouble() > fill)
                {
                    continue;
                }

                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var candidate = pool[random.Next(pool.Count)];
                    if (used.Contains(candidate.Id) || !Fits(taken, rows, columns, row, column, candidate.Width, candidate.Height))
                    {
                        continue;
                    }

                    for (var r = row; r < row + candidate.Height; r++)
                    {
                        for (var c = column; c < column + candidate.Width; c++)
                        {
                            taken[r, c] = true;
                        }
                    }

                    used.Add(candidate.Id);
                    placed.Add(new(candidate, row, column));
                    break;
                }
            }
        }

        return placed;
    }

    private static bool Fits(bool[,] taken, int rows, int columns, int row, int column, int width, int height)
    {
        if (row + height > rows || column + width > columns)
        {
            return false;
        }

        for (var r = row; r < row + height; r++)
        {
            for (var c = column; c < column + width; c++)
            {
                if (taken[r, c])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void Dim(byte[] pixels, Random random)
    {
        for (var offset = 0; offset + 3 < pixels.Length; offset += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                var value = (pixels[offset + channel] * 0.9) + random.Next(-4, 5);
                pixels[offset + channel] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
            }
        }
    }
}
