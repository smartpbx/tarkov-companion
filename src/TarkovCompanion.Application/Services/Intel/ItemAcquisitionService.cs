using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Intel;

public enum ItemAcquisitionKind
{
    Cash,
    Barter,
}

/// <summary>One cash purchase retained in an item's json.tarkov.dev payload.</summary>
public sealed record ItemCashOffer(
    string ItemId,
    string TraderId,
    long PriceRoubles,
    int? MinimumTraderLevel,
    string? TaskUnlockId);

public interface IItemCashOfferCatalog
{
    Task<IReadOnlyList<ItemCashOffer>> GetAllAsync(CancellationToken cancellationToken);
}

/// <summary>One catalog-backed way to obtain an item from a trader.</summary>
public sealed record ItemAcquisitionOffer(
    string ItemId,
    ItemAcquisitionKind Kind,
    string TraderId,
    string TraderName,
    int? MinimumTraderLevel,
    string? TaskUnlockId,
    string? TaskUnlockName,
    long? PriceRoubles,
    IReadOnlyList<IntelTradeIngredient> BarterCost);

/// <summary>The active profile's answer for one trader source.</summary>
public sealed record ItemAcquisitionAvailability(bool IsObtainable, string RequirementLabel);

/// <summary>
/// Applies only requirements the catalog states. An absent profile entry means loyalty zero or
/// an unfinished task; an absent offer requirement is not invented.
/// </summary>
public static class ItemObtainabilityRule
{
    public static ItemAcquisitionAvailability Evaluate(ItemAcquisitionOffer offer, PlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(profile);

        var requirements = new List<string>(2);
        if (offer.MinimumTraderLevel is > 0 and var requiredLevel &&
            profile.TraderLevels.GetValueOrDefault(offer.TraderId) < requiredLevel)
        {
            requirements.Add($"LL{requiredLevel} {offer.TraderName}");
        }

        if (offer.TaskUnlockId is { Length: > 0 } taskId && !profile.CompletedTaskIds.Contains(taskId))
        {
            requirements.Add($"after quest {offer.TaskUnlockName ?? taskId}");
        }

        return requirements.Count == 0
            ? new(true, "Available now")
            : new(false, string.Join(" · ", requirements));
    }
}

public sealed record ProfiledItemAcquisition(
    ItemAcquisitionOffer Offer,
    ItemAcquisitionAvailability Availability);

public interface IItemAcquisitionService
{
    Task<IReadOnlyList<ProfiledItemAcquisition>> GetAsync(
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// Combines cash offers and barters, then applies the same profile rule for every UI surface.
/// </summary>
public sealed class ItemAcquisitionService(
    IItemCashOfferCatalog cashOffers,
    IBarterCatalog barters,
    ITraderCatalog traders,
    IItemRepository items,
    IPlayerProfileService profiles,
    IQuestReadService? quests = null) : IItemAcquisitionService
{
    public async Task<IReadOnlyList<ProfiledItemAcquisition>> GetAsync(
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0)
        {
            return [];
        }

        var wanted = itemIds.ToHashSet(StringComparer.Ordinal);
        var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var traderNames = await traders.GetNamesAsync(cancellationToken).ConfigureAwait(false);
        var taskNames = await TaskNamesAsync(profile, cancellationToken).ConfigureAwait(false);
        var built = new List<ItemAcquisitionOffer>();

        foreach (var cash in await cashOffers.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!wanted.Contains(cash.ItemId))
            {
                continue;
            }

            built.Add(new(
                cash.ItemId,
                ItemAcquisitionKind.Cash,
                cash.TraderId,
                traderNames.GetValueOrDefault(cash.TraderId, cash.TraderId),
                cash.MinimumTraderLevel,
                cash.TaskUnlockId,
                NameOfTask(cash.TaskUnlockId, taskNames),
                cash.PriceRoubles,
                []));
        }

        foreach (var barter in await barters.GetAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!wanted.Contains(barter.Gives.ItemId) || barter.TraderId is not { Length: > 0 } traderId)
            {
                continue;
            }

            var cost = new List<IntelTradeIngredient>(barter.Wants.Count);
            foreach (var input in barter.Wants)
            {
                var item = await items.GetAsync(input.ItemId, cancellationToken).ConfigureAwait(false);
                cost.Add(new(input.ItemId, item?.Name ?? input.ItemId, Math.Max(1, input.Count)));
            }

            built.Add(new(
                barter.Gives.ItemId,
                ItemAcquisitionKind.Barter,
                traderId,
                traderNames.GetValueOrDefault(traderId, traderId),
                barter.MinimumTraderLevel,
                barter.TaskUnlock,
                NameOfTask(barter.TaskUnlock, taskNames),
                null,
                cost));
        }

        return built
            .Select(offer => new ProfiledItemAcquisition(offer, ItemObtainabilityRule.Evaluate(offer, profile)))
            .OrderByDescending(row => row.Availability.IsObtainable)
            .ThenBy(row => row.Offer.PriceRoubles ?? long.MaxValue)
            .ThenBy(row => row.Offer.TraderName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyDictionary<string, string>> TaskNamesAsync(
        PlayerProfile profile,
        CancellationToken cancellationToken)
    {
        if (quests is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            var board = await quests.GetQuestBoardAsync(scope, cancellationToken).ConfigureAwait(false);
            return board.Tasks.ToDictionary(task => task.TaskId, task => task.Name, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string? NameOfTask(string? taskId, IReadOnlyDictionary<string, string> names) =>
        taskId is { Length: > 0 } ? names.GetValueOrDefault(taskId, taskId) : null;
}
