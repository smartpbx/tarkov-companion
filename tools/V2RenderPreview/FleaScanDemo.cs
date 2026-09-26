using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// <c>--flea-scan-demo &lt;item name&gt;</c>: Intel &gt; Flea over a photographed flea screen, through
/// the composed <see cref="ICaptureResultHandoff"/>, capture bridge and shell.
/// </summary>
/// <remarks>
/// OCR is Windows-only, so the rows are the fixture: five prices spread round the item's own
/// 24-hour average in the seeded catalog, which is what makes each verdict appear. The item, its
/// trader price, its average, the fee and every verdict are the application's own.
/// </remarks>
internal static class FleaScanDemo
{
    internal static void Run(IServiceProvider services, Action<Task> drain, Action<int> pump, string itemName, string? fleaRates)
    {
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        drain(ScanFrame.SeedFleaRatesAsync(services, fleaRates, now));
        var items = services.GetRequiredService<IItemRepository>();
        var search = items.SearchAsync(itemName, 5, CancellationToken.None);
        drain(search);
        var item = search.Result.FirstOrDefault(hit => string.Equals(hit.Item.Name, itemName, StringComparison.OrdinalIgnoreCase))?.Item
            ?? search.Result.First().Item;
        var price = items.GetPriceAsync(item.Id, CancellationToken.None);
        drain(price);
        var average = price.Result?.Average24HourRoubles ?? price.Result?.FleaPriceRoubles ?? 100_000;
        var trader = price.Result?.BestTrader?.ValueRoubles ?? average / 3;
        CaptureFleaListing Row(
            long roubles,
            int? quantity,
            double confidence,
            ItemConditionReading? condition = null,
            string currency = "RUB",
            long? original = null,
            long rate = 1) =>
            new(
                roubles,
                quantity,
                new Confidence(confidence),
                $"{item.ShortName} {(original ?? roubles)} {currency}",
                currency,
                original ?? roubles,
                rate,
                currency == "RUB" ? null : new DataProvenance("render catalog currency", now.AddMinutes(-10)),
                condition);

        // #712 1-12: --flea-correct says the screen was for the first alternate, on two frames, so
        // the reading becomes a learned name and Setup's count shows it.
        var correct = Environment.GetCommandLineArgs().Contains("--flea-correct");
        foreach (var hash in correct ? new[] { 'd', 'c' } : ['c'])
        {
        var analysis = new CaptureAnalysis(
            new string(hash, 64),
            RecognizedContext.Flea,
            false,
            true,
            null,
            new Confidence(0.9),
            Identified:
            [
                new(item.Id, item.Name, new Confidence(0.88), $"ocr-fuzzy; observed={item.ShortName.ToLowerInvariant()}; matched={item.Name}"),
                .. search.Result.Where(hit => hit.Item.Id != item.Id).Take(2)
                    .Select(hit => new CaptureIdentifiedItem(hit.Item.Id, hit.Item.Name, new Confidence(0.52), hit.Item.ShortName)),
            ],
            FleaListings:
            [
                Row(trader * 9 / 10, 1, 0.93, new ItemConditionReading(ItemConditionKind.Durability, 41.5, 60)),
                Row((average * 80 / 100 / 222) * 222, 4, 0.9, new ItemConditionReading(ItemConditionKind.Uses, 3, 5), "EUR", average * 80 / 100 / 222, 222),
                Row(average * 97 / 100, null, 0.74),
                Row(average * 104 / 100, 1, 0.91),
                Row(average * 125 / 100, 2, 0.86),
            ]);
        // --flea-scan-age <minutes>: the screenshot was taken that long ago, so the ranking's
        // "Offers as seen at" line can be rendered in its aged, may-be-gone wording (#284).
        var shot = now.AddMinutes(-AgeMinutes());
        var session = new CaptureSessionId(Guid.NewGuid());
        var request = new CaptureHandoffRequest(
            session,
            "flea-demo-frame",
            analysis,
            services.GetRequiredService<ShellCaptureContextSource>().Describe(),
            CaptureCorrelationId.New(),
            CaptureSourceKind.GameWrittenScreenshot,
            shot.AddSeconds(-5),
            shot.AddSeconds(-4),
            null,
            CaptureDeliveryKind.WatchedFile,
            new EvidenceProvenance(
                EvidenceSourceClass.GameWrittenScreenshot,
                "render://flea-demo-frame",
                shot.AddSeconds(-5),
                EvidenceConfidence.Unscored,
                new ProducerIdentity("v2-render-preview", "1")),
            0,
            CaptureReviewAction.UseDetected,
            ScanIntent.Flea,
            new CaptureCorrection(CaptureReviewAction.UseDetected, ScanIntent.Flea, RecognizedContext.Flea, 0, now, "render-preview"));
        drain(services.GetRequiredService<ICaptureResultHandoff>().AcceptAsync(request, CancellationToken.None).AsTask());
        pump(80);
        if (correct && analysis.Identified.Count > 1)
        {
            var corrected = services.GetRequiredService<FleaCaptureHandoff>().CorrectItemAsync(analysis.Identified[1].CanonicalId, CancellationToken.None);
            drain(corrected);
            pump(80);
            Console.WriteLine($"[flea-correct] {analysis.Identified[1].DisplayName}: {corrected.Result}");
        }
        }

        if (correct)
        {
            var counts = services.GetRequiredService<TarkovCompanion.Infrastructure.Recognition.CorrectionMemory>().CountAsync(CancellationToken.None);
            drain(counts);
            Console.WriteLine($"[flea-correct] learned: {counts.Result}");
        }
    }

    private static int AgeMinutes()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--flea-scan-age");
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var minutes) ? minutes : 0;
    }
}
