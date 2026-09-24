using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

public sealed record FileIconEvidenceCacheOptions(string CacheDirectory)
{
    public int MaximumContentBytes { get; init; } = IconContentWriteRequest.MaximumContentBytes;

    public int MaximumDocumentBytes { get; init; } = 6 * 1024 * 1024;

    public long MaximumDecodedPixels { get; init; } = IconPixelDimensions.MaximumPixels;

    public int MaximumEntries { get; init; } = 4096;

    public long MaximumCacheBytes { get; init; } = 512L * 1024 * 1024;
}

/// <summary>A bounded, atomic, machine-local store for icon bytes and their fingerprint evidence.</summary>
/// <remarks>
/// Each entry is one document so metadata can never name half-written content. A replacement is
/// written beside the target and renamed only after the bounded write has completed. Every read
/// re-hashes and re-decodes the untrusted document; changing the bytes, dimensions, algorithm, or
/// fingerprint therefore makes the entry invalid instead of silently changing recognition input.
/// Invalid documents are rebuildable cache data, so they are removed or ignored rather than
/// poisoning every other icon. An exclusive lock file coordinates the complete validation and
/// commit sequence across cache instances and processes that use this implementation.
///
/// Skia's whole-image codec call cannot observe managed cancellation while native code is running.
/// The encoded-byte, dimension, and decoded-pixel ceilings bound that window; cancellation is
/// checked immediately before and after it. Listing and batch reads decode on at most half the
/// machine's cores under one lease (#572), so the window per decode is unchanged. The cache has
/// no network client and no relay/export surface by design.
/// </remarks>
public sealed class FileIconEvidenceCache : IIconEvidenceCache, IIconEvidenceBatchReader
{
    private const int SchemaVersion = 1;
    private const string FileSuffix = ".icon-evidence-v1.json";
    private const string TemporaryFileSuffix = ".tmp";
    private const string LockFileName = ".icon-evidence.lock";
    private const int CacheKeyHexLength = SHA256.HashSizeInBytes * 2;
    private const int TemporaryNonceHexLength = 32;
    private const int LockRetryMilliseconds = 20;
    private const int MaximumConfiguredEntries = 65_536;
    private const long MaximumConfiguredCacheBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumConfiguredDocumentBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Each provenance level is an object plus its inputs array. The Core contract permits
        // eight input levels, so the JSON reader must admit that valid bounded lineage while
        // still refusing an unbounded nesting attack.
        MaxDepth = (EvidenceProvenance.MaxInputDepth * 2) + 8,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly FileIconEvidenceCacheOptions _options;
    private readonly string _cacheDirectory;

    public FileIconEvidenceCache(FileIconEvidenceCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CacheDirectory);
        if (options.MaximumContentBytes is < 1 or > IconContentWriteRequest.MaximumContentBytes ||
            options.MaximumDocumentBytes < options.MaximumContentBytes ||
            options.MaximumDocumentBytes > MaximumConfiguredDocumentBytes ||
            options.MaximumDecodedPixels is < 1 or > IconPixelDimensions.MaximumPixels ||
            options.MaximumEntries is < 1 or > MaximumConfiguredEntries ||
            options.MaximumCacheBytes is < 1 or > MaximumConfiguredCacheBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Icon cache limits must be positive and bounded.");
        }

        _options = options;
        _cacheDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.CacheDirectory));
    }

    public async Task<IconContentEvidenceAsset> StoreAsync(
        IconContentWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Content.Length > _options.MaximumContentBytes)
        {
            throw new InvalidDataException("The icon content exceeds the configured cache entry limit.");
        }

        await using var lease = await AcquireDirectoryLeaseAsync(cancellationToken).ConfigureAwait(false);
        CleanupTemporaryFiles(cancellationToken);

        // Keep every large copy and native decode behind the directory lease. Per-entry ceilings
        // alone did not prevent many queued StoreAsync calls retaining several copies apiece.
        var content = request.Content.ToArray();
        var decoded = Decode(content, _options.MaximumDecodedPixels, cancellationToken);
        var contentSha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var evidence = new IconContentEvidence(
            request.Key,
            request.RetrievedUtc,
            contentSha256,
            request.Provenance,
            decoded.Dimensions,
            decoded.Fingerprint);
        var document = CacheDocument.From(evidence, content);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (serialized.Length > _options.MaximumDocumentBytes)
        {
            throw new InvalidDataException("The icon cache document exceeds its size limit.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var targetPath = GetPath(request.Key);
        await EnsureCapacityAsync(targetPath, serialized.Length, cancellationToken).ConfigureAwait(false);

        var temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + TemporaryFileSuffix;
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(serialized, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDeleteCacheArtifact(temporaryPath);
        }

        return new IconContentEvidenceAsset(evidence, content);
    }

    public async Task<IconContentEvidenceAsset?> GetAsync(
        IconEvidenceKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(_cacheDirectory))
        {
            return null;
        }

        await using var lease = await AcquireDirectoryLeaseAsync(cancellationToken).ConfigureAwait(false);
        CleanupTemporaryFiles(cancellationToken);
        var path = GetPath(key);
        return !File.Exists(path)
            ? null
            : await TryReadAsync(path, key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads, validates and decodes many entries under one lease, handing each decoded picture to
    /// <paramref name="project"/> and keeping only what it returns.
    /// </summary>
    /// <remarks>
    /// #572: the reference index used to call <see cref="GetAsync"/> once per icon from every
    /// grid cell at once. Each call took the exclusive lease (a busy lease is retried every
    /// 20 ms), listed the directory for stray temporary files, validated by decoding, and then the
    /// caller decoded the same bytes a second time. One lease, one directory sweep and one decode
    /// per icon is what this is for. Decodes run on at most <paramref name="maximumParallelism"/>
    /// threads; each is still bounded by the same byte and pixel ceilings, so cancellation is
    /// observed as promptly as for a single decode.
    /// </remarks>
    public async Task<IReadOnlyDictionary<IconEvidenceKey, T>> ReadDecodedAsync<T>(
        IReadOnlyList<IconEvidenceKey> keys,
        Func<IconContentEvidence, CapturedImage, T?> project,
        int maximumParallelism,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        var results = new System.Collections.Concurrent.ConcurrentDictionary<IconEvidenceKey, T>();
        if (keys.Count == 0 || !Directory.Exists(_cacheDirectory))
        {
            return results;
        }

        await using var lease = await AcquireDirectoryLeaseAsync(cancellationToken).ConfigureAwait(false);
        CleanupTemporaryFiles(cancellationToken);
        await Parallel.ForEachAsync(
                keys,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Max(1, maximumParallelism),
                },
                async (key, token) =>
                {
                    var path = GetPath(key);
                    if (!File.Exists(path))
                    {
                        return;
                    }

                    T? projected = null;
                    try
                    {
                        await ReadAsync(path, key, token, (evidence, image) => projected = project(evidence, image)).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (IsDamagedCacheEntry(exception, token))
                    {
                        TryDeleteCacheArtifact(path);
                        return;
                    }

                    if (projected is not null)
                    {
                        results[key] = projected;
                    }
                })
            .ConfigureAwait(false);
        return results;
    }

    public async Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_cacheDirectory))
        {
            return [];
        }

        await using var lease = await AcquireDirectoryLeaseAsync(cancellationToken).ConfigureAwait(false);
        CleanupTemporaryFiles(cancellationToken);
        var entries = EnumerateBoundedEntries(excludedPath: null, cancellationToken);
        // #572: every entry is validated by a decode, 2.2 s for 5,320 icons one after another on
        // dev; each is independent, so they run on half the machine (the order is restored below).
        var evidence = new System.Collections.Concurrent.ConcurrentBag<IconContentEvidence>();
        await Parallel.ForEachAsync(
                entries,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                },
                async (entry, token) =>
                {
                    var asset = await TryReadAsync(entry.Path, expectedKey: null, token).ConfigureAwait(false);
                    if (asset is not null)
                    {
                        evidence.Add(asset.Evidence);
                    }
                })
            .ConfigureAwait(false);

        return evidence
            .OrderBy(item => item.CanonicalItemId, StringComparer.Ordinal)
            .ThenBy(item => item.SourceUri.AbsoluteUri, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<CacheDirectoryLease> AcquireDirectoryLeaseAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var lockPath = Path.Combine(_cacheDirectory, LockFileName);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
                return new CacheDirectoryLease(stream);
            }
            catch (IOException)
            {
                // FileShare.None is the cross-process lease. A crashed owner releases its handle;
                // keeping the zero-byte file avoids a create/delete race between later owners.
                await Task.Delay(LockRetryMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<IconContentEvidenceAsset?> TryReadAsync(
        string path,
        IconEvidenceKey? expectedKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(path, expectedKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsDamagedCacheEntry(exception, cancellationToken))
        {
            TryDeleteCacheArtifact(path);
            return null;
        }
    }

    private async Task<IconContentEvidenceAsset> ReadAsync(
        string path,
        IconEvidenceKey? expectedKey,
        CancellationToken cancellationToken,
        Action<IconContentEvidence, CapturedImage>? observeDecoded = null)
    {
        byte[] serialized;
        await using (var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length is < 1 ||
                stream.Length > _options.MaximumDocumentBytes ||
                stream.Length > Array.MaxLength)
            {
                throw new InvalidDataException("The icon cache document has an invalid size.");
            }

            serialized = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            await stream.ReadExactlyAsync(serialized, cancellationToken).ConfigureAwait(false);
        }

        CacheDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CacheDocument>(serialized, JsonOptions);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The icon cache document is not valid JSON.", exception);
        }

        if (document is null || document.SchemaVersion != SchemaVersion || document.Content is null)
        {
            throw new InvalidDataException("The icon cache document is missing required content.");
        }

        if (document.Content.Length is < 1 || document.Content.Length > _options.MaximumContentBytes)
        {
            throw new InvalidDataException("The cached icon content exceeds its size limit.");
        }

        IconContentEvidence evidence;
        try
        {
            if (!Uri.TryCreate(document.SourceUri, UriKind.Absolute, out var sourceUri) ||
                !TryParseFingerprint(document.FingerprintHex, out var fingerprintValue))
            {
                throw new InvalidDataException("The icon cache identity or fingerprint is invalid.");
            }

            var key = new IconEvidenceKey(document.CanonicalItemId!, sourceUri);
            if (expectedKey is not null && !KeysEqual(key, expectedKey))
            {
                throw new InvalidDataException("The icon cache document does not match the requested identity.");
            }

            if (!string.Equals(Path.GetFileName(path), Path.GetFileName(GetPath(key)), StringComparison.Ordinal))
            {
                throw new InvalidDataException("The icon cache document is stored under the wrong key.");
            }

            evidence = new IconContentEvidence(
                key,
                document.RetrievedUtc,
                document.ContentSha256!,
                document.Provenance!,
                new IconPixelDimensions(document.PixelWidth, document.PixelHeight),
                new IconFingerprintEvidence(
                    document.FingerprintAlgorithm!,
                    document.FingerprintAlgorithmVersion,
                    document.FingerprintBitCount,
                    fingerprintValue));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The icon cache metadata is invalid.", exception);
        }

        var actualHash = Convert.ToHexStringLower(SHA256.HashData(document.Content));
        if (!string.Equals(actualHash, evidence.ContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The cached icon content failed its SHA-256 check.");
        }

        var decoded = Decode(
            document.Content,
            _options.MaximumDecodedPixels,
            cancellationToken,
            observeDecoded is null ? null : (dimensions, fingerprint, image) =>
            {
                // Only a picture that is what the document says it is reaches the observer.
                if (dimensions == evidence.Dimensions && fingerprint == evidence.Fingerprint)
                {
                    observeDecoded(evidence, image);
                }
            });
        if (decoded.Dimensions != evidence.Dimensions || decoded.Fingerprint != evidence.Fingerprint)
        {
            throw new InvalidDataException("The cached icon dimensions or fingerprint do not match its content.");
        }

        return new IconContentEvidenceAsset(evidence, document.Content);
    }

    private async Task EnsureCapacityAsync(
        string targetPath,
        int replacementBytes,
        CancellationToken cancellationToken)
    {
        var entries = EnumerateBoundedEntries(targetPath, cancellationToken);
        var totalBytes = entries.Sum(entry => entry.Length);
        if (entries.Count >= _options.MaximumEntries || replacementBytes > _options.MaximumCacheBytes - totalBytes)
        {
            // Validate only when a write would otherwise be rejected. This recovers capacity from
            // a damaged but superficially well-sized document without decoding the whole cache on
            // every ordinary write.
            foreach (var entry in entries.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryReadAsync(entry.Path, expectedKey: null, cancellationToken).ConfigureAwait(false) is not null)
                {
                    continue;
                }

                entries.Remove(entry);
                totalBytes -= entry.Length;
                if (entries.Count < _options.MaximumEntries &&
                    replacementBytes <= _options.MaximumCacheBytes - totalBytes)
                {
                    break;
                }
            }
        }

        if (entries.Count >= _options.MaximumEntries)
        {
            throw new InvalidDataException("The local icon evidence cache has reached its entry limit.");
        }

        if (replacementBytes > _options.MaximumCacheBytes - totalBytes)
        {
            throw new InvalidDataException("The icon cache write would exceed the total byte limit.");
        }
    }

    private List<CacheFile> EnumerateBoundedEntries(
        string? excludedPath,
        CancellationToken cancellationToken)
    {
        var selected = new SortedDictionary<string, CacheFile>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*" + FileSuffix, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludedPath is not null && string.Equals(path, excludedPath, StringComparison.Ordinal))
            {
                continue;
            }

            long length;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDeleteCacheArtifact(path);
                continue;
            }

            if (length is < 1 || length > _options.MaximumDocumentBytes)
            {
                TryDeleteCacheArtifact(path);
                continue;
            }

            selected[path] = new CacheFile(path, length);
            if (selected.Count > _options.MaximumEntries)
            {
                // A prior crash, old implementation, or external damage may have left the cache
                // over its limits. Retain the ordinally first bounded set regardless of filesystem
                // enumeration order, and never retain an unbounded in-memory path list.
                var overflow = selected.Last();
                selected.Remove(overflow.Key);
                TryDeleteCacheArtifact(overflow.Value.Path);
            }
        }

        var entries = new List<CacheFile>(selected.Count);
        long totalBytes = 0;
        foreach (var entry in selected.Values)
        {
            if (entry.Length > _options.MaximumCacheBytes - totalBytes)
            {
                TryDeleteCacheArtifact(entry.Path);
                continue;
            }

            totalBytes = checked(totalBytes + entry.Length);
            entries.Add(entry);
        }

        return entries;
    }

    private string GetPath(IconEvidenceKey key)
    {
        var identity = key.CanonicalItemId + "\n" + key.SourceUri.AbsoluteUri;
        var cacheKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_cacheDirectory, cacheKey + FileSuffix);
    }

    private static bool KeysEqual(IconEvidenceKey left, IconEvidenceKey right) =>
        string.Equals(left.CanonicalItemId, right.CanonicalItemId, StringComparison.Ordinal) &&
        string.Equals(left.SourceUri.AbsoluteUri, right.SourceUri.AbsoluteUri, StringComparison.Ordinal);

    private static bool TryParseFingerprint(string? value, out ulong fingerprint)
    {
        fingerprint = default;
        return value is { Length: 16 } &&
               ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out fingerprint);
    }

    private void CleanupTemporaryFiles(CancellationToken cancellationToken)
    {
        // The configured directory can be shared or misconfigured. Match and validate the exact
        // name produced by StoreAsync so cache recovery never deletes another owner's temp file.
        foreach (var path in Directory.EnumerateFiles(
                     _cacheDirectory,
                     "*" + FileSuffix + ".*" + TemporaryFileSuffix,
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsCacheTemporaryArtifact(path))
            {
                TryDeleteCacheArtifact(path);
            }
        }
    }

    private static bool IsCacheTemporaryArtifact(string path)
    {
        ReadOnlySpan<char> fileName = Path.GetFileName(path);
        var nonceSeparatorIndex = CacheKeyHexLength + FileSuffix.Length;
        var expectedLength = nonceSeparatorIndex + 1 + TemporaryNonceHexLength + TemporaryFileSuffix.Length;
        return fileName.Length == expectedLength &&
               IsLowercaseHex(fileName[..CacheKeyHexLength]) &&
               fileName.Slice(CacheKeyHexLength, FileSuffix.Length).SequenceEqual(FileSuffix) &&
               fileName[nonceSeparatorIndex] == '.' &&
               IsLowercaseHex(fileName.Slice(nonceSeparatorIndex + 1, TemporaryNonceHexLength)) &&
               fileName[^TemporaryFileSuffix.Length..].SequenceEqual(TemporaryFileSuffix);
    }

    private static bool IsLowercaseHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDeleteCacheArtifact(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsDamagedCacheEntry(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException &&
        !cancellationToken.IsCancellationRequested &&
        exception is InvalidDataException or IOException or UnauthorizedAccessException;

    private static DecodedIcon Decode(
        byte[] content,
        long maximumDecodedPixels,
        CancellationToken cancellationToken,
        Action<IconPixelDimensions, IconFingerprintEvidence, CapturedImage>? observeDecoded = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var data = SKData.CreateCopy(content);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            throw new InvalidDataException("The icon content is not a supported image.");
        }

        var source = codec.Info;
        if (source.Width <= 0 ||
            source.Height <= 0 ||
            source.Width > IconPixelDimensions.MaximumDimension ||
            source.Height > IconPixelDimensions.MaximumDimension ||
            (long)source.Width * source.Height > maximumDecodedPixels)
        {
            throw new InvalidDataException("The decoded icon dimensions exceed their limits.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var target = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(target.RowBytes * target.Height));
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            // SKCodec exposes no cancellation token for this whole-image path. The call is bracketed
            // by cancellation checks and constrained to one leased decode of at most the validated
            // encoded-byte and decoded-pixel ceilings.
            if (codec.GetPixels(target, handle.AddrOfPinnedObject()) != SKCodecResult.Success)
            {
                throw new InvalidDataException("The icon image is incomplete or could not be decoded.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var image = new CapturedImage(
                pixels,
                target.Width,
                target.Height,
                target.RowBytes,
                PixelFormat.Bgra8888,
                DateTimeOffset.UnixEpoch,
                "local icon evidence");
            var hash = SkiaPerceptualIconMatcher.ComputeDifferenceHash(image);
            var decoded = new DecodedIcon(
                new IconPixelDimensions(source.Width, source.Height),
                new IconFingerprintEvidence(
                    IconFingerprintAlgorithms.DifferenceHashLuminance9X8,
                    IconFingerprintAlgorithms.DifferenceHashLuminance9X8Version,
                    IconFingerprintAlgorithms.DifferenceHashLuminance9X8Bits,
                    hash));
            // The pixels are cleared below, so an observer has to take what it needs now.
            observeDecoded?.Invoke(decoded.Dimensions, decoded.Fingerprint, image);
            return decoded;
        }
        finally
        {
            handle.Free();
            Array.Clear(pixels);
        }
    }

    private sealed record CacheDocument
    {
        public int SchemaVersion { get; init; }

        public string? CanonicalItemId { get; init; }

        public string? SourceUri { get; init; }

        public DateTimeOffset RetrievedUtc { get; init; }

        public string? ContentSha256 { get; init; }

        public EvidenceProvenance? Provenance { get; init; }

        public int PixelWidth { get; init; }

        public int PixelHeight { get; init; }

        public string? FingerprintAlgorithm { get; init; }

        public int FingerprintAlgorithmVersion { get; init; }

        public int FingerprintBitCount { get; init; }

        public string? FingerprintHex { get; init; }

        public byte[]? Content { get; init; }

        public static CacheDocument From(IconContentEvidence evidence, byte[] content) => new()
        {
            SchemaVersion = FileIconEvidenceCache.SchemaVersion,
            CanonicalItemId = evidence.CanonicalItemId,
            SourceUri = evidence.SourceUri.AbsoluteUri,
            RetrievedUtc = evidence.RetrievedUtc,
            ContentSha256 = evidence.ContentSha256,
            Provenance = evidence.Provenance,
            PixelWidth = evidence.Dimensions.Width,
            PixelHeight = evidence.Dimensions.Height,
            FingerprintAlgorithm = evidence.Fingerprint.Algorithm,
            FingerprintAlgorithmVersion = evidence.Fingerprint.AlgorithmVersion,
            FingerprintBitCount = evidence.Fingerprint.BitCount,
            FingerprintHex = evidence.Fingerprint.Value.ToString("x16", CultureInfo.InvariantCulture),
            Content = content.ToArray(),
        };
    }

    private sealed record DecodedIcon(
        IconPixelDimensions Dimensions,
        IconFingerprintEvidence Fingerprint);

    private sealed record CacheFile(string Path, long Length);

    private sealed class CacheDirectoryLease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
