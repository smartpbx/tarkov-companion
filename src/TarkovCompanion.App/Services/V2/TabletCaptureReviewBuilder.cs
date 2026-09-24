using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.App.ViewModels.V2.StashScan;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.App.Services.V2;

/// <summary>
/// The Stash page's and Intel &gt; Flea's own results, cut down to the tablet's review cards (#290).
/// </summary>
/// <remarks>
/// Built from the desktop's view models, like <see cref="TabletLootResultBuilder"/>, so the tablet
/// shows the words, order and confidence the desktop shows and never re-derives a verdict.
/// </remarks>
public static class TabletCaptureReviewBuilder
{
    public const string StashKind = "stash";

    public const string FleaKind = "flea";

    /// <summary>The Stash page's current snapshot, or null when it shows none or an older one.</summary>
    public static TabletCaptureReview? FromStash(StashScanWorkspaceViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.SelectedSnapshot is not { IsCurrent: true } snapshot)
        {
            return null;
        }

        return FromStash(
            snapshot.SnapshotId,
            snapshot.RecordedUtc,
            page.ReconstructionLabel,
            page.StashValueLabel,
            page.PlanTiles,
            page.Items);
    }

    /// <summary>The same from the page's parts, for tests that do not build the whole page.</summary>
    public static TabletCaptureReview FromStash(
        Guid snapshotId,
        DateTimeOffset recordedUtc,
        string reconstruction,
        string value,
        IReadOnlyList<StashPlanTileViewModel> planTiles,
        IReadOnlyList<StashItemRowViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(planTiles);
        ArgumentNullException.ThrowIfNull(items);
        var summary = string.IsNullOrEmpty(value) ? reconstruction : $"{reconstruction} · {value} known";
        var plan = string.Join(
            " · ",
            planTiles.Where(tile => tile.IsWired && tile.Count > 0).Select(tile => $"{tile.Label} {tile.Count.ToString(CultureInfo.CurrentCulture)}"));
        var rows = items
            .Select(item => new TabletReviewRow(
                item.DisplayName,
                item.GroupLabel,
                StashTagKind(item),
                item.QuantityLabel,
                StashConfidence(item.EvidenceLabel),
                item.WhyLabel))
            .ToArray();
        return TabletCaptureReview.Bounded(
            StashKind,
            snapshotId.ToString("N"),
            recordedUtc,
            "Stash scan",
            summary,
            plan.Length == 0 ? null : plan,
            rows);
    }

    /// <summary>A photographed flea screen as Intel &gt; Flea ranks it.</summary>
    public static TabletCaptureReview FromFlea(FleaScanViewModel scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var note = string.Join(
            " · ",
            new[] { scan.TraderLabel, scan.AverageLabel, scan.MarketDataNote }.Where(part => !string.IsNullOrEmpty(part)));
        var rows = scan.Rows
            .Select(row => new TabletReviewRow(
                $"{row.RankLabel} · {row.PriceLabel}",
                row.VerdictLabel,
                FleaTagKind(row.Verdict),
                row.StackLabel,
                row.ConfidenceLabel,
                row.WhyLabel))
            .ToArray();
        return TabletCaptureReview.Bounded(
            FleaKind,
            scan.Scan.ArtifactId,
            scan.Scan.ObservedUtc,
            scan.Heading,
            scan.SummaryLabel,
            note,
            rows);
    }

    /// <summary>
    /// The stash row's "GameWrittenScreenshot · 93%" as the flea card words it, "read 93% sure": the
    /// source class is the same for every row of one scan and says nothing a player can use.
    /// </summary>
    internal static string StashConfidence(string evidence)
    {
        var score = evidence[(evidence.LastIndexOf('·') + 1)..].Trim();
        return score.EndsWith('%') ? $"read {score} sure"
            : score == "unscored" ? "confidence not scored"
            : evidence;
    }

    /// <summary>The tablet colours a tag by one of five words, not by the desktop's enums.</summary>
    internal static string StashTagKind(StashItemRowViewModel item) => item.IsIgnored
        ? "Neutral"
        : item.Group switch
        {
            StashPlanGroup.Keep or StashPlanGroup.UseSoon => "Good",
            StashPlanGroup.Sell => "Sell",
            StashPlanGroup.Organize => "Neutral",
            _ => "Review",
        };

    internal static string FleaTagKind(FleaRowVerdict verdict) => verdict switch
    {
        FleaRowVerdict.ProfitToTrader or FleaRowVerdict.ProfitOnFlea => "Good",
        FleaRowVerdict.UnderAverage => "Neutral",
        FleaRowVerdict.OverAverage => "Bad",
        _ => "Review",
    };
}
