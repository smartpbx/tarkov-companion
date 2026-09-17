using System.Globalization;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Quests;

public sealed record QuestTrackingOptions(string Language = "en")
{
    public string NormalizedLanguage => string.IsNullOrWhiteSpace(Language)
        ? throw new ArgumentException("A quest catalog language is required.", nameof(Language))
        : Language.Trim().ToLowerInvariant();
}

public sealed class QuestProgressCommandService(
    IPlayerProfileService profileService,
    IQuestCatalog questCatalog,
    IQuestProgressStore progressStore,
    QuestTrackingOptions options,
    TimeProvider? timeProvider = null) : IQuestProgressCommandService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly string _language = options.NormalizedLanguage;

    public async Task<QuestProgressCommandResult> SetTaskStateAsync(
        QuestProfileScope scope,
        string taskId,
        RecordedTaskState state,
        CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(scope, cancellationToken).ConfigureAwait(false);
        var normalizedId = RequiredId(taskId, nameof(taskId));
        if (!context.Catalog.Tasks.Any(task => task.Id == normalizedId))
        {
            throw new InvalidOperationException($"Task '{normalizedId}' is not present in the {scope.GameMode} catalog.");
        }

        return await progressStore.ApplyAsync(
            new SetTaskStateMutation(
                scope,
                context.Profile.Name,
                normalizedId,
                state,
                QuestProgressActor.User,
                "Manual",
                Guid.NewGuid(),
                _timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<QuestProgressCommandResult> SetObjectiveProgressAsync(
        QuestProfileScope scope,
        string objectiveId,
        RecordedObjectiveState state,
        decimal? count,
        CancellationToken cancellationToken)
    {
        if (count is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Objective count cannot be negative.");
        }

        var context = await GetContextAsync(scope, cancellationToken).ConfigureAwait(false);
        var normalizedId = RequiredId(objectiveId, nameof(objectiveId));
        if (!context.Catalog.Tasks.SelectMany(task => task.Objectives).Any(objective => objective.Id == normalizedId))
        {
            throw new InvalidOperationException(
                $"Objective '{normalizedId}' is not present in the {scope.GameMode} catalog.");
        }

        return await progressStore.ApplyAsync(
            new SetObjectiveProgressMutation(
                scope,
                context.Profile.Name,
                normalizedId,
                state,
                count,
                QuestProgressActor.User,
                "Manual",
                Guid.NewGuid(),
                _timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<QuestProgressCommandResult> SetItemHoldingAsync(
        QuestProfileScope scope,
        string itemId,
        bool foundInRaid,
        int? count,
        CancellationToken cancellationToken)
    {
        if (count is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Item holding count cannot be negative.");
        }

        var context = await GetContextAsync(scope, cancellationToken).ConfigureAwait(false);
        var normalizedId = RequiredId(itemId, nameof(itemId));
        if (!context.Catalog.Tasks
                .SelectMany(task => task.Objectives)
                .SelectMany(objective => objective.ItemTargets)
                .Any(target => target.ItemId == normalizedId))
        {
            throw new InvalidOperationException(
                $"Item '{normalizedId}' is not present in a quest objective for the {scope.GameMode} catalog.");
        }

        return await progressStore.ApplyAsync(
            new SetItemHoldingMutation(
                scope,
                context.Profile.Name,
                normalizedId,
                foundInRaid,
                count,
                QuestProgressActor.User,
                "Manual",
                Guid.NewGuid(),
                _timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<QuestProgressCommandResult> SetPinAsync(
        QuestProfileScope scope,
        QuestPinTargetKind targetKind,
        string targetId,
        bool isPinned,
        int sortOrder,
        string? note,
        CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(scope, cancellationToken).ConfigureAwait(false);
        var normalizedId = RequiredId(targetId, nameof(targetId));
        var exists = targetKind switch
        {
            QuestPinTargetKind.Task => context.Catalog.Tasks.Any(task => task.Id == normalizedId),
            QuestPinTargetKind.Objective => context.Catalog.Tasks
                .SelectMany(task => task.Objectives)
                .Any(objective => objective.Id == normalizedId),
            _ => throw new ArgumentOutOfRangeException(nameof(targetKind)),
        };
        if (!exists)
        {
            throw new InvalidOperationException(
                $"{targetKind} '{normalizedId}' is not present in the {scope.GameMode} catalog.");
        }

        return await progressStore.ApplyAsync(
            new SetQuestPinMutation(
                scope,
                context.Profile.Name,
                targetKind,
                normalizedId,
                isPinned,
                sortOrder,
                note,
                QuestProgressActor.User,
                "Manual",
                Guid.NewGuid(),
                _timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandContext> GetContextAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        EnsureScope(profile, scope);
        var catalog = await questCatalog.GetAsync(scope.GameMode, _language, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No validated {scope.GameMode} quest catalog is available; progress was not changed.");
        return new(profile, catalog);
    }

    internal static void EnsureScope(PlayerProfile profile, QuestProfileScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (profile.Id != scope.ProfileId ||
            profile.GameMode != scope.GameMode ||
            !string.Equals(profile.ProfileGeneration, scope.Generation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Quest progress scope does not match the active profile, exact game mode, and generation.");
        }
    }

    private static string RequiredId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private sealed record CommandContext(PlayerProfile Profile, QuestCatalogSnapshot Catalog);
}

public sealed class QuestEligibilityEvaluator
{
    public QuestEligibility Evaluate(
        QuestTaskDefinition task,
        QuestCatalogSnapshot catalog,
        QuestProgressSnapshot progress,
        PlayerProfile profile,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(profile);

        var tasks = catalog.Tasks.ToDictionary(value => value.Id, StringComparer.Ordinal);
        if (HasPrerequisiteCycle(task.Id, tasks))
        {
            return Indeterminate(new(
                "prerequisite-cycle",
                "The catalog prerequisite graph contains a cycle.",
                task.Id));
        }

        var reasons = new List<QuestEligibilityReason>();
        var definitelyLocked = false;
        EvaluateFaction(task, profile, reasons, ref definitelyLocked);
        if (!string.IsNullOrWhiteSpace(task.RequiredPrestigeId))
        {
            reasons.Add(new(
                "unknown-profile-prestige",
                "The task requires a prestige, but this profile does not record prestige state."));
        }

        if (task.MinimumPlayerLevel is int minimumLevel && profile.Level < minimumLevel)
        {
            definitelyLocked = true;
            reasons.Add(new(
                "player-level",
                $"Recorded player level {profile.Level} is below required level {minimumLevel}."));
        }

        foreach (var requirement in task.Requirements.OrderBy(value => value.SourceOrdinal))
        {
            if (!tasks.ContainsKey(requirement.RequiredTaskId))
            {
                reasons.Add(new(
                    "missing-prerequisite-task",
                    $"{Name(tasks, requirement.RequiredTaskId)} is absent from the selected catalog.",
                    requirement.RequiredTaskId));
                continue;
            }

            var acceptedStates = new HashSet<RecordedTaskState>();
            var hasUnknownStatus = false;
            foreach (var status in requirement.RequiredStatuses)
            {
                if (TryMapRequiredStatus(status, out var accepted))
                {
                    acceptedStates.Add(accepted);
                }
                else
                {
                    hasUnknownStatus = true;
                }
            }

            if (!progress.Tasks.TryGetValue(requirement.RequiredTaskId, out var recorded) ||
                recorded.State == RecordedTaskState.Unknown)
            {
                reasons.Add(new(
                    "unknown-prerequisite-state",
                    $"{Name(tasks, requirement.RequiredTaskId)} has no explicit recorded state.",
                    requirement.RequiredTaskId));
                continue;
            }

            if (acceptedStates.Contains(recorded.State))
            {
                continue;
            }

            if (hasUnknownStatus || acceptedStates.Count == 0)
            {
                reasons.Add(new(
                    "unsupported-prerequisite-status",
                    $"{Name(tasks, requirement.RequiredTaskId)} has a prerequisite status this version cannot evaluate.",
                    requirement.RequiredTaskId));
                continue;
            }

            definitelyLocked = true;
            reasons.Add(new(
                "prerequisite-state",
                $"{Name(tasks, requirement.RequiredTaskId)} must be {string.Join(" or ", requirement.RequiredStatuses)} and is recorded {recorded.State}.",
                requirement.RequiredTaskId));
        }

        if (definitelyLocked)
        {
            return new(QuestEligibilityState.Locked, reasons);
        }

        if (reasons.Count > 0)
        {
            return new(QuestEligibilityState.Indeterminate, reasons);
        }

        return EvaluateDelay(task, progress, nowUtc.ToUniversalTime());
    }

    private static QuestEligibility EvaluateDelay(
        QuestTaskDefinition task,
        QuestProgressSnapshot progress,
        DateTimeOffset nowUtc)
    {
        var minimumSeconds = task.AvailableDelaySecondsMinimum;
        var maximumSeconds = task.AvailableDelaySecondsMaximum;
        if (minimumSeconds is null && maximumSeconds is null)
        {
            return new(QuestEligibilityState.Available, []);
        }

        if (minimumSeconds is < 0 || maximumSeconds is < 0)
        {
            return Indeterminate(new("invalid-delay", "The catalog contains a negative availability delay."));
        }

        var completedPrerequisites = task.Requirements
            .Select(requirement => progress.Tasks.GetValueOrDefault(requirement.RequiredTaskId))
            .Where(recorded => recorded?.State == RecordedTaskState.Completed)
            .Select(recorded => recorded!.ModifiedUtc)
            .ToArray();
        if (completedPrerequisites.Length == 0)
        {
            return Indeterminate(new(
                "unknown-delay-origin",
                "The availability delay has no recorded prerequisite completion time."));
        }

        var origin = completedPrerequisites.Max().ToUniversalTime();
        var min = minimumSeconds ?? maximumSeconds ?? 0;
        var max = maximumSeconds ?? minimumSeconds ?? 0;
        if (max < min)
        {
            return Indeterminate(new("invalid-delay-window", "The catalog availability delay range is invalid."));
        }

        var earliest = origin.AddSeconds(min);
        var latest = origin.AddSeconds(max);
        if (nowUtc < earliest)
        {
            return new(
                QuestEligibilityState.Delayed,
                [new("availability-delay", "The recorded prerequisite completion is still inside the minimum delay.")],
                earliest);
        }

        if (nowUtc < latest)
        {
            return new(
                QuestEligibilityState.Indeterminate,
                [new("availability-delay-window", "The exact time inside the catalog delay range is unknown.")],
                latest);
        }

        return new(QuestEligibilityState.Available, []);
    }

    private static void EvaluateFaction(
        QuestTaskDefinition task,
        PlayerProfile profile,
        ICollection<QuestEligibilityReason> reasons,
        ref bool definitelyLocked)
    {
        if (string.IsNullOrWhiteSpace(task.FactionName) ||
            task.FactionName.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!Enum.TryParse<Faction>(task.FactionName, ignoreCase: true, out var requiredFaction) ||
            requiredFaction == Faction.Unknown)
        {
            reasons.Add(new("unknown-catalog-faction", "The catalog task faction cannot be evaluated."));
            return;
        }

        if (profile.Faction == Faction.Unknown)
        {
            reasons.Add(new("unknown-profile-faction", "The profile faction has not been recorded."));
            return;
        }

        if (profile.Faction != requiredFaction)
        {
            definitelyLocked = true;
            reasons.Add(new(
                "faction",
                $"Task requires {requiredFaction} but the profile is {profile.Faction}."));
        }
    }

    private static bool TryMapRequiredStatus(string status, out RecordedTaskState state)
    {
        if (status.Equals("complete", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            state = RecordedTaskState.Completed;
            return true;
        }

        if (status.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            state = RecordedTaskState.Active;
            return true;
        }

        if (status.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            state = RecordedTaskState.Failed;
            return true;
        }

        state = RecordedTaskState.Unknown;
        return false;
    }

    private static bool HasPrerequisiteCycle(
        string taskId,
        IReadOnlyDictionary<string, QuestTaskDefinition> tasks)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return Visit(taskId);

        bool Visit(string currentId)
        {
            if (!tasks.TryGetValue(currentId, out var current))
            {
                return false;
            }

            if (!visiting.Add(currentId))
            {
                return true;
            }

            if (!visited.Add(currentId))
            {
                visiting.Remove(currentId);
                return false;
            }

            foreach (var requirement in current.Requirements)
            {
                if (Visit(requirement.RequiredTaskId))
                {
                    return true;
                }
            }

            visiting.Remove(currentId);
            return false;
        }
    }

    /// <summary>
    /// Names the quest that is blocking, because a task id is not something a player knows.
    /// </summary>
    /// <remarks>
    /// The reason has carried the blocking task's id since it was written and nothing ever
    /// showed it, so every locked quest on the board explained itself as "the prerequisite
    /// task", singular and anonymous, whichever of three it was. The id is still carried for
    /// anything that wants to follow it; this is the half a person reads.
    ///
    /// A task absent from the catalog has no name to give, which is the one case where the id
    /// is the most honest thing to print.
    /// </remarks>
    private static string Name(IReadOnlyDictionary<string, QuestTaskDefinition> tasks, string taskId) =>
        tasks.TryGetValue(taskId, out var task) && !string.IsNullOrWhiteSpace(task.Name)
            ? task.Name
            : taskId;

    private static QuestEligibility Indeterminate(QuestEligibilityReason reason) =>
        new(QuestEligibilityState.Indeterminate, [reason]);
}

public sealed class QuestReadService(
    IPlayerProfileService profileService,
    IQuestCatalog questCatalog,
    IQuestProgressStore progressStore,
    QuestEligibilityEvaluator eligibilityEvaluator,
    QuestTrackingOptions options,
    TimeProvider? timeProvider = null,
    ITraderCatalog? traderCatalog = null) : IQuestReadService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly string _language = options.NormalizedLanguage;

    public async Task<QuestBoardReadModel> GetQuestBoardAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(scope, cancellationToken).ConfigureAwait(false);
        if (context.Catalog is null)
        {
            return new(
                scope,
                context.Progress.Revision,
                null,
                [],
                BuildOrphans(context.Progress, null),
                $"No validated {scope.GameMode} quest catalog is available.");
        }

        var pinnedTasks = context.Progress.Pins
            .Where(pin => pin.TargetKind == QuestPinTargetKind.Task)
            .Select(pin => pin.TargetId)
            .ToHashSet(StringComparer.Ordinal);
        var summaries = context.Catalog.Tasks
            .OrderBy(task => task.Name, StringComparer.Ordinal)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .Select(task => BuildTask(task, context, pinnedTasks.Contains(task.Id)))
            .ToArray();
        return new(
            scope,
            context.Progress.Revision,
            context.Catalog.Provenance,
            summaries,
            BuildOrphans(context.Progress, context.Catalog));
    }

    public async Task<QuestItemNeedsReadModel> GetItemNeedsAsync(
        QuestProfileScope scope,
        string itemId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var normalizedItemId = itemId.Trim();
        var context = await GetContextAsync(scope, cancellationToken).ConfigureAwait(false);
        var firHeld = HoldingCount(context.Progress, normalizedItemId, foundInRaid: true);
        var nonFirHeld = HoldingCount(context.Progress, normalizedItemId, foundInRaid: false);
        if (context.Catalog is null)
        {
            return new(
                scope,
                normalizedItemId,
                firHeld,
                nonFirHeld,
                [],
                BuildOrphans(context.Progress, null),
                $"No validated {scope.GameMode} quest catalog is available.");
        }

        var requirements = new List<QuestItemRequirementReadModel>();
        foreach (var task in context.Catalog.Tasks
                     .Where(task => context.Progress.Tasks.GetValueOrDefault(task.Id)?.State == RecordedTaskState.Active)
                     .OrderBy(task => task.Id, StringComparer.Ordinal))
        {
            foreach (var objective in task.Objectives
                         .Where(objective => !objective.IsUnsupported && objective.Optional == false)
                         .OrderBy(objective => objective.SourceOrdinal)
                         .ThenBy(objective => objective.Id, StringComparer.Ordinal))
            {
                var recorded = context.Progress.Objectives.GetValueOrDefault(objective.Id);
                if (recorded?.State == RecordedObjectiveState.Completed)
                {
                    continue;
                }

                foreach (var group in objective.ItemTargets
                             .GroupBy(target => (target.SourceField, target.AlternativeGroup))
                             .OrderBy(group => group.Key.SourceField, StringComparer.Ordinal)
                             .ThenBy(group => group.Key.AlternativeGroup))
                {
                    if (!group.Any(target => target.ItemId == normalizedItemId))
                    {
                        continue;
                    }

                    var groupTargetCounts = group
                        .Where(target => target.TargetCount is not null)
                        .Select(target => target.TargetCount!.Value)
                        .ToArray();
                    var targetCount = objective.TargetCount ?? (groupTargetCounts.Length == 0
                        ? null
                        : groupTargetCounts.Max());
                    var recordedCount = recorded?.Count;
                    decimal? remaining = recordedCount is null || targetCount is null
                        ? null
                        : Math.Max(0, targetCount.Value - recordedCount.Value);
                    requirements.Add(new(
                        task.Id,
                        task.Name,
                        objective.Id,
                        group.Select(target => target.ItemId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                        group.Key.SourceField,
                        group.Key.AlternativeGroup,
                        FoundInRaidRequirement(objective, group, normalizedItemId),
                        targetCount,
                        recordedCount,
                        remaining));
                }
            }
        }

        return new(
            scope,
            normalizedItemId,
            firHeld,
            nonFirHeld,
            requirements,
            BuildOrphans(context.Progress, context.Catalog));
    }

    public async Task<QuestMapObjectivesReadModel> GetActiveMapObjectivesAsync(
        QuestProfileScope scope,
        IReadOnlyCollection<string> mapIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapIds);
        var requestedMapIds = mapIds
            .Where(mapId => !string.IsNullOrWhiteSpace(mapId))
            .Select(mapId => mapId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestedMapIds.Length == 0)
        {
            throw new ArgumentException("At least one map identity is required.", nameof(mapIds));
        }

        var context = await GetContextAsync(scope, cancellationToken).ConfigureAwait(false);
        if (context.Catalog is null)
        {
            return new(
                scope,
                context.Progress.Revision,
                null,
                requestedMapIds,
                [],
                BuildOrphans(context.Progress, null),
                $"No validated {scope.GameMode} quest catalog is available.");
        }

        var taskPins = context.Progress.Pins
            .Where(pin => pin.TargetKind == QuestPinTargetKind.Task)
            .ToDictionary(pin => pin.TargetId, StringComparer.Ordinal);
        var objectivePins = context.Progress.Pins
            .Where(pin => pin.TargetKind == QuestPinTargetKind.Objective)
            .ToDictionary(pin => pin.TargetId, StringComparer.Ordinal);
        var objectives = new List<QuestMapObjectiveReadModel>();
        foreach (var task in context.Catalog.Tasks)
        {
            var taskProgress = context.Progress.Tasks.GetValueOrDefault(task.Id);
            var taskPinned = taskPins.GetValueOrDefault(task.Id);
            foreach (var objective in task.Objectives)
            {
                var objectiveProgress = context.Progress.Objectives.GetValueOrDefault(objective.Id);
                var objectivePinned = objectivePins.GetValueOrDefault(objective.Id);
                var isActiveIncomplete = taskProgress?.State == RecordedTaskState.Active &&
                    objectiveProgress?.State != RecordedObjectiveState.Completed;
                if (!isActiveIncomplete && taskPinned is null && objectivePinned is null)
                {
                    continue;
                }

                var objectiveMapIds = objective.MapAssociations
                    .Select(association => association.MapId)
                    .Concat(objective.Zones.Where(zone => zone.MapId is not null).Select(zone => zone.MapId!))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var associatedMapIds = objectiveMapIds.Length == 0 && task.PrimaryMapId is not null
                    ? [task.PrimaryMapId]
                    : objectiveMapIds;
                if (!associatedMapIds.Any(associated => requestedMapIds.Contains(associated, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var pinOrder = new[] { taskPinned?.SortOrder, objectivePinned?.SortOrder }
                    .Where(value => value is not null)
                    .Select(value => value!.Value)
                    .DefaultIfEmpty()
                    .Min();
                objectives.Add(new(
                    task.Id,
                    task.Name,
                    task.TraderId,
                    objective.Id,
                    objective.SourceOrdinal,
                    objective.Description,
                    objective.Kind,
                    objective.IsUnsupported,
                    objective.Optional,
                    taskProgress?.State ?? RecordedTaskState.Unknown,
                    objectiveProgress?.State ?? RecordedObjectiveState.Unknown,
                    taskPinned is not null,
                    objectivePinned is not null,
                    taskPinned is null && objectivePinned is null ? null : pinOrder,
                    objectiveProgress?.Source ?? taskProgress?.Source ?? "No progress assertion",
                    objectiveProgress?.ModifiedUtc ?? taskProgress?.ModifiedUtc,
                    objective.FoundInRaidRequired,
                    associatedMapIds,
                    objective.Zones,
                    objective.ItemTargets)
                {
                    TraderName = NameOfTrader(task.TraderId, context),
                });
            }
        }

        var ordered = objectives
            .OrderBy(objective => objective.PinSortOrder is null ? 1 : 0)
            .ThenBy(objective => objective.PinSortOrder)
            .ThenBy(objective => objective.TaskName, StringComparer.Ordinal)
            .ThenBy(objective => objective.TaskId, StringComparer.Ordinal)
            .ThenBy(objective => objective.SourceOrdinal)
            .ThenBy(objective => objective.ObjectiveId, StringComparer.Ordinal)
            .ToArray();
        return new(
            scope,
            context.Progress.Revision,
            context.Catalog.Provenance,
            requestedMapIds,
            ordered,
            BuildOrphans(context.Progress, context.Catalog));
    }

    private QuestSummaryReadModel BuildTask(
        QuestTaskDefinition task,
        ReadContext context,
        bool isPinned)
    {
        var taskProgress = context.Progress.Tasks.GetValueOrDefault(task.Id);
        var pinnedObjectives = context.Progress.Pins
            .Where(pin => pin.TargetKind == QuestPinTargetKind.Objective)
            .Select(pin => pin.TargetId)
            .ToHashSet(StringComparer.Ordinal);
        var objectives = task.Objectives
            .OrderBy(objective => objective.SourceOrdinal)
            .ThenBy(objective => objective.Id, StringComparer.Ordinal)
            .Select(objective =>
            {
                var recorded = context.Progress.Objectives.GetValueOrDefault(objective.Id);
                return new QuestObjectiveReadModel(
                    objective.Id,
                    objective.Description,
                    objective.Kind,
                    objective.Optional,
                    objective.IsUnsupported,
                    recorded?.State ?? RecordedObjectiveState.Unknown,
                    recorded?.Count,
                    objective.TargetCount,
                    objective.FoundInRaidRequired,
                    recorded?.Source ?? "No progress assertion",
                    recorded?.ModifiedUtc,
                    pinnedObjectives.Contains(objective.Id),
                    objective.MapAssociations
                        .Select(association => association.MapId)
                        .Concat(objective.Zones.Where(zone => zone.MapId is not null).Select(zone => zone.MapId!))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    objective.ItemTargets);
            })
            .ToArray();
        return new(
            task.Id,
            task.Name,
            task.TraderId,
            task.PrimaryMapId,
            taskProgress?.State ?? RecordedTaskState.Unknown,
            taskProgress?.Source ?? "No progress assertion",
            taskProgress?.ModifiedUtc,
            eligibilityEvaluator.Evaluate(
                task,
                context.Catalog!,
                context.Progress,
                context.Profile,
                _timeProvider.GetUtcNow()),
            ObjectiveSatisfaction(objectives),
            isPinned,
            task.Restartable,
            task.FailureConditions.Count > 0,
            task.FailureConditions
                .OrderBy(condition => condition.SourceOrdinal)
                .ThenBy(condition => condition.Id, StringComparer.Ordinal)
                .Select(condition => $"{condition.Description} ({condition.Kind})")
                .ToArray(),
            task.Requirements
                .OrderBy(requirement => requirement.SourceOrdinal)
                .ThenBy(requirement => requirement.RequiredTaskId, StringComparer.Ordinal)
                .Select(requirement => new QuestPrerequisiteReadModel(
                    requirement.RequiredTaskId,
                    requirement.RequiredStatuses,
                    context.Progress.Tasks.GetValueOrDefault(requirement.RequiredTaskId)?.State
                        ?? RecordedTaskState.Unknown))
                .ToArray(),
            objectives)
        {
            TraderName = NameOfTrader(task.TraderId, context),
            WikiUri = task.WikiUri,
        };
    }

    /// <summary>
    /// What a trader id is called, or null where the last sync stored no name for it.
    /// </summary>
    /// <remarks>
    /// Null rather than the id, so the consumer decides what to print. The Quests page falls
    /// back to the id, which is the honest answer: a trader the catalog does not know about is
    /// something to notice rather than something to hide behind a blank.
    /// </remarks>
    private static string? NameOfTrader(string? traderId, ReadContext context) =>
        traderId is { Length: > 0 } id && context.TraderNames.TryGetValue(id, out var name) ? name : null;

    private async Task<ReadContext> GetContextAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        QuestProgressCommandService.EnsureScope(profile, scope);
        var progress = await progressStore.GetAsync(scope, cancellationToken).ConfigureAwait(false);
        var catalog = await questCatalog.GetAsync(scope.GameMode, _language, cancellationToken).ConfigureAwait(false);
        var traders = traderCatalog is null
            ? null
            : await traderCatalog.GetNamesAsync(cancellationToken).ConfigureAwait(false);
        return traders is null
            ? new(profile, progress, catalog)
            : new(profile, progress, catalog) { TraderNames = traders };
    }

    private static RecordedObjectivesSatisfaction ObjectiveSatisfaction(
        IReadOnlyList<QuestObjectiveReadModel> objectives)
    {
        var required = objectives.Where(objective => objective.IsOptional == false).ToArray();
        if (objectives.Any(objective => objective.IsOptional is null) ||
            required.Any(objective => objective.IsUnsupported || objective.RecordedState == RecordedObjectiveState.Unknown))
        {
            return RecordedObjectivesSatisfaction.Indeterminate;
        }

        return required.All(objective => objective.RecordedState == RecordedObjectiveState.Completed)
            ? RecordedObjectivesSatisfaction.Satisfied
            : RecordedObjectivesSatisfaction.NotSatisfied;
    }

    private static bool? FoundInRaidRequirement(
        QuestObjectiveDefinition objective,
        IEnumerable<QuestObjectiveItemTarget> targets,
        string itemId)
    {
        if (objective.FoundInRaidRequired == true)
        {
            return true;
        }

        var matching = targets
            .Where(target => target.ItemId == itemId)
            .Select(target => target.FoundInRaidRequired)
            .ToArray();
        if (matching.Any(value => value == true))
        {
            return true;
        }

        return objective.FoundInRaidRequired == false && matching.All(value => value == false)
            ? false
            : null;
    }

    private static int? HoldingCount(QuestProgressSnapshot progress, string itemId, bool foundInRaid) =>
        progress.ItemHoldings
            .SingleOrDefault(holding => holding.ItemId == itemId && holding.FoundInRaid == foundInRaid)
            ?.Count;

    private static IReadOnlyList<OrphanedQuestProgress> BuildOrphans(
        QuestProgressSnapshot progress,
        QuestCatalogSnapshot? catalog)
    {
        var taskIds = catalog?.Tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var objectiveIds = catalog?.Tasks
            .SelectMany(task => task.Objectives)
            .Select(objective => objective.Id)
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var itemIds = catalog?.Tasks
            .SelectMany(task => task.Objectives)
            .SelectMany(objective => objective.ItemTargets)
            .Select(target => target.ItemId)
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var values = new List<OrphanedQuestProgress>();
        values.AddRange(progress.Tasks.Values
            .Where(value => !taskIds.Contains(value.TaskId))
            .Select(value => new OrphanedQuestProgress(
                QuestProgressEntityKind.Task,
                value.TaskId,
                value.State.ToString())));
        values.AddRange(progress.Objectives.Values
            .Where(value => !objectiveIds.Contains(value.ObjectiveId))
            .Select(value => new OrphanedQuestProgress(
                QuestProgressEntityKind.Objective,
                value.ObjectiveId,
                value.Count is null
                    ? value.State.ToString()
                    : $"{value.State}:{value.Count.Value.ToString(CultureInfo.InvariantCulture)}")));
        values.AddRange(progress.ItemHoldings
            .Where(value => !itemIds.Contains(value.ItemId))
            .Select(value => new OrphanedQuestProgress(
                QuestProgressEntityKind.ItemHolding,
                value.ItemId,
                $"{(value.FoundInRaid ? "FIR" : "non-FIR")}:{value.Count}")));
        values.AddRange(progress.Pins
            .Where(value => value.TargetKind == QuestPinTargetKind.Task
                ? !taskIds.Contains(value.TargetId)
                : !objectiveIds.Contains(value.TargetId))
            .Select(value => new OrphanedQuestProgress(
                QuestProgressEntityKind.Pin,
                value.TargetId,
                value.TargetKind.ToString())));
        return values
            .OrderBy(value => value.EntityKind)
            .ThenBy(value => value.ExternalId, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record ReadContext(
        PlayerProfile Profile,
        QuestProgressSnapshot Progress,
        QuestCatalogSnapshot? Catalog)
    {
        /// <summary>
        /// What the traders are called, read once for the whole board.
        /// </summary>
        /// <remarks>
        /// Per read rather than per task, because a board is several hundred tasks and they
        /// share about a dozen traders. Empty where nothing has synced yet, which prints ids
        /// exactly as it did before — the same answer, and not a worse one.
        /// </remarks>
        public IReadOnlyDictionary<string, string> TraderNames { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

}
