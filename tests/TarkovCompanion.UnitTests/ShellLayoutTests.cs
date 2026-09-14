using TarkovCompanion.Application.Services.Shell;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Opening the window where it was left, on a screen that still exists.
/// </summary>
/// <remarks>
/// The window opened at 1500 by 900 every launch, in whatever place the operating system chose,
/// and no store had an entry for it — so a companion that shares a 3840×1080 screen with the
/// game was dragged back into its slot every time. The original handoff spec asked for placement
/// memory and it was never built.
///
/// The clamp is the part worth testing. Restoring exactly what was saved is easy and wrong:
/// somebody who left the companion on a second monitor and then unplugged it gets a window in
/// empty space with no title bar to drag it back by, which is the one failure that cannot be
/// recovered from inside the application.
/// </remarks>
public sealed class ShellLayoutTests
{
    private static readonly ScreenBounds Primary = new(0, 0, 1920, 1040);
    private static readonly ScreenBounds Second = new(1920, 0, 3840, 1040);

    [Fact]
    public void A_window_on_a_screen_that_is_still_there_is_left_alone()
    {
        var saved = At(2000, 100);

        Assert.Equal(saved, saved.ClampTo([Primary, Second]));
    }

    [Fact]
    public void A_window_on_a_monitor_that_has_gone_is_brought_back()
    {
        var stranded = At(2000, 100);

        var moved = stranded.ClampTo([Primary]);

        Assert.NotEqual(stranded.Left, moved.Left);
        Assert.InRange(moved.Left!.Value, Primary.Left, Primary.Right);
        Assert.InRange(moved.Top!.Value, Primary.Top, Primary.Bottom);
    }

    [Fact]
    public void A_window_hanging_off_an_edge_on_purpose_is_not_tidied_away()
    {
        // The test is overlap rather than containment. Somebody who put the window half off
        // the right-hand side did that deliberately, and moving it would be undoing a choice.
        var deliberate = At(1700, 60);

        Assert.Equal(deliberate, deliberate.ClampTo([Primary]));
    }

    [Fact]
    public void A_window_with_only_a_sliver_on_screen_has_nothing_to_drag_it_back_by()
    {
        // A few pixels of the right-hand edge is not a title bar. This is the case that looks
        // recoverable and is not.
        var barely = At(1900, 60);

        Assert.NotEqual(barely.Left, barely.ClampTo([Primary]).Left);
    }

    [Fact]
    public void A_window_above_the_top_of_every_screen_is_brought_back()
    {
        // Off the top is worse than off the side: the title bar is the part that has gone.
        var above = At(200, -900);

        Assert.NotEqual(above.Top, above.ClampTo([Primary]).Top);
    }

    [Fact]
    public void A_saved_size_larger_than_the_only_screen_is_cut_down_to_it()
    {
        var huge = new ShellLayout(5000, 3000, -9000, -9000, false, false);

        var moved = huge.ClampTo([Primary]);

        Assert.True(moved.Width <= Primary.Right - Primary.Left);
        Assert.True(moved.Height <= Primary.Bottom - Primary.Top);
    }

    [Fact]
    public void A_layout_that_was_never_placed_is_left_for_the_operating_system()
    {
        // Null position is a first launch. Choosing a corner for it would override the
        // operating system's own placement, which is better than anything guessed here.
        var fresh = ShellLayout.Default;

        Assert.Equal(fresh, fresh.ClampTo([Primary]));
    }

    [Fact]
    public void Knowing_of_no_screens_changes_nothing()
    {
        // Rather than moving the window to a corner of a screen nobody has described. If the
        // screens cannot be enumerated, what was saved is the best information available.
        var saved = At(2000, 100);

        Assert.Equal(saved, saved.ClampTo([]));
    }

    [Theory]
    [InlineData(0, 900)]
    [InlineData(1500, 0)]
    [InlineData(320, 240)]
    [InlineData(double.NaN, 900)]
    public void A_size_the_shell_cannot_use_is_refused(double width, double height)
    {
        // A window smaller than the shell's own minimum is a file somebody edited or a save
        // that caught the window mid-collapse. Opening at the default is recoverable; opening
        // at nought by nought is not.
        Assert.False(new ShellLayout(width, height, 0, 0, false, false).IsUsable);
    }

    [Fact]
    public void The_size_the_window_has_always_opened_at_is_usable()
    {
        Assert.True(ShellLayout.Default.IsUsable);
    }

    private static ShellLayout At(double left, double top) => new(1500, 900, left, top, false, false);
}
