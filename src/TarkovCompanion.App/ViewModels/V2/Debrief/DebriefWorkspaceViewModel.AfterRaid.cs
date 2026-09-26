using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Debrief;

/// <summary>
/// [#712 0-8] The after-raid card: when the game reports a raid over, Debrief asks once how it
/// ended and shows a recap of the raid with the source of every fact.
/// </summary>
/// <remarks>
/// The logs never say survived or died, and nothing here infers it from position or timing: an
/// unanswered raid says "Outcome not recorded". The answer goes through the same CorrectAsync a
/// Debrief correction uses, so its correction event labels it Manual in the list, the filters,
/// the survival chart and the export; an <see cref="RaidOutcomeAnswer"/> event beside it (the
/// player, and when) is what stops the question being asked twice, answered or dismissed.
/// </remarks>
public sealed partial class DebriefWorkspaceViewModel
{
    private IMapDataService? _maps;
    private RaidEnded? _afterRaidEnd;
    private int _selectedCountedScans;
    private long _selectedScanValue;

    /// <summary>The after-raid card; hidden until a raid ends while the companion is running.</summary>
    public RaidOutcomeCardViewModel AfterRaid { get; private set; } = null!;

    private void InitialiseAfterRaid(IRaidEndSignal? raidEnds, IMapDataService? maps, Action<Action>? dispatch)
    {
        _maps = maps;
        AfterRaid = new RaidOutcomeCardViewModel(AnswerAfterRaidAsync, DismissAfterRaidAsync);
        if (raidEnds is null)
        {
            return;
        }

        // The signal fires on whichever thread published the snapshot; the card is UI state.
        dispatch ??= action => Avalonia.Threading.Dispatcher.UIThread.Post(action);
        raidEnds.RaidEnded += (_, ended) => dispatch(() =>
            OfferAfterRaidAsync(ended, CancellationToken.None).Observe("debrief", "ask how the raid ended"));
    }

    /// <summary>
    /// A raid just ended: shows the card for it, asking the question only when the raid has no
    /// outcome yet and the question was not already answered or dismissed.
    /// </summary>
    public async Task OfferAfterRaidAsync(RaidEnded ended, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ended);
        _afterRaidEnd = ended;
        var raid = (await _raidHistoryService.ListAsync(cancellationToken).ConfigureAwait(true))
            .FirstOrDefault(entry => entry.Id == ended.RaidId);
        var answers = await ReadAnswersAsync(ended.RaidId, cancellationToken).ConfigureAwait(true);
        // A raid the outbox has not delivered yet has no row to read; it has no outcome either.
        var ask = RaidOutcomeQuestion.ShouldAsk(raid?.Outcome, answers);
        AfterRaid.Show(
            ended.RaidId,
            DebriefText.RaidEndedAt(LocalTime.ShortTime(ended.EndedUtc)),
            ask,
            raid?.Outcome,
            raid is null ? string.Empty : DebriefText.Kind(await OutcomeKindAsync(raid, cancellationToken).ConfigureAwait(true)));
        await LoadAsync(cancellationToken).ConfigureAwait(true);
        await SelectRaidAsync(ended.RaidId, cancellationToken).ConfigureAwait(true);
    }

    private async Task<IReadOnlyList<RaidOutcomeAnswer>> ReadAnswersAsync(Guid raidId, CancellationToken cancellationToken) =>
        RaidOutcomeAnswer.ParseAll(await _raidHistoryService
            .ListEventPayloadsAsync(raidId, RaidOutcomeAnswer.EventType, cancellationToken)
            .ConfigureAwait(true));

    private async Task<RaidFactKind> OutcomeKindAsync(RaidHistoryEntry raid, CancellationToken cancellationToken) =>
        RaidFactRules.Classify(raid, await LoadCorrectionsAsync(raid.Id, cancellationToken).ConfigureAwait(true)).Outcome;

    private async Task AnswerAfterRaidAsync(RaidOutcomeBucket bucket)
    {
        if (AfterRaid.RaidId is not { } raidId || !AfterRaid.IsAsking)
        {
            return;
        }

        try
        {
            var raid = (await _raidHistoryService.ListAsync(CancellationToken.None).ConfigureAwait(true))
                .FirstOrDefault(entry => entry.Id == raidId)
                ?? throw new KeyNotFoundException(DebriefText.Unknown);
            if (!string.IsNullOrWhiteSpace(raid.Outcome))
            {
                // Something recorded an outcome since the card opened; the tap never overwrites it.
                AfterRaid.Close(DebriefText.OutcomeAlreadyRecorded(raid.Outcome), DebriefText.Kind(
                    await OutcomeKindAsync(raid, CancellationToken.None).ConfigureAwait(true)));
                return;
            }

            var now = _clock.GetUtcNow();
            await _raidHistoryService.CorrectAsync(raidId, RaidOutcomeQuestion.StoredText(bucket), raid.Notes, CancellationToken.None)
                .ConfigureAwait(true);
            await _raidHistoryService.RecordEventAsync(
                raidId,
                RaidOutcomeAnswer.EventType,
                now,
                RaidOutcomeAnswer.Answered(bucket, now).ToPayload(),
                CancellationToken.None).ConfigureAwait(true);
            AfterRaid.Close(
                DebriefText.OutcomeAnswered(DebriefText.Answer(bucket), LocalTime.ShortTime(now)),
                DebriefText.Kind(RaidFactKind.Manual));
            await ReloadKeepingSelectionAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AfterRaid.Status = DebriefText.OutcomeNotSaved(exception.Message);
        }
    }

    private async Task DismissAfterRaidAsync()
    {
        if (AfterRaid.RaidId is not { } raidId || !AfterRaid.IsAsking)
        {
            return;
        }

        try
        {
            var now = _clock.GetUtcNow();
            await _raidHistoryService.RecordEventAsync(
                raidId,
                RaidOutcomeAnswer.EventType,
                now,
                RaidOutcomeAnswer.Dismissal(now).ToPayload(),
                CancellationToken.None).ConfigureAwait(true);
            AfterRaid.Close(null, string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AfterRaid.Status = DebriefText.OutcomeNotSaved(exception.Message);
        }
    }

    private async Task ReloadKeepingSelectionAsync()
    {
        var selected = _selected?.Id;
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        if (selected is { } id)
        {
            await SelectRaidAsync(id, CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>Remembers the selected raid's counted scans and their value, for the recap.</summary>
    private void NoteScanTotals(int counted, long valueRoubles)
    {
        _selectedCountedScans = counted;
        _selectedScanValue = valueRoubles;
    }

    /// <summary>Rebuilds the card's recap when the selected raid is the one the card is about.</summary>
    private async Task RefreshAfterRaidRecapAsync(CancellationToken cancellationToken)
    {
        if (_selected is not { } raid || AfterRaid.RaidId != raid.Id)
        {
            return;
        }

        var record = _allRecords.FirstOrDefault(candidate => candidate.Raid.Id == raid.Id);
        var map = raid.MapId is { } mapId && _maps is not null
            ? await _maps.GetAsync(mapId, cancellationToken).ConfigureAwait(true)
            : null;
        var side = record?.Side ?? _afterRaidEnd?.Side;
        var lines = new List<RaidRecapLineViewModel>();

        var mapLabel = raid.MapId is { } id ? MapLabel(id) : DebriefText.UnknownMap;
        lines.Add(new("Clock", DebriefText.RecapInRaid(Duration(raid), mapLabel), DebriefText.Kind(_selectedSources.Ended == RaidFactKind.Unknown
            ? _selectedSources.Map
            : _selectedSources.Ended)));
        if (side is { Length: > 0 })
        {
            lines.Add(new("Target", DebriefText.RecapSide(side), DebriefText.Kind(RaidFactKind.Inferred)));
        }

        if (raid.StartedUtc is { } started && raid.EndedUtc is { } ended && ended > started
            && RaidTimer.LengthFor(side, map?.PmcRaidDuration, map?.ScavRaidDuration) is { } length && length > TimeSpan.Zero)
        {
            // The log's start and end against the catalog's raid length: a bound, not the game's clock.
            lines.Add(new(
                "Clock",
                DebriefText.RecapRaidClock(Math.Round((ended - started).TotalMinutes), Math.Round(length.TotalMinutes)),
                DebriefText.Kind(RaidFactKind.Estimated)));
        }

        if (record?.UsedExtract is { Length: > 0 } used)
        {
            lines.Add(new("Exit", DebriefText.RecapExtractUsed(used), DebriefText.Kind(RaidFactKind.Manual)));
        }
        else if (map is not null
            && RaidOutcomeQuestion.NearestExtract(_selectedPositions.LastOrDefault(), map.Extracts, record?.OfferedExtracts) is { } near)
        {
            lines.Add(new("Exit", DebriefText.RecapEndedNear(near.Name), DebriefText.Kind(RaidFactKind.Inferred)));
        }
        else
        {
            lines.Add(new("Exit", DebriefText.RecapExtractUnknown, string.Empty));
        }

        lines.Add(_selectedCountedScans == 0
            ? new("Box", DebriefText.RecapNoLoot, string.Empty)
            : new("Box",
                DebriefText.RecapLoot(UnitText.RoublesShort(_selectedScanValue), DebriefText.ScanCount(_selectedCountedScans)),
                DebriefText.Kind(RaidFactKind.Estimated)));

        var tasks = SelectedQuestEvents.Select(row => row.QuestLabel).Distinct(StringComparer.CurrentCulture).ToArray();
        lines.Add(tasks.Length == 0
            ? new("Clipboard", DebriefText.RecapNoTasks, string.Empty)
            : new("Clipboard", DebriefText.RecapTasks(Shortlist(tasks)), DebriefText.Kind(RaidFactKind.Observed)));

        if (_afterRaidEnd is { Squad.Count: > 0 } end && end.RaidId == raid.Id)
        {
            lines.Add(new("People", DebriefText.RecapSquad(Shortlist(end.Squad)), DebriefText.Kind(RaidFactKind.Observed)));
        }

        AfterRaid.Recap = lines;
    }

    private static string Shortlist(IReadOnlyList<string> names) => names.Count <= 3
        ? string.Join(", ", names)
        : DebriefText.RecapMore(string.Join(", ", names.Take(2)), names.Count - 2);
}
