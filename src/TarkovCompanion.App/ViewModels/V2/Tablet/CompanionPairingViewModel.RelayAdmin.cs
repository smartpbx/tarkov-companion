using Avalonia.Threading;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Application.Services.Devices;

namespace TarkovCompanion.App.ViewModels.V2.Tablet;

/// <summary>
/// [#920] The relay owner's admin panel, shown under this desktop's devices. It rides on the owner
/// session this panel's bridge already keeps, and asks the relay again each time that session is
/// verified: the relay, not this desktop, decides who its owner is.
/// </summary>
public sealed partial class CompanionPairingViewModel
{
    private RelayAdminPanelViewModel? _relayAdmin;

    /// <summary>Null without a relay bridge; hidden (<c>IsRelayOwner</c> false) for anybody but the owner.</summary>
    public RelayAdminPanelViewModel? RelayAdmin
    {
        get
        {
            if (_relayAdmin is null && _relayMarksBridge is { } bridge)
            {
                var panel = new RelayAdminPanelViewModel(new RelayAdminClient(bridge), _timeProvider);
                _relayAdmin = panel;
                bridge.OwnerLinkChanged += link =>
                {
                    if (link == RelayOwnerLinkState.Verified)
                    {
                        Dispatcher.UIThread.Post(() => _ = panel.RefreshAsync(_lifetime.Token));
                    }
                };
                if (bridge.OwnerLink is RelayOwnerLinkState.Verified)
                {
                    Dispatcher.UIThread.Post(() => _ = panel.RefreshAsync(_lifetime.Token));
                }
            }

            return _relayAdmin;
        }
    }

    /// <summary>The render tool's seam: an admin panel with no relay behind it.</summary>
    internal RelayAdminPanelViewModel PreviewRelayAdmin()
    {
        _relayAdmin ??= new RelayAdminPanelViewModel(
            new RelayAdminClient((_, _, _) => Task.FromResult<HttpResponseMessage?>(null)),
            _timeProvider);
        OnPropertyChanged(nameof(RelayAdmin));
        return _relayAdmin;
    }
}
