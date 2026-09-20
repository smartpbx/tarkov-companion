using System.ComponentModel;
using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// The top bar names the wipe and the language beside the profile and its game mode (#288): the
/// four things that decide whose progress and which catalog every page is showing. The words come
/// from the Setup profiles pane, which already reads the profile context; the chip follows it.
/// </summary>
public sealed partial class V2ShellViewModel
{
    private void WireProfileChip()
    {
        if (SetupWorkspace?.Profiles is { } profiles)
        {
            profiles.PropertyChanged += ProfileChipChanged;
        }
    }

    private void ProfileChipChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (!_disposed && eventArgs.PropertyName == nameof(SetupProfilesViewModel.HeaderSuffix))
        {
            OnPropertyChanged(nameof(TopBarModeLabel));
        }
    }

    private string WithWipeAndLanguage(string label) =>
        SetupWorkspace?.Profiles?.HeaderSuffix is { Length: > 0 } suffix ? $"{label} · {suffix}" : label;
}
