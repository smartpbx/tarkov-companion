using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed record IconFingerprintReference(
    string CanonicalId,
    string DisplayName,
    ulong DifferenceHash);

public sealed class SkiaPerceptualIconMatcher : IIconMatcher
{
    private readonly IReadOnlyList<IconFingerprintReference> _references;

    public SkiaPerceptualIconMatcher(IEnumerable<IconFingerprintReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        _references = references.ToArray();
    }

    public static IconFingerprintReference CreateReference(
        string canonicalId,
        string displayName,
        CapturedImage image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new(canonicalId, displayName, ComputeDifferenceHash(image));
    }

    public Task<IReadOnlyList<RecognitionCandidate>> MatchAsync(
        CapturedImage image,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var hash = ComputeDifferenceHash(image);
        IReadOnlyList<RecognitionCandidate> candidates = _references
            .Select(reference =>
            {
                var distance = BitOperations.PopCount(hash ^ reference.DifferenceHash);
                var similarity = 1d - (distance / 64d);
                return new RecognitionCandidate(
                    reference.CanonicalId,
                    reference.DisplayName,
                    new Confidence(similarity),
                    $"skia-dhash; hamming={distance}",
                    new PixelRect(0, 0, image.Width, image.Height));
            })
            .Where(candidate => candidate.Confidence.Value >= RecognitionPolicy.CandidateFloor)
            .OrderByDescending(candidate => candidate.Confidence.Value)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        return Task.FromResult(candidates);
    }

    public static ulong ComputeDifferenceHash(CapturedImage image)
    {
        CapturedImagePixels.Validate(image);
        try
        {
            return ComputeWithSkia(image);
        }
        catch (Exception exception) when (IsMissingNativeSkia(exception))
        {
            return ComputeManaged(image);
        }
    }

    private static ulong ComputeWithSkia(CapturedImage image)
    {
        using var bitmap = CreateBitmap(image);
        Span<byte> luminance = stackalloc byte[9 * 8];
        for (var row = 0; row < 8; row++)
        {
            var sourceY = Math.Clamp(((2 * row + 1) * bitmap.Height) / 16, 0, bitmap.Height - 1);
            for (var column = 0; column < 9; column++)
            {
                var sourceX = Math.Clamp(((2 * column + 1) * bitmap.Width) / 18, 0, bitmap.Width - 1);
                var color = bitmap.GetPixel(sourceX, sourceY);
                luminance[(row * 9) + column] =
                    (byte)(((color.Red * 77) + (color.Green * 150) + (color.Blue * 29)) >> 8);
            }
        }

        return BuildHash(luminance);
    }

    private static ulong ComputeManaged(CapturedImage image)
    {
        Span<byte> luminance = stackalloc byte[9 * 8];
        for (var row = 0; row < 8; row++)
        {
            var sourceY = Math.Clamp(((2 * row + 1) * image.Height) / 16, 0, image.Height - 1);
            for (var column = 0; column < 9; column++)
            {
                var sourceX = Math.Clamp(((2 * column + 1) * image.Width) / 18, 0, image.Width - 1);
                luminance[(row * 9) + column] = CapturedImagePixels.GetLuminance(image, sourceX, sourceY);
            }
        }

        return BuildHash(luminance);
    }

    private static ulong BuildHash(ReadOnlySpan<byte> luminance)
    {
        ulong hash = 0;
        var bit = 0;
        for (var row = 0; row < 8; row++)
        {
            for (var column = 0; column < 8; column++)
            {
                if (luminance[(row * 9) + column] > luminance[(row * 9) + column + 1])
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    private static bool IsMissingNativeSkia(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is DllNotFoundException)
            {
                return true;
            }
        }

        return false;
    }

    private static SKBitmap CreateBitmap(CapturedImage image)
    {
        var colorType = image.Format switch
        {
            PixelFormat.Bgra8888 => SKColorType.Bgra8888,
            PixelFormat.Rgba8888 => SKColorType.Rgba8888,
            PixelFormat.Gray8 => SKColorType.Gray8,
            _ => throw new ArgumentOutOfRangeException(nameof(image)),
        };
        var alphaType = image.Format == PixelFormat.Gray8 ? SKAlphaType.Opaque : SKAlphaType.Unpremul;
        var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, colorType, alphaType));
        var source = image.Pixels.ToArray();
        var bytesPerRow = checked(image.Width * CapturedImagePixels.BytesPerPixel(image.Format));
        var destination = bitmap.GetPixels();
        for (var row = 0; row < image.Height; row++)
        {
            Marshal.Copy(
                source,
                row * image.Stride,
                IntPtr.Add(destination, row * bitmap.RowBytes),
                bytesPerRow);
        }

        return bitmap;
    }
}
