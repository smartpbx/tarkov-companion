using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Ask;
using TarkovCompanion.Application.Services.Ask;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// [#712 2-5] The palette's Ask mode: a question typed after Ctrl+K is answered on a card above the
/// commands, and the card's links land on the page that holds the rest.
/// </summary>
/// <remarks>
/// A separate partial so the palette's own files (#910) gain one line each: the query setter hands
/// the words on, and the view shows <see cref="Ask"/>'s card. Ctrl+K works only inside the companion;
/// there is no global hotkey (rejected in #712: it would listen to keys while the game has focus).
/// </remarks>
public sealed partial class V2ShellViewModel
{
    /// <summary>The Ask card's view model; null in compositions without the Ask service.</summary>
    public AskViewModel? Ask { get; private set; }

    public bool HasAsk => Ask is not null;

    /// <summary>Wires the Ask box into the palette; called from composition and by tests.</summary>
    public void AttachAsk(AskViewModel ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        Ask = ask;
        ask.AttachNavigation(OpenAskLink);
        ask.QueryReplaced += (_, text) => PaletteQuery = text;
        ask.SetQuery(PaletteQuery);
        OnPropertyChanged(nameof(Ask));
        OnPropertyChanged(nameof(HasAsk));
    }

    /// <summary>Opens where an answer's link points, closing the palette first.</summary>
    internal void OpenAskLink(AskLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.Kind == AskLinkKind.IntelItem && link.Target is { Length: > 0 } itemId)
        {
            OpenSuggestedItem(itemId, "v2-ask-open-intel");
            return;
        }

        var route = link.Kind switch
        {
            AskLinkKind.PlanQuest => V2Routes.Plan,
            AskLinkKind.PlanHideout => V2Routes.Hideout,
            AskLinkKind.Raid => V2Routes.Raid,
            AskLinkKind.IntelAmmo => V2Routes.Ammo,
            AskLinkKind.IntelCrafts => V2Routes.Crafts,
            _ => (V2RouteId?)null,
        };
        if (route is null)
        {
            return;
        }

        var invoker = _dialogInvoker ?? V2ShellFocusTargets.Palette;
        if (HasOpenDialog)
        {
            CloseDialog(restoreInvoker: false);
        }

        var opened = Router.Navigate(route.Value, invoker);
        Act(opened);
        if (opened.Succeeded && link.Kind == AskLinkKind.PlanQuest && _plan is not null && link.Target is { Length: > 0 } questName)
        {
            // Plan's own search, so the quest is the row on screen and its detail is one tap away.
            _plan.SearchText = questName;
        }
    }
}
