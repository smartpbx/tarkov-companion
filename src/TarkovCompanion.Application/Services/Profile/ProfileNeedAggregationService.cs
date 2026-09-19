using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
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

    /// <param name="trackedTaskIds">
    /// The quests the player is actually on — active or pinned. Null where nobody could say,
    /// and then nothing is reported as tracked rather than everything being reported as such,
    /// which is the direction that cannot mislead.
    /// </param>
    public AggregatedItemNeed GetItemNeed(
        PlayerProfile profile,
        string itemId,
        IReadOnlySet<string>? trackedTaskIds = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        var outstandingItems = 0;
        var outstandingFoundInRaidItems = 0;
        // Distinct quests, not requirements. A quest with two objectives wanting the same item
        // is one quest that wants it, and the Keys page says so in words — "two quests need it"
        // has to mean two quests.
        var questsNeedingIt = new HashSet<string>(StringComparer.Ordinal);
        var trackedQuestsNeedingIt = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in _questRequirements.Where(x =>
                     StringComparer.Ordinal.Equals(x.ItemId, itemId) &&
                     !profile.CompletedTaskIds.Contains(x.TaskId)))
        {
            var progress = profile.ObjectiveProgress.GetValueOrDefault(requirement.ObjectiveId);
            var remaining = Math.Max(0, requirement.Required - progress);
            outstandingItems = checked(outstandingItems + remaining);
            if (requirement.FoundInRaidRequired)
            {
                outstandingFoundInRaidItems = checked(outstandingFoundInRaidItems + remaining);
            }

            if (remaining <= 0)
            {
                continue;
            }

            questsNeedingIt.Add(requirement.TaskId);
            if (trackedTaskIds?.Contains(requirement.TaskId) == true)
            {
                trackedQuestsNeedingIt.Add(requirement.TaskId);
            }
        }

        var hideoutRequired = _hideoutRequirements
            .Where(x => StringComparer.Ordinal.Equals(x.ItemId, itemId))
            .Where(x => profile.HideoutStationLevels.GetValueOrDefault(x.StationId) < x.TargetLevel)
            .Sum(x => x.Required);
        var owned = profile.OwnedItemCounts.GetValueOrDefault(itemId);
        var hideoutRemaining = Math.Max(0, hideoutRequired - owned);

        return new(
            new(outstandingItems, outstandingFoundInRaidItems, hideoutRemaining)
            {
                QuestsNeedingIt = questsNeedingIt.Count,
                TrackedQuestsNeedingIt = trackedQuestsNeedingIt.Count,
            },
            profile.WishlistItemIds.Contains(itemId));
    }

    /// <summary>
    /// The same outstanding needs as <see cref="GetItemNeed"/>, one row each instead of a sum.
    /// </summary>
    /// <remarks>
    /// The sums are enough for a page that says "two quests need it". A take-or-leave call has
    /// to say which quest and how near it is, and rank a found-in-raid hand-in over a hideout
    /// level three builds away, so it needs the rows the sums were added up from.
    ///
    /// What the profile says is already owned is spent on the nearest hideout level first, the
    /// same subtraction the sum makes, so the two never disagree about whether anything is left.
    /// </remarks>
    public OutstandingItemRequirements GetOutstandingRequirements(PlayerProfile profile, string itemId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        var quests = _questRequirements
            .Where(x => StringComparer.Ordinal.Equals(x.ItemId, itemId) && !profile.CompletedTaskIds.Contains(x.TaskId))
            .Select(x => new OutstandingQuestRequirement(
                x,
                Math.Max(0, x.Required - profile.ObjectiveProgress.GetValueOrDefault(x.ObjectiveId))))
            .Where(x => x.Remaining > 0)
            .ToArray();

        var owned = profile.OwnedItemCounts.GetValueOrDefault(itemId);
        var hideout = new List<OutstandingHideoutRequirement>();
        foreach (var requirement in _hideoutRequirements
                     .Where(x => StringComparer.Ordinal.Equals(x.ItemId, itemId))
                     .Select(x => (Requirement: x, Current: profile.HideoutStationLevels.GetValueOrDefault(x.StationId)))
                     .Where(x => x.Current < x.Requirement.TargetLevel && x.Requirement.Required > 0)
                     .OrderBy(x => x.Requirement.TargetLevel - x.Current)
                     .ThenBy(x => x.Requirement.StationId, StringComparer.Ordinal)
                     .ThenBy(x => x.Requirement.TargetLevel))
        {
            var spent = Math.Min(owned, requirement.Requirement.Required);
            owned -= spent;
            if (requirement.Requirement.Required - spent is > 0 and var remaining)
            {
                hideout.Add(new(requirement.Requirement, requirement.Current, remaining));
            }
        }

        return new(quests, hideout);
    }
}

/// <summary>One quest objective that still wants an item, and how many of it.</summary>
public sealed record OutstandingQuestRequirement(QuestItemRequirement Requirement, int Remaining);

/// <summary>One hideout level not yet built that wants an item, after what is already owned.</summary>
public sealed record OutstandingHideoutRequirement(HideoutItemRequirement Requirement, int CurrentLevel, int Remaining);

public sealed record OutstandingItemRequirements(
    IReadOnlyList<OutstandingQuestRequirement> Quests,
    IReadOnlyList<OutstandingHideoutRequirement> Hideout);

public sealed class ProfileQuestProgressService(
    IPlayerProfileService profileService,
    ProfileNeedAggregationService aggregationService,
    // Which quests the player is on, which the profile does not carry: the board holds recorded
    // state and pins, and the profile holds only what has been completed. Optional so the
    // compositions that build this by hand keep working; without it nothing is reported as
    // tracked, which is the direction that cannot mislead.
    IQuestReadService? quests = null,
    TimeProvider? timeProvider = null) : IQuestProgressService
{
    /// <summary>
    /// How long the tracked set is reused for.
    /// </summary>
    /// <remarks>
    /// The Keys page asks this once per key and there are two hundred and fifty-seven of them,
    /// so without a cache one page load is two hundred and fifty-seven reads of the whole quest
    /// board. A minute is long enough to make that one read and short enough that pinning a
    /// quest shows up while somebody is still looking at the page.
    /// </remarks>
    private static readonly TimeSpan RereadAfter = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlySet<string>? _tracked;
    private DateTimeOffset _readUtc = DateTimeOffset.MinValue;

    public async Task<ItemNeedSummary> GetItemNeedsAsync(string itemId, CancellationToken cancellationToken)
    {
        var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var tracked = await TrackedAsync(profile, cancellationToken).ConfigureAwait(false);
        return aggregationService.GetItemNeed(profile, itemId, tracked).Summary;
    }

    /// <summary>
    /// The quests the player is on: active, or pinned whatever their state.
    /// </summary>
    /// <remarks>
    /// The same rule the group exchange uses to decide what is worth telling a squadmate, and
    /// for the same reason — a pin is the player saying which one they are actually doing.
    ///
    /// A board that cannot be read reports nothing tracked rather than failing the caller. The
    /// verdict then falls back to "a quest ahead of you needs it", which is true and weaker,
    /// instead of a page that cannot say anything at all.
    /// </remarks>
    private async Task<IReadOnlySet<string>?> TrackedAsync(
        PlayerProfile profile,
        CancellationToken cancellationToken)
    {
        if (quests is null)
        {
            return null;
        }

        if (_timeProvider.GetUtcNow() - _readUtc < RereadAfter)
        {
            return _tracked;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_timeProvider.GetUtcNow() - _readUtc < RereadAfter)
            {
                return _tracked;
            }

            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            var board = await quests.GetQuestBoardAsync(scope, cancellationToken).ConfigureAwait(false);
            _tracked = board.Tasks
                .Where(task => task.IsPinned || task.RecordedState == RecordedTaskState.Active)
                .Select(task => task.TaskId)
                .ToHashSet(StringComparer.Ordinal);
            _readUtc = _timeProvider.GetUtcNow();
            return _tracked;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _readUtc = _timeProvider.GetUtcNow();
            return _tracked;
        }
        finally
        {
            _gate.Release();
        }
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
            need.Summary.OutstandingItems,
            need.Summary.OutstandingFoundInRaidItems,
            need.Summary.HideoutCount,
            need.IsWishlisted,
            eventState,
            profile.ItemOverrides.GetValueOrDefault(itemId),
            specializedAdvice,
            confidence);
    }
}
