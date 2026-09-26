using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.App.Views.V2.Now;
using TarkovCompanion.Core.Domain.Situations;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.V2Now;

/// <summary>
/// [#712 0-4] "The Now panel never scrolls at 1920x1080": the real view, with real fonts, laid out
/// in the room the Raid page gives it at that size.
/// </summary>
/// <remarks>
/// 400 x 950 is the panel's own card in a 1920x1080 window: the column's minimum width, and the
/// height left under the top bar and the Raid page's tab row (measured on a V2RenderPreview
/// render: the card runs from y 112 to 1066). The blocks are clipped rather than scrolled, so a
/// block that no longer fits would silently lose its bottom line; this is what says so.
/// </remarks>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class NowPanelFitTests
{
    private const double Width = 400;
    private const double Height = 950;

    [Fact]
    public async Task Mid_raid_with_every_block_full_fits_without_scrolling()
    {
        var overflow = await MeasureAsync(panel =>
        {
            var situation = NowPanelStateTests.InRaid(19, 35);
            panel.Show(situation with
            {
                You = situation.You! with { AreaName = "Scav Checkpoint Bunker Entrance", FloorName = "2nd floor" },
                Squad =
                [
                    situation.Squad[0] with { AreaName = "New Gas Station Parking Lot" },
                    situation.Squad[1],
                    situation.Squad[2] with { State = SquadMemberState.InRaid, AreaName = "Dorms 3-story" },
                ],
                Next = situation.Next! with { Label = "Locate and obtain Secure Folder 0031 in one of the bunkhouses on Customs" },
                Then = situation.Then! with { Label = "Stash a regular Zibbo lighter at Dorm room 303 on Customs" },
            });
            panel.SetExits([NowPanelStateTests.Exit("Old Road Gate", 310, "W", offered: false)]);
        });

        Assert.True(overflow <= 0.5, $"the Now panel's blocks need {overflow:0} px more than a 1080 high window gives them");
    }

    [Fact]
    public async Task Late_raid_with_a_fresh_verdict_fits_without_scrolling()
    {
        var overflow = await MeasureAsync(panel =>
        {
            panel.Show(NowPanelStateTests.InRaid(26, 35));
            panel.SetExits([NowPanelStateTests.Exit("ZB-1011", 310, "W", offered: true)]);
            var rows = Enumerable.Range(0, NowPanelState.MaximumVerdictRows)
                .Select(index => new NowLootRow(
                    TarkovCompanion.Core.Domain.Loot.LootScanVerdict.Take,
                    $"Virtex programmable processor {index}",
                    "Current quest · 1 for Gunsmith - Part 22 and 2 for hideout",
                    "₽86k / sq"))
                .ToArray();
            panel.ShowLoot(new NowLootVerdict(2, 1, 1, 1, rows, DateTimeOffset.UtcNow));
        });

        Assert.True(overflow <= 0.5, $"the late-raid verdict needs {overflow:0} px more than a 1080 high window gives it");
    }

    private static async Task<double> MeasureAsync(Action<NowPanelViewModel> show)
    {
        using var session = HeadlessSessions.StartNew(typeof(NowPanelApp));
        return await session.Dispatch(
            () =>
            {
                using var scope = NowPanelStateTests.English();
                using var panel = new NowPanelViewModel(null, post: action => action(), tick: false);
                show(panel);
                var view = new NowPanelView { DataContext = panel };
                var window = new Window
                {
                    Width = Width,
                    Height = Height,
                    Content = view,
                    FontFamily = new Avalonia.Media.FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter"),
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var blocks = view.FindControl<StackPanel>("Blocks") ?? throw new InvalidOperationException("no Blocks");
                blocks.Measure(new Size(blocks.Bounds.Width, double.PositiveInfinity));
                var needed = blocks.DesiredSize.Height;
                window.Close();
                return needed - blocks.Bounds.Height;
            },
            CancellationToken.None);
    }

    /// <summary>The app's theme, V2 styles and Inter, drawn by Skia so text measures as it does on screen.</summary>
    public sealed class NowPanelApp : Avalonia.Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<NowPanelApp>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont();

        public override void Initialize()
        {
            var root = new Uri("avares://TarkovCompanion/");
            Resources.MergedDictionaries.Add(new ResourceInclude(root) { Source = new("avares://TarkovCompanion/Themes/V2/V2Resources.axaml") });
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(root) { Source = new("avares://TarkovCompanion/Themes/InstrumentStyles.axaml") });
            Styles.Add(new StyleInclude(root) { Source = new("avares://TarkovCompanion/Themes/V2/V2PrimitiveStyles.axaml") });
            Styles.Add(new StyleInclude(root) { Source = new("avares://TarkovCompanion/Themes/V2/V2WorkspaceStyles.axaml") });
        }
    }
}
