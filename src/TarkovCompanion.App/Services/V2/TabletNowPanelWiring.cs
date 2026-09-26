using System.ComponentModel;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.Application.Services.Devices;

namespace TarkovCompanion.App.Services.V2;

/// <summary>
/// [#712 0-11] Hands the Raid page's Now panel to the paired tablets, while the <c>now-panel</c>
/// flag has it on the desk.
/// </summary>
/// <remarks>
/// The panel exists once the cockpit has a situation, which can be after the shell is built, so this
/// follows the host's layout changes: a panel arriving is attached then, and the flag going off sends
/// null (the tablet puts its own panel away). The panel refreshes every second; the publisher drops
/// every refresh that says nothing new (<see cref="TabletMapSurfacePublisher.ShowNow"/>).
/// </remarks>
public static class TabletNowPanelWiring
{
    public static void Attach(NowPanelHost host, Func<TabletNowPanel?, bool> show)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(show);
        NowPanelViewModel? watched = null;

        void Send()
        {
            if (host.IsNowOn && host.Panel is { } panel)
            {
                show(TabletNowPanelBuilder.Build(panel.State, panel.Situation, ColourOf(panel)));
            }
            else
            {
                show(null);
            }
        }

        void StateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null or nameof(NowPanelViewModel.State))
            {
                Send();
            }
        }

        void Follow()
        {
            if (!ReferenceEquals(watched, host.Panel))
            {
                if (watched is not null)
                {
                    watched.PropertyChanged -= StateChanged;
                }

                watched = host.Panel;
                if (watched is not null)
                {
                    watched.PropertyChanged += StateChanged;
                }
            }

            Send();
        }

        host.LayoutChanged += (_, _) => Follow();
        Follow();
    }

    /// <summary>The row's colour as the desk draws it, so a tablet row matches the desk's dot.</summary>
    private static Func<string, string?> ColourOf(NowPanelViewModel panel) => name =>
        panel.SquadRows.FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.Ordinal))?.Colour is { Length: 7 } colour
            ? colour
            : null;
}
