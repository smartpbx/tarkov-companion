using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Intel;

/// <summary>One real item row for the Intel landing page: never a placeholder.</summary>
public sealed record IntelLandingRow(
    string ItemId,
    string Name,
    string ShortName,
    string Category,
    long? ValueRoubles,
    string? SaleChannelLabel,
    int Count = 0);

public sealed record IntelLandingSnapshot(
    IReadOnlyList<IntelLandingRow> NeededNow,
    IReadOnlyList<IntelLandingRow> Pinned,
    IReadOnlyList<IntelLandingRow> Recent,
    IReadOnlyList<IntelLandingRow> HighestValue);

/// <summary>
/// What the Intel workspace shows the moment it opens, before anything is searched.
/// </summary>
/// <remarks>
/// Four real sections, each built from state the application already keeps: what the active
/// profile's tracked quests and next hideout levels still need, the items the player pinned, the
/// items they opened most recently, and the catalog's own highest-worth items. Every row is
/// resolved through <see cref="IItemIntelService"/> — the same facts the detail pane shows — so a
/// row here and the page it opens never disagree.
/// </remarks>
public interface IIntelLandingService
{
    Task<IntelLandingSnapshot> GetAsync(
        IReadOnlyList<string> pinnedItemIds,
        IReadOnlyList<string> recentItemIds,
        CancellationToken cancellationToken);
}

public sealed class IntelLandingService(
    IPlayerProfileService profileService,
    ProfileNeedAggregationService aggregation,
    IItemIntelService intel,
    IHighValueItemCatalog highValue,
    IQuestReadService? quests = null) : IIntelLandingService
{
    private const int NeededNowLimit = 6;
    private const int PinnedLimit = 6;
    private const int RecentLimit = 6;
    private const int HighestValueLimit = 8;

    public async Task<IntelLandingSnapshot> GetAsync(
        IReadOnlyList<string> pinnedItemIds,
        IReadOnlyList<string> recentItemIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pinnedItemIds);
        ArgumentNullException.ThrowIfNull(recentItemIds);

        var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var tracked = await TrackedTaskIdsAsync(profile, cancellationToken).ConfigureAwait(false);
        var needs = aggregation.GetActiveProfileNeeds(profile, tracked).Take(NeededNowLimit);

        var neededNow = await BuildRowsAsync(
            needs.Select(row => (row.ItemId, row.Total)),
            cancellationToken).ConfigureAwait(false);
        var pinned = await BuildRowsAsync(
            Distinct(pinnedItemIds, PinnedLimit).Select(id => (id, 0)),
            cancellationToken).ConfigureAwait(false);
        var recent = await BuildRowsAsync(
            Distinct(recentItemIds, RecentLimit).Select(id => (id, 0)),
            cancellationToken).ConfigureAwait(false);

        var top = await highValue.GetTopAsync(HighestValueLimit, cancellationToken).ConfigureAwait(false);
        var highestValueRows = await BuildRowsAsync(
            top.Select(item => (item.ItemId, 0)),
            cancellationToken).ConfigureAwait(false);

        return new(neededNow, pinned, recent, highestValueRows);
    }

    private static IEnumerable<string> Distinct(IReadOnlyList<string> ids, int limit) =>
        ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Take(limit);

    private async Task<IReadOnlyList<IntelLandingRow>> BuildRowsAsync(
        IEnumerable<(string ItemId, int Count)> items,
        CancellationToken cancellationToken)
    {
        var rows = new List<IntelLandingRow>();
        foreach (var (itemId, count) in items)
        {
            V2ItemIntelResult result;
            try
            {
                result = await intel.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                continue;
            }

            // Never a placeholder row: an id the catalog no longer resolves is dropped rather
            // than shown as a name-less entry.
            if (result.Kind == V2IntelKind.Unknown)
            {
                continue;
            }

            rows.Add(new(
                result.ItemId,
                result.Name,
                result.ShortName,
                result.Category.ToString(),
                result.Value?.ValueRoubles,
                result.Value?.SaleChannelLabel,
                count));
        }

        return rows;
    }

    /// <summary>The quests the player is actually on: active, or pinned whatever their state.</summary>
    /// <remarks>Mirrors <c>ProfileQuestProgressService.TrackedAsync</c>: no board reachable reports nothing tracked.</remarks>
    private async Task<IReadOnlySet<string>> TrackedTaskIdsAsync(PlayerProfile profile, CancellationToken cancellationToken)
    {
        if (quests is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            var board = await quests.GetQuestBoardAsync(scope, cancellationToken).ConfigureAwait(false);
            return board.Tasks
                .Where(task => task.IsPinned || task.RecordedState == RecordedTaskState.Active)
                .Select(task => task.TaskId)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
