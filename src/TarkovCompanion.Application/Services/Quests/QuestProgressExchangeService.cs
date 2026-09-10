using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Quests;

public sealed class QuestProgressExchangeService(
    IPlayerProfileService profileService,
    IQuestCatalog questCatalog,
    IQuestProgressStore progressStore,
    IQuestProgressImportStore importStore,
    IProjectQuestProgressJson projectJson,
    QuestTrackingOptions options) : IQuestProgressExchangeService, IQuestProgressImportPlanner
{
    private readonly string _language = options.NormalizedLanguage;

    public async Task<QuestProgressExportResult> ExportAsync(
        QuestProfileScope scope,
        string filePath,
        CancellationToken cancellationToken)
    {
        var profile = await ActiveProfileAsync(scope, cancellationToken).ConfigureAwait(false);
        var progress = await progressStore.GetAsync(scope, cancellationToken).ConfigureAwait(false);
        return await projectJson.WriteAsync(filePath, profile, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QuestProgressImportPreview> PreviewImportAsync(
        QuestProfileScope scope,
        string filePath,
        CancellationToken cancellationToken)
    {
        var profile = await ActiveProfileAsync(scope, cancellationToken).ConfigureAwait(false);
        var document = await projectJson.ReadAsync(filePath, cancellationToken).ConfigureAwait(false);
        var incoming = SelectExactProfile(document, profile);
        return await PreviewAsync(
            scope,
            new(
                document.IsLegacyProfileSettingsEnvelope
                    ? QuestProgressImportSource.LegacyProfileJsonV1
                    : QuestProgressImportSource.ProjectJsonV2,
                incoming.GameMode,
                incoming.ProfileGeneration,
                document.PayloadSha256,
                document.AppVersion,
                document.ExportedUtc,
                document.ProvenanceSummary,
                incoming.Tasks.Select(value => new QuestProgressImportTask(value.TaskId, value.State)).ToArray(),
                incoming.Objectives.Select(value => new QuestProgressImportObjective(
                    value.ObjectiveId,
                    value.State,
                    value.Count)).ToArray(),
                incoming.Holdings,
                incoming.Pins),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<QuestProgressImportPreview> PreviewAsync(
        QuestProfileScope scope,
        QuestProgressImportSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(scope, snapshot);
        var profile = await ActiveProfileAsync(scope, cancellationToken).ConfigureAwait(false);
        var progress = await progressStore.GetAsync(scope, cancellationToken).ConfigureAwait(false);
        var catalog = await questCatalog.GetAsync(scope.GameMode, _language, cancellationToken).ConfigureAwait(false);
        var knownTasks = catalog?.Tasks.Select(value => value.Id).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var knownObjectives = catalog?.Tasks
            .SelectMany(value => value.Objectives)
            .Select(value => value.Id)
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var knownItems = catalog?.Tasks
            .SelectMany(value => value.Objectives)
            .SelectMany(value => value.ItemTargets)
            .Select(value => value.ItemId)
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var proposals = new List<QuestImportProposal>();
        foreach (var task in snapshot.Tasks)
        {
            progress.Tasks.TryGetValue(task.TaskId, out var local);
            proposals.Add(TaskProposal(task, local, knownTasks.Contains(task.TaskId)));
        }

        foreach (var objective in snapshot.Objectives)
        {
            progress.Objectives.TryGetValue(objective.ObjectiveId, out var local);
            proposals.Add(ObjectiveProposal(objective, local, knownObjectives.Contains(objective.ObjectiveId)));
        }

        var holdings = progress.ItemHoldings.ToDictionary(
            value => (value.ItemId, value.FoundInRaid),
            value => value);
        foreach (var holding in snapshot.Holdings)
        {
            holdings.TryGetValue((holding.ItemId, holding.FoundInRaid), out var local);
            proposals.Add(HoldingProposal(holding, local, knownItems.Contains(holding.ItemId)));
        }

        var pins = progress.Pins.ToDictionary(
            value => (value.TargetKind, value.TargetId),
            value => value);
        foreach (var pin in snapshot.Pins)
        {
            pins.TryGetValue((pin.TargetKind, pin.TargetId), out var local);
            var isKnown = pin.TargetKind switch
            {
                QuestPinTargetKind.Task => knownTasks.Contains(pin.TargetId),
                QuestPinTargetKind.Objective => knownObjectives.Contains(pin.TargetId),
                _ => false,
            };
            proposals.Add(PinProposal(pin, local, isKnown));
        }

        return WithHash(new(
            scope,
            profile.Name,
            progress.Revision,
            snapshot.PayloadSha256,
            string.Empty,
            snapshot.SourceVersion,
            snapshot.ObservedUtc,
            snapshot.ProvenanceSummary,
            snapshot.Source == QuestProgressImportSource.LegacyProfileJsonV1,
            proposals.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray(),
            snapshot.Source));
    }

    public async Task<QuestImportApplyResult> ApplyImportAsync(
        QuestProgressImportPreview preview,
        IReadOnlyDictionary<string, QuestImportResolution> resolutions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(resolutions);
        var profile = await ActiveProfileAsync(preview.Scope, cancellationToken).ConfigureAwait(false);
        if (!profile.Name.Equals(preview.ProfileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The active profile name changed after import preview; preview again.");
        }

        var actualHash = QuestImportPreviewHash.Compute(preview);
        if (!actualHash.Equals(preview.PreviewSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The import preview was modified after it was created.");
        }

        var conflicts = preview.Conflicts.Select(value => value.Key).ToHashSet(StringComparer.Ordinal);
        if (conflicts.Any(key => !resolutions.ContainsKey(key)))
        {
            throw new InvalidOperationException("Every import conflict requires an explicit resolution before apply.");
        }

        if (resolutions.Keys.Any(key => !conflicts.Contains(key)))
        {
            throw new InvalidOperationException("Import resolutions contain an item that is not a preview conflict.");
        }

        return await importStore.ApplyImportAsync(
            preview,
            preview.PreviewSha256,
            resolutions,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<QuestImportUndoResult> UndoImportAsync(
        QuestProfileScope scope,
        Guid importId,
        CancellationToken cancellationToken)
    {
        await ActiveProfileAsync(scope, cancellationToken).ConfigureAwait(false);
        return await importStore.UndoImportAsync(scope, importId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlayerProfile> ActiveProfileAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        QuestProgressCommandService.EnsureScope(profile, scope);
        return profile;
    }

    private static ProjectQuestProgressProfile SelectExactProfile(
        ProjectQuestProgressDocument document,
        PlayerProfile profile)
    {
        var matching = document.Profiles.Where(value =>
            value.ProfileId == profile.Id &&
            value.GameMode == profile.GameMode &&
            value.ProfileGeneration.Equals(profile.ProfileGeneration, StringComparison.Ordinal)).ToArray();
        if (matching.Length != 1)
        {
            var sameId = document.Profiles.Any(value => value.ProfileId == profile.Id);
            throw new InvalidOperationException(sameId
                ? "Quest progress import has the wrong game mode or profile generation for the active profile."
                : "Quest progress import does not contain the active profile id.");
        }

        if (!matching[0].ProfileName.Equals(profile.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Quest progress import profile name does not exactly match the active profile.");
        }

        return matching[0];
    }

    private static QuestProgressImportPreview WithHash(QuestProgressImportPreview preview) =>
        preview with { PreviewSha256 = QuestImportPreviewHash.Compute(preview) };

    private static void ValidateSnapshot(QuestProfileScope scope, QuestProgressImportSnapshot snapshot)
    {
        if (!Enum.IsDefined(snapshot.Source) || !Enum.IsDefined(snapshot.GameMode) ||
            snapshot.GameMode != scope.GameMode)
        {
            throw new InvalidOperationException("The progress snapshot source or game mode is invalid.");
        }

        if (snapshot.Source != QuestProgressImportSource.TarkovTracker &&
            !string.Equals(snapshot.SourceGeneration, scope.Generation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The progress snapshot profile generation does not match the active scope.");
        }

        if (snapshot.Source == QuestProgressImportSource.TarkovTracker && snapshot.SourceGeneration is not null)
        {
            throw new InvalidOperationException(
                "TarkovTracker does not expose a source generation; the adapter must not manufacture one.");
        }

        if (snapshot.PayloadSha256.Length != 64 || !snapshot.PayloadSha256.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(snapshot.SourceVersion) || snapshot.SourceVersion.Length > 256 ||
            string.IsNullOrWhiteSpace(snapshot.ProvenanceSummary) || snapshot.ProvenanceSummary.Length > 4096)
        {
            throw new InvalidOperationException("The progress snapshot metadata is invalid.");
        }

        var recordCount = snapshot.Tasks.Count + snapshot.Objectives.Count + snapshot.Holdings.Count + snapshot.Pins.Count;
        if (recordCount > 50_000 ||
            snapshot.Tasks.Select(value => value.TaskId).Distinct(StringComparer.Ordinal).Count() != snapshot.Tasks.Count ||
            snapshot.Objectives.Select(value => value.ObjectiveId).Distinct(StringComparer.Ordinal).Count() != snapshot.Objectives.Count)
        {
            throw new InvalidOperationException("The progress snapshot contains too many records or duplicate ids.");
        }
    }

    private static QuestImportProposal TaskProposal(
        QuestProgressImportTask incoming,
        RecordedTaskProgress? local,
        bool isKnown)
    {
        var localValue = local is null ? null : new QuestImportValue(TaskState: local.State);
        var incomingValue = new QuestImportValue(TaskState: incoming.State);
        if (incoming.UnresolvedReason is not null || incoming.State is null)
        {
            return Proposal(
                "task",
                QuestProgressEntityKind.Task,
                incoming.TaskId,
                null,
                QuestImportClassification.UnresolvedSourceRecord,
                localValue,
                incomingValue,
                incoming.UnresolvedReason ?? "The source task record could not be mapped safely.");
        }

        var incomingState = incoming.State.Value;
        if (!isKnown)
        {
            return Proposal(
                "task",
                QuestProgressEntityKind.Task,
                incoming.TaskId,
                null,
                QuestImportClassification.UnresolvedUnknownId,
                localValue,
                incomingValue,
                "Task id is not present in the selected mode catalog.");
        }

        var localState = local?.State ?? RecordedTaskState.Unknown;
        if (localState == incomingState)
        {
            return Proposal(
                "task", QuestProgressEntityKind.Task, incoming.TaskId, null,
                QuestImportClassification.IgnoredUnchanged, localValue, incomingValue,
                "Incoming task state matches local state; absence elsewhere is not deletion.");
        }

        var conflict = localState == RecordedTaskState.Failed || incomingState == RecordedTaskState.Failed ||
            TaskStrength(incomingState) < TaskStrength(localState);
        return Proposal(
            "task", QuestProgressEntityKind.Task, incoming.TaskId, null,
            conflict ? QuestImportClassification.Conflict : QuestImportClassification.SafeMonotonic,
            localValue,
            incomingValue,
            conflict
                ? "Incoming task state conflicts with or regresses stronger local progress."
                : "Incoming task state is a monotonic promotion.");
    }

    private static QuestImportProposal ObjectiveProposal(
        QuestProgressImportObjective incoming,
        RecordedObjectiveProgress? local,
        bool isKnown)
    {
        var localValue = local is null
            ? null
            : new QuestImportValue(ObjectiveState: local.State, ObjectiveCount: local.Count);
        var incomingValue = new QuestImportValue(
            ObjectiveState: incoming.State,
            ObjectiveCount: incoming.Count);
        if (incoming.UnresolvedReason is not null || incoming.State is null)
        {
            return Proposal(
                "objective",
                QuestProgressEntityKind.Objective,
                incoming.ObjectiveId,
                null,
                QuestImportClassification.UnresolvedSourceRecord,
                localValue,
                incomingValue,
                incoming.UnresolvedReason ?? "The source objective record could not be mapped safely.");
        }

        var incomingState = incoming.State.Value;
        if (!isKnown)
        {
            return Proposal(
                "objective", QuestProgressEntityKind.Objective, incoming.ObjectiveId, null,
                QuestImportClassification.UnresolvedUnknownId, localValue, incomingValue,
                "Objective id is not present in the selected mode catalog.");
        }

        var localState = local?.State ?? RecordedObjectiveState.Unknown;
        var localCount = local?.Count;
        if (localState == incomingState && localCount == incoming.Count)
        {
            return Proposal(
                "objective", QuestProgressEntityKind.Objective, incoming.ObjectiveId, null,
                QuestImportClassification.IgnoredUnchanged, localValue, incomingValue,
                "Incoming objective progress matches local progress; absence elsewhere is not deletion.");
        }

        var countRegression = localCount is not null &&
            (incoming.Count is null || incoming.Count.Value < localCount.Value);
        var stateRegression = ObjectiveStrength(incomingState) < ObjectiveStrength(localState);
        var classification = countRegression || stateRegression
            ? QuestImportClassification.Conflict
            : QuestImportClassification.SafeMonotonic;
        return Proposal(
            "objective", QuestProgressEntityKind.Objective, incoming.ObjectiveId, null,
            classification, localValue, incomingValue,
            classification == QuestImportClassification.Conflict
                ? "Incoming objective state or count regresses stronger local progress."
                : "Incoming objective state or count is a monotonic promotion.");
    }

    private static QuestImportProposal HoldingProposal(
        ProjectQuestProgressHolding incoming,
        RecordedItemHolding? local,
        bool isKnown)
    {
        var localValue = local is null
            ? null
            : new QuestImportValue(HoldingFoundInRaid: local.FoundInRaid, HoldingCount: local.Count);
        var incomingValue = new QuestImportValue(
            HoldingFoundInRaid: incoming.FoundInRaid,
            HoldingCount: incoming.Count);
        var subkey = incoming.FoundInRaid ? "fir" : "non-fir";
        if (!isKnown)
        {
            return Proposal(
                "holding", QuestProgressEntityKind.ItemHolding, incoming.ItemId, subkey,
                QuestImportClassification.UnresolvedUnknownId, localValue, incomingValue,
                "Holding item id is not present in a quest objective in the selected mode catalog.");
        }

        if (local?.Count == incoming.Count)
        {
            return Proposal(
                "holding", QuestProgressEntityKind.ItemHolding, incoming.ItemId, subkey,
                QuestImportClassification.IgnoredUnchanged, localValue, incomingValue,
                "Incoming explicit holding matches the local FIR class count.");
        }

        var classification = local is not null && incoming.Count < local.Count
            ? QuestImportClassification.Conflict
            : QuestImportClassification.SafeMonotonic;
        return Proposal(
            "holding", QuestProgressEntityKind.ItemHolding, incoming.ItemId, subkey,
            classification, localValue, incomingValue,
            classification == QuestImportClassification.Conflict
                ? "Incoming explicit holding count is lower than local progress."
                : "Incoming explicit holding is new or increased for this exact FIR class.");
    }

    private static QuestImportProposal PinProposal(
        ProjectQuestProgressPin incoming,
        RecordedQuestPin? local,
        bool isKnown)
    {
        var localValue = local is null
            ? null
            : new QuestImportValue(
                PinTargetKind: local.TargetKind,
                PinSortOrder: local.SortOrder,
                PinNote: local.Note);
        var incomingValue = new QuestImportValue(
            PinTargetKind: incoming.TargetKind,
            PinSortOrder: incoming.SortOrder,
            PinNote: incoming.Note);
        var subkey = incoming.TargetKind.ToString();
        if (!isKnown)
        {
            return Proposal(
                "pin", QuestProgressEntityKind.Pin, incoming.TargetId, subkey,
                QuestImportClassification.UnresolvedUnknownId, localValue, incomingValue,
                "Pin target id is not present in the selected mode catalog.");
        }

        if (local?.SortOrder == incoming.SortOrder && local?.Note == incoming.Note)
        {
            return Proposal(
                "pin", QuestProgressEntityKind.Pin, incoming.TargetId, subkey,
                QuestImportClassification.IgnoredUnchanged, localValue, incomingValue,
                "Incoming pin matches the local pin.");
        }

        var classification = local is null
            ? QuestImportClassification.SafeMonotonic
            : QuestImportClassification.Conflict;
        return Proposal(
            "pin", QuestProgressEntityKind.Pin, incoming.TargetId, subkey,
            classification, localValue, incomingValue,
            classification == QuestImportClassification.Conflict
                ? "Incoming pin ordering or note differs from the local pin."
                : "Incoming pin adds a new owned pin.");
    }

    private static QuestImportProposal Proposal(
        string prefix,
        QuestProgressEntityKind entityKind,
        string entityId,
        string? subkey,
        QuestImportClassification classification,
        QuestImportValue? localValue,
        QuestImportValue incomingValue,
        string reason) => new(
            $"{prefix}:{entityId}:{subkey ?? string.Empty}",
            entityKind,
            entityId,
            subkey,
            classification,
            localValue,
            incomingValue,
            reason);

    private static int TaskStrength(RecordedTaskState state) => state switch
    {
        RecordedTaskState.Unknown => 0,
        RecordedTaskState.NotStarted => 1,
        RecordedTaskState.Active => 2,
        RecordedTaskState.Completed => 3,
        RecordedTaskState.Failed => -1,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static int ObjectiveStrength(RecordedObjectiveState state) => state switch
    {
        RecordedObjectiveState.Unknown => 0,
        RecordedObjectiveState.InProgress => 1,
        RecordedObjectiveState.Completed => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
