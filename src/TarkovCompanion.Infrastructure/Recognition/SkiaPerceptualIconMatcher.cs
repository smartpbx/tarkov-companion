using System.Numerics;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed record IconFingerprintReference(
    string CanonicalId,
    string DisplayName,
    ulong DifferenceHash);

/// <summary>
/// Experimental ROI matcher. Production recognition keeps this disabled until
/// licensed cached icons have populated a calibrated reference repository.
/// </summary>
public sealed class SkiaPerceptualIconMatcher : IIconMatcher
{
    public const int MaximumHammingDistance = 12;
    private const double MaximumCandidateScore = RecognitionThresholds.Ambiguous - 0.01;
    private readonly IReadOnlyList<IconFingerprintReference> _references;

    public SkiaPerceptualIconMatcher(IEnumerable<IconFingerprintReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        _references = references.ToArray();
    }

    public static IconFingerprintReference CreateReference(
        string canonicalId,
        string displayName,
        CapturedImage itemRegion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new(canonicalId, displayName, ComputeDifferenceHash(itemRegion));
    }

    public Task<IReadOnlyList<RecognitionCandidate>> MatchAsync(
        CapturedImage itemRegion,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        cancellationToken.ThrowIfCancellationRequested();
        var hash = ComputeDifferenceHash(itemRegion);
        IReadOnlyList<RecognitionCandidate> candidates = _references
            .Select(reference => (Reference: reference, Distance: BitOperations.PopCount(hash ^ reference.DifferenceHash)))
            .Where(match => match.Distance <= MaximumHammingDistance)
            .Select(match => new RecognitionCandidate(
                match.Reference.CanonicalId,
                match.Reference.DisplayName,
                DistanceScore(match.Distance),
                $"icon-dhash; hamming={match.Distance}; cutoff={MaximumHammingDistance}; score-is-not-probability",
                new PixelRect(0, 0, itemRegion.Width, itemRegion.Height)))
            .OrderByDescending(candidate => candidate.Confidence.Value)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        return Task.FromResult(candidates);
    }

    public static ulong ComputeDifferenceHash(CapturedImage itemRegion)
    {
        CapturedImagePixels.Validate(itemRegion);
        Span<byte> luminance = stackalloc byte[9 * 8];
        for (var row = 0; row < 8; row++)
        {
            var top = (itemRegion.Height * row) / 8;
            var bottom = Math.Max(top + 1, (itemRegion.Height * (row + 1)) / 8);
            for (var column = 0; column < 9; column++)
            {
                var left = (itemRegion.Width * column) / 9;
                var right = Math.Max(left + 1, (itemRegion.Width * (column + 1)) / 9);
                luminance[(row * 9) + column] = AverageLuminance(itemRegion, left, top, right, bottom);
            }
        }

        return BuildHash(luminance);
    }

    private static Confidence DistanceScore(int distance)
    {
        var closeness = (MaximumHammingDistance - distance) / (double)MaximumHammingDistance;
        var score = RecognitionThresholds.Candidate +
                    (closeness * (MaximumCandidateScore - RecognitionThresholds.Candidate));
        return new(Math.Clamp(score, RecognitionThresholds.Candidate, MaximumCandidateScore));
    }

    private static byte AverageLuminance(
        CapturedImage image,
        int left,
        int top,
        int right,
        int bottom)
    {
        var stepX = Math.Max(1, (right - left) / 16);
        var stepY = Math.Max(1, (bottom - top) / 16);
        long sum = 0;
        var count = 0;
        for (var y = top; y < bottom; y += stepY)
        {
            for (var x = left; x < right; x += stepX)
            {
                sum += CapturedImagePixels.GetLuminance(image, x, y);
                count++;
            }
        }

        return (byte)(sum / Math.Max(1, count));
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
}
