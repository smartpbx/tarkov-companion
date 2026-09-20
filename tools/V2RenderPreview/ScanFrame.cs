using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Infrastructure.Persistence;
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
    /// <param name="fleaRates">
    /// <c>--loot-scan-flea-rates 0.05,0.05</c>: a seeded database was copied before the rates
    /// were kept, so its table is empty and every flea item would read "fee not known". This
    /// writes the row an items refresh would have written, through the same table.
    /// </param>
    /// <param name="phase"><c>--loot-scan-phase early</c>: the preview is never in a raid.</param>
    internal static async Task<(LootScanResult Result, ILootScanWorkspaceControls Controls)> EvaluateAsync(
        IServiceProvider services,
        string framePath,
        string? iconCacheDirectory,
        string? evaluateAtUtc,
        string? fleaRates = null,
        string? phase = null)
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
        await SeedFleaRatesAsync(services, fleaRates, clock.GetUtcNow());

        var preference = services.GetRequiredService<LootScanRaidPreference>();
        if (phase is not null)
        {
            preference.Phase = Enum.Parse<RecommendationRaidPhase>(phase, ignoreCase: true);
        }

        // The composed source, so the render shows what the app decides from and not a
        // catalog-only valuation. The clock stays the preview's own: see --loot-scan-now.
        var handoff = new LootScanCaptureHandoff(
            profiles,
            new InventoryGridReconstructor(),
            new LootScanDecisionService(clock),
            clock,
            recommendations: services.GetRequiredService<LootScanRecommendationSource>());
        var controls = new LootScanWorkspaceControls(profiles, preference, handoff);
        var correlation = CaptureCorrelationId.New();
        return (await handoff.EvaluateAsync(
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
            CancellationToken.None), controls);
    }

    /// <summary>
    /// Writes the row an items refresh would have written, and records that refresh, in a seeded
    /// database copied before either was kept. Does nothing where no rates are given.
    /// </summary>
    internal static async Task SeedFleaRatesAsync(IServiceProvider services, string? fleaRates, DateTimeOffset now)
    {
        if (fleaRates?.Split(',') is not [var offer, var requirement])
        {
            return;
        }

        var observed = now.AddHours(-1).ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO flea_market_settings(id, sell_offer_fee_rate, sell_requirement_fee_rate, observed_utc)
            VALUES (1, $offer, $requirement, $observed);
            UPDATE sync_state SET last_success_utc = $observed WHERE source_key = 'items';
            """;
        command.Parameters.AddWithValue("$offer", double.Parse(offer, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$requirement", double.Parse(requirement, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$observed", observed);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
