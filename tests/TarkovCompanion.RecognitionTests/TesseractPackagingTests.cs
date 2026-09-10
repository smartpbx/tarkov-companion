using System.Reflection;
using System.Security.Cryptography;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

public sealed class TesseractPackagingTests
{
    private const string ModelResource =
        "TarkovCompanion.Infrastructure.Recognition.Tessdata.eng.traineddata";
    private const string ExpectedModelHash =
        "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2";

    [Fact]
    public void ProviderOutputContainsPinnedWindowsX64NativeAssetsAndModel()
    {
        var output = AppContext.BaseDirectory;
        Assert.True(File.Exists(Path.Combine(output, "x64", "tesseract55.dll")));
        Assert.True(File.Exists(Path.Combine(output, "x64", "leptonica-1.85.0.dll")));

        var assembly = typeof(TesseractOcrEngine).Assembly;
        using var model = assembly.GetManifestResourceStream(ModelResource);
        Assert.NotNull(model);
        var hash = Convert.ToHexStringLower(SHA256.HashData(model));
        Assert.Equal(ExpectedModelHash, hash);
        Assert.Contains(
            "TarkovCompanion.Infrastructure.Recognition.anchors.en.json",
            assembly.GetManifestResourceNames());
    }

    [Fact]
    public async Task ProviderAvailabilityIsExplicitAndNeverFallsBackToScriptedText()
    {
        using var provider = new TesseractOcrEngine();
        var image = new CapturedImage(
            new byte[64 * 32],
            64,
            32,
            64,
            PixelFormat.Gray8,
            DateTimeOffset.UtcNow,
            "fixture://blank");
        var result = await provider.RecognizeAsync(
            image,
            new OcrRequest(ScanContext.Unknown),
            CancellationToken.None);

        Assert.Equal("tesseract-5.5.1-wrapper-5.5.2", provider.Availability.Provider);
        Assert.DoesNotContain("fixture", result.Engine, StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows() &&
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.X64)
        {
            Assert.True(provider.Availability.IsAvailable, provider.Availability.Reason);
            Assert.True(result.IsAvailable, result.DiagnosticCode);
        }
        else
        {
            Assert.False(provider.Availability.IsAvailable);
            Assert.False(result.IsAvailable);
            Assert.Empty(result.Lines);
            Assert.Equal("ocr_provider_unavailable", result.DiagnosticCode);
        }
    }

    [Fact]
    public async Task RecognitionSelfTestDistinguishesRequiredReadinessFromDisabledIconFallback()
    {
        var provider = new AvailableOcrEngine();
        var catalog = new InMemoryRecognitionCatalogRepository(
            [new CanonicalItemReference("item", "Item")]);
        var selfTest = new RecognitionSelfTest(provider, catalog);

        var result = await selfTest.RunAsync(CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Contains(result.Capabilities, capability =>
            capability is { Capability: "offline-ocr", IsAvailable: true });
        Assert.Contains(result.Capabilities, capability =>
            capability is { Capability: "icon-fallback", IsAvailable: false } &&
            capability.Detail.Contains("no icon result will be fabricated", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class AvailableOcrEngine : IOcrEngine, IOcrEngineStatus
    {
        public OcrEngineAvailability Availability { get; } = new(true, "test-provider");

        public Task<OcrResult> RecognizeAsync(
            CapturedImage image,
            OcrRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult([], TimeSpan.Zero, Availability.Provider));
    }
}
