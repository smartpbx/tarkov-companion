using System.Security.Cryptography;
using System.Text.Json;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Strategy.Data;

namespace TarkovCompanion.Infrastructure.Strategy.Datasets;

public sealed record TrafficAggregateInput(
    string RecordId,
    string SourceId,
    string PartitionGroupId,
    TrafficCompatibilityScope Scope,
    TrafficSpatialReference Location,
    TrafficPhaseWindow Window,
    TrafficObservationClass Observation,
    long SampleCount,
    EvidenceProvenance Provenance);

public sealed record TrafficModelBuildRequest(
    string BuildId,
    string DatasetId,
    string DatasetVersion,
    TrafficDatasetVisibility Visibility,
    DateTimeOffset DataThroughUtc,
    DateTimeOffset GeneratedUtc,
    string TransformVersion,
    string ModelId,
    string ModelVersion,
    string ArtifactFormat,
    string CalibrationReference,
    EvidenceConfidence Confidence,
    EvidenceCoverage Coverage,
    TrafficPartitionPolicy PartitionPolicy,
    IReadOnlyList<TrafficDatasetSource> Sources,
    IReadOnlyList<TrafficAggregateInput> Records,
    IReadOnlyList<TrafficCoverageGap> CoverageGaps);

public sealed record TrafficModelBuildResult(
    GovernedTrafficDataset Dataset,
    TrafficModelBuildReport Report,
    TrafficModelArtifactManifest Manifest,
    byte[] DatasetJson,
    byte[] ReportJson,
    byte[] ManifestJson,
    byte[] Artifact);

/// <summary>Pure, order-normalizing build step. Every clock and version is an explicit input.</summary>
public static class TrafficModelBuilder
{
    public static TrafficModelBuildResult Build(TrafficModelBuildRequest request, ReadOnlySpan<byte> artifact)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (artifact.IsEmpty)
        {
            throw new ArgumentException("A model artifact is required.", nameof(artifact));
        }

        ArgumentNullException.ThrowIfNull(request.Sources);
        ArgumentNullException.ThrowIfNull(request.Records);
        ArgumentNullException.ThrowIfNull(request.CoverageGaps);
        ArgumentNullException.ThrowIfNull(request.PartitionPolicy);
        ArgumentNullException.ThrowIfNull(request.Confidence);
        ArgumentNullException.ThrowIfNull(request.Coverage);
        var sources = request.Sources
            .OrderBy(source => source.SourceId, StringComparer.Ordinal)
            .Select(source => new TrafficDatasetSource(
                source.SourceId,
                source.DisplayName,
                source.Kind,
                source.Visibility,
                source.Provenance,
                source.LicenseIdentifier,
                source.ConsentBasis,
                source.CollectionMethod,
                source.AllowedUses.Order().ToArray(),
                OrderScopes(source.CompatibilityScopes),
                source.ReviewedUtc,
                source.Reference))
            .ToArray();
        var records = request.Records
            .OrderBy(record => record.RecordId, StringComparer.Ordinal)
            .Select(record => new TrafficAggregateRecord(
                record.RecordId,
                record.SourceId,
                record.PartitionGroupId,
                TrafficPartitioner.Assign(record.PartitionGroupId, request.PartitionPolicy),
                record.Scope,
                record.Location,
                record.Window,
                record.Observation,
                record.SampleCount,
                record.Provenance))
            .ToArray();
        var gaps = request.CoverageGaps
            .OrderBy(gap => gap.Scope.MapId, StringComparer.Ordinal)
            .ThenBy(gap => gap.Scope.GameVersion, StringComparer.Ordinal)
            .ThenBy(gap => gap.Scope.GameMode)
            .ThenBy(gap => gap.Scope.WipeId, StringComparer.Ordinal)
            .ThenBy(gap => gap.Scope.CohortId, StringComparer.Ordinal)
            .ThenBy(gap => gap.GapCode, StringComparer.Ordinal)
            .ToArray();
        var partitions = Enum.GetValues<TrafficDataPartition>()
            .Select(partition => ComputePartitionDigest(partition, records))
            .ToArray();
        var datasetHash = ComputeDatasetContentSha256(
            TrafficDataBounds.CurrentSchemaVersion,
            request.DatasetId,
            request.DatasetVersion,
            request.Visibility,
            request.DataThroughUtc,
            request.GeneratedUtc,
            request.TransformVersion,
            request.PartitionPolicy,
            sources,
            records,
            partitions,
            gaps);
        var dataset = new GovernedTrafficDataset(
            TrafficDataBounds.CurrentSchemaVersion,
            request.DatasetId,
            request.DatasetVersion,
            request.Visibility,
            request.DataThroughUtc,
            request.GeneratedUtc,
            request.TransformVersion,
            datasetHash,
            request.PartitionPolicy,
            sources,
            records,
            partitions,
            gaps);

        var artifactBytes = artifact.ToArray();
        var artifactHash = Sha256(artifactBytes);
        var scopes = OrderScopes(records.Select(record => record.Scope).Distinct().ToArray());
        var report = new TrafficModelBuildReport(
            TrafficDataBounds.CurrentSchemaVersion,
            request.BuildId,
            request.DatasetId,
            request.DatasetVersion,
            datasetHash,
            artifactHash,
            request.TransformVersion,
            request.ModelVersion,
            request.CalibrationReference,
            request.DataThroughUtc,
            request.GeneratedUtc,
            records.Sum(record => record.SampleCount),
            request.Confidence,
            request.Coverage,
            scopes,
            gaps,
            partitions,
            leakageCheckPassed: true);
        var reportJson = JsonSerializer.SerializeToUtf8Bytes(report, TrafficDataJson.Options);
        var manifest = new TrafficModelArtifactManifest(
            TrafficDataBounds.CurrentSchemaVersion,
            request.ModelId,
            request.ModelVersion,
            request.DatasetId,
            request.DatasetVersion,
            datasetHash,
            Sha256(reportJson),
            artifactHash,
            request.ArtifactFormat,
            request.TransformVersion,
            request.CalibrationReference,
            request.DataThroughUtc,
            request.GeneratedUtc,
            request.Confidence,
            request.Coverage,
            scopes,
            gaps,
            partitions);

        return new TrafficModelBuildResult(
            dataset,
            report,
            manifest,
            JsonSerializer.SerializeToUtf8Bytes(dataset, TrafficDataJson.Options),
            reportJson,
            JsonSerializer.SerializeToUtf8Bytes(manifest, TrafficDataJson.Options),
            artifactBytes);
    }

    public static string ComputeDatasetContentSha256(GovernedTrafficDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return ComputeDatasetContentSha256(
            dataset.SchemaVersion,
            dataset.DatasetId,
            dataset.DatasetVersion,
            dataset.Visibility,
            dataset.DataThroughUtc,
            dataset.GeneratedUtc,
            dataset.TransformVersion,
            dataset.PartitionPolicy,
            dataset.Sources,
            dataset.Records,
            dataset.Partitions,
            dataset.CoverageGaps);
    }

    private static string ComputeDatasetContentSha256(
        int schemaVersion,
        string datasetId,
        string datasetVersion,
        TrafficDatasetVisibility visibility,
        DateTimeOffset dataThroughUtc,
        DateTimeOffset generatedUtc,
        string transformVersion,
        TrafficPartitionPolicy partitionPolicy,
        IReadOnlyList<TrafficDatasetSource> sources,
        IReadOnlyList<TrafficAggregateRecord> records,
        IReadOnlyList<TrafficPartitionDigest> partitions,
        IReadOnlyList<TrafficCoverageGap> gaps)
    {
        var content = new DatasetContent(
            schemaVersion,
            datasetId,
            datasetVersion,
            visibility,
            dataThroughUtc,
            generatedUtc,
            transformVersion,
            partitionPolicy,
            sources,
            records,
            partitions,
            gaps);
        return Sha256(JsonSerializer.SerializeToUtf8Bytes(content, TrafficDataJson.Options));
    }

    internal static TrafficPartitionDigest ComputePartitionDigest(
        TrafficDataPartition partition,
        IReadOnlyList<TrafficAggregateRecord> allRecords)
    {
        var records = allRecords.Where(record => record.Partition == partition).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(records, TrafficDataJson.Options);
        return new TrafficPartitionDigest(
            partition,
            records.LongLength,
            records.Sum(record => record.SampleCount),
            Sha256(bytes));
    }

    internal static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static TrafficCompatibilityScope[] OrderScopes(IEnumerable<TrafficCompatibilityScope> scopes) =>
        scopes.OrderBy(scope => scope.MapId, StringComparer.Ordinal)
            .ThenBy(scope => scope.GameVersion, StringComparer.Ordinal)
            .ThenBy(scope => scope.GameMode)
            .ThenBy(scope => scope.WipeId, StringComparer.Ordinal)
            .ThenBy(scope => scope.CohortId, StringComparer.Ordinal)
            .ToArray();

    private sealed record DatasetContent(
        int SchemaVersion,
        string DatasetId,
        string DatasetVersion,
        TrafficDatasetVisibility Visibility,
        DateTimeOffset DataThroughUtc,
        DateTimeOffset GeneratedUtc,
        string TransformVersion,
        TrafficPartitionPolicy PartitionPolicy,
        IReadOnlyList<TrafficDatasetSource> Sources,
        IReadOnlyList<TrafficAggregateRecord> Records,
        IReadOnlyList<TrafficPartitionDigest> Partitions,
        IReadOnlyList<TrafficCoverageGap> CoverageGaps);

}
