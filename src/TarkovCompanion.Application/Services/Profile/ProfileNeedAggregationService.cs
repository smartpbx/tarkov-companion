using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.Application.Services.Profile;

public sealed record QuestItemRequirement(
    string TaskId,
    string ObjectiveId,
    string ItemId,
    int Required,
    bool FoundInRaidRequired);

public sealed record HideoutItemRequirement(
    string StationId,
    int TargetLevel,
    string ItemId,
    int Required);

public sealed record AggregatedItemNeed(
    ItemNeedSummary Summary,
    bool IsWishlisted);

public sealed class ProfileNeedAggregationService
{
    private volatile IReadOnlyList<QuestItemRequirement> _questRequirements;
    private volatile IReadOnlyList<HideoutItemRequirement> _hideoutRequirements;

    public ProfileNeedAggregationService(
        IEnumerable<QuestItemRequirement> questRequirements,
        IEnumerable<HideoutItemRequirement> hideoutRequirements)
    {
        ArgumentNullException.ThrowIfNull(questRequirements);
        ArgumentNullException.ThrowIfNull(hideoutRequirements);

        _questRequirements = Validate(questRequirements, hideoutRequirements, out var hideout);
        _hideoutRequirements = hideout;
    }

    /// <summary>
    /// Replaces the requirements this service answers from.
    /// </summary>
    /// <remarks>
    /// The requirements come out of SQLite, which on a clean install is still empty when this
    /// service is first built and is filled seconds later by the first sync. Constructing the
    /// service from a blocking read instead was worse in both directions: it captured empty
    /// data on a fresh machine, and the block itself was enough to stall a scan behind it.
    /// One long-lived instance whose data is swapped in when it arrives avoids both.
    /// </remarks>
    public void Update(
        IEnumerable<QuestItemRequirement> questRequirements,
        IEnumerable<HideoutItemRequirement> hideoutRequirements)
    {
        ArgumentNullException.ThrowIfNull(questRequirements);
        ArgumentNullException.ThrowIfNull(hideoutRequirements);

        var quest = Validate(questRequirements, hideoutRequirements, out var hideout);
        _questRequirements = quest;
        _hideoutRequirements = hideout;
    }

    private static IReadOnlyList<QuestItemRequirement> Validate(
        IEnumerable<QuestItemRequirement> questRequirements,
        IEnumerable<HideoutItemRequirement> hideoutRequirements,
        out IReadOnlyList<HideoutItemRequirement> hideout)
    {
        var quest = questRequirements.ToArray();
        hideout = hideoutRequirements.ToArray();
        if (Array.Exists(quest, x => x.Required < 0) ||
            hideout.Any(x => x.Required < 0 || x.TargetLevel <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(questRequirements),
                "Requirement quantities must be non-negative and hideout target levels must be positive.");
        }

        return quest;
    }

    public AggregatedItemNeed GetItemNeed(PlayerProfile profile, string itemId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        var questCount = 0;
        var foundInRaidQuestCount = 0;
        foreach (var requirement in _questRequirements.Where(x =>
                     StringComparer.Ordinal.Equals(x.ItemId, itemId) &&
                     !profile.CompletedTaskIds.Contains(x.TaskId)))
        {
            var progress = profile.ObjectiveProgress.GetValueOrDefault(requirement.ObjectiveId);
            var remaining = Math.Max(0, requirement.Required - progress);
            questCount = checked(questCount + remaining);
            if (requirement.FoundInRaidRequired)
            {
                foundInRaidQuestCount = checked(foundInRaidQuestCount + remaining);
            }
        }

        var hideoutRequired = _hideoutRequirements
            .Where(x => StringComparer.Ordinal.Equals(x.ItemId, itemId))
            .Where(x => profile.HideoutStationLevels.GetValueOrDefault(x.StationId) < x.TargetLevel)
            .Sum(x => x.Required);
        var owned = profile.OwnedItemCounts.GetValueOrDefault(itemId);
        var hideoutRemaining = Math.Max(0, hideoutRequired - owned);

        return new(
            new(questCount, foundInRaidQuestCount, hideoutRemaining),
            profile.WishlistItemIds.Contains(itemId));
    }
}

public sealed class ProfileQuestProgressService(
    IPlayerProfileService profileService,
    ProfileNeedAggregationService aggregationService) : IQuestProgressService
{
    public async Task<ItemNeedSummary> GetItemNeedsAsync(string itemId, CancellationToken cancellationToken)
    {
        var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        return aggregationService.GetItemNeed(profile, itemId).Summary;
    }
}

public sealed class ProfileHideoutProgressService(
    IPlayerProfileService profileService,
    ProfileNeedAggregationService aggregationService) : IHideoutProgressService
{
    public async Task<int> GetRemainingItemCountAsync(string itemId, CancellationToken cancellationToken)
    {
        var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        return aggregationService.GetItemNeed(profile, itemId).Summary.HideoutCount;
    }
}

public sealed class RecommendationContextService(
    IPlayerProfileService profileService,
    ProfileNeedAggregationService aggregationService,
    IEventTrackerService eventTrackerService)
{
    public async Task<RecommendationContext> BuildAsync(
        string itemId,
        bool isFoundInRaid,
        string? eventId,
        string? specializedAdvice,
        Confidence confidence,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var need = aggregationService.GetItemNeed(profile, itemId);
        var eventState = string.IsNullOrWhiteSpace(eventId)
            ? EventItemState.Unknown
            : await eventTrackerService.GetItemStateAsync(eventId, itemId, cancellationToken).ConfigureAwait(false);

        return new(
            isFoundInRaid,
            need.Summary.QuestCount,
            need.Summary.FoundInRaidQuestCount,
            need.Summary.HideoutCount,
            need.IsWishlisted,
            eventState,
            profile.ItemOverrides.GetValueOrDefault(itemId),
            specializedAdvice,
            confidence);
    }
}
