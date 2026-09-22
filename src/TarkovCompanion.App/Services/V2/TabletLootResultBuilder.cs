using System.Linq;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.Application.Services.Devices;

namespace TarkovCompanion.App.Services.V2;

/// <summary>
/// The Loot page's own result, cut down to what the paired tablet lists (#572).
/// </summary>
/// <remarks>
/// Built from <see cref="LootScanViewModel"/> rather than the planner's raw result, so the tablet
/// shows the same order, verdict words, values and reasons as the desktop's Loot page, and a
/// change to how the desktop words a row reaches the tablet without a second copy of it.
/// </remarks>
public static class TabletLootResultBuilder
{
    public static TabletLootResult From(LootScanViewModel result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var rows = result.Decisions
            .Take(TabletLootResult.MaximumRows)
            .Select(decision => new TabletLootRow(
                decision.Name,
                decision.Verdict.ToString(),
                decision.VerdictLabel,
                decision.ShortValueLabel,
                decision.HeadlineReason))
            .ToArray();
        return new TabletLootResult(
            result.Result.ScanId,
            result.Result.EvaluatedUtc,
            result.Heading,
            $"{result.TakeSummary} · {result.SwapSummary} · {result.LeaveSummary} · {result.ReviewSummary}",
            rows,
            Math.Max(0, result.Decisions.Count - rows.Length));
    }
}
