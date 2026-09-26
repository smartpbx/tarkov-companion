using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.V2.Appearance;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.App.Views.V2.Now;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Personalization;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Situations;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.V2Now;

/// <summary>
/// [#712 0-7] The glance ratchet: the Now panel never scrolls, nothing on it is cut off, the raid
/// clock is on it once, and every guess it shows carries its label where the player can see it.
/// </summary>
/// <remarks>
/// <para>
/// The real view, Skia and Inter, in every phase and in a worst case (four squadmates, long area
/// names, a late raid, a full verdict), in the room the Raid page really gives the panel. That room
/// was measured with <c>V2RenderPreview --route raid --raid-demo --now-probe</c>: the panel is
/// 384 wide at all six sizes, and its height is what is left under the top bar and the tab row,
/// which grow with the text. 1009 is 1080 less the Windows taskbar, a maximised window.
/// </para>
/// <para>
/// This is a ratchet like <c>scripts/sweep-prose.sh</c>: it fails, it does not warn. Its first run
/// on main found 364 faults, fixed with it: the worst case overflowed at every size (52 px at
/// 1080 and 100%, about 700 px at 1009 and 150% with a fresh verdict), cutting SQUAD's rows, NEXT
/// and LAST SCAN off with their labels, so the panel now folds (NowPanelViewModel.Fold); at 125%
/// "Waiting for the game", "Loading Customs" and "Time left unknown" ran past their own width and
/// the run-through line off the panel's edge; and a late raid with a fresh verdict named, in
/// "Leave for …", an exit the extract list never offered, with "not seen on your extract list"
/// hidden. The clock check is the panel's half of "one clock on the Raid page"; the top bar and
/// the strip under the map drop theirs while the panel shows (V2ShellViewModel.NowPanel).
/// </para>
/// </remarks>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed partial class NowPanelGlanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 14, 0, 0, TimeSpan.Zero);

    /// <summary>The Now panel's own size on the Raid page, measured (see remarks).</summary>
    internal static readonly Room[] Rooms =
    [
        new(1080, 100, 384, 953),
        new(1080, 125, 384, 949),
        new(1080, 150, 384, 945),
        new(1009, 100, 384, 882),
        new(1009, 125, 384, 878),
        new(1009, 150, 384, 874),
    ];

    /// <summary>One walk for both rules: laying the panel out is the cost, about five seconds.</summary>
    private static readonly Lazy<Task<Walked>> Walk = new(WalkAsync);

    [Fact]
    public async Task The_Now_panel_never_scrolls_or_clips_and_shows_the_raid_clock_once()
    {
        var failures = (await Walk.Value).Glance;

        Assert.True(failures.Count == 0, Report("The Now panel no longer fits at a glance", failures));
    }

    [Fact]
    public async Task Every_guess_on_the_Now_panel_shows_its_label()
    {
        var failures = (await Walk.Value).Labels;

        Assert.True(failures.Count == 0, Report("A guess on the Now panel shows without its label", failures));
    }

    /// <summary>Each fixture at each size: the view laid out, then both checks over the result.</summary>
    private static async Task<Walked> WalkAsync()
    {
        using var session = HeadlessSessions.StartNew(typeof(NowPanelFitTests.NowPanelApp));
        return await session.Dispatch(
            () =>
            {
                using var english = NowPanelStateTests.English();
                using var zone = LocalTime.UseZone(TimeZoneInfo.Utc);
                var application = Avalonia.Application.Current ?? throw new InvalidOperationException("no application");
                var baseline = V2AppearanceResources.BaselineKeys.ToDictionary(
                    key => key,
                    key => application.TryGetResource(key, application.ActualThemeVariant, out var value) ? value : null);
                var walked = new Walked([], []);
                foreach (var room in Rooms)
                {
                    foreach (var (key, value) in V2AppearanceResources.Overrides(new WorkspacePreferences(TextScalePercent: room.TextPercent), baseline))
                    {
                        application.Resources[key] = value;
                    }

                    foreach (var fixture in Fixtures())
                    {
                        using var panel = new NowPanelViewModel(null, new FixedClock(Now), post: action => action(), tick: false);
                        panel.Show(fixture.Situation);
                        panel.SetExits(fixture.Exits);
                        panel.ShowLoot(fixture.Verdict);
                        var view = new NowPanelView { DataContext = panel };
                        var window = new Window
                        {
                            Width = room.PanelWidth,
                            Height = room.PanelHeight,
                            Content = view,
                            FontFamily = new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter"),
                        };
                        window.Show();
                        Dispatcher.UIThread.RunJobs();
                        string Where(string failure) => $"{room} {fixture.Name} (fold {panel.Fold}): {failure}";
                        var glance = Glance(panel, view).Select(Where).ToArray();
                        var labels = Labels(fixture, panel, view).Select(Where).ToArray();
                        walked.Glance.AddRange(glance);
                        walked.Labels.AddRange(labels);
                        var found = glance.Concat(labels).ToArray();
                        if (Environment.GetEnvironmentVariable("TARKOV_GLANCE_CAPTURES") is { Length: > 0 } captures)
                        {
                            // Set it to a scratch directory to look at every case; never under bin/.
                            Directory.CreateDirectory(captures);
                            window.CaptureRenderedFrame()?.Save(Path.Combine(
                                captures,
                                $"{room.WindowHeight}-{room.TextPercent}-{(found.Length > 0 ? "FAIL-" : string.Empty)}{string.Concat(fixture.Name.Where(char.IsLetterOrDigit))}-fold{panel.Fold}.png"),
                                new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                        }

                        window.Close();
                    }
                }

                return walked;
            },
            CancellationToken.None);
    }

    private static IEnumerable<string> Glance(NowPanelViewModel panel, NowPanelView view)
    {
        // The fold is worked out inside the view's measure; it must settle, not fold and unfold.
        var fold = panel.Fold;
        Dispatcher.UIThread.RunJobs();
        if (!view.IsMeasureValid || !view.IsArrangeValid || panel.Fold != fold)
        {
            yield return $"the layout does not settle (fold {fold}, then {panel.Fold})";
        }

        foreach (var scroll in view.GetVisualDescendants().OfType<ScrollViewer>().Where(scroll => scroll.IsEffectivelyVisible))
        {
            if (scroll.Extent.Height > scroll.Viewport.Height + 0.5 || scroll.Extent.Width > scroll.Viewport.Width + 0.5)
            {
                yield return $"a scroll viewer scrolls ({scroll.Extent} in {scroll.Viewport})";
            }
        }

        var blocks = Blocks(view);
        blocks.Measure(new Size(blocks.Bounds.Width, double.PositiveInfinity));
        var needed = blocks.DesiredSize.Height;
        blocks.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
        if (needed > blocks.Bounds.Height + 0.5)
        {
            yield return $"the blocks need {needed - blocks.Bounds.Height:0} px more than the panel has";
        }

        foreach (var text in Texts(view))
        {
            if (!text.Seen)
            {
                yield return $"'{text.Content}' is cut off by {text.Why}";
            }
        }

        // The epic's NOW block is the only clock (the strip and top bar drop theirs while it shows).
        if (panel.State.Phase == SituationPhase.InRaid && ClockPattern().Match(panel.State.NowHeadline) is { Success: true } clock)
        {
            var shown = Texts(view).Count(text => text.Content.Contains(clock.Value, StringComparison.Ordinal));
            if (shown != 1)
            {
                yield return $"the raid clock {clock.Value} is shown {shown} times";
            }
        }
    }

    /// <summary>
    /// Walks what the panel shows from the situation, and for each thing that is worked out rather
    /// than read (NowPanelState's own list: the clock's basis, a screenshot's age, an exit not seen
    /// offered, a squadmate's age, a planned leg, an ending somebody reported) wants its label on
    /// screen, uncut. A new inferred fact the panel starts to show without a rule here fails too.
    /// </summary>
    private static IEnumerable<string> Labels(Fixture fixture, NowPanelViewModel panel, NowPanelView view)
    {
        var situation = panel.Situation;
        var state = panel.State;
        var seen = Texts(view).Where(text => text.Seen).Select(text => text.Content).ToArray();
        bool Shows(string label) => label.Length > 0 && seen.Any(text => text.Contains(label, StringComparison.Ordinal));
        var inRaid = state.Phase == SituationPhase.InRaid;

        if (inRaid && situation.Clock is not { IsInferred: false })
        {
            string[] bases = [NowText.ClockCounted, NowText.ClockElapsedBasis, NowText.ClockNoStart];
            if (!bases.Contains(state.NowDetail) || !Shows(state.NowDetail))
            {
                yield return $"the raid clock '{state.NowHeadline}' is a count, and '{state.NowDetail}' is not on screen";
            }
        }

        if (!inRaid && (situation.Phase.IsInferred || situation.Outcome is { IsInferred: true }) && !Shows(state.NowDetail))
        {
            yield return $"the phase '{state.NowHeadline}' is worked out, and its line '{state.NowDetail}' is not on screen";
        }

        if (state.Phase is SituationPhase.Dead or SituationPhase.Extracted &&
            !Shows(NowText.PhaseLine(state.Phase)) && !Shows(NowText.PhaseLine(state.Phase, fromScreen: true)))
        {
            yield return $"'{state.NowHeadline}' does not say who reported it";
        }

        if (state.Phase == SituationPhase.Loading && situation.Map is { IsInferred: true })
        {
            yield return "the map being loaded is a guess, and the panel has no label for it";
        }

        if (inRaid && situation.You is not null && !Shows(state.YouAge))
        {
            yield return $"YOU '{state.YouWhere}' is shown without its screenshot's age";
        }

        var exit = NowPanelState.ChooseExit(fixture.Exits);
        var namesExit = exit is not null && seen.Any(text => text.Contains(exit.Name, StringComparison.Ordinal));
        if (inRaid && exit is { IsOffered: false } && namesExit && !Shows(NowText.ExitUnconfirmed))
        {
            yield return $"the exit {exit.Name} was never seen offered, and '{NowText.ExitUnconfirmed}' is not on screen";
        }

        // [#403] Companions' rows or the game's party list: either way, where they came from is on screen.
        if (state.HasSquad && state.ShowsSquad && !Shows(state.SquadSource))
        {
            yield return $"SQUAD is shown without '{state.SquadSource}'";
        }

        if (panel.ShowsSquadRows)
        {
            foreach (var row in state.Squad.Where(row => row.Age.Length > 0))
            {
                if (seen.Any(text => text.Contains(row.Name, StringComparison.Ordinal)) && !Shows(row.Age))
                {
                    yield return $"{row.Name}'s row is shown without its age {row.Age}";
                }
            }
        }

        if (state.HasNext && Shows(state.NextLabel[..Math.Min(12, state.NextLabel.Length)]) && !Shows(NowText.NextOnRoute))
        {
            yield return $"NEXT '{state.NextLabel}' is a planned stop, and '{NowText.NextOnRoute}' is not on screen";
        }
    }

    private static StackPanel Blocks(NowPanelView view) =>
        view.FindControl<StackPanel>("Blocks") ?? throw new InvalidOperationException("no Blocks");

    /// <summary>Every piece of text the player can see, and whether all of it is inside what clips it.</summary>
    private static IEnumerable<Shown> Texts(NowPanelView view)
    {
        var blocks = Blocks(view);
        foreach (var text in view.GetVisualDescendants().OfType<TextBlock>())
        {
            var content = Content(text);
            if (!text.IsEffectivelyVisible || content.Length == 0 || text.Bounds.Width <= 0 || text.Bounds.Height <= 0)
            {
                continue;
            }

            // The blocks clip; everything else (the "because" line, More) must sit inside the card.
            Visual frame = text.GetVisualAncestors().Contains(blocks) ? blocks : view;
            var origin = text.TranslatePoint(default, frame) ?? default;
            var box = new Rect(origin, text.Bounds.Size);
            var layout = text.TextLayout;
            var why =
                box.Bottom > frame.Bounds.Height + 0.5 ? $"the bottom of the panel ({box.Bottom - frame.Bounds.Height:0} px below)" :
                box.Right > frame.Bounds.Width + 0.5 ? $"the right edge of the panel ({box.Right - frame.Bounds.Width:0} px past)" :
                layout.Width > text.Bounds.Width - text.Padding.Left - text.Padding.Right + 0.5 ? $"its own width ({layout.Width:0} px of text in {text.Bounds.Width:0})" :
                null;
            yield return new(content, why is null, why ?? string.Empty);
        }
    }

    private static string Content(TextBlock text) =>
        text.Text is { Length: > 0 } plain
            ? plain
            : string.Concat(text.Inlines?.OfType<Run>().Select(run => run.Text) ?? []).Trim();

    private static string Report(string heading, List<string> failures) =>
        $"{heading} ({failures.Count}):{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(60))}";

    [GeneratedRegex(@"\d{1,2}:\d{2}(:\d{2})?")]
    private static partial Regex ClockPattern();

    /// <summary>Every phase, and the worst cases the epic names (#712 0-7).</summary>
    internal static IEnumerable<Fixture> Fixtures()
    {
        yield return new("unknown", Situation.Initial with { ComputedUtc = Now });
        foreach (var phase in new[] { SituationPhase.Menu, SituationPhase.PostRaid })
        {
            yield return new(phase.ToString(), Out(phase));
        }

        yield return new("screen", Out(SituationPhase.Screen) with
        {
            LastScan = new(ScanContext.SingleItem, "Graphics card", 1, "Sell", Now.AddSeconds(-30), "read"),
        });
        // [#403] The queue with its stage line and a full party nobody shares from.
        yield return new("Matching", Out(SituationPhase.Matching) with
        {
            Stages = [new(RaidPhaseMarkerKind.MatchingStarted, Now.AddSeconds(-50)), new(RaidPhaseMarkerKind.MatchingStep, Now.AddSeconds(-48))],
            Party = FullParty,
        });
        yield return new("loading", Out(SituationPhase.Loading) with
        {
            Map = new("customs", Confidence.Certain, SituationSource.GameLog, Now, "log"),
        });
        yield return new("spawning, every stage", Out(SituationPhase.Loading) with
        {
            Map = new("customs", Confidence.Certain, SituationSource.GameLog, Now, "log"),
            Stages =
            [
                new(RaidPhaseMarkerKind.MatchingStarted, Now.AddSeconds(-80)),
                new(RaidPhaseMarkerKind.MatchingCompleted, Now.AddSeconds(-60)),
                new(RaidPhaseMarkerKind.LocationLoaded, Now.AddSeconds(-40)),
                new(RaidPhaseMarkerKind.Spawning, Now.AddSeconds(-12)),
                new(RaidPhaseMarkerKind.Spawned, Now.AddSeconds(-2)),
            ],
            Party = FullParty,
        });
        yield return new("dead", Reported(SituationPhase.Dead, SituationOutcome.Died, SituationSource.Player));
        yield return new("extracted", Reported(SituationPhase.Extracted, SituationOutcome.Survived, SituationSource.Screenshot));

        var early = NowPanelStateTests.InRaid(minutesIn: 3, length: 40);
        yield return new("in raid, early, no screenshot", early with { You = null, Squad = [], Next = null, Then = null });
        yield return new("in raid, clock unknown", early with { Clock = null });
        yield return new("in raid, scav elapsed", early with
        {
            Clock = new(SituationClockBasis.Elapsed, null, null, Now.AddMinutes(-3), "joined"),
        });

        var mid = Worst(NowPanelStateTests.InRaid(minutesIn: 19, length: 40));
        yield return new("in raid, worst case", mid, [NowPanelStateTests.Exit("Old Road Gate", 310, "W", offered: false)]);

        var late = Worst(NowPanelStateTests.InRaid(minutesIn: 32, length: 40));
        NowExit[] far = [NowPanelStateTests.Exit("Scav Checkpoint Crossroads", 180, "SW", offered: false)];
        yield return new("late raid, worst case", late, far);
        yield return new("late raid, worst case, old verdict", late, far, Verdict(Now.AddMinutes(-4)));
        yield return new("late raid, worst case, fresh verdict", late, far, Verdict(Now.AddSeconds(-2)));
        yield return new("late raid, past leaving, fresh verdict", Worst(NowPanelStateTests.InRaid(minutesIn: 37, length: 40)), far, Verdict(Now.AddSeconds(-2)));
    }

    /// <summary>Four squadmates in the raid with long places, a long YOU, and a long route.</summary>
    private static Situation Worst(Situation situation) => situation with
    {
        You = situation.You! with { AreaName = "Scav Checkpoint Bunker Entrance", FloorName = "2nd floor" },
        Squad =
        [
            new("Geo", SquadMemberState.InRaid, "customs", "New Gas Station Parking Lot", 140, "NE", Now.AddSeconds(-12), "shared"),
            new("Riley", SquadMemberState.InRaid, "customs", "Dorms 3-story Stairwell", 60, "N", Now.AddSeconds(-40), "shared"),
            new("Sam", SquadMemberState.Quiet, "customs", "Big Red Warehouse Loading Dock", 320, "SW", Now.AddMinutes(-4), "shared"),
            new("Kai", SquadMemberState.InRaid, "customs", "Construction Site Crane", 510, "SE", Now.AddSeconds(-5), "shared"),
        ],
        Next = situation.Next! with { Label = "Locate and obtain Secure Folder 0031 in one of the bunkhouses on Customs" },
        Then = situation.Then! with { Label = "Stash a regular Zibbo lighter at Dorm room 303 on Customs" },
    };

    private static SituationParty FullParty { get; } = new(
        [
            new("PLAYER_WITH_A_LONG_NAME_B", true, true),
            new("PLAYER_WITH_A_LONG_NAME_C", false, false),
            new("PLAYER_WITH_A_LONG_NAME_D", null, false),
            new("PLAYER_WITH_A_LONG_NAME_E", true, false),
        ],
        2,
        Now.AddSeconds(-20),
        "The game's group notifications.");

    private static Situation Out(SituationPhase phase) =>
        new(3, Now, new(phase, new Confidence(0.9), SituationSource.GameLog, Now.AddMinutes(-1), "log"));

    private static Situation Reported(SituationPhase phase, SituationOutcome outcome, SituationSource source) => Out(phase) with
    {
        Outcome = new(outcome, Confidence.Certain, source, Now.AddSeconds(-20), "reported"),
    };

    private static NowLootVerdict Verdict(DateTimeOffset received) => new(
        2,
        1,
        1,
        0,
        [.. Enumerable.Range(0, NowPanelState.MaximumVerdictRows).Select(index => new NowLootRow(
            index < 2 ? LootScanVerdict.Take : index == 2 ? LootScanVerdict.Swap : LootScanVerdict.Leave,
            $"Virtex programmable processor {index}",
            "Current quest · 1 for Gunsmith - Part 22 and 2 for hideout",
            "₽86k / sq"))],
        received);

    internal sealed record Room(int WindowHeight, int TextPercent, double PanelWidth, double PanelHeight)
    {
        public override string ToString() => $"1920x{WindowHeight} at {TextPercent}%";
    }

    internal sealed record Fixture(string Name, Situation Situation, IReadOnlyList<NowExit>? Exits = null, NowLootVerdict? Verdict = null)
    {
        public IReadOnlyList<NowExit> Exits { get; } = Exits ?? [];
    }

    private sealed record Shown(string Content, bool Seen, string Why);

    private sealed record Walked(List<string> Glance, List<string> Labels);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
