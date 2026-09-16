using System.Collections;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.RecognitionTests.Grid;

public sealed class IconCandidateSeparatorTests
{
    private static readonly DateTimeOffset RetrievedUtc =
        new(2026, 9, 16, 4, 0, 0, TimeSpan.Zero);

    private static readonly IconCandidateSeparationPolicy Policy = new(
        "fixture-policy-v1",
        maximumCandidateDistanceBits: 12,
        minimumRunnerUpGapBits: 4,
        maximumReturnedCandidates: 8);

    [Fact]
    public void ExactNearNeighbourStaysAmbiguousWithRawDistanceEvidence()
    {
        var query = Fingerprint(0);
        var references = new[]
        {
            Evidence("item-a", 0),
            Evidence("item-b", 1),
        };

        var result = new IconCandidateSeparator().Separate(query, references, Policy);

        Assert.Equal(IconCandidateSeparationOutcome.Ambiguous, result.Outcome);
        Assert.Equal(IconCandidateSeparationReason.RunnerUpGapTooSmall, result.Reason);
        Assert.Equal(1, result.RunnerUpGapBits);
        Assert.Equal(IconCandidateSeparationResult.DistanceMetric, "hamming-bits-not-probability");
        Assert.Collection(
            result.Candidates,
            first =>
            {
                Assert.Same(references[0], first.Evidence);
                Assert.Equal(0, first.HammingDistanceBits);
            },
            second =>
            {
                Assert.Same(references[1], second.Evidence);
                Assert.Equal(1, second.HammingDistanceBits);
            });
    }

    [Fact]
    public void NonExactBestStaysAmbiguousDespiteLargeRunnerUpLead()
    {
        var result = new IconCandidateSeparator().Separate(
            Fingerprint(0),
            [Evidence("item-a", 1), Evidence("item-b", ulong.MaxValue)],
            Policy);

        Assert.Equal(IconCandidateSeparationOutcome.Ambiguous, result.Outcome);
        Assert.Equal(IconCandidateSeparationReason.BestFingerprintNotExact, result.Reason);
        Assert.Equal(63, result.RunnerUpGapBits);
        Assert.Collection(result.Candidates, candidate => Assert.Equal("item-a", candidate.Evidence.CanonicalItemId));
    }

    [Fact]
    public void UniqueExactFingerprintWithPolicyGapIsOnlySeparatedCandidateShape()
    {
        var result = new IconCandidateSeparator().Separate(
            Fingerprint(0),
            [Evidence("item-a", 0), Evidence("item-b", 0xff)],
            Policy);

        Assert.Equal(IconCandidateSeparationOutcome.Separated, result.Outcome);
        Assert.Equal(IconCandidateSeparationReason.DistinctExactFingerprint, result.Reason);
        Assert.Equal(8, result.RunnerUpGapBits);
        Assert.Equal("item-a", result.Candidates[0].Evidence.CanonicalItemId);
        Assert.Equal(0, result.Candidates[0].HammingDistanceBits);
    }

    [Fact]
    public void ExactTieAcrossCanonicalItemsStaysAmbiguous()
    {
        var result = new IconCandidateSeparator().Separate(
            Fingerprint(0x1234),
            [Evidence("item-a", 0x1234), Evidence("item-b", 0x1234)],
            Policy);

        Assert.Equal(IconCandidateSeparationOutcome.Ambiguous, result.Outcome);
        Assert.Equal(IconCandidateSeparationReason.RunnerUpGapTooSmall, result.Reason);
        Assert.Equal(0, result.RunnerUpGapBits);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void MultipleSourcesForOneItemDoNotManufactureSeparation()
    {
        var result = new IconCandidateSeparator().Separate(
            Fingerprint(0),
            [
                Evidence("item-a", 0, sourceSuffix: "first"),
                Evidence("item-a", 0xff, sourceSuffix: "second"),
            ],
            Policy);

        Assert.Equal(IconCandidateSeparationOutcome.Ambiguous, result.Outcome);
        Assert.Equal(IconCandidateSeparationReason.InsufficientReferenceSet, result.Reason);
        Assert.Equal(2, result.CompatibleReferenceCount);
        Assert.Equal(1, result.CompatibleCanonicalItemCount);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public void EqualDuplicateSourcesChooseTheSameUriAcrossInputPermutations()
    {
        var sharedHash = new string('a', IconContentEvidence.Sha256HexLength);
        var laterUri = Evidence("item-a", 0, sourceSuffix: "z-source", contentHash: sharedHash);
        var earlierUri = Evidence("item-a", 0, sourceSuffix: "a-source", contentHash: sharedHash);
        var runnerUp = Evidence("item-b", 0xff);
        var separator = new IconCandidateSeparator();

        var forward = separator.Separate(Fingerprint(0), [laterUri, earlierUri, runnerUp], Policy);
        var reverse = separator.Separate(Fingerprint(0), [runnerUp, earlierUri, laterUri], Policy);

        Assert.Equal(IconCandidateSeparationOutcome.Separated, forward.Outcome);
        Assert.Equal(forward.Candidates.Select(candidate => candidate.Evidence),
            reverse.Candidates.Select(candidate => candidate.Evidence));
        Assert.Same(earlierUri, forward.Candidates[0].Evidence);
    }

    [Fact]
    public void IncompatibleAlgorithmVersionsAreNotCompared()
    {
        var result = new IconCandidateSeparator().Separate(
            Fingerprint(0),
            [Evidence("item-a", 0, algorithmVersion: 2)],
            Policy);

        Assert.Equal(IconCandidateSeparationOutcome.NoCandidate, result.Outcome);
        Assert.Equal(IconCandidateSeparationReason.NoCompatibleReferences, result.Reason);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void FingerprintRejectsBitsOutsideItsDeclaredWidth()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IconFingerprintEvidence("fixture", algorithmVersion: 1, bitCount: 8, value: 0x100));

        Assert.Equal("value", exception.ParamName);
        Assert.Equal(
            0xffUL,
            new IconFingerprintEvidence("fixture", algorithmVersion: 1, bitCount: 8, value: 0xff).Value);
        Assert.Equal(
            ulong.MaxValue,
            new IconFingerprintEvidence("fixture", algorithmVersion: 1, bitCount: 64, value: ulong.MaxValue).Value);
    }

    [Fact]
    public void CandidateOutsidePolicyDistanceIsExplicitlyAbsent()
    {
        var result = new IconCandidateSeparator().Separate(
            Fingerprint(0),
            [Evidence("item-a", ulong.MaxValue), Evidence("item-b", ulong.MaxValue - 1)],
            Policy);

        Assert.Equal(IconCandidateSeparationOutcome.NoCandidate, result.Outcome);
        Assert.Equal(IconCandidateSeparationReason.OutsideCandidateDistance, result.Reason);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void OversizedReferenceListIsRejectedBeforeIndexerAccess()
    {
        var hostile = new HostileList(IconCandidateSeparator.MaximumReferences + 1);

        var exception = Assert.Throws<ArgumentException>(() =>
            new IconCandidateSeparator().Separate(Fingerprint(0), hostile, Policy));

        Assert.Equal("references", exception.ParamName);
        Assert.Equal(0, hostile.IndexerReads);
    }

    [Fact]
    public void CancellationIsObservedBeforeReferenceAccess()
    {
        var hostile = new HostileList(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new IconCandidateSeparator().Separate(Fingerprint(0), hostile, Policy, cancellation.Token));
        Assert.Equal(0, hostile.IndexerReads);
    }

    [Fact]
    public void ResultCannotRelabelDistanceOrDuplicateACanonicalCandidate()
    {
        var query = Fingerprint(0);
        var first = new IconFingerprintCandidate(Evidence("item-a", 0), 1);
        var second = new IconFingerprintCandidate(Evidence("item-a", 1, sourceSuffix: "second"), 1);

        Assert.Throws<ArgumentException>(() => new IconCandidateSeparationResult(
            query,
            Policy,
            IconCandidateSeparationOutcome.Ambiguous,
            IconCandidateSeparationReason.RunnerUpGapTooSmall,
            compatibleReferenceCount: 2,
            compatibleCanonicalItemCount: 1,
            runnerUpGapBits: 0,
            candidates: [first]));

        Assert.Throws<ArgumentException>(() => new IconCandidateSeparationResult(
            query,
            Policy,
            IconCandidateSeparationOutcome.Ambiguous,
            IconCandidateSeparationReason.RunnerUpGapTooSmall,
            compatibleReferenceCount: 2,
            compatibleCanonicalItemCount: 1,
            runnerUpGapBits: 0,
            candidates: [
                new IconFingerprintCandidate(Evidence("item-a", 0), 0),
                second,
            ]));
    }

    [Fact]
    public void ResultRejectsInconsistentCountsReasonsAndRunnerUpGap()
    {
        var query = Fingerprint(0);
        var exact = new IconFingerprintCandidate(Evidence("item-a", 0), 0);
        var runnerUp = new IconFingerprintCandidate(Evidence("item-b", 0xff), 8);

        Assert.Throws<ArgumentException>(() => new IconCandidateSeparationResult(
            query,
            Policy,
            IconCandidateSeparationOutcome.Ambiguous,
            IconCandidateSeparationReason.RunnerUpGapTooSmall,
            compatibleReferenceCount: 0,
            compatibleCanonicalItemCount: 0,
            runnerUpGapBits: 0,
            candidates: [exact]));

        Assert.Throws<ArgumentException>(() => new IconCandidateSeparationResult(
            query,
            Policy,
            IconCandidateSeparationOutcome.Separated,
            IconCandidateSeparationReason.NoCompatibleReferences,
            compatibleReferenceCount: 2,
            compatibleCanonicalItemCount: 2,
            runnerUpGapBits: 8,
            candidates: [exact, runnerUp]));

        Assert.Throws<ArgumentException>(() => new IconCandidateSeparationResult(
            query,
            Policy,
            IconCandidateSeparationOutcome.Separated,
            IconCandidateSeparationReason.DistinctExactFingerprint,
            compatibleReferenceCount: 2,
            compatibleCanonicalItemCount: 2,
            runnerUpGapBits: 7,
            candidates: [exact, runnerUp]));
    }

    private static IconContentEvidence Evidence(
        string canonicalItemId,
        ulong value,
        int algorithmVersion = IconFingerprintAlgorithms.DifferenceHashLuminance9X8Version,
        string sourceSuffix = "default",
        string? contentHash = null)
    {
        var source = new Uri($"https://assets.tarkov.dev/icons/{canonicalItemId}/{sourceSuffix}.png");
        contentHash ??= Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{canonicalItemId}:{sourceSuffix}:{value}")));
        return new IconContentEvidence(
            new IconEvidenceKey(canonicalItemId, source),
            RetrievedUtc,
            contentHash,
            Provenance(source),
            new IconPixelDimensions(64, 64),
            Fingerprint(value, algorithmVersion));
    }

    private static IconFingerprintEvidence Fingerprint(
        ulong value,
        int algorithmVersion = IconFingerprintAlgorithms.DifferenceHashLuminance9X8Version) => new(
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8,
            algorithmVersion,
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8Bits,
            value);

    private static EvidenceProvenance Provenance(Uri source) => new(
        EvidenceSourceClass.PublicStructuredData,
        source.AbsoluteUri,
        RetrievedUtc,
        EvidenceConfidence.Unscored,
        new ProducerIdentity("icon-separator-fixture", "1"),
        reference: source.AbsoluteUri);

    private sealed class HostileList(int count) : IReadOnlyList<IconContentEvidence>
    {
        public int Count { get; } = count;

        public int IndexerReads { get; private set; }

        public IconContentEvidence this[int index]
        {
            get
            {
                IndexerReads++;
                throw new InvalidOperationException("The indexer must not be reached.");
            }
        }

        public IEnumerator<IconContentEvidence> GetEnumerator() =>
            throw new InvalidOperationException("Enumeration must not be reached.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
