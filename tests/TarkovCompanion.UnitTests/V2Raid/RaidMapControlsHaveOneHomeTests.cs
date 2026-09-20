using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.UnitTests.V2DesignSystem;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// Build 11 drew "Drawing" twice and controlled the floors from two places. Each control now lives
/// in exactly one, and these fail if a second copy comes back.
/// </summary>
public sealed class RaidMapControlsHaveOneHomeTests
{
    private static readonly string[] Cockpit = ["src", "TarkovCompanion.App", "Views", "V2", "Raid", "RaidCockpitView.axaml"];
    // The mode segment and the floor ladder are their own control, which the renderer's pill hosts
    // and the Raid strip hosts; it is still the one place they are written.
    private static readonly string[] Renderer = ["src", "TarkovCompanion.App", "Views", "V2", "MapRenderer", "MapPresentationControls.axaml"];

    [Fact]
    public void The_bottom_strip_no_longer_repeats_the_artwork_choice_or_the_floor_controls()
    {
        var view = V2DesignSystemFiles.ReadText(Cockpit);

        // Artwork is chosen from the variant list (Drawing / Photo / ...), which is per map.
        Assert.DoesNotContain("v2-raid-artwork\"", view, StringComparison.Ordinal);
        Assert.Contains("v2-raid-artwork-variants", view, StringComparison.Ordinal);
        // Floors are chosen in the plan's own top bar: the presentation mode (Floor stack) and the
        // ladder. Neither its "Stack" switch, its "Floors" switch nor the stack's status line is here.
        Assert.DoesNotContain("v2-raid-stack\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("v2-raid-auto-floor", view, StringComparison.Ordinal);
        Assert.DoesNotContain("v2-raid-stack-status", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Drawing\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Stack\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Following_the_floor_you_are_on_is_in_the_floor_menu_beside_the_ladder()
    {
        var view = V2DesignSystemFiles.ReadText(Renderer);

        Assert.Contains("v2-map-follow-floor", view, StringComparison.Ordinal);
        Assert.Contains("v2-map-floor-picker", view, StringComparison.Ordinal);
    }

    [Fact]
    public void The_floor_menu_offers_following_only_when_the_host_provides_it_and_tells_the_view_when_it_changes()
    {
        var renderer = new MapSceneRendererViewModel(
            new(
                1,
                "customs",
                "customs-plan",
                "transform-1",
                new(0, 0, 100, 100),
                ["base", "second"],
                new(MapSceneCapability.Available, MapSceneCapability.Unavailable("No floors."), MapSceneCapability.Unavailable("No interior.")),
                new(MapSceneMode.Flat2D, "base", new(50, 50, 1, 0, 0), []),
                [new MapSceneLayer(new("extracts"), "Extracts", 10, true)],
                [],
                []),
            MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc));
        var told = new List<string>();
        renderer.PropertyChanged += (_, e) => told.Add(e.PropertyName ?? string.Empty);
        Assert.False(renderer.HasFollowFloorSwitch);

        var pressed = 0;
        renderer.SetFollowFloor(isOn: true, new Command(() => pressed++));

        Assert.True(renderer.HasFollowFloorSwitch);
        Assert.True(renderer.FollowsFloor);
        Assert.Equal("Follow my floor", renderer.FollowFloorLabel);
        Assert.Contains(nameof(MapSceneRendererViewModel.FollowsFloor), told);
        renderer.FollowFloorCommand!.Execute(null);
        Assert.Equal(1, pressed);

        told.Clear();
        renderer.SetFollowFloor(isOn: false, renderer.FollowFloorCommand);
        Assert.False(renderer.FollowsFloor);
        Assert.Contains(nameof(MapSceneRendererViewModel.FollowsFloor), told);
    }

    private sealed class Command(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
