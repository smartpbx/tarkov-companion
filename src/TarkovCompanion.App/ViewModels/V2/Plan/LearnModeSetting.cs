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
        _isEnabled = Read(store);
        if (store is not null)
        {
            // [#902] Backup & reset replaced the layout: show what it holds now, without a restart.
            store.Replaced += (_, _) =>
            {
                _isEnabled = Read(store);
                OnPropertyChanged(nameof(IsEnabled));
            };
        }
    }

    private static bool Read(IWorkspaceLayoutStore? store) => string.Equals(
        store?.Get(WorkspaceLayoutKeys.PlanLearnMode),
        "on",
        StringComparison.Ordinal);

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
