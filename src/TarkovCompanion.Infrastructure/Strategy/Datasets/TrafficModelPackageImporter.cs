using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using TarkovCompanion.Core.Domain.Strategy.Data;

namespace TarkovCompanion.Infrastructure.Strategy.Datasets;

public sealed record TrafficModelImportLimits(
    int MaximumDatasetBytes = 16 * 1024 * 1024,
    int MaximumReportBytes = 2 * 1024 * 1024,
    int MaximumManifestBytes = 256 * 1024,
    int MaximumSignatureBytes = 16 * 1024,
    int MaximumArtifactBytes = 64 * 1024 * 1024)
{
    public TrafficModelImportLimits Validate()
    {
        const int absoluteMaximumBytes = 256 * 1024 * 1024;
        if (MaximumDatasetBytes is <= 0 or > absoluteMaximumBytes ||
            MaximumReportBytes is <= 0 or > absoluteMaximumBytes ||
            MaximumManifestBytes is <= 0 or > absoluteMaximumBytes ||
            MaximumSignatureBytes is <= 0 or > absoluteMaximumBytes ||
            MaximumArtifactBytes is <= 0 or > absoluteMaximumBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(TrafficModelImportLimits), "Import bounds must be positive.");
        }

        return this;
    }
}

public sealed record TrafficModelPackageStreams(
    Stream Dataset,
    Stream BuildReport,
    Stream Manifest,
    Stream Signature,
    Stream Artifact);

public enum TrafficModelImportDisposition
{
    Accepted = 1,
    Quarantined,
    Incompatible,
}

public sealed record TrafficModelImportResult(
    TrafficModelImportDisposition Disposition,
    string ReceiptSha256,
    string ReasonCode,
    TrafficModelPublication? Publication,
    byte[]? DatasetJson,
    byte[]? BuildReportJson,
    byte[]? ManifestJson,
    byte[]? SignatureJson,
    byte[]? Artifact)
{
    public bool IsAccepted => Disposition == TrafficModelImportDisposition.Accepted;
}

public interface ITrafficArtifactSignatureVerifier
{
    bool Verify(
        ReadOnlySpan<byte> manifestSha256,
        TrafficArtifactSignature signature);
}

public sealed class EcdsaTrafficArtifactSignatureVerifier : ITrafficArtifactSignatureVerifier, IDisposable
{
    private readonly IReadOnlyDictionary<string, TrustedKey> _trustedKeys;

    public EcdsaTrafficArtifactSignatureVerifier(IReadOnlyDictionary<string, byte[]> subjectPublicKeyInfoByKeyId)
    {
        ArgumentNullException.ThrowIfNull(subjectPublicKeyInfoByKeyId);
        var keys = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in subjectPublicKeyInfoByKeyId)
            {
                var keyId = RequireKeyId(pair.Key);
                ArgumentNullException.ThrowIfNull(pair.Value);
                var key = ImportTrustedKey(pair.Value, nameof(subjectPublicKeyInfoByKeyId));
                if (!keys.TryAdd(keyId, key))
                {
                    key.Dispose();
                    throw new ArgumentException("Traffic signing key ids must be unique.", nameof(subjectPublicKeyInfoByKeyId));
                }
            }

            if (keys.Count == 0)
            {
                throw new ArgumentException("At least one traffic signing key is required.", nameof(subjectPublicKeyInfoByKeyId));
            }

            _trustedKeys = keys;
        }
        catch
        {
            foreach (var key in keys.Values)
            {
                key.Dispose();
            }

            throw;
        }
    }

    public bool Verify(ReadOnlySpan<byte> manifestSha256, TrafficArtifactSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (manifestSha256.Length != SHA256.HashSizeInBytes ||
            signature.Algorithm != TrafficSignatureAlgorithm.EcdsaP256Sha256 ||
            !_trustedKeys.TryGetValue(signature.KeyId, out var key))
        {
            return false;
        }

        return key.Verify(
            manifestSha256,
            Convert.FromBase64String(signature.SignatureBase64));
    }

    public void Dispose()
    {
        foreach (var key in _trustedKeys.Values)
        {
            key.Dispose();
        }
    }

    private static string RequireKeyId(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > TrafficDataBounds.MaximumTextLength ||
            keyId != keyId.Trim() || keyId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("A canonical traffic signing key id is required.", nameof(keyId));
        }

        return keyId;
    }

    private static TrustedKey ImportTrustedKey(byte[] subjectPublicKeyInfo, string parameterName)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != subjectPublicKeyInfo.Length || key.KeySize != 256)
            {
                throw new ArgumentException("Traffic signing keys must be exact P-256 public keys.", parameterName);
            }

            return new TrustedKey(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <remarks>
    /// <see cref="ECDsa"/> does not promise concurrent instance safety. Package downloads may be
    /// validated in parallel, so each trusted key serializes verification and disposal rather than
    /// sharing an unguarded native handle.
    /// </remarks>
    private sealed class TrustedKey(ECDsa key) : IDisposable
    {
        private readonly object _gate = new();
        private ECDsa? _key = key;

        public bool Verify(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature)
        {
            lock (_gate)
            {
                var current = _key ?? throw new ObjectDisposedException(nameof(TrustedKey));
                return current.VerifyHash(hash, signature, DSASignatureFormat.Rfc3279DerSequence);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                Interlocked.Exchange(ref _key, null)?.Dispose();
            }
        }
    }
}

/// <summary>Reads an untrusted package completely before exposing it as an install candidate.</summary>
public sealed class TrafficModelPackageImporter
{
    private readonly ITrafficArtifactSignatureVerifier _signatureVerifier;
    private readonly TrafficModelImportLimits _limits;

    public TrafficModelPackageImporter(
        ITrafficArtifactSignatureVerifier signatureVerifier,
        TrafficModelImportLimits? limits = null)
    {
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _limits = (limits ?? new TrafficModelImportLimits()).Validate();
    }

    public async Task<TrafficModelImportResult> ImportAsync(
        TrafficModelPackageStreams package,
        TrafficCompatibilityScope? requiredScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        var receiptHash = "unavailable";
        try
        {
            var datasetJson = await ReadBoundedAsync(package.Dataset, _limits.MaximumDatasetBytes, cancellationToken).ConfigureAwait(false);
            var reportJson = await ReadBoundedAsync(package.BuildReport, _limits.MaximumReportBytes, cancellationToken).ConfigureAwait(false);
            var manifestJson = await ReadBoundedAsync(package.Manifest, _limits.MaximumManifestBytes, cancellationToken).ConfigureAwait(false);
            var signatureJson = await ReadBoundedAsync(package.Signature, _limits.MaximumSignatureBytes, cancellationToken).ConfigureAwait(false);
            var artifact = await ReadBoundedAsync(package.Artifact, _limits.MaximumArtifactBytes, cancellationToken).ConfigureAwait(false);
            receiptHash = ReceiptHash(datasetJson, reportJson, manifestJson, signatureJson, artifact);

            var dataset = Deserialize<GovernedTrafficDataset>(datasetJson, "dataset");
            var report = Deserialize<TrafficModelBuildReport>(reportJson, "build report");
            var manifest = Deserialize<TrafficModelArtifactManifest>(manifestJson, "manifest");
            var signature = Deserialize<TrafficArtifactSignature>(signatureJson, "signature");

            if (!string.Equals(TrafficModelBuilder.ComputeDatasetContentSha256(dataset), dataset.ContentSha256, StringComparison.Ordinal) ||
                !dataset.Partitions.SequenceEqual(Enum.GetValues<TrafficDataPartition>()
                    .Select(partition => TrafficModelBuilder.ComputePartitionDigest(partition, dataset.Records))))
            {
                return Rejected(TrafficModelImportDisposition.Quarantined, receiptHash, "dataset-content-mismatch");
            }

            var reportHash = TrafficModelBuilder.Sha256(reportJson);
            var artifactHash = TrafficModelBuilder.Sha256(artifact);
            var manifestHashBytes = SHA256.HashData(manifestJson);
            var manifestHash = Convert.ToHexStringLower(manifestHashBytes);
            if (!string.Equals(reportHash, manifest.BuildReportSha256, StringComparison.Ordinal) ||
                !string.Equals(artifactHash, manifest.ArtifactSha256, StringComparison.Ordinal) ||
                !string.Equals(manifestHash, signature.SignedManifestSha256, StringComparison.Ordinal))
            {
                return Rejected(TrafficModelImportDisposition.Quarantined, receiptHash, "package-integrity-mismatch");
            }

            if (!_signatureVerifier.Verify(manifestHashBytes, signature))
            {
                return Rejected(TrafficModelImportDisposition.Quarantined, receiptHash, "signature-invalid");
            }

            var publication = new TrafficModelPublication(dataset, report, manifest, signature);
            if (requiredScope is not null && !manifest.Supports(requiredScope))
            {
                return Rejected(TrafficModelImportDisposition.Incompatible, receiptHash, "coverage-unknown-for-scope");
            }

            return new TrafficModelImportResult(
                TrafficModelImportDisposition.Accepted,
                receiptHash,
                "accepted",
                publication,
                datasetJson,
                reportJson,
                manifestJson,
                signatureJson,
                artifact);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException or CryptographicException)
        {
            return Rejected(TrafficModelImportDisposition.Quarantined, receiptHash, "invalid-or-hostile-package");
        }
    }

    private static T Deserialize<T>(byte[] bytes, string description)
    {
        try
        {
            RejectDuplicateProperties(bytes, description);
            return JsonSerializer.Deserialize<T>(bytes, TrafficDataJson.Options)
                   ?? throw new InvalidDataException($"Traffic {description} was null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Traffic {description} did not satisfy the strict schema.", exception);
        }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes, string description)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = TrafficDataJson.MaxDepth,
        });
        var containers = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    containers.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    break;
                case JsonTokenType.StartArray:
                    containers.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (containers.Count == 0)
                    {
                        throw new InvalidDataException($"Traffic {description} has an invalid container boundary.");
                    }

                    containers.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var name = reader.GetString()!;
                    if (containers.Count == 0 || containers.Peek() is not { } properties || !properties.Add(name))
                    {
                        throw new InvalidDataException($"Traffic {description} contains a duplicate property.");
                    }

                    break;
            }
        }

        if (containers.Count != 0)
        {
            throw new InvalidDataException($"Traffic {description} has an unterminated container.");
        }
    }

    private static TrafficModelImportResult Rejected(
        TrafficModelImportDisposition disposition,
        string receiptHash,
        string reasonCode) =>
        new(disposition, receiptHash, reasonCode, null, null, null, null, null, null);

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var destination = new MemoryStream(capacity: Math.Min(maximumBytes, 64 * 1024));
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (destination.Length + read > maximumBytes)
                {
                    throw new InvalidDataException("Traffic package member exceeds its byte limit.");
                }

                destination.Write(buffer, 0, read);
            }

            if (destination.Length == 0)
            {
                throw new InvalidDataException("Traffic package members cannot be empty.");
            }

            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string ReceiptHash(params byte[][] members)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var member in members)
        {
            hash.AppendData(SHA256.HashData(member));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
