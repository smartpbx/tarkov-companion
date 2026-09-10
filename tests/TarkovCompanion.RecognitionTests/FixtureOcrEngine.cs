using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.RecognitionTests;

internal sealed record FixtureOcrScene(string Source, IReadOnlyList<OcrLine> Lines);

internal sealed class FixtureOcrEngine : IOcrEngine, IOcrEngineStatus
{
    private readonly IReadOnlyDictionary<string, FixtureOcrScene> _scenes;

    public FixtureOcrEngine(IEnumerable<FixtureOcrScene> scenes)
    {
        ArgumentNullException.ThrowIfNull(scenes);
        _scenes = scenes.ToDictionary(scene => scene.Source, StringComparer.Ordinal);
    }

    public OcrEngineAvailability Availability { get; } = new(true, "scripted-post-ocr-fixture");

    public Task<OcrResult> RecognizeAsync(
        CapturedImage image,
        OcrRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_scenes.TryGetValue(image.Source, out var scene))
        {
            return Task.FromResult<OcrResult>(new([], TimeSpan.Zero, Availability.Provider));
        }

        var lines = request.Region is null
            ? scene.Lines
            : scene.Lines.Where(line => Intersects(request.Region, line.Bounds)).ToArray();
        return Task.FromResult(new OcrResult(lines, TimeSpan.Zero, Availability.Provider));
    }

    private static bool Intersects(PixelRect left, PixelRect right) =>
        left.X < right.X + right.Width &&
        left.X + left.Width > right.X &&
        left.Y < right.Y + right.Height &&
        left.Y + left.Height > right.Y;
}
