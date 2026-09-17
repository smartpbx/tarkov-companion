using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Whether the map's floating chrome should be up or faded, driven by a fake clock instead of a
/// real pointer and a real timer.
/// </summary>
/// <remarks>
/// On a second monitor the mouse usually lives in the game, so the toolbars sit doing nothing
/// but covering the map. They should fade a few idle seconds after the pointer leaves and come
/// back the instant it returns — except while a dropdown is open, the Layers panel is expanded,
/// or something inside an overlay has keyboard focus, none of which is "idle".
/// </remarks>
public sealed class MapControlsIdleStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Starts_visible_before_anything_happens()
    {
        var state = new MapControlsIdleState();

        Assert.True(state.IsVisible);
    }

    [Fact]
    public void Fades_once_the_pointer_has_been_away_past_the_idle_delay()
    {
        var state = new MapControlsIdleState();

        state.PointerExited(Start);
        state.Tick(Start + MapControlsIdleState.IdleDelay + TimeSpan.FromSeconds(1));

        Assert.False(state.IsVisible);
    }

    [Fact]
    public void Stays_up_until_the_idle_delay_has_actually_passed()
    {
        var state = new MapControlsIdleState();

        state.PointerExited(Start);
        state.Tick(Start + MapControlsIdleState.IdleDelay - TimeSpan.FromMilliseconds(1));

        Assert.True(state.IsVisible);
    }

    [Fact]
    public void The_pointer_returning_brings_chrome_back_immediately()
    {
        var state = new MapControlsIdleState();
        state.PointerExited(Start);
        state.Tick(Start + MapControlsIdleState.IdleDelay + TimeSpan.FromSeconds(5));
        Assert.False(state.IsVisible);

        state.PointerEntered(Start + MapControlsIdleState.IdleDelay + TimeSpan.FromSeconds(5));

        Assert.True(state.IsVisible);
    }

    [Fact]
    public void An_open_dropdown_keeps_chrome_up_however_long_the_pointer_has_been_away()
    {
        var state = new MapControlsIdleState();
        state.PointerExited(Start);
        state.SetKeepVisible(true, Start);

        state.Tick(Start + MapControlsIdleState.IdleDelay + TimeSpan.FromMinutes(10));

        Assert.True(state.IsVisible);
    }

    [Fact]
    public void Closing_the_dropdown_starts_the_idle_clock_from_that_moment_not_from_when_the_pointer_first_left()
    {
        var state = new MapControlsIdleState();
        state.PointerExited(Start);
        state.SetKeepVisible(true, Start);
        // The pointer left ten minutes ago; none of that counts against the idle delay while
        // something was asking to stay visible.
        var closed = Start + TimeSpan.FromMinutes(10);
        state.SetKeepVisible(false, closed);

        state.Tick(closed + MapControlsIdleState.IdleDelay - TimeSpan.FromMilliseconds(1));
        Assert.True(state.IsVisible);

        state.Tick(closed + MapControlsIdleState.IdleDelay + TimeSpan.FromMilliseconds(1));
        Assert.False(state.IsVisible);
    }

    [Fact]
    public void Focus_inside_an_overlay_keeps_it_up_the_same_way_a_dropdown_does()
    {
        var state = new MapControlsIdleState();
        state.PointerExited(Start);

        state.SetKeepVisible(true, Start + TimeSpan.FromSeconds(1));
        state.Tick(Start + MapControlsIdleState.IdleDelay + TimeSpan.FromSeconds(30));

        Assert.True(state.IsVisible);
    }
}
