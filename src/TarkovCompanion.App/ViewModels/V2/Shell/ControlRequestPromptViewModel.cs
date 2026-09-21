using System.ComponentModel;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels.V2.Tablet;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// A paired tablet asking to drive this desktop, shown where the player actually is.
/// </summary>
/// <remarks>
/// #562: "when i swap to control mode it says its waiting for the desktop to allow but i dont see
/// a way to do that in the desktop." Allow/Deny already existed on <see
/// cref="CompanionPairingViewModel"/> and were already drawn on Team &gt; Tablet — the request was
/// never lost, only shown on a page Clayton was not looking at while the request timed out. This
/// wraps the same state and the same two commands so <see
/// cref="TarkovCompanion.App.ViewModels.V2.Shell.V2ShellViewModel"/> can show the identical prompt
/// across the top of whatever V2 page is actually open, without restating
/// <see cref="CompanionPairingViewModel"/>'s own control-request bookkeeping a second time.
/// </remarks>
public sealed class ControlRequestPromptViewModel : BindableViewModel
{
    private readonly CompanionPairingViewModel? _pairing;

    public ControlRequestPromptViewModel(CompanionPairingViewModel? pairing)
    {
        _pairing = pairing;
        if (_pairing is not null)
        {
            _pairing.PropertyChanged += OnPairingChanged;
        }
    }

    public bool IsVisible => _pairing?.HasControlRequest ?? false;

    public string Message => _pairing?.ControlRequestMessage ?? string.Empty;

    public ICommand? AllowCommand => _pairing?.AllowControlCommand;

    public ICommand? DenyCommand => _pairing?.DenyControlCommand;

    private void OnPairingChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(CompanionPairingViewModel.HasControlRequest)
            or nameof(CompanionPairingViewModel.ControlRequestMessage))
        {
            OnPropertyChanged(nameof(IsVisible));
            OnPropertyChanged(nameof(Message));
        }
    }
}
