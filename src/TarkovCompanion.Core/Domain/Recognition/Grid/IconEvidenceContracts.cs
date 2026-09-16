using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Core.Domain.Recognition.Grid;

/// <summary>The versioned local fingerprint formats understood by grid recognition.</summary>
public static class IconFingerprintAlgorithms
{
    public const string DifferenceHashLuminance9X8 = "dhash-luminance-9x8";

    public const int DifferenceHashLuminance9X8Version = 1;

    public const int DifferenceHashLuminance9X8Bits = 64;
}

/// <summary>The bounded decoded dimensions of one locally cached icon.</summary>
public sealed record IconPixelDimensions
{
    public const int MaximumDimension = 4096;

    public const long MaximumPixels = 4_194_304;

    public IconPixelDimensions(int width, int height)
    {
        if (width is < 1 or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height is < 1 or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if ((long)width * height > MaximumPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "An icon exceeds the decoded pixel ceiling.");
        }

        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}

/// <summary>A raw, versioned fingerprint; its bits are evidence and never a probability.</summary>
public sealed record IconFingerprintEvidence
{
    public const int MaximumAlgorithmLength = 64;

    public IconFingerprintEvidence(string algorithm, int algorithmVersion, int bitCount, ulong value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);
        var normalizedAlgorithm = algorithm.Trim();
        if (normalizedAlgorithm.Length > MaximumAlgorithmLength)
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm));
        }

        if (algorithmVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(algorithmVersion));
        }

        if (bitCount is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }

        Algorithm = normalizedAlgorithm;
        AlgorithmVersion = algorithmVersion;
        BitCount = bitCount;
        Value = value;
    }

    public string Algorithm { get; }

    public int AlgorithmVersion { get; }

    public int BitCount { get; }

    public ulong Value { get; }

    public bool IsCompatibleWith(IconFingerprintEvidence other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Algorithm, other.Algorithm, StringComparison.Ordinal) &&
               AlgorithmVersion == other.AlgorithmVersion &&
               BitCount == other.BitCount;
    }
}

/// <summary>The stable identity used for one item icon source in the local cache.</summary>
public sealed record IconEvidenceKey
{
    public const int MaximumCanonicalItemIdLength = 128;

    public const int MaximumSourceUriUtf8Bytes = 4096;

    public IconEvidenceKey(string canonicalItemId, Uri sourceUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalItemId);
        ArgumentNullException.ThrowIfNull(sourceUri);

        var normalizedItemId = canonicalItemId.Trim();
        if (normalizedItemId.Length > MaximumCanonicalItemIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(canonicalItemId));
        }

        if (!sourceUri.IsAbsoluteUri ||
            !string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(sourceUri.UserInfo) ||
            !string.IsNullOrEmpty(sourceUri.Fragment) ||
            Encoding.UTF8.GetByteCount(sourceUri.AbsoluteUri) > MaximumSourceUriUtf8Bytes)
        {
            throw new ArgumentException(
                "An icon source must be a bounded absolute HTTPS URI without credentials or a fragment.",
                nameof(sourceUri));
        }

        CanonicalItemId = normalizedItemId;
        SourceUri = sourceUri;
    }

    public string CanonicalItemId { get; }

    public Uri SourceUri { get; }
}

/// <summary>Metadata that keeps locally derived icon evidence attributable and reproducible.</summary>
/// <remarks>
/// Item renders have no redistribution grant in the upstream chain. This record therefore
/// describes a cache entry on the machine that fetched it; neither the bytes nor this derived
/// fingerprint belongs in a release, relay, fixture, or published catalog.
/// </remarks>
public sealed record IconContentEvidence
{
    public const int Sha256HexLength = 64;

    public const int MaximumProvenanceUtf8Bytes = 65_536;

    public IconContentEvidence(
        IconEvidenceKey key,
        DateTimeOffset retrievedUtc,
        string contentSha256,
        EvidenceProvenance provenance,
        IconPixelDimensions dimensions,
        IconFingerprintEvidence fingerprint)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(fingerprint);

        if (retrievedUtc == default)
        {
            throw new ArgumentException("A retrieval timestamp is required.", nameof(retrievedUtc));
        }

        var normalizedRetrievedUtc = retrievedUtc.ToUniversalTime();
        if (provenance.ObservedUtc > normalizedRetrievedUtc)
        {
            throw new ArgumentException("Icon provenance cannot be observed after retrieval.", nameof(provenance));
        }

        if (provenance.SourceClass is not (EvidenceSourceClass.PublicStructuredData or EvidenceSourceClass.CuratedData))
        {
            throw new ArgumentException("Icon cache provenance must name public or curated data.", nameof(provenance));
        }

        var normalizedHash = contentSha256.Trim().ToLowerInvariant();
        if (normalizedHash.Length != Sha256HexLength || normalizedHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Content SHA-256 must be exactly 64 hexadecimal characters.", nameof(contentSha256));
        }

        if (ProvenanceUtf8Bytes(provenance) > MaximumProvenanceUtf8Bytes)
        {
            throw new ArgumentException("Icon provenance exceeds its serialized text budget.", nameof(provenance));
        }

        Key = key;
        RetrievedUtc = normalizedRetrievedUtc;
        ContentSha256 = normalizedHash;
        Provenance = provenance;
        Dimensions = dimensions;
        Fingerprint = fingerprint;
    }

    public IconEvidenceKey Key { get; }

    public string CanonicalItemId => Key.CanonicalItemId;

    public Uri SourceUri => Key.SourceUri;

    public DateTimeOffset RetrievedUtc { get; }

    public string ContentSha256 { get; }

    public EvidenceProvenance Provenance { get; }

    public IconPixelDimensions Dimensions { get; }

    public IconFingerprintEvidence Fingerprint { get; }

    private static long ProvenanceUtf8Bytes(EvidenceProvenance provenance)
    {
        long total = TextBytes(provenance.SourceIdentifier) +
                     TextBytes(provenance.Reference) +
                     TextBytes(provenance.Confidence.CalibrationReference) +
                     TextBytes(provenance.Coverage?.Description) +
                     TextBytes(provenance.Producer.Name) +
                     TextBytes(provenance.Producer.Version) +
                     TextBytes(provenance.Producer.ModelVersion);
        foreach (var input in provenance.Inputs)
        {
            total += ProvenanceUtf8Bytes(input);
            if (total > MaximumProvenanceUtf8Bytes)
            {
                return total;
            }
        }

        return total;
    }

    private static int TextBytes(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);
}

/// <summary>One bounded cache write; content is copied before asynchronous work begins.</summary>
public sealed record IconContentWriteRequest
{
    public const int MaximumContentBytes = 4 * 1024 * 1024;

    public IconContentWriteRequest(
        IconEvidenceKey key,
        DateTimeOffset retrievedUtc,
        EvidenceProvenance provenance,
        ReadOnlyMemory<byte> content)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(provenance);
        if (retrievedUtc == default)
        {
            throw new ArgumentException("A retrieval timestamp is required.", nameof(retrievedUtc));
        }

        if (content.Length is < 1 or > MaximumContentBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(content));
        }

        Key = key;
        RetrievedUtc = retrievedUtc.ToUniversalTime();
        Provenance = provenance;
        Content = content.ToArray();
    }

    public IconEvidenceKey Key { get; }

    public DateTimeOffset RetrievedUtc { get; }

    public EvidenceProvenance Provenance { get; }

    public ReadOnlyMemory<byte> Content { get; }
}

/// <summary>A verified local cache record and a private copy of its original encoded content.</summary>
public sealed record IconContentEvidenceAsset
{
    public IconContentEvidenceAsset(IconContentEvidence evidence, ReadOnlyMemory<byte> content)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (content.Length is < 1 or > IconContentWriteRequest.MaximumContentBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(content));
        }

        var copy = content.ToArray();
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(copy));
        if (!string.Equals(actualHash, evidence.ContentSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("Icon content does not match its retained SHA-256.", nameof(content));
        }

        Evidence = evidence;
        Content = copy;
    }

    public IconContentEvidence Evidence { get; }

    public ReadOnlyMemory<byte> Content { get; }
}

/// <summary>The local-only persistence boundary for icon bytes and their derived evidence.</summary>
public interface IIconEvidenceCache
{
    Task<IconContentEvidenceAsset> StoreAsync(
        IconContentWriteRequest request,
        CancellationToken cancellationToken);

    Task<IconContentEvidenceAsset?> GetAsync(
        IconEvidenceKey key,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken);
}

/// <summary>Externally versioned separation constraints; benchmark policy remains owned by #272.</summary>
public sealed record IconCandidateSeparationPolicy
{
    public const int MaximumPolicyVersionLength = 128;

    public const int MaximumReturnedCandidatesLimit = 32;

    public IconCandidateSeparationPolicy(
        string policyVersion,
        int maximumCandidateDistanceBits,
        int minimumRunnerUpGapBits,
        int maximumReturnedCandidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        var normalizedVersion = policyVersion.Trim();
        if (normalizedVersion.Length > MaximumPolicyVersionLength)
        {
            throw new ArgumentOutOfRangeException(nameof(policyVersion));
        }

        if (maximumCandidateDistanceBits is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidateDistanceBits));
        }

        if (minimumRunnerUpGapBits is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRunnerUpGapBits));
        }

        if (maximumReturnedCandidates is < 2 or > MaximumReturnedCandidatesLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumReturnedCandidates));
        }

        PolicyVersion = normalizedVersion;
        MaximumCandidateDistanceBits = maximumCandidateDistanceBits;
        MinimumRunnerUpGapBits = minimumRunnerUpGapBits;
        MaximumReturnedCandidates = maximumReturnedCandidates;
    }

    public string PolicyVersion { get; }

    public int MaximumCandidateDistanceBits { get; }

    public int MinimumRunnerUpGapBits { get; }

    public int MaximumReturnedCandidates { get; }
}

public enum IconCandidateSeparationOutcome
{
    NoCandidate = 1,
    Ambiguous,
    Separated,
}

public enum IconCandidateSeparationReason
{
    NoCompatibleReferences = 1,
    OutsideCandidateDistance,
    InsufficientReferenceSet,
    BestFingerprintNotExact,
    RunnerUpGapTooSmall,
    DistinctExactFingerprint,
}

/// <summary>One item candidate with a raw Hamming-bit distance and its complete cache evidence.</summary>
public sealed record IconFingerprintCandidate
{
    public IconFingerprintCandidate(IconContentEvidence evidence, int hammingDistanceBits)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (hammingDistanceBits is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(hammingDistanceBits));
        }

        Evidence = evidence;
        HammingDistanceBits = hammingDistanceBits;
    }

    public IconContentEvidence Evidence { get; }

    public int HammingDistanceBits { get; }
}

/// <summary>A bounded comparison result that deliberately carries no probability or confidence.</summary>
public sealed record IconCandidateSeparationResult
{
    public const string DistanceMetric = "hamming-bits-not-probability";

    public IconCandidateSeparationResult(
        IconFingerprintEvidence query,
        IconCandidateSeparationPolicy policy,
        IconCandidateSeparationOutcome outcome,
        IconCandidateSeparationReason reason,
        int compatibleReferenceCount,
        int compatibleCanonicalItemCount,
        int? runnerUpGapBits,
        IReadOnlyList<IconFingerprintCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (compatibleReferenceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(compatibleReferenceCount));
        }

        if (compatibleCanonicalItemCount is < 0 || compatibleCanonicalItemCount > compatibleReferenceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(compatibleCanonicalItemCount));
        }

        if (runnerUpGapBits is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(runnerUpGapBits));
        }

        ArgumentNullException.ThrowIfNull(candidates);
        var copy = candidates.ToArray();
        if (copy.Length > policy.MaximumReturnedCandidates || copy.Any(candidate => candidate is null))
        {
            throw new ArgumentException("Icon candidates exceed their result bounds.", nameof(candidates));
        }

        var canonicalIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < copy.Length; index++)
        {
            var candidate = copy[index];
            if (!query.IsCompatibleWith(candidate.Evidence.Fingerprint) ||
                candidate.HammingDistanceBits !=
                System.Numerics.BitOperations.PopCount(query.Value ^ candidate.Evidence.Fingerprint.Value) ||
                candidate.HammingDistanceBits > policy.MaximumCandidateDistanceBits)
            {
                throw new ArgumentException(
                    "Icon candidates must be compatible, within policy distance, and retain their measured Hamming distance.",
                    nameof(candidates));
            }

            if (!canonicalIds.Add(candidate.Evidence.CanonicalItemId) ||
                (index > 0 && CompareCandidates(copy[index - 1], candidate) >= 0))
            {
                throw new ArgumentException(
                    "Icon candidates must be distinct canonical items in deterministic distance order.",
                    nameof(candidates));
            }
        }

        if ((outcome == IconCandidateSeparationOutcome.NoCandidate) != (copy.Length == 0))
        {
            throw new ArgumentException("Only a no-candidate result may have no candidate evidence.", nameof(candidates));
        }

        if (outcome == IconCandidateSeparationOutcome.Separated &&
            (copy[0].HammingDistanceBits != 0 ||
             runnerUpGapBits < policy.MinimumRunnerUpGapBits ||
             compatibleCanonicalItemCount < 2))
        {
            throw new ArgumentException("Separated icon evidence must be exact and have a sufficient runner-up gap.");
        }

        Query = query;
        Policy = policy;
        Outcome = outcome;
        Reason = reason;
        CompatibleReferenceCount = compatibleReferenceCount;
        CompatibleCanonicalItemCount = compatibleCanonicalItemCount;
        RunnerUpGapBits = runnerUpGapBits;
        Candidates = Array.AsReadOnly(copy);
    }

    public IconFingerprintEvidence Query { get; }

    public IconCandidateSeparationPolicy Policy { get; }

    public IconCandidateSeparationOutcome Outcome { get; }

    public IconCandidateSeparationReason Reason { get; }

    public int CompatibleReferenceCount { get; }

    public int CompatibleCanonicalItemCount { get; }

    public int? RunnerUpGapBits { get; }

    public ReadOnlyCollection<IconFingerprintCandidate> Candidates { get; }

    private static int CompareCandidates(IconFingerprintCandidate left, IconFingerprintCandidate right)
    {
        var byDistance = left.HammingDistanceBits.CompareTo(right.HammingDistanceBits);
        if (byDistance != 0)
        {
            return byDistance;
        }

        var byCanonicalId = string.CompareOrdinal(
            left.Evidence.CanonicalItemId,
            right.Evidence.CanonicalItemId);
        return byCanonicalId != 0
            ? byCanonicalId
            : string.CompareOrdinal(left.Evidence.ContentSha256, right.Evidence.ContentSha256);
    }
}
