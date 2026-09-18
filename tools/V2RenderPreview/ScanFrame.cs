using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// Package 37: scans a picture on disk with the shipped recognizer, handoff and decision service,
/// so the Loot decision workspace can be rendered over a result nobody typed in.
/// </summary>
/// <remarks>
/// <c>--loot-scan-frame &lt;image&gt;</c> names the picture, <c>--icon-cache &lt;directory&gt;</c> an
/// already filled icon evidence cache (the preview's own starts empty and it is offline), and
/// <c>--loot-scan-now &lt;utc&gt;</c> the moment to evaluate at, because a seeded database's
/// prices are as old as the day it was copied and would otherwise all read as expired.
/// </remarks>
internal static class ScanFrame
{
    internal static async Task<LootScanResult> EvaluateAsync(
        IServiceProvider services,
        string framePath,
        string? iconCacheDirectory,
        string? evaluateAtUtc)
    {
        var image = await new SkiaScreenshotImageLoader().LoadAsync(framePath, CancellationToken.None)
            ?? throw new InvalidOperationException($"Could not decode {framePath}.");
        TimeProvider clock = evaluateAtUtc is null
            ? TimeProvider.System
            : new FixedClock(DateTimeOffset.Parse(evaluateAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        var items = services.GetRequiredService<IItemRepository>();
        var cache = iconCacheDirectory is null
            ? services.GetRequiredService<IIconEvidenceCache>()
            : new FileIconEvidenceCache(new FileIconEvidenceCacheOptions(iconCacheDirectory) { MaximumEntries = 8192 });
        var builder = new GridPixelReconstructionBuilder(cache, items, services.GetRequiredService<IOcrEngine>());
        var grid = await builder.BuildAsync(image, InventoryGridSurface.VisibleLoot, clock.GetUtcNow());

        var profiles = services.GetRequiredService<IProfileRuntimeContextService>();
        var profile = profiles.Current.ActiveProfile
            ?? throw new InvalidOperationException("The demo composition has no active profile.");
        var handoff = new LootScanCaptureHandoff(
            profiles,
            new InventoryGridReconstructor(),
            new LootScanDecisionService(clock),
            clock,
            recommendations: new LootScanRecommendationSource(items));
        var correlation = CaptureCorrelationId.New();
        return await handoff.EvaluateAsync(
            new LootScanFrame(
                correlation.ToString(),
                new CaptureSessionId(Guid.NewGuid()),
                correlation,
                new CaptureContextMetadata(null, null, null, null, null, null, "desktop"),
                "render-preview-frame",
                1,
                Convert.ToHexStringLower(SHA256.HashData(image.Pixels.Span)),
                "desktop",
                grid),
            profile,
            CancellationToken.None);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
