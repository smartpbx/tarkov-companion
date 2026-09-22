using System.Windows.Input;
using TarkovCompanion.App.ViewModels;

namespace TarkovCompanion.App.ViewModels.V2.Tablet;

/// <summary>
/// "Send to tablet", the desktop's half of the tablet's "Show on desktop" (#290).
/// </summary>
/// <remarks>
/// A tablet in Independent keeps its own view, which is the point of Independent, and until this
/// the only way to bring it to what the desk shows was to switch it to Follow and back. The desk
/// now publishes its current view with a fresh stamp and the tablet jumps there, staying in
/// Independent. <see cref="SendMapToTablet"/> is set once at startup by whatever publishes the
/// map (TabletMapSurfacePublisher.SendToTabletAsync); with nothing set, the button is not shown.
/// </remarks>
public sealed partial class CompanionPairingViewModel
{
    private Func<CancellationToken, Task<bool>>? _sendMapToTablet;
    private AsyncDelegateCommand? _sendMapToTabletCommand;
    private string? _sendMapToTabletMessage;

    /// <summary>Publishes the desktop's current map view as a send; true when the relay took it.</summary>
    public Func<CancellationToken, Task<bool>>? SendMapToTablet
    {
        get => _sendMapToTablet;
        set
        {
            _sendMapToTablet = value;
            OnPropertyChanged(nameof(CanSendMapToTablet));
        }
    }

    public bool CanSendMapToTablet => _sendMapToTablet is not null;

    public ICommand SendMapToTabletCommand => _sendMapToTabletCommand ??= new AsyncDelegateCommand(SendMapToTabletAsync);

    /// <summary>What the last send did, in a few words.</summary>
    public string? SendMapToTabletMessage
    {
        get => _sendMapToTabletMessage;
        private set
        {
            if (SetProperty(ref _sendMapToTabletMessage, value))
            {
                OnPropertyChanged(nameof(HasSendMapToTabletMessage));
            }
        }
    }

    public bool HasSendMapToTabletMessage => !string.IsNullOrEmpty(SendMapToTabletMessage);

    private async Task SendMapToTabletAsync()
    {
        if (_sendMapToTablet is not { } send)
        {
            return;
        }

        bool sent;
        try
        {
            sent = await send(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            sent = false;
        }

        SendMapToTabletMessage = sent
            ? "Sent this map view to your tablets."
            : "Not sent. Open a map on Raid and check the relay.";
    }
}
