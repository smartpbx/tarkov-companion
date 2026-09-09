using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed record FixtureOcrScene(string Source, IReadOnlyList<OcrLine> Lines);

public sealed class FixtureOcrEngine : IOcrEngine
{
    private readonly IReadOnlyDictionary<string, FixtureOcrScene> _scenes;

    public FixtureOcrEngine(IEnumerable<FixtureOcrScene> scenes)
    {
        ArgumentNullException.ThrowIfNull(scenes);
        _scenes = scenes.ToDictionary(scene => scene.Source, StringComparer.Ordinal);
    }

    public Task<OcrResult> RecognizeAsync(
        CapturedImage image,
        OcrRequest request,
        CancellationToken cancellationToken)
    {
        CapturedImagePixels.Validate(image);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_scenes.TryGetValue(image.Source, out var scene))
        {
            return Task.FromResult<OcrResult>(new([], TimeSpan.Zero, "fixture-ocr"));
        }

        var lines = request.Region is null
            ? scene.Lines
            : scene.Lines.Where(line => CapturedImagePixels.Intersects(request.Region, line.Bounds)).ToArray();
        return Task.FromResult(new OcrResult(lines, TimeSpan.Zero, "fixture-ocr"));
    }
}
