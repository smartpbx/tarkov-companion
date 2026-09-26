using System.ComponentModel;
using TarkovCompanion.App.Services.FeatureFlags;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.Application.Services.ReleaseExperience;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Features;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// [#712 0-4] The right column's switch between the Raid plan cards and the Now panel.
/// </summary>
/// <remarks>
/// The Now panel exists once a situation is attached (the composed app; a bare cockpit in a test
/// keeps the cards), and shows while the <c>now-panel</c> flag is on. The cards stay built and
/// bound either way: they are the panel's More drawer.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    /// <summary>The epic's 360–420 px: the Now panel's large type needs at least this much.</summary>
    public const double NowPanelMinimumWidth = 400;

    private NowPanelHost? _nowHost;
    private bool _watchingNowFlags;

    public NowPanelHost NowHost => _nowHost ??= CreateNowHost();

    /// <summary>The Now panel is the column: the strip under the map and the top bar drop their clocks.</summary>
    public bool ShowsNowPanel => NowHost.ShowsNowPanel;

    /// <summary>The column's width: the dragged width, widened to the Now panel's minimum while it shows.</summary>
    public double RightColumnWidth => NowHost.ShowsNowPanel ? Math.Max(ContextPanelWidth, NowPanelMinimumWidth) : ContextPanelWidth;

    /// <summary>Builds the Now panel over the situation; called once, from <see cref="AttachSituation"/>.</summary>
    private void AttachNowPanel(SituationService situation)
    {
        if (NowHost.Panel is not null)
        {
            return;
        }

        var panel = new NowPanelViewModel(situation, _timeProvider, PostToInterface)
        {
            MemberColour = name => _map.GroupColorFor(name) is { Length: 9 } argb ? "#" + argb[3..] : null,
            PingMember = _groupSession is null ? null : PingSquadmateAsync,
        };
        NowHost.Panel = panel;
        panel.SetExits(NowExits());
        PropertyChanged += NowPanelInputChanged;
        if (AppFeatureFlags.Current is FeatureFlagService flags && !_watchingNowFlags)
        {
            _watchingNowFlags = true;
            flags.Changed += (_, _) => PostToInterface(NowHost.Refresh);
        }
    }

    private NowPanelHost CreateNowHost()
    {
        var host = new NowPanelHost(() => AppFeatureFlags.Current.IsOn(Flag.NowPanel), RevealMoreTopic);
        host.LayoutChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowsNowPanel));
            OnPropertyChanged(nameof(RightColumnWidth));
            OnPropertyChanged(nameof(ShowsStripPhase));
        };
        return host;
    }

    private void NowPanelInputChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ExtractPanel):
                NowHost.Panel?.SetExits(NowExits());
                break;
            case nameof(ContextPanelWidth):
                OnPropertyChanged(nameof(RightColumnWidth));
                break;
        }
    }

    private IReadOnlyList<NowExit> NowExits() =>
        [.. _map.NearbyExits
            .Where(exit => exit.HasKnownPosition)
            .Select(exit => new NowExit(exit.Name, exit.MetresFromPlayer, exit.Bearing, exit.WasOffered, exit.IsTransit))];

    /// <summary>More at a topic opens the cards that topic groups; "wrong?" opens Corrections.</summary>
    private void RevealMoreTopic(NowMoreTopic topic)
    {
        var cards = typeof(RaidPanelCards).GetProperties()
            .Where(property => property.PropertyType == typeof(RaidPanelCardViewModel))
            .Select(property => (RaidPanelCardViewModel)property.GetValue(Cards)!)
            .ToArray();
        foreach (var id in NowPanelHost.CardsOf(topic))
        {
            cards.FirstOrDefault(card => card.Id == id)?.Reveal();
        }

        if (topic == NowMoreTopic.Corrections)
        {
            Corrections.IsOpen = true;
        }
    }

    /// <summary>[#712 0-5] Ping from a SQUAD row: a group ping at that squadmate's last shared spot.</summary>
    /// <remarks>Only what their own companion shared: the relay's position for them, never anything else.</remarks>
    private async Task<bool> PingSquadmateAsync(string name, CancellationToken cancellationToken)
    {
        if (_groupSession is not { } session ||
            _stateStore.Current.Group.Members.FirstOrDefault(member => string.Equals(member.Name, name, StringComparison.Ordinal)) is not
            { MapId: { } mapId, Position: { } position })
        {
            return false;
        }

        return await session.SendMarkAsync(mapId, position, label: name, isPing: true, cancellationToken).ConfigureAwait(true) is not null;
    }

    private static void PostToInterface(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
    }
}
