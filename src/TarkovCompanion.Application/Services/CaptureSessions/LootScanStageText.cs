using System.Globalization;

namespace TarkovCompanion.Application.Services.CaptureSessions;

/// <summary>
/// The words a player reads for a loot scan's stages (#572): the Loot page's progress line while
/// one runs, and Setup › Diagnostics' timing of the last one.
/// </summary>
/// <remarks>
/// The stage names are the ones the pipeline marks (<see cref="ICaptureStageTimeline.Mark"/>),
/// which read like log keys. A mark says a stage finished, so the progress line names the next
/// one: the thing the player is actually waiting on.
/// </remarks>
public static class LootScanStageText
{
    private static readonly (string Stage, string Label)[] Order =
    [
        ("settle_wait", "Waiting for the file"),
        ("context_ocr", "Reading the screen"),
        ("grid_and_icon_matching", "Matching icons"),
        ("grid_reconstruct", "Rebuilding the grid"),
        ("profile_lookup", "Checking your needs"),
        ("recommendation", "Weighing value"),
        ("decide", "Deciding"),
    ];

    /// <summary>A stage's own label; an unknown stage keeps its log name rather than vanishing.</summary>
    public static string Label(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        foreach (var (key, label) in Order)
        {
            if (string.Equals(key, stage, StringComparison.Ordinal))
            {
                return label;
            }
        }

        return stage.Replace('_', ' ');
    }

    /// <summary>"Scanning · Matching icons" while a scan runs; "Scanned in 0.9 s" once it is done.</summary>
    public static string Progress(CaptureStageStep progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.Summary is { } summary)
        {
            return $"Scanned in {Seconds(summary.TotalMilliseconds)}";
        }

        return $"Scanning · {Next(progress.LastStage)}";
    }

    /// <summary>The stage after <paramref name="lastStage"/>: what the scan is doing now.</summary>
    public static string Next(string? lastStage)
    {
        if (lastStage is null)
        {
            return Order[0].Label;
        }

        for (var index = 0; index < Order.Length; index++)
        {
            if (string.Equals(Order[index].Stage, lastStage, StringComparison.Ordinal))
            {
                return index + 1 < Order.Length ? Order[index + 1].Label : "Showing the result";
            }
        }

        return "Working";
    }

    /// <summary>One line per stage, "Matching icons · 120 ms", in the order they ran.</summary>
    public static IReadOnlyList<string> Rows(CaptureStageSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return [.. summary.Stages.Select(stage => $"{Label(stage.Stage)} · {Milliseconds(stage.ElapsedMilliseconds)}")];
    }

    public static string Seconds(double milliseconds) =>
        (milliseconds / 1000).ToString(milliseconds < 10_000 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";

    public static string Milliseconds(double milliseconds) =>
        milliseconds >= 1000
            ? Seconds(milliseconds)
            : Math.Round(milliseconds).ToString("0", CultureInfo.InvariantCulture) + " ms";
}
