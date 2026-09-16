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
/// The cache has no network client and no relay/export surface by design.
/// </remarks>
public sealed class FileIconEvidenceCache : IIconEvidenceCache
{
    private const int SchemaVersion = 1;
    private const string FileSuffix = ".icon-evidence-v1.json";
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
    private readonly SemaphoreSlim _gate = new(1, 1);

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
        _cacheDirectory = Path.GetFullPath(options.CacheDirectory);
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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_cacheDirectory);
            var targetPath = GetPath(request.Key);
            EnsureCapacity(targetPath, serialized.Length, cancellationToken);

            var temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
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
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }

        return new IconContentEvidenceAsset(evidence, content);
    }

    public async Task<IconContentEvidenceAsset?> GetAsync(
        IconEvidenceKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(key);
            return !File.Exists(path)
                ? null
                : await ReadAsync(path, key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                return [];
            }

            var paths = EnumerateBoundedPaths(cancellationToken);
            var evidence = new List<IconContentEvidence>(paths.Count);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var asset = await ReadAsync(path, expectedKey: null, cancellationToken).ConfigureAwait(false);
                evidence.Add(asset.Evidence);
            }

            return evidence
                .OrderBy(item => item.CanonicalItemId, StringComparer.Ordinal)
                .ThenBy(item => item.SourceUri.AbsoluteUri, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IconContentEvidenceAsset> ReadAsync(
        string path,
        IconEvidenceKey? expectedKey,
        CancellationToken cancellationToken)
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
        catch (JsonException exception)
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

        var decoded = Decode(document.Content, _options.MaximumDecodedPixels, cancellationToken);
        if (decoded.Dimensions != evidence.Dimensions || decoded.Fingerprint != evidence.Fingerprint)
        {
            throw new InvalidDataException("The cached icon dimensions or fingerprint do not match its content.");
        }

        return new IconContentEvidenceAsset(evidence, document.Content);
    }

    private void EnsureCapacity(string targetPath, int replacementBytes, CancellationToken cancellationToken)
    {
        var entryCount = 0;
        long totalBytes = 0;
        foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*" + FileSuffix, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(path, targetPath, StringComparison.Ordinal))
            {
                continue;
            }

            entryCount++;
            if (entryCount >= _options.MaximumEntries)
            {
                throw new InvalidDataException("The local icon evidence cache has reached its entry limit.");
            }

            var length = new FileInfo(path).Length;
            if (length is < 1 || length > _options.MaximumDocumentBytes)
            {
                throw new InvalidDataException("An existing icon cache document has an invalid size.");
            }

            totalBytes = checked(totalBytes + length);
            if (totalBytes > _options.MaximumCacheBytes)
            {
                throw new InvalidDataException("The local icon evidence cache exceeds its byte limit.");
            }
        }

        if (replacementBytes > _options.MaximumCacheBytes - totalBytes)
        {
            throw new InvalidDataException("The icon cache write would exceed the total byte limit.");
        }
    }

    private List<string> EnumerateBoundedPaths(CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        long totalBytes = 0;
        foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*" + FileSuffix, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paths.Count == _options.MaximumEntries)
            {
                throw new InvalidDataException("The local icon evidence cache exceeds its entry limit.");
            }

            var length = new FileInfo(path).Length;
            if (length is < 1 || length > _options.MaximumDocumentBytes)
            {
                throw new InvalidDataException("An existing icon cache document has an invalid size.");
            }

            totalBytes = checked(totalBytes + length);
            if (totalBytes > _options.MaximumCacheBytes)
            {
                throw new InvalidDataException("The local icon evidence cache exceeds its byte limit.");
            }

            paths.Add(path);
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
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

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The committed target is already complete. A locked temp remains inert and is never
            // enumerated as cache evidence; a later external cache cleanup may remove it.
        }
        catch (UnauthorizedAccessException)
        {
            // As above: cleanup failure must not turn an already committed atomic write into a
            // reported cache failure.
        }
    }

    private static DecodedIcon Decode(
        byte[] content,
        long maximumDecodedPixels,
        CancellationToken cancellationToken)
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
            return new DecodedIcon(
                new IconPixelDimensions(source.Width, source.Height),
                new IconFingerprintEvidence(
                    IconFingerprintAlgorithms.DifferenceHashLuminance9X8,
                    IconFingerprintAlgorithms.DifferenceHashLuminance9X8Version,
                    IconFingerprintAlgorithms.DifferenceHashLuminance9X8Bits,
                    hash));
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
}
