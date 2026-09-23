using System.Globalization;
using System.Text;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.Profiles;
using TarkovCompanion.UnitTests.Runtime;
using Xunit.Abstractions;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.LootScanMeasurement;

/// <summary>
/// One real in-raid loot screenshot through the path the running app takes: the capture session,
/// the shipped recognition pipeline, the Gear-screen reader, the icon matcher, the handoff and
/// the planner.
/// </summary>
/// <remarks>
/// <para>
/// The screenshot is Clayton's (2026-09-20, a wooden ammo box open beside a full Duffle bag) and
/// carries a raid id, so it stays outside the repository (<c>TARKOV_LOOT_FRAME</c>); the test
/// skips without it or without the icon corpus.
/// </para>
/// <para>
/// OCR is Windows-only, so the text pass is replaced by the three lines the Windows engine read
/// off this very frame: the running app scored it Container at 0.70, which is exactly
/// "tactical rig" + "backpack" + "pockets" (0.25 + 0.25 + 0.20) and nothing else. Everything
/// after the text is the shipped code.
/// </para>
/// </remarks>
public sealed class RealLootFrameEndToEndTests(ITestOutputHelper output)
{
    private const string DefaultFrame =
        "/root/orca/incoming/loot-2026-09-20/2026-09-20[21-12]_-86.18, 19.72, -91.78_0.25662, -0.42024, 0.12520, 0.86132_11.45 (0).png";

    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARealLootScreenIsPlannedAgainstEveryCarriedGridItShows()
    {
        var path = Environment.GetEnvironmentVariable("TARKOV_LOOT_FRAME") ?? DefaultFrame;
        if (!File.Exists(path) || IconCorpus.TryLoad() is not { } corpus)
        {
            output.WriteLine("[real-loot-frame] skipped: no screenshot or no icon corpus.");
            return;
        }

        var image = await new SkiaScreenshotImageLoader().LoadAsync(path, CancellationToken.None);
        Assert.NotNull(image);
        var cache = await LootScanMeasurementIndex.OpenAsync(corpus, Now);
        var items = corpus.CreateRepository(Now);
        var builder = new GridPixelReconstructionBuilder(cache, items, new AnchorLinesOcr());
        var pipeline = new CaptureRecognitionPipeline(
            new OcrCoordinator(new AnchorLinesOcr(), new ScanContextDetector()),
            builder,
            new ManualTimeProvider(Now));
        using var runtime = await ReadyProfileContextAsync();
        var clock = new ManualTimeProvider(Now);
        var handoff = new LootScanCaptureHandoff(
            runtime,
            new InventoryGridReconstructor(),
            new LootScanDecisionService(clock),
            clock,
            recommendations: new LootScanRecommendationSource(items));
        var (_, result) = await new LootScanPathHarness(builder, handoff, pipeline).ScanAsync(image!, Now);

        Assert.NotNull(result);
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"status {result!.Status.Completeness} {result.Status.Code}");
        foreach (var issue in result.Issues)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"issue {issue.Code}: {issue.Explanation}");
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"carried grid {result.CarriedGrid?.Geometry.Columns.Value}x{result.CarriedGrid?.Geometry.Rows.Value}, {result.CarriedGrid?.Cells.Count} items");
        foreach (var carried in result.CarriedGrids)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  {carried.Identity.Kind} {carried.Identity.Index + 1}: {carried.Recognition.Geometry.Columns.Value}x{carried.Recognition.Geometry.Rows.Value}, {carried.Recognition.Cells.Count} items");
        }
        foreach (var decision in result.Decisions)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"r{decision.SourceAnchor.Row} c{decision.SourceAnchor.Column} {decision.Item.Value?.DisplayName.Value ?? "unnamed"}: {decision.Verdict} [{string.Join("; ", decision.Reasons.Select(reason => reason.Code))}] placement={(decision.Placement is { } place ? $"{place.CarriedGrid.Kind}[{place.CarriedGrid.Index}] r{place.Anchor.Row}c{place.Anchor.Column}" : "-")} drops={decision.Drops.Count}");
        }

        output.WriteLine(report.ToString());

        // The backpack is read: a 4x3 Duffle, all twelve squares taken by seven items.
        Assert.Equal(4, result.CarriedGrid?.Geometry.Columns.Value);
        Assert.Equal(3, result.CarriedGrid?.Geometry.Rows.Value);
        Assert.Equal(7, result.CarriedGrid?.Cells.Count);
        Assert.DoesNotContain(result.Issues, issue => issue.Code is "carried.coverage-partial" or "carried.capacity-unavailable");

        Assert.Contains(result.CarriedGrids, grid => grid.Identity.Kind == CarriedGridKind.TacticalRig);
        Assert.Contains(result.CarriedGrids, grid => grid.Identity.Kind == CarriedGridKind.Pockets);

        // The loot is the ammo box's one pack and every carried grid reaches the result. This
        // fixture has no complete profile recommendation, so advice stops before placement; the
        // focused planner tests prove the full-backpack/empty-rig decision itself.
        var loot = Assert.Single(result.Decisions);
        Assert.Equal("64ace9ff03378853630da538", loot.Item.Value?.CanonicalId.Value);
        Assert.DoesNotContain(loot.Reasons, reason => reason.Code == "carried.capacity-incomplete");
        Assert.Equal(LootScanVerdict.Review, loot.Verdict);
        Assert.Equal("recommendation.incomplete", Assert.Single(loot.Reasons).Code);
    }

    private static async Task<ProfileRuntimeContextService> ReadyProfileContextAsync()
    {
        var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
        var profile = Profile(Context(Id(922), "generation-a", ProfileGameMode.Pvp), "item-a");
        await profiles.CreateAsync(Request(profile), CancellationToken.None);
        var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);
        return runtime;
    }

    /// <summary>The section headers of the frame, where they sit, and nothing for a crop.</summary>
    private sealed class AnchorLinesOcr : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Region is not null
                ? new OcrResult([], TimeSpan.Zero, "windows-media-ocr-lines")
                : new OcrResult(
                    [
                        new OcrLine("TACTICAL RIG", new(1620, 93, 96, 13), null),
                        new OcrLine("POCKETS", new(1620, 399, 60, 13), null),
                        new OcrLine("BACKPACK", new(1620, 510, 68, 13), null),
                    ],
                    TimeSpan.Zero,
                    "windows-media-ocr-lines"));
    }
}
