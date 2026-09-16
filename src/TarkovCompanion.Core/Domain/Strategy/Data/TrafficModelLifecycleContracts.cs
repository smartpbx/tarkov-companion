using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Core.Domain.Strategy.Data;

public enum TrafficSignatureAlgorithm
{
    EcdsaP256Sha256 = 1,
}

public sealed record TrafficModelBuildReport
{
    public TrafficModelBuildReport(
        int schemaVersion,
        string buildId,
        string datasetId,
        string datasetVersion,
        string datasetSha256,
        string artifactSha256,
        string transformVersion,
        string modelVersion,
        string calibrationReference,
        DateTimeOffset dataThroughUtc,
        DateTimeOffset generatedUtc,
        long sampleSize,
        EvidenceConfidence confidence,
        EvidenceCoverage coverage,
        IReadOnlyList<TrafficCompatibilityScope> compatibilityScopes,
        IReadOnlyList<TrafficCoverageGap> coverageGaps,
        IReadOnlyList<TrafficPartitionDigest> partitions,
        bool leakageCheckPassed)
    {
        if (schemaVersion != TrafficDataBounds.CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        if (sampleSize is < 1 or > TrafficDataBounds.MaximumSampleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleSize));
        }

        if (!leakageCheckPassed)
        {
            throw new ArgumentException("A publishable build report must record a passing leakage check.", nameof(leakageCheckPassed));
        }

        SchemaVersion = schemaVersion;
        BuildId = TrafficDataGuard.Token(buildId, nameof(buildId));
        DatasetId = TrafficDataGuard.Token(datasetId, nameof(datasetId));
        DatasetVersion = TrafficDataGuard.Token(datasetVersion, nameof(datasetVersion));
        DatasetSha256 = TrafficDataGuard.Sha256(datasetSha256, nameof(datasetSha256));
        ArtifactSha256 = TrafficDataGuard.Sha256(artifactSha256, nameof(artifactSha256));
        TransformVersion = TrafficDataGuard.Token(transformVersion, nameof(transformVersion));
        ModelVersion = TrafficDataGuard.Token(modelVersion, nameof(modelVersion));
        CalibrationReference = TrafficDataGuard.Text(calibrationReference, nameof(calibrationReference));
        DataThroughUtc = TrafficDataGuard.Utc(dataThroughUtc, nameof(dataThroughUtc));
        GeneratedUtc = TrafficDataGuard.Utc(generatedUtc, nameof(generatedUtc));
        if (DataThroughUtc > GeneratedUtc)
        {
            throw new ArgumentException("Build data-through time cannot follow generation time.");
        }

        Confidence = confidence ?? throw new ArgumentNullException(nameof(confidence));
        if (confidence.Kind != EvidenceConfidenceKind.CalibratedEstimate ||
            !string.Equals(confidence.CalibrationReference, CalibrationReference, StringComparison.Ordinal))
        {
            throw new ArgumentException("Build confidence must be calibrated by the named reference.", nameof(confidence));
        }

        Coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
        if (coverage.SampleSize != sampleSize)
        {
            throw new ArgumentException("Build sample size and evidence coverage must agree.", nameof(coverage));
        }

        SampleSize = sampleSize;
        CompatibilityScopes = TrafficDataGuard.List(
            compatibilityScopes,
            nameof(compatibilityScopes),
            TrafficDataBounds.MaximumScopes,
            requireNonEmpty: true);
        CoverageGaps = TrafficDataGuard.List(
            coverageGaps,
            nameof(coverageGaps),
            TrafficDataBounds.MaximumCoverageGaps,
            requireNonEmpty: false);
        Partitions = TrafficDataGuard.List(
            partitions,
            nameof(partitions),
            Enum.GetValues<TrafficDataPartition>().Length,
            requireNonEmpty: true);
        LeakageCheckPassed = leakageCheckPassed;

        if (CompatibilityScopes.Distinct().Count() != CompatibilityScopes.Count)
        {
            throw new ArgumentException("Build compatibility scopes must be distinct.", nameof(compatibilityScopes));
        }

        var expected = Enum.GetValues<TrafficDataPartition>();
        if (Partitions.Count != expected.Length ||
            !Partitions.Select(partition => partition.Partition).Order().SequenceEqual(expected.Order()) ||
            Partitions.Sum(partition => partition.SampleCount) != sampleSize)
        {
            throw new ArgumentException("Build partitions must be complete and reconcile with sample size.", nameof(partitions));
        }

        if (CoverageGaps.Any(gap => !CompatibilityScopes.Contains(gap.Scope)))
        {
            throw new ArgumentException("Every coverage gap must belong to a published compatibility scope.", nameof(coverageGaps));
        }
    }

    public int SchemaVersion { get; }
    public string BuildId { get; }
    public string DatasetId { get; }
    public string DatasetVersion { get; }
    public string DatasetSha256 { get; }
    public string ArtifactSha256 { get; }
    public string TransformVersion { get; }
    public string ModelVersion { get; }
    public string CalibrationReference { get; }
    public DateTimeOffset DataThroughUtc { get; }
    public DateTimeOffset GeneratedUtc { get; }
    public long SampleSize { get; }
    public EvidenceConfidence Confidence { get; }
    public EvidenceCoverage Coverage { get; }
    public IReadOnlyList<TrafficCompatibilityScope> CompatibilityScopes { get; }
    public IReadOnlyList<TrafficCoverageGap> CoverageGaps { get; }
    public IReadOnlyList<TrafficPartitionDigest> Partitions { get; }
    public bool LeakageCheckPassed { get; }
}

/// <summary>A detached manifest signature; signing UTC is informational and is not signed metadata.</summary>
public sealed record TrafficArtifactSignature
{
    public TrafficArtifactSignature(
        TrafficSignatureAlgorithm algorithm,
        string keyId,
        string signedManifestSha256,
        string signatureBase64,
        DateTimeOffset signedUtc)
    {
        if (!Enum.IsDefined(algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm));
        }

        Algorithm = algorithm;
        KeyId = TrafficDataGuard.Token(keyId, nameof(keyId));
        SignedManifestSha256 = TrafficDataGuard.Sha256(signedManifestSha256, nameof(signedManifestSha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(signatureBase64);
        if (signatureBase64.Length > 128 || signatureBase64 != signatureBase64.Trim())
        {
            throw new ArgumentException("Signature text is outside its canonical bound.", nameof(signatureBase64));
        }

        try
        {
            var signature = Convert.FromBase64String(signatureBase64);
            if (signature.Length is < 64 or > 80 ||
                !string.Equals(Convert.ToBase64String(signature), signatureBase64, StringComparison.Ordinal))
            {
                throw new ArgumentException("Signature length is outside the ECDSA P-256 bounds.", nameof(signatureBase64));
            }
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Signature must be canonical base64.", nameof(signatureBase64), exception);
        }

        SignatureBase64 = signatureBase64;
        SignedUtc = TrafficDataGuard.Utc(signedUtc, nameof(signedUtc));
    }

    public TrafficSignatureAlgorithm Algorithm { get; }
    public string KeyId { get; }
    public string SignedManifestSha256 { get; }
    public string SignatureBase64 { get; }
    public DateTimeOffset SignedUtc { get; }
}

/// <summary>The immutable files that move together through validation, installation, and rollback.</summary>
public sealed record TrafficModelPublication
{
    public TrafficModelPublication(
        GovernedTrafficDataset dataset,
        TrafficModelBuildReport buildReport,
        TrafficModelArtifactManifest manifest,
        TrafficArtifactSignature signature)
    {
        Dataset = dataset ?? throw new ArgumentNullException(nameof(dataset));
        BuildReport = buildReport ?? throw new ArgumentNullException(nameof(buildReport));
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Signature = signature ?? throw new ArgumentNullException(nameof(signature));

        if (!string.Equals(dataset.DatasetId, buildReport.DatasetId, StringComparison.Ordinal) ||
            !string.Equals(dataset.DatasetId, manifest.DatasetId, StringComparison.Ordinal) ||
            !string.Equals(dataset.DatasetVersion, buildReport.DatasetVersion, StringComparison.Ordinal) ||
            !string.Equals(dataset.DatasetVersion, manifest.DatasetVersion, StringComparison.Ordinal) ||
            !string.Equals(dataset.ContentSha256, buildReport.DatasetSha256, StringComparison.Ordinal) ||
            !string.Equals(dataset.ContentSha256, manifest.DatasetSha256, StringComparison.Ordinal) ||
            !string.Equals(buildReport.ArtifactSha256, manifest.ArtifactSha256, StringComparison.Ordinal) ||
            !string.Equals(dataset.TransformVersion, buildReport.TransformVersion, StringComparison.Ordinal) ||
            !string.Equals(dataset.TransformVersion, manifest.TransformVersion, StringComparison.Ordinal) ||
            !string.Equals(buildReport.ModelVersion, manifest.ModelVersion, StringComparison.Ordinal) ||
            !string.Equals(buildReport.CalibrationReference, manifest.CalibrationReference, StringComparison.Ordinal) ||
            dataset.DataThroughUtc != buildReport.DataThroughUtc ||
            dataset.DataThroughUtc != manifest.DataThroughUtc ||
            dataset.GeneratedUtc != buildReport.GeneratedUtc ||
            buildReport.GeneratedUtc != manifest.GeneratedUtc ||
            !dataset.Partitions.SequenceEqual(buildReport.Partitions) ||
            !dataset.Partitions.SequenceEqual(manifest.Partitions) ||
            !buildReport.CompatibilityScopes.SequenceEqual(manifest.CompatibilityScopes) ||
            !dataset.Records.Select(record => record.Scope).Distinct().OrderBy(scope => scope.MapId, StringComparer.Ordinal)
                .ThenBy(scope => scope.GameVersion, StringComparer.Ordinal).ThenBy(scope => scope.GameMode)
                .ThenBy(scope => scope.WipeId, StringComparer.Ordinal)
                .ThenBy(scope => scope.CohortId, StringComparer.Ordinal)
                .SequenceEqual(buildReport.CompatibilityScopes) ||
            !dataset.CoverageGaps.SequenceEqual(buildReport.CoverageGaps) ||
            !dataset.CoverageGaps.SequenceEqual(manifest.CoverageGaps) ||
            buildReport.Confidence != manifest.Confidence ||
            buildReport.Coverage != manifest.Coverage)
        {
            throw new ArgumentException("Dataset, build report, and artifact manifest evidence do not reconcile.");
        }
    }

    public GovernedTrafficDataset Dataset { get; }
    public TrafficModelBuildReport BuildReport { get; }
    public TrafficModelArtifactManifest Manifest { get; }
    public TrafficArtifactSignature Signature { get; }
}
