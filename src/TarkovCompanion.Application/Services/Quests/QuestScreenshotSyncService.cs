using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Application.Services.Quests;

public sealed record QuestScreenshotSyncPreview(
    int ImageCount,
    string OcrEngine,
    IReadOnlyList<QuestListLineMatch> Lines,
    QuestHistoryInferencePreview History)
{
    public IReadOnlyList<string> ConfirmedTaskIds => Lines
        .Select(line => line.Confirmed?.TaskId)
        .Where(taskId => taskId is not null)
        .Select(taskId => taskId!)
        .ToArray();
}

public sealed record QuestScreenshotSyncApplyResult(
    int Changed,
    QuestHistoryInferencePreview AppliedPreview);

public sealed record QuestScreenshotImageLoad(
    IReadOnlyList<CapturedImage> Images,
    int Skipped,
    string? Error = null);

public interface IQuestScreenshotImageSource
{
    Task<QuestScreenshotImageLoad> LoadFilesAsync(
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken);

    Task<QuestScreenshotImageLoad> LoadRecentAsync(
        string? screenshotRoot,
        DateTimeOffset takenSinceUtc,
        CancellationToken cancellationToken);
}

public enum QuestScreenshotLayout
{
    SideTaskList,
    OperationalTaskList,
    StoryChapter,
}

public sealed record QuestScreenshotTextRegion(
    QuestScreenshotLayout Layout,
    PixelRect Region);

public interface IQuestTaskColumnRegionDetector
{
    QuestScreenshotTextRegion Detect(CapturedImage image);
}

/// <summary>Reads quest-list screenshots, previews their consequences, then applies confirmation.</summary>
/// <remarks>
/// OCR and matching do not write progress. Confirmation re-reads the current progress before it
/// applies, so a game-log event arriving while the preview was open is not overwritten by an old
/// snapshot. Finished and failed states remain protected by <see cref="QuestHistoryInference"/>.
/// </remarks>
public sealed class QuestScreenshotSyncService(
    IPlayerProfileService profiles,
    IQuestCatalog catalog,
    IQuestProgressStore progress,
    IQuestProgressCommandService commands,
    IOcrEngine ocr,
    IQuestTaskColumnRegionDetector taskColumn,
    QuestListMatcher matcher,
    QuestListMatchMerger merger,
    QuestHistoryInference inference,
    QuestTrackingOptions options)
{
    public async Task<bool> HasNoRecordedProgressAsync(CancellationToken cancellationToken)
    {
        var (_, snapshot, _) = await ContextAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Tasks.Count == 0 && snapshot.Objectives.Count == 0;
    }

    public async Task<QuestScreenshotSyncPreview> AnalyzeAsync(
        IReadOnlyCollection<CapturedImage> images,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
        {
            throw new ArgumentException("At least one screenshot is required.", nameof(images));
        }

        var catalogLines = new List<string>();
        var operationalLines = new List<string>();
        var engines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detected = taskColumn.Detect(image);
            var result = await ocr.RecognizeAsync(
                    image,
                    new OcrRequest(ScanContext.Unknown, detected.Region),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsAvailable)
            {
                throw new InvalidOperationException(
                    $"Text recognition is unavailable ({result.DiagnosticCode ?? result.Engine}).");
            }

            engines.Add(result.Engine);
            var destination = detected.Layout == QuestScreenshotLayout.OperationalTaskList
                ? operationalLines
                : catalogLines;
            destination.AddRange(result.Lines.Select(line => line.Text));
        }

        return await AnalyzeLinesAsync(
            catalogLines,
            images.Count,
            string.Join(" + ", engines.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
            operationalLines,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<QuestScreenshotSyncPreview> AnalyzeLinesAsync(
        IEnumerable<string> ocrLines,
        int imageCount,
        string ocrEngine,
        CancellationToken cancellationToken) =>
        await AnalyzeLinesAsync(ocrLines, imageCount, ocrEngine, [], cancellationToken).ConfigureAwait(false);

    private async Task<QuestScreenshotSyncPreview> AnalyzeLinesAsync(
        IEnumerable<string> ocrLines,
        int imageCount,
        string ocrEngine,
        IEnumerable<string> operationalLines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ocrLines);
        var (_, snapshot, questCatalog) = await ContextAsync(cancellationToken).ConfigureAwait(false);
        var matched = merger.Merge(
            matcher.Match(ocrLines, questCatalog.Tasks).Lines.Concat(
                operationalLines
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => new QuestListLineMatch(line.Trim(), QuestListLineKind.Unmatched, []))));
        var history = inference.Preview(matched.Matched.Select(line => line.Confirmed!.TaskId), questCatalog, snapshot);
        return new(imageCount, ocrEngine, matched.Lines, history);
    }

    public async Task<QuestHistoryInferencePreview> PreviewSelectionAsync(
        IEnumerable<string> activeTaskIds,
        CancellationToken cancellationToken)
    {
        var (_, snapshot, questCatalog) = await ContextAsync(cancellationToken).ConfigureAwait(false);
        return inference.Preview(activeTaskIds, questCatalog, snapshot);
    }

    public async Task<QuestScreenshotSyncApplyResult> ApplyAsync(
        IEnumerable<string> activeTaskIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeTaskIds);
        var ids = activeTaskIds.Distinct(StringComparer.Ordinal).ToArray();
        var (scope, snapshot, questCatalog) = await ContextAsync(cancellationToken).ConfigureAwait(false);
        var current = inference.Preview(ids, questCatalog, snapshot);
        var changed = 0;
        foreach (var change in current.Changes
                     .OrderBy(change => change.Reason == QuestHistoryInferenceReason.ConfirmedActive ? 1 : 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await commands.SetTaskStateAsync(
                    scope,
                    change.TaskId,
                    change.NewState,
                    QuestProgressActor.Import,
                    "ScreenshotSync",
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Changed)
            {
                changed++;
            }
        }

        return new(changed, current);
    }

    private async Task<(QuestProfileScope Scope, QuestProgressSnapshot Progress, QuestCatalogSnapshot Catalog)> ContextAsync(
        CancellationToken cancellationToken)
    {
        var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var snapshot = await progress.GetAsync(scope, cancellationToken).ConfigureAwait(false);
        var questCatalog = await catalog
            .GetAsync(profile.GameMode, options.NormalizedLanguage, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Quest data is not ready yet.");
        return (scope, snapshot, questCatalog);
    }
}
