using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>
/// One item search hit, worded for the paired tablet the way the desktop's own Intel list words it.
/// </summary>
/// <remarks>
/// [#379, #407] The tablet's answers carried a name and two prices while the desktop's list also
/// says what kind of item it is, how much room it takes, what it is worth per slot and which
/// trader pays that, and links the wiki. A player holding the tablet had to walk to the desk for
/// the rest. The wiki link is only ever one <see cref="WikiLinkPolicy"/> allows; the tablet opens
/// it in a new tab, which is the one thing a browser does better than the desktop here.
/// </remarks>
public static class TabletSearchResultBuilder
{
    public static TabletSearchResult From(ItemSearchHit hit, ItemPriceSnapshot? price, bool isAllergic)
    {
        ArgumentNullException.ThrowIfNull(hit);
        var item = hit.Item;
        var best = price?.BestEconomicValue ?? 0;
        return new TabletSearchResult(
            item.Id,
            item.Name,
            item.ShortName,
            price?.FleaPriceRoubles,
            price?.BestTrader?.ValueRoubles,
            isAllergic,
            item.Category.ToString(),
            $"{item.Dimensions.Width}×{item.Dimensions.Height}",
            best > 0 && item.Dimensions.Slots > 0 ? item.ValuePerSlot(price!) : null,
            price?.BestTrader?.TraderName,
            WikiLinkPolicy.IsAllowed(item.WikiUri) ? item.WikiUri : null);
    }
}
