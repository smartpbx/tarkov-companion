using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.App.Views.V2.Now;
using TarkovCompanion.Core.Common;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.V2Now;

/// <summary>
/// [#712 0-5, 0-6] The real Now panel view: a SQUAD row's tap pings, its right-click places a
/// waypoint (once), the "wrong?" chips are on screen, and the late-raid line shows its label.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class NowPanelSquadRowViewTests
{
    [Fact]
    public async Task A_tap_pings_a_right_click_places_one_waypoint_and_the_chips_and_estimate_show()
    {
        using var session = HeadlessSessions.StartNew(typeof(NowPanelFitTests.NowPanelApp));
        var result = await session.Dispatch(
            () =>
            {
                using var scope = NowPanelStateTests.English();
                using var zone = LocalTime.UseZone(TimeZoneInfo.Utc);
                var pinged = new List<string>();
                var placed = new List<string>();
                using var panel = new NowPanelViewModel(null, new NowPanelSquadAndLateRaidTests.FixedClock(new(2026, 9, 26, 14, 0, 0, TimeSpan.Zero)), post: action => action(), tick: false)
                {
                    PingMember = (name, _) =>
                    {
                        pinged.Add(name);
                        return Task.FromResult(true);
                    },
                    WaypointMember = (name, _) =>
                    {
                        placed.Add(name);
                        return Task.FromResult(true);
                    },
                };
                panel.Show(NowPanelStateTests.InRaid(26, 35));
                panel.SetExits([NowPanelStateTests.Exit("ZB-1011", 310, "W", offered: true)]);
                var view = new NowPanelView { DataContext = panel };
                var window = new Window
                {
                    Width = 400,
                    Height = 950,
                    Content = view,
                    FontFamily = new Avalonia.Media.FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter"),
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var rows = view.GetVisualDescendants().OfType<Button>()
                    .Where(button => AutomationProperties.GetAutomationId(button) == "v2-now-squad-ping")
                    .ToArray();
                var geo = rows.First(row => row.DataContext is NowSquadRowViewModel { Name: "Geo" });
                geo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                geo.RaiseEvent(new ContextRequestedEventArgs());
                geo.RaiseEvent(new ContextRequestedEventArgs()); // a touch hold raises it again: still one waypoint
                Dispatcher.UIThread.RunJobs();

                string[] seen = [.. view.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? string.Empty)];
                string[] chips = [.. view.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.IsEffectivelyVisible)
                    .Select(button => AutomationProperties.GetAutomationId(button) ?? string.Empty)
                    .Where(id => id.StartsWith("v2-now-wrong", StringComparison.Ordinal))];
                window.Close();
                return (Pinged: pinged, Placed: placed, Seen: seen, Chips: chips, RowCount: rows.Length);
            },
            CancellationToken.None);

        Assert.Equal(3, result.RowCount);
        Assert.Equal(["Geo"], result.Pinged);
        Assert.Equal(["Geo"], result.Placed);
        Assert.Equal(["v2-now-wrong-side", "v2-now-wrong-exits"], result.Chips);
        Assert.Contains(result.Seen, text => text.StartsWith("Leave for ZB-1011 by ", StringComparison.Ordinal));
        Assert.Contains(NowText.LeaveEstimate, result.Seen);
    }
}
