using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.App.ViewModels.V2.Raid;

namespace TarkovCompanion.App.Views.V2.Raid;

/// <summary>[#712 0-4] The right column: the More drawer opens scrolled to what was asked for.</summary>
public sealed partial class RaidCockpitView
{
    private NowPanelHost? _nowHost;

    private void WatchNowHost(RaidCockpitViewModel? cockpit)
    {
        if (_nowHost is not null)
        {
            _nowHost.MoreOpened -= NowMoreOpened;
        }

        _nowHost = cockpit?.NowHost;
        if (_nowHost is not null)
        {
            _nowHost.MoreOpened += NowMoreOpened;
        }
    }

    /// <summary>
    /// "wrong?" opens the drawer at Corrections, the last card; More opens it at the top. Posted:
    /// the card stack has only just been made visible and has no layout to scroll yet.
    /// </summary>
    private void NowMoreOpened(object? sender, NowMoreTopic topic) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (this.FindControl<ScrollViewer>("RaidPanelScroll") is not { } scroll)
                {
                    return;
                }

                if (NowPanelHost.IsCorrection(topic))
                {
                    scroll.ScrollToEnd();
                    // [#712 0-6] A "wrong?" chip brings its own fact's row into view, not just the card.
                    var target = topic switch
                    {
                        NowMoreTopic.CorrectSide => "v2-raid-correct-side-value",
                        NowMoreTopic.CorrectExits => "v2-raid-correct-extracts-value",
                        _ => null,
                    };
                    if (target is not null &&
                        scroll.GetVisualDescendants().OfType<Control>()
                            .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == target) is { } row)
                    {
                        row.BringIntoView();
                    }
                }
                else
                {
                    scroll.ScrollToHome();
                }
            },
            DispatcherPriority.Background);
}
