using System.Diagnostics;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

public sealed class ResolverPerformanceTests
{
    [Fact]
    public void RealisticCatalogConstructionAndNoisyResolutionStayWithinScanBudget()
    {
        var catalog = Enumerable.Range(0, 5_000)
            .Select(index => new CanonicalItemReference(
                "item-" + index.ToString("D4"),
                "Industrial Relay Module " + index.ToString("D4"),
                ["IRM " + index.ToString("D4")]))
            .ToArray();
        var stopwatch = Stopwatch.StartNew();
        var resolver = new FuzzyCanonicalItemResolver(catalog);
        CanonicalItemResolution? result = null;
        for (var index = 0; index < 40; index++)
        {
            result = resolver.Resolve("Industrlal Relay Module 2999", new Confidence(0.92));
        }

        stopwatch.Stop();

        Assert.Equal("item-2999", result?.Best?.CanonicalId);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1.5),
            "5,000-item catalog plus 40 noisy lines took " + stopwatch.Elapsed.TotalMilliseconds.ToString("F0") + " ms.");
    }
}
