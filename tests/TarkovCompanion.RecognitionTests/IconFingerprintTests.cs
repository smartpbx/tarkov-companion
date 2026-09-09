using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

public sealed class IconFingerprintTests
{
    [Fact]
    public async Task SkiaDifferenceHashMatchesSameIconAndRejectsOppositeGradient()
    {
        var referenceImage = CreateGradient(reverse: false);
        var oppositeImage = CreateGradient(reverse: true);
        var reference = SkiaPerceptualIconMatcher.CreateReference("icon-a", "Icon A", referenceImage);
        var matcher = new SkiaPerceptualIconMatcher([reference]);

        var exact = await matcher.MatchAsync(referenceImage, 3, CancellationToken.None);
        var opposite = await matcher.MatchAsync(oppositeImage, 3, CancellationToken.None);

        Assert.Collection(exact, candidate =>
        {
            Assert.Equal("icon-a", candidate.CanonicalId);
            Assert.Equal(1, candidate.Confidence.Value);
        });
        Assert.Empty(opposite);
    }

    [Fact]
    public void DifferenceHashIsStableAcrossResolution()
    {
        var small = CreateGradient(reverse: false, width: 90, height: 80);
        var large = CreateGradient(reverse: false, width: 180, height: 160);

        Assert.Equal(
            SkiaPerceptualIconMatcher.ComputeDifferenceHash(small),
            SkiaPerceptualIconMatcher.ComputeDifferenceHash(large));
    }

    private static CapturedImage CreateGradient(bool reverse, int width = 90, int height = 80)
    {
        var pixels = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)((x * 255) / Math.Max(1, width - 1));
                pixels[(y * width) + x] = reverse ? (byte)(255 - value) : value;
            }
        }

        return new(
            pixels,
            width,
            height,
            width,
            PixelFormat.Gray8,
            new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero),
            "fixture://icon");
    }
}
