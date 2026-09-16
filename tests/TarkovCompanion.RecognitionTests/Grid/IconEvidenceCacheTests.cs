using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SkiaSharp;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.RecognitionTests.Grid;

public sealed class IconEvidenceCacheTests : IDisposable
{
    private static readonly DateTimeOffset RetrievedUtc =
        new(2026, 9, 16, 3, 30, 0, TimeSpan.FromHours(2));

    private readonly string _cacheDirectory = Path.Combine(
        Path.GetTempPath(),
        $"tarkov-icon-evidence-{Guid.NewGuid():N}");

    [Fact]
    public async Task StoreRoundTripsContentAndAllRequiredEvidence()
    {
        var cache = CreateCache();
        var key = Key("item-a");
        var content = CreateGradientPng(reverse: false, width: 18, height: 16);
        var provenance = Provenance(key.SourceUri, RetrievedUtc.AddMinutes(-1));

        var stored = await cache.StoreAsync(
            new IconContentWriteRequest(key, RetrievedUtc, provenance, content),
            CancellationToken.None);
        var loaded = Assert.IsType<IconContentEvidenceAsset>(
            await cache.GetAsync(key, CancellationToken.None));
        var listed = await cache.ListEvidenceAsync(CancellationToken.None);

        Assert.Equal(content, loaded.Content.ToArray());
        Assert.Equal(key.CanonicalItemId, loaded.Evidence.CanonicalItemId);
        Assert.Equal(key.SourceUri, loaded.Evidence.SourceUri);
        Assert.Equal(RetrievedUtc.ToUniversalTime(), loaded.Evidence.RetrievedUtc);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(content)),
            loaded.Evidence.ContentSha256);
        Assert.Equal(provenance, loaded.Evidence.Provenance);
        Assert.Equal(new IconPixelDimensions(18, 16), loaded.Evidence.Dimensions);
        Assert.Equal(IconFingerprintAlgorithms.DifferenceHashLuminance9X8, loaded.Evidence.Fingerprint.Algorithm);
        Assert.Equal(
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8Version,
            loaded.Evidence.Fingerprint.AlgorithmVersion);
        Assert.Equal(IconFingerprintAlgorithms.DifferenceHashLuminance9X8Bits, loaded.Evidence.Fingerprint.BitCount);
        Assert.Equal(stored.Evidence, loaded.Evidence);
        Assert.Collection(listed, item => Assert.Equal(loaded.Evidence, item));
        Assert.Single(Directory.GetFiles(_cacheDirectory, "*.icon-evidence-v1.json"));
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task ReplacementOfSameIdentityCommitsOneCompleteDocument()
    {
        var cache = CreateCache();
        var key = Key("item-a");
        var first = CreateGradientPng(reverse: false);
        var second = CreateGradientPng(reverse: true);

        var original = await cache.StoreAsync(Request(key, first), CancellationToken.None);
        var replacement = await cache.StoreAsync(Request(key, second), CancellationToken.None);
        var loaded = Assert.IsType<IconContentEvidenceAsset>(
            await cache.GetAsync(key, CancellationToken.None));

        Assert.NotEqual(original.Evidence.ContentSha256, replacement.Evidence.ContentSha256);
        Assert.Equal(replacement.Evidence, loaded.Evidence);
        Assert.Equal(second, loaded.Content.ToArray());
        Assert.Single(Directory.GetFiles(_cacheDirectory, "*.icon-evidence-v1.json"));
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task UnknownJsonFieldsAreToleratedButFingerprintTamperingBecomesARecoverableMiss()
    {
        var cache = CreateCache();
        var key = Key("item-a");
        var stored = await cache.StoreAsync(Request(key, CreateGradientPng(reverse: false)), CancellationToken.None);
        var path = Assert.Single(Directory.GetFiles(_cacheDirectory, "*.icon-evidence-v1.json"));
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["futureMetadata"] = new JsonObject { ["ignored"] = true };
        await File.WriteAllTextAsync(path, document.ToJsonString());

        var withUnknownField = await cache.GetAsync(key, CancellationToken.None);

        Assert.NotNull(withUnknownField);

        document["fingerprintHex"] = (stored.Evidence.Fingerprint.Value ^ 1UL).ToString("x16");
        await File.WriteAllTextAsync(path, document.ToJsonString());

        Assert.Null(await cache.GetAsync(key, CancellationToken.None));
        Assert.False(File.Exists(path));

        var repaired = await cache.StoreAsync(Request(key, CreateGradientPng(reverse: true)), CancellationToken.None);
        Assert.Equal(repaired.Evidence, (await cache.GetAsync(key, CancellationToken.None))?.Evidence);
    }

    [Fact]
    public async Task MetadataThatClaimsHostileDimensionsIsRemovedBeforeUse()
    {
        var cache = CreateCache();
        var key = Key("item-a");
        await cache.StoreAsync(Request(key, CreateGradientPng(reverse: false)), CancellationToken.None);
        var path = Assert.Single(Directory.GetFiles(_cacheDirectory, "*.icon-evidence-v1.json"));
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["pixelWidth"] = IconPixelDimensions.MaximumDimension + 1;
        await File.WriteAllTextAsync(path, document.ToJsonString());

        Assert.Null(await cache.GetAsync(key, CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task InvalidOrOversizedEncodedContentNeverCreatesAnEntry()
    {
        var cache = CreateCache(new FileIconEvidenceCacheOptions(_cacheDirectory)
        {
            MaximumContentBytes = 64,
            MaximumDocumentBytes = 1024,
        });
        var key = Key("item-a");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.StoreAsync(Request(key, new byte[65]), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.StoreAsync(Request(key, new byte[] { 1, 2, 3, 4 }), CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.icon-evidence-v1.json"));
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task EntryLimitRejectsNewIdentityWithoutDamagingExistingEvidence()
    {
        var cache = CreateCache(new FileIconEvidenceCacheOptions(_cacheDirectory)
        {
            MaximumEntries = 1,
        });
        var firstKey = Key("item-a");
        var secondKey = Key("item-b");
        await cache.StoreAsync(Request(firstKey, CreateGradientPng(reverse: false)), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.StoreAsync(Request(secondKey, CreateGradientPng(reverse: true)), CancellationToken.None));

        Assert.NotNull(await cache.GetAsync(firstKey, CancellationToken.None));
        Assert.Null(await cache.GetAsync(secondKey, CancellationToken.None));
        Assert.Single(Directory.GetFiles(_cacheDirectory, "*.icon-evidence-v1.json"));
    }

    [Fact]
    public async Task CancellationBeforeStoreLeavesNoCacheArtifacts()
    {
        var cache = CreateCache();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.StoreAsync(Request(Key("item-a"), CreateGradientPng(reverse: false)), cancellation.Token));

        Assert.False(Directory.Exists(_cacheDirectory));
    }

    [Fact]
    public async Task ListRepairsMoreDocumentsThanItsConfiguredBound()
    {
        var cache = CreateCache(new FileIconEvidenceCacheOptions(_cacheDirectory)
        {
            MaximumEntries = 1,
        });
        await cache.StoreAsync(Request(Key("item-a"), CreateGradientPng(reverse: false)), CancellationToken.None);
        var original = Assert.Single(Directory.GetFiles(_cacheDirectory, "*.icon-evidence-v1.json"));
        File.Copy(original, Path.Combine(_cacheDirectory, "hostile.icon-evidence-v1.json"));

        var listed = await cache.ListEvidenceAsync(CancellationToken.None);

        Assert.Single(listed);
        Assert.Single(Directory.GetFiles(_cacheDirectory, "*.icon-evidence-v1.json"));
    }

    [Fact]
    public async Task ListRemovesDocumentsThatExceedItsConfiguredByteBudget()
    {
        await CreateCache().StoreAsync(
            Request(Key("item-a"), CreateGradientPng(reverse: false)),
            CancellationToken.None);
        var cache = CreateCache(new FileIconEvidenceCacheOptions(_cacheDirectory) { MaximumCacheBytes = 1 });

        Assert.Empty(await cache.ListEvidenceAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.icon-evidence-v1.json"));
    }

    [Fact]
    public async Task ListSkipsMalformedDocumentAndKeepsValidEvidence()
    {
        var cache = CreateCache();
        var valid = await cache.StoreAsync(
            Request(Key("item-a"), CreateGradientPng(reverse: false)),
            CancellationToken.None);
        var malformedPath = Path.Combine(_cacheDirectory, "malformed.icon-evidence-v1.json");
        await File.WriteAllTextAsync(malformedPath, "{");

        var listed = await cache.ListEvidenceAsync(CancellationToken.None);

        Assert.Collection(listed, evidence => Assert.Equal(valid.Evidence, evidence));
        Assert.False(File.Exists(malformedPath));
    }

    [Fact]
    public async Task StoreRecoversCapacityFromMalformedAndOversizedUnrelatedDocuments()
    {
        var options = new FileIconEvidenceCacheOptions(_cacheDirectory)
        {
            MaximumContentBytes = 4096,
            MaximumDocumentBytes = 8192,
            MaximumEntries = 1,
        };
        Directory.CreateDirectory(_cacheDirectory);
        var malformedPath = Path.Combine(_cacheDirectory, "0000.icon-evidence-v1.json");
        var oversizedPath = Path.Combine(_cacheDirectory, "ffff.icon-evidence-v1.json");
        await File.WriteAllTextAsync(malformedPath, "{");
        await File.WriteAllBytesAsync(oversizedPath, new byte[options.MaximumDocumentBytes + 1]);

        var stored = await CreateCache(options).StoreAsync(
            Request(Key("item-a"), CreateGradientPng(reverse: false)),
            CancellationToken.None);

        Assert.Equal(stored.Evidence, (await CreateCache(options)
            .GetAsync(Key("item-a"), CancellationToken.None))?.Evidence);
        Assert.False(File.Exists(malformedPath));
        Assert.False(File.Exists(oversizedPath));
        Assert.Single(Directory.GetFiles(_cacheDirectory, "*.icon-evidence-v1.json"));
    }

    [Fact]
    public async Task StoreRemovesStaleTemporaryArtifactBeforeCommit()
    {
        Directory.CreateDirectory(_cacheDirectory);
        var stale = Path.Combine(_cacheDirectory, "abandoned.icon-evidence-v1.json.deadbeef.tmp");
        await File.WriteAllBytesAsync(stale, new byte[32]);

        await CreateCache().StoreAsync(
            Request(Key("item-a"), CreateGradientPng(reverse: false)),
            CancellationToken.None);

        Assert.False(File.Exists(stale));
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task SeparateInstancesCoordinateTheEntryLimit()
    {
        var options = new FileIconEvidenceCacheOptions(_cacheDirectory) { MaximumEntries = 1 };
        var first = CreateCache(options).StoreAsync(
            Request(Key("item-a"), CreateGradientPng(reverse: false)),
            CancellationToken.None);
        var second = CreateCache(options).StoreAsync(
            Request(Key("item-b"), CreateGradientPng(reverse: true)),
            CancellationToken.None);

        var failures = await Task.WhenAll(
            Record.ExceptionAsync(() => first),
            Record.ExceptionAsync(() => second));

        Assert.Equal(1, failures.Count(exception => exception is null));
        Assert.Equal(1, failures.Count(exception => exception is InvalidDataException));
        Assert.Single(Directory.GetFiles(_cacheDirectory, "*.icon-evidence-v1.json"));
    }

    [Fact]
    public async Task CancellationWhileWaitingForDirectoryLeaseOccursBeforeDecode()
    {
        Directory.CreateDirectory(_cacheDirectory);
        await using var heldLease = new FileStream(
            Path.Combine(_cacheDirectory, ".icon-evidence.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource();

        var pending = CreateCache().StoreAsync(
            Request(Key("item-a"), [1, 2, 3, 4]),
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.icon-evidence-v1.json"));
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task SeparateInstancesSerializeSameKeyReplacements()
    {
        var key = Key("item-a");
        var first = CreateCache().StoreAsync(
            Request(key, CreateGradientPng(reverse: false)),
            CancellationToken.None);
        var second = CreateCache().StoreAsync(
            Request(key, CreateGradientPng(reverse: true)),
            CancellationToken.None);

        var stored = await Task.WhenAll(first, second);
        var loaded = Assert.IsType<IconContentEvidenceAsset>(
            await CreateCache().GetAsync(key, CancellationToken.None));

        Assert.Contains(stored, asset => asset.Evidence == loaded.Evidence);
        Assert.Single(Directory.GetFiles(_cacheDirectory, "*.icon-evidence-v1.json"));
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task VersionOneGoldenVectorsLockAlphaCompositeScalingAndBitOrder()
    {
        var cache = CreateCache();
        var transparent = await cache.StoreAsync(
            Request(Key("transparent"), CreateGoldenPng(scale: 1, compositedOverGrid: false)),
            CancellationToken.None);
        var composite = await cache.StoreAsync(
            Request(Key("composite"), CreateGoldenPng(scale: 1, compositedOverGrid: true)),
            CancellationToken.None);
        var scaledComposite = await cache.StoreAsync(
            Request(Key("composite-scaled"), CreateGoldenPng(scale: 2, compositedOverGrid: true)),
            CancellationToken.None);

        Assert.Equal(IconFingerprintAlgorithms.DifferenceHashLuminance9X8Version,
            transparent.Evidence.Fingerprint.AlgorithmVersion);
        Assert.Equal(0xaaaaaaaaaaaaaaaaUL, transparent.Evidence.Fingerprint.Value);
        Assert.Equal(0x5555555555555555UL, composite.Evidence.Fingerprint.Value);
        Assert.Equal(composite.Evidence.Fingerprint.Value, scaledComposite.Evidence.Fingerprint.Value);
    }

    [Fact]
    public void CacheIdentityRejectsNonHttpsCredentialsFragmentsAndUnboundedIds()
    {
        Assert.Throws<ArgumentException>(() =>
            new IconEvidenceKey("item", new Uri("http://assets.tarkov.dev/item.png")));
        Assert.Throws<ArgumentException>(() =>
            new IconEvidenceKey("item", new Uri("https://user@example.test/item.png")));
        Assert.Throws<ArgumentException>(() =>
            new IconEvidenceKey("item", new Uri("https://example.test/item.png#fragment")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IconEvidenceKey(new string('i', IconEvidenceKey.MaximumCanonicalItemIdLength + 1),
                new Uri("https://example.test/item.png")));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A failed test may still have a file handle settling; the OS temp directory owns it.
        }
        catch (UnauthorizedAccessException)
        {
            // As above. Tests never use a path outside their unique temporary directory.
        }
    }

    private FileIconEvidenceCache CreateCache(FileIconEvidenceCacheOptions? options = null) =>
        new(options ?? new FileIconEvidenceCacheOptions(_cacheDirectory));

    private static IconContentWriteRequest Request(IconEvidenceKey key, byte[] content) =>
        new(key, RetrievedUtc, Provenance(key.SourceUri, RetrievedUtc.AddMinutes(-1)), content);

    private static IconEvidenceKey Key(string canonicalItemId) => new(
        canonicalItemId,
        new Uri($"https://assets.tarkov.dev/icons/{canonicalItemId}.png"));

    private static EvidenceProvenance Provenance(Uri sourceUri, DateTimeOffset observedUtc) => new(
        EvidenceSourceClass.PublicStructuredData,
        sourceUri.AbsoluteUri,
        observedUtc,
        EvidenceConfidence.Unscored,
        new ProducerIdentity("icon-cache-fixture", "1"),
        reference: sourceUri.AbsoluteUri);

    private static byte[] CreateGradientPng(bool reverse, int width = 90, int height = 80)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)((x * 255) / Math.Max(1, width - 1));
                if (reverse)
                {
                    value = (byte)(255 - value);
                }

                bitmap.SetPixel(x, y, new SKColor(value, value, value, 255));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateGoldenPng(int scale, bool compositedOverGrid)
    {
        var width = 9 * scale;
        var height = 8 * scale;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var logicalColumn = x / scale;
                SKColor color;
                if (compositedOverGrid)
                {
                    // Transparent source columns expose a bright grid cell; half-alpha icon
                    // columns composite to a darker 110 value over the neighbouring dark cell.
                    var value = (byte)(logicalColumn % 2 == 0 ? 180 : 110);
                    color = new SKColor(value, value, value, 255);
                }
                else
                {
                    color = logicalColumn % 2 == 0
                        ? new SKColor(255, 255, 255, 0)
                        : new SKColor(200, 200, 200, 128);
                }

                bitmap.SetPixel(x, y, color);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
