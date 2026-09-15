using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Platform.Windows.Ocr;
using Windows.Graphics.Imaging;

namespace TarkovCompanion.Platform.Windows.OcrTests;

public sealed class WindowsMediaOcrEngineTests
{
    [WindowsX64Fact]
    public void TilePlanCovers4kAndUltrawideAtNativeResolution()
    {
        var fourK = WindowsMediaOcrEngine.PlanTiles(new(0, 0, 3840, 2160), 2600, 128);
        var ultrawide = WindowsMediaOcrEngine.PlanTiles(new(0, 0, 7680, 1440), 2600, 128);

        Assert.Equal(2, fourK.Count);
        Assert.Equal(new PixelRect(0, 0, 2600, 2160), fourK[0]);
        Assert.Equal(new PixelRect(2472, 0, 1368, 2160), fourK[1]);
        Assert.Equal(4, ultrawide.Count);
        Assert.Equal(0, ultrawide[0].X);
        Assert.Equal(7416, ultrawide[^1].X);
        Assert.Equal(264, ultrawide[^1].Width);
        Assert.All(ultrawide, tile => Assert.Equal(1440, tile.Height));
        AssertCoverage(ultrawide, 7680);
    }

    [WindowsX64Fact]
    public async Task TiledLinesReturnInOriginalCoordinatesAndOverlapDuplicatesCollapseDeterministically()
    {
        var recognizer = new FakeRecognizer((call, _, _) => Task.FromResult<IReadOnlyList<WindowsOcrNativeLine>>(
            call == 0
                ? [new("Graphics", new(50, 20, 60, 12))]
                : [new("Graphics Card", new(0, 20, 100, 12))]));
        var engine = Engine(recognizer);

        var execution = await engine.RecognizeDetailedAsync(
            Frame(180, 80),
            new OcrRequest(ScanContext.Unknown),
            CancellationToken.None);

        Assert.Equal(WindowsOcrExecutionStatus.Complete, execution.Status);
        Assert.Equal(1, execution.Scale);
        Assert.Equal(2, execution.TileCount);
        Assert.Equal(2, execution.CompletedTileCount);
        Assert.Equal(new PixelRect(0, 0, 180, 80), execution.SourceRegion);
        Assert.Equal(180, execution.SourceWidth);
        Assert.Equal(80, execution.SourceHeight);
        Assert.True(execution.EstimatedPeakBytes >= 180 * 80);
        var line = Assert.Single(execution.Result.Lines);
        Assert.Equal("Graphics Card", line.Text);
        Assert.Equal(new PixelRect(80, 20, 100, 12), line.Bounds);
        Assert.Null(line.Confidence);
    }

    [WindowsX64Fact]
    public async Task OneFailedTileReturnsExplicitPartialEvidence()
    {
        var recognizer = new FakeRecognizer((call, _, _) => call == 0
            ? Task.FromResult<IReadOnlyList<WindowsOcrNativeLine>>([new("INSPECT", new(10, 10, 50, 12))])
            : throw new InvalidOperationException("synthetic tile failure"));

        var execution = await Engine(recognizer).RecognizeDetailedAsync(
            Frame(180, 80),
            new OcrRequest(ScanContext.Unknown),
            CancellationToken.None);

        Assert.Equal(WindowsOcrExecutionStatus.Partial, execution.Status);
        Assert.Equal("ocr_partial_tiles", execution.DiagnosticCode);
        Assert.True(execution.Result.IsAvailable);
        Assert.Single(execution.Result.Lines);
        Assert.Equal([WindowsOcrExecutionStatus.Complete, WindowsOcrExecutionStatus.Failed],
            execution.Tiles.Select(tile => tile.Status));
    }

    [WindowsX64Fact]
    public async Task EmptyTextIsACompleteEmptyReadRatherThanProviderFailure()
    {
        var recognizer = new FakeRecognizer((_, _, _) =>
            Task.FromResult<IReadOnlyList<WindowsOcrNativeLine>>([]));

        var execution = await Engine(recognizer).RecognizeDetailedAsync(
            Frame(80, 60),
            new OcrRequest(ScanContext.Unknown),
            CancellationToken.None);

        Assert.Equal(WindowsOcrExecutionStatus.Complete, execution.Status);
        Assert.True(execution.Result.IsAvailable);
        Assert.Empty(execution.Result.Lines);
    }

    [WindowsX64Fact]
    public async Task PerFrameTimeoutReturnsWithoutStartingAnotherTile()
    {
        var recognizer = new FakeRecognizer((_, _, token) =>
            WaitForeverAsync(token));
        var engine = Engine(recognizer, new WindowsMediaOcrOptions
        {
            FrameTimeout = TimeSpan.FromMilliseconds(40),
            MaximumTileDimension = 100,
            TileOverlap = 20,
        });
        var watch = Stopwatch.StartNew();

        var execution = await engine.RecognizeDetailedAsync(
            Frame(180, 80),
            new OcrRequest(ScanContext.Unknown),
            CancellationToken.None);

        Assert.Equal(WindowsOcrExecutionStatus.TimedOut, execution.Status);
        Assert.Equal("ocr_frame_timeout", execution.DiagnosticCode);
        Assert.False(execution.Result.IsAvailable);
        Assert.Equal(1, recognizer.CallCount);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [WindowsX64Fact]
    public async Task TimeoutAfterACompletedTileReturnsItsPartialEvidence()
    {
        var recognizer = new FakeRecognizer((call, _, token) => call == 0
            ? Task.FromResult<IReadOnlyList<WindowsOcrNativeLine>>([new("INSPECT", new(10, 10, 50, 12))])
            : WaitForeverAsync(token));
        var engine = Engine(recognizer, new WindowsMediaOcrOptions
        {
            FrameTimeout = TimeSpan.FromMilliseconds(40),
            MaximumTileDimension = 100,
            TileOverlap = 20,
        });

        var execution = await engine.RecognizeDetailedAsync(
            Frame(180, 80),
            new OcrRequest(ScanContext.Unknown),
            CancellationToken.None);

        Assert.Equal(WindowsOcrExecutionStatus.Partial, execution.Status);
        Assert.Equal("ocr_frame_timeout_partial", execution.DiagnosticCode);
        Assert.True(execution.Result.IsAvailable);
        Assert.Single(execution.Result.Lines);
        Assert.Equal(1, execution.CompletedTileCount);
        Assert.Equal(2, execution.AttemptedTileCount);
    }

    [WindowsX64Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        var recognizer = new FakeRecognizer((_, _, token) => WaitForeverAsync(token));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Engine(recognizer).RecognizeDetailedAsync(
                Frame(80, 60),
                new OcrRequest(ScanContext.Unknown),
                cancellation.Token));
    }

    [WindowsX64Fact]
    public async Task PixelAndMemoryCeilingsRejectBeforeNativeOcr()
    {
        var recognizer = new FakeRecognizer((_, _, _) =>
            Task.FromResult<IReadOnlyList<WindowsOcrNativeLine>>([]));
        var pixelLimited = Engine(recognizer, new WindowsMediaOcrOptions
        {
            MaximumSourcePixels = 15,
            MaximumTileDimension = 100,
            TileOverlap = 20,
        });
        var memoryLimited = Engine(recognizer, new WindowsMediaOcrOptions
        {
            MaximumEstimatedPeakBytes = 400,
            MaximumTileDimension = 100,
            TileOverlap = 20,
        });

        var pixel = await pixelLimited.RecognizeDetailedAsync(
            Frame(4, 4),
            new OcrRequest(ScanContext.Unknown),
            CancellationToken.None);
        var memory = await memoryLimited.RecognizeDetailedAsync(
            Frame(20, 20),
            new OcrRequest(ScanContext.Unknown),
            CancellationToken.None);

        Assert.Equal("ocr_input_limit_exceeded", pixel.DiagnosticCode);
        Assert.Equal(WindowsOcrExecutionStatus.Rejected, pixel.Status);
        Assert.Equal("ocr_memory_limit_exceeded", memory.DiagnosticCode);
        Assert.Equal(WindowsOcrExecutionStatus.Rejected, memory.Status);
        Assert.Equal(0, recognizer.CallCount);
    }

    [WindowsX64Fact]
    public async Task UnsupportedProviderReportsUnavailableWithoutTouchingPixels()
    {
        var engine = new WindowsMediaOcrEngine(null, unavailableReason: "synthetic unsupported Windows");

        var execution = await engine.RecognizeDetailedAsync(
            Frame(80, 60),
            new OcrRequest(ScanContext.Unknown),
            CancellationToken.None);

        Assert.Equal(WindowsOcrExecutionStatus.Unavailable, execution.Status);
        Assert.Equal("ocr_provider_unavailable", execution.DiagnosticCode);
        Assert.False(execution.Result.IsAvailable);
        Assert.Equal("synthetic unsupported Windows", engine.Availability.Reason);
    }

    [WindowsX64Fact]
    public async Task ProductionWindowsEngineReadsRenderedTextBeyondTheNativeFirstTile()
    {
        var engine = new WindowsMediaOcrEngine();
        Assert.True(engine.Availability.IsAvailable, engine.Availability.Reason);
        var image = Render(3840, 2160, "INSPECT", 3000, 500, 96);

        var execution = await engine.RecognizeDetailedAsync(
            image,
            new OcrRequest(ScanContext.Unknown),
            CancellationToken.None);

        Assert.Equal(WindowsOcrExecutionStatus.Complete, execution.Status);
        Assert.Equal(2, execution.TileCount);
        Assert.Contains(execution.Result.Lines, line =>
            Normalize(line.Text).Contains("inspect", StringComparison.Ordinal));
        Assert.All(execution.Result.Lines, line => Assert.Null(line.Confidence));
    }

    private static WindowsMediaOcrEngine Engine(
        IWindowsOcrRecognizer recognizer,
        WindowsMediaOcrOptions? options = null) => new(
            recognizer,
            options ?? new WindowsMediaOcrOptions
            {
                MaximumTileDimension = 100,
                TileOverlap = 20,
            });

    private static async Task<IReadOnlyList<WindowsOcrNativeLine>> WaitForeverAsync(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return [];
    }

    private static void AssertCoverage(IReadOnlyList<PixelRect> tiles, int width)
    {
        Assert.Equal(0, tiles[0].X);
        Assert.Equal(width, tiles[^1].X + tiles[^1].Width);
        for (var index = 1; index < tiles.Count; index++)
        {
            Assert.True(tiles[index].X < tiles[index - 1].X + tiles[index - 1].Width);
        }
    }

    private static CapturedImage Frame(int width, int height) => new(
        new byte[checked(width * height)],
        width,
        height,
        width,
        PixelFormat.Gray8,
        new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero),
        "fixture://windows-ocr");

    private static CapturedImage Render(
        int width,
        int height,
        string text,
        float x,
        float baselineY,
        float size)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(18, 20, 22));
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, size);
        canvas.DrawText(text, x, baselineY, SKTextAlign.Left, font, paint);
        var pixels = new byte[checked(bitmap.RowBytes * bitmap.Height)];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return new(
            pixels,
            width,
            height,
            bitmap.RowBytes,
            PixelFormat.Bgra8888,
            new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero),
            "rendered://windows-ocr");
    }

    private static string Normalize(string text) => new(
        text.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private sealed class FakeRecognizer(
        Func<int, SoftwareBitmap, CancellationToken, Task<IReadOnlyList<WindowsOcrNativeLine>>> read)
        : IWindowsOcrRecognizer
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<IReadOnlyList<WindowsOcrNativeLine>> RecognizeAsync(
            SoftwareBitmap bitmap,
            CancellationToken cancellationToken)
        {
            var call = _callCount++;
            return read(call, bitmap, cancellationToken);
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class WindowsX64FactAttribute : FactAttribute
{
    public WindowsX64FactAttribute()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Skip = "Windows.Media.Ocr tests require a Windows x64 host; Linux only builds this project.";
        }
    }
}
