using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Keys;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Intelligence.Keys;

/// <summary>The one profile-aware key answer used by lookup, search, capture, stash and planning.</summary>
public interface IProfileAwareKeyIntelligenceSource
{
    Task<ProfileAwareKeyIntelligenceResult?> GetAsync(
        string itemId,
        KeyIntelligenceEntryPoint entryPoint,
        CancellationToken cancellationToken);
}

/// <summary>
/// Builds the evaluator's exact evidence request from the sources the running application owns.
/// Missing loot, room, condition, trader and route facts stay missing; item names are never parsed
/// to fill them.
/// </summary>
public sealed class ProfileAwareKeyIntelligenceSource(
    IItemFactCatalog facts,
    IPlayerProfileService profiles,
    ProfileNeedAggregationService needs,
    IQuestReadService quests,
    IProfileRuntimeContextService? profileContext = null,
    IGameVersionSource? gameVersion = null,
    TimeProvider? timeProvider = null) : IProfileAwareKeyIntelligenceSource
{
    private const string UnknownVersion = "unknown";
    private static readonly ProducerIdentity Producer = new(
        "Tarkov Companion key fact adapter",
        ProfileAwareKeyIntelligenceService.CurrentRulesetVersion);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ProfileAwareKeyIntelligenceResult?> GetAsync(
        string itemId,
        KeyIntelligenceEntryPoint entryPoint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (!Enum.IsDefined(entryPoint))
        {
            throw new ArgumentOutOfRangeException(nameof(entryPoint));
        }

        var normalizedItemId = itemId.Trim();
        var key = (await facts.GetKeyFactsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(candidate.ItemId, normalizedItemId, StringComparison.Ordinal));
        if (key is null)
        {
            return null;
        }

        var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var evaluatedUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var contextSnapshot = profileContext?.Current.ActiveProfile?.Context;
        var dataSnapshotId = contextSnapshot?.DataSnapshot.SnapshotId ?? "unversioned";
        var mapDataVersion = dataSnapshotId;
        var observedGameVersion = gameVersion is null
            ? null
            : await gameVersion.GetAsync(cancellationToken).ConfigureAwait(false);
        var scope = new InventoryProfileScope(profile.Id, profile.ProfileGeneration, profile.GameMode.ToString());
        var context = new KeyIntelligenceContext(
            scope,
            normalizedItemId,
            dataSnapshotId,
            string.IsNullOrWhiteSpace(observedGameVersion) ? UnknownVersion : observedGameVersion,
            mapDataVersion);

        var sourceProvenance = PublicProvenance(key.Provenance, evaluatedUtc);
        var profileProvenance = new EvidenceProvenance(
            EvidenceSourceClass.UserEntered,
            $"profile:{profile.Id:N}:{profile.ProfileGeneration}",
            Earlier(profile.UpdatedUtc, evaluatedUtc),
            EvidenceConfidence.Certain,
            Producer,
            reference: "profile/progress");

        var questScope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var (requirementsStatus, requirementsProvenance, requirements, firHeld) =
            await RequirementsAsync(context, questScope, profile, sourceProvenance, profileProvenance, evaluatedUtc, cancellationToken)
                .ConfigureAwait(false);

        var inventoryHasCoverage = profile.OwnedItemCounts.Count > 0;
        var totalOwned = inventoryHasCoverage ? profile.OwnedItemCounts.GetValueOrDefault(normalizedItemId) : (int?)null;
        var inventory = new KeyInventoryFacts(
            scope,
            normalizedItemId,
            Value("inventory.total", totalOwned, profileProvenance, evaluatedUtc),
            Value("inventory.found-in-raid", firHeld, profileProvenance, evaluatedUtc),
            Value<int>("inventory.duplicates", totalOwned is { } total ? Math.Max(0, total - 1) : null, profileProvenance, evaluatedUtc),
            Value("inventory.maximum-uses", key.MaximumUses, sourceProvenance, evaluatedUtc),
            Unknown<int>("inventory.remaining-uses", evaluatedUtc));

        var associations = Associations(key, sourceProvenance, evaluatedUtc);
        var hasUtilityFact = associations.Count > 0 || key.AcquisitionCostRoubles is not null;
        var utilityStatus = new ResultStatus(
            hasUtilityFact ? ResultCompleteness.Partial : ResultCompleteness.Unknown,
            hasUtilityFact ? Freshness(sourceProvenance, evaluatedUtc) : FreshnessState.Unknown,
            hasUtilityFact ? "key.utility.partial" : "key.utility.unknown",
            "Only source-stated access and cost facts are populated; absent room, loot and route facts remain unknown.");
        var utility = new KeyUtilityFacts(
            utilityStatus,
            sourceProvenance,
            associations,
            Unknown<KeyObtainability>("utility.trader-obtainability", evaluatedUtc),
            Unknown<KeyObtainability>("utility.flea-obtainability", evaluatedUtc),
            Value("utility.acquisition-cost", key.AcquisitionCostRoubles, sourceProvenance, evaluatedUtc),
            Value("utility.expected-loot", key.ExpectedLootRoubles, sourceProvenance, evaluatedUtc),
            Value("utility.unique-access", key.GrantsUniqueAccess, sourceProvenance, evaluatedUtc),
            Value<double>("utility.route-utility", key.Utility is { } utilityScore ? utilityScore / 100d : null, sourceProvenance, evaluatedUtc),
            Value("utility.route-risk", key.RouteRisk, sourceProvenance, evaluatedUtc));

        var request = new ProfileAwareKeyIntelligenceRequest(
            entryPoint,
            context,
            evaluatedUtc,
            inventory,
            requirementsStatus,
            requirementsProvenance,
            requirements,
            utility);
        return new ProfileAwareKeyIntelligenceService().Evaluate(request, cancellationToken);
    }

    private async Task<(ResultStatus Status, EvidenceProvenance Provenance, IReadOnlyList<KeyRequirementFact> Requirements, int? FirHeld)>
        RequirementsAsync(
            KeyIntelligenceContext context,
            QuestProfileScope questScope,
            TarkovCompanion.Core.Domain.Profile.PlayerProfile profile,
            EvidenceProvenance catalog,
            EvidenceProvenance progress,
            DateTimeOffset evaluatedUtc,
            CancellationToken cancellationToken)
    {
        try
        {
            var board = await quests.GetQuestBoardAsync(questScope, cancellationToken).ConfigureAwait(false);
            var holdings = await quests.GetItemNeedsAsync(questScope, context.ItemId, cancellationToken).ConfigureAwait(false);
            if (board.UnavailableReason is not null)
            {
                return (Partial("key.requirements.unavailable", board.UnavailableReason), progress, [], holdings.FoundInRaidHeldCount);
            }

            var tracked = board.Tasks
                .Where(task => task.RecordedState == RecordedTaskState.Active || task.IsPinned)
                .Select(task => task.TaskId)
                .ToHashSet(StringComparer.Ordinal);
            var evidence = Derived("key-requirements", evaluatedUtc, catalog, progress);
            var outstanding = needs.GetOutstandingRequirements(profile, context.ItemId).Quests;
            var rows = outstanding
                .Take(KeyIntelligenceBounds.MaximumRequirements)
                .Select(row => new KeyRequirementFact(
                    context,
                    $"{row.Requirement.TaskId}:{row.Requirement.ObjectiveId}",
                    tracked.Contains(row.Requirement.TaskId) ? KeyRequirementTiming.Current : KeyRequirementTiming.Future,
                    row.Remaining,
                    row.Requirement.FoundInRaidRequired,
                    Complete(Freshness(evidence, evaluatedUtc), "key.requirement.sourced"),
                    evidence))
                .ToArray();
            var complete = rows.Length == outstanding.Count;
            return (
                complete
                    ? Complete(Freshness(evidence, evaluatedUtc), "key.requirements.complete")
                    : Partial("key.requirements.bounded", "Additional requirements exceed the bounded response."),
                evidence,
                rows,
                holdings.FoundInRaidHeldCount);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (
                Partial("key.requirements.failed", exception.Message),
                progress,
                [],
                null);
        }
    }

    private static IReadOnlyList<KeyAccessAssociation> Associations(
        KeyFacts key,
        EvidenceProvenance provenance,
        DateTimeOffset evaluatedUtc)
    {
        var facts = key.LockFacts.Count > 0
            ? key.LockFacts
            : key.Locks.Select(lockId => new KeyLockFact(lockId, key.MapId)).ToArray();
        return facts
            .Take(KeyIntelligenceBounds.MaximumAssociations)
            .Select((fact, index) => new KeyAccessAssociation(
                $"{key.ItemId}:lock:{index}",
                TextValue($"access.{index}.map", fact.MapId, provenance, evaluatedUtc),
                TextValue($"access.{index}.lock", fact.LockId, provenance, evaluatedUtc),
                TextValue($"access.{index}.room", fact.RoomId, provenance, evaluatedUtc),
                provenance))
            .ToArray();
    }

    private static EvidenceProvenance PublicProvenance(DataProvenance source, DateTimeOffset evaluatedUtc)
    {
        var observed = Earlier(source.ObservedUtc == default ? evaluatedUtc : source.ObservedUtc, evaluatedUtc);
        var through = source.SourceUpdatedUtc is { } updated ? Earlier(updated, observed) : observed;
        var confidence = source.Confidence is { } scored
            ? new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, scored.Value)
            : EvidenceConfidence.Unscored;
        return new(
            EvidenceSourceClass.PublicStructuredData,
            string.IsNullOrWhiteSpace(source.Source) ? "key-catalog" : source.Source,
            observed,
            confidence,
            Producer,
            dataThroughUtc: through,
            reference: source.Reference);
    }

    private static EvidenceProvenance Derived(
        string identifier,
        DateTimeOffset evaluatedUtc,
        params EvidenceProvenance[] inputs) =>
        new(
            EvidenceSourceClass.DerivedCalculation,
            identifier,
            evaluatedUtc,
            inputs.All(input => input.Confidence.Score is not null)
                ? new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, inputs.Min(input => input.Confidence.Score!.Value))
                : EvidenceConfidence.Unscored,
            Producer,
            generatedUtc: evaluatedUtc,
            inputs: inputs);

    private static ResultStatus Complete(FreshnessState freshness, string code) =>
        new(ResultCompleteness.Complete, freshness, code);

    private static ResultStatus Partial(string code, string detail) =>
        new(ResultCompleteness.Partial, FreshnessState.Unknown, code, detail);

    private static FreshnessState Freshness(EvidenceProvenance provenance, DateTimeOffset nowUtc) =>
        nowUtc - provenance.EvidenceThroughUtc > TimeSpan.FromDays(30) ? FreshnessState.Stale : FreshnessState.Current;

    private static DateTimeOffset Earlier(DateTimeOffset value, DateTimeOffset ceiling) =>
        (value > ceiling ? ceiling : value).ToUniversalTime();

    private static EvidencedValue<T?> Value<T>(
        string fieldId,
        T? value,
        EvidenceProvenance provenance,
        DateTimeOffset evaluatedUtc)
        where T : struct =>
        value is { } known
            ? new(fieldId, known, Complete(Freshness(provenance, evaluatedUtc), "key.fact.sourced"), provenance)
            : new(fieldId, null, new(ResultCompleteness.Unknown, FreshnessState.Unknown, "key.fact.unknown"), provenance);

    private static EvidencedValue<T?> Unknown<T>(string fieldId, DateTimeOffset observedUtc)
        where T : struct =>
        new(
            fieldId,
            null,
            new(ResultCompleteness.Unknown, FreshnessState.Unknown, "key.fact.unknown"),
            new EvidenceProvenance(
                EvidenceSourceClass.Unknown,
                $"missing:{fieldId}",
                observedUtc,
                EvidenceConfidence.Unscored,
                Producer));

    private static EvidencedValue<string> TextValue(
        string fieldId,
        string? value,
        EvidenceProvenance provenance,
        DateTimeOffset evaluatedUtc) =>
        string.IsNullOrWhiteSpace(value)
            ? new(
                fieldId,
                null,
                new(ResultCompleteness.Unknown, FreshnessState.Unknown, "key.fact.unknown"),
                new EvidenceProvenance(EvidenceSourceClass.Unknown, $"missing:{fieldId}", evaluatedUtc, EvidenceConfidence.Unscored, Producer))
            : new(fieldId, value, Complete(Freshness(provenance, evaluatedUtc), "key.fact.sourced"), provenance);
}
