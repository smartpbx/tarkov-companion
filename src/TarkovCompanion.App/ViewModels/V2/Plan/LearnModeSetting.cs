using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.App.ViewModels.V2.Plan;

/// <summary>The one remembered Learn Mode switch shared by Plan and its supporting item rows.</summary>
/// <remarks>
/// The explanations come from the engines that already made each decision. This setting only
/// decides whether their short reason is drawn; it never invents or changes a recommendation.
/// </remarks>
public sealed class LearnModeSetting : BindableViewModel
{
    private readonly IWorkspaceLayoutStore? _store;
    private bool _isEnabled;

    public LearnModeSetting(IWorkspaceLayoutStore? store = null)
    {
        _store = store;
        _isEnabled = string.Equals(
            store?.Get(WorkspaceLayoutKeys.PlanLearnMode),
            "on",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// [#902 P8] The store every page that reads Learn mode also keeps its filters in. Carried here
    /// because each of those pages already takes this setting, so none needs another argument.
    /// </summary>
    public IWorkspaceLayoutStore? Layout => _store;

    /// <summary>[#902 P8] One page's remembered filters, in the same store.</summary>
    public PageState Page(string key) => new(_store, key);

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                _store?.Set(WorkspaceLayoutKeys.PlanLearnMode, value ? "on" : "off");
            }
        }
    }
}
