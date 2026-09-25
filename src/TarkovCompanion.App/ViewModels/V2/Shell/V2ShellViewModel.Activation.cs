using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

public sealed partial class V2ShellViewModel
{
    /// <summary>
    /// [#893] Opens the page a second launch named with <c>--page</c>: a V2 address, or a V1 page
    /// name through the same aliases a cold <c>--page</c> launch uses. False when neither resolves.
    /// </summary>
    public bool OpenRequestedPage(string page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(page);
        var address = Router.Addresses.Parse(page).Location is not null
            ? page
            : V2LegacyPageAddressAliases.TryResolve(Registry, Variant, page);
        if (address is null)
        {
            return false;
        }

        var result = Router.NavigateToAddress(address);
        Act(result);
        return result.Succeeded;
    }
}
