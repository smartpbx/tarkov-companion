using System.Numerics;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>Compares compatible local fingerprints without turning distance into confidence.</summary>
/// <remarks>
/// A perceptual-hash distance is only a ranking measurement. Separation is reported solely for
/// a unique exact best fingerprint with a runner-up gap supplied by the externally versioned
/// policy. A transformed best match, a one-item reference set, and every weak or near-neighbour
/// lead stay ambiguous for later icon, OCR, dimensions, category, and geometry reconciliation.
/// </remarks>
public sealed class IconCandidateSeparator
{
    public const int MaximumReferences = 8192;

    public IconCandidateSeparationResult Separate(
        IconFingerprintEvidence query,
        IReadOnlyList<IconContentEvidence> references,
        IconCandidateSeparationPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();

        var count = references.Count;
        if (count is < 0 or > MaximumReferences)
        {
            throw new ArgumentException(
                $"Icon separation cannot inspect more than {MaximumReferences} references.",
                nameof(references));
        }

        var compatibleCount = 0;
        var closestByCanonicalItem = new Dictionary<string, IconFingerprintCandidate>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = references[index] ??
                throw new ArgumentException("Icon references cannot contain null entries.", nameof(references));
            if (!query.IsCompatibleWith(reference.Fingerprint))
            {
                continue;
            }

            compatibleCount++;
            var distance = BitOperations.PopCount(query.Value ^ reference.Fingerprint.Value);
            var candidate = new IconFingerprintCandidate(reference, distance);
            if (!closestByCanonicalItem.TryGetValue(reference.CanonicalItemId, out var current) ||
                IsBetter(candidate, current))
            {
                closestByCanonicalItem[reference.CanonicalItemId] = candidate;
            }
        }

        var ordered = closestByCanonicalItem.Values
            .OrderBy(candidate => candidate.HammingDistanceBits)
            .ThenBy(candidate => candidate.Evidence.CanonicalItemId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Evidence.ContentSha256, StringComparer.Ordinal)
            .ToArray();
        var visible = ordered
            .Where(candidate => candidate.HammingDistanceBits <= policy.MaximumCandidateDistanceBits)
            .Take(policy.MaximumReturnedCandidates)
            .ToArray();

        if (compatibleCount == 0)
        {
            return Result(
                query,
                policy,
                IconCandidateSeparationOutcome.NoCandidate,
                IconCandidateSeparationReason.NoCompatibleReferences,
                compatibleCount,
                ordered.Length,
                null,
                []);
        }

        if (visible.Length == 0)
        {
            return Result(
                query,
                policy,
                IconCandidateSeparationOutcome.NoCandidate,
                IconCandidateSeparationReason.OutsideCandidateDistance,
                compatibleCount,
                ordered.Length,
                ordered.Length > 1
                    ? ordered[1].HammingDistanceBits - ordered[0].HammingDistanceBits
                    : null,
                []);
        }

        if (ordered.Length < 2)
        {
            return Result(
                query,
                policy,
                IconCandidateSeparationOutcome.Ambiguous,
                IconCandidateSeparationReason.InsufficientReferenceSet,
                compatibleCount,
                ordered.Length,
                null,
                visible);
        }

        var runnerUpGap = ordered[1].HammingDistanceBits - ordered[0].HammingDistanceBits;
        if (ordered[0].HammingDistanceBits != 0)
        {
            return Result(
                query,
                policy,
                IconCandidateSeparationOutcome.Ambiguous,
                IconCandidateSeparationReason.BestFingerprintNotExact,
                compatibleCount,
                ordered.Length,
                runnerUpGap,
                visible);
        }

        if (runnerUpGap < policy.MinimumRunnerUpGapBits)
        {
            return Result(
                query,
                policy,
                IconCandidateSeparationOutcome.Ambiguous,
                IconCandidateSeparationReason.RunnerUpGapTooSmall,
                compatibleCount,
                ordered.Length,
                runnerUpGap,
                visible);
        }

        return Result(
            query,
            policy,
            IconCandidateSeparationOutcome.Separated,
            IconCandidateSeparationReason.DistinctExactFingerprint,
            compatibleCount,
            ordered.Length,
            runnerUpGap,
            visible);
    }

    private static bool IsBetter(IconFingerprintCandidate candidate, IconFingerprintCandidate current) =>
        candidate.HammingDistanceBits < current.HammingDistanceBits ||
        (candidate.HammingDistanceBits == current.HammingDistanceBits &&
         string.CompareOrdinal(candidate.Evidence.ContentSha256, current.Evidence.ContentSha256) < 0);

    private static IconCandidateSeparationResult Result(
        IconFingerprintEvidence query,
        IconCandidateSeparationPolicy policy,
        IconCandidateSeparationOutcome outcome,
        IconCandidateSeparationReason reason,
        int compatibleReferenceCount,
        int compatibleCanonicalItemCount,
        int? runnerUpGapBits,
        IReadOnlyList<IconFingerprintCandidate> candidates) => new(
            query,
            policy,
            outcome,
            reason,
            compatibleReferenceCount,
            compatibleCanonicalItemCount,
            runnerUpGapBits,
            candidates);
}
