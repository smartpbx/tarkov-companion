using TarkovCompanion.Application.Services.Shell;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// How large the window draws itself.
/// </summary>
/// <remarks>
/// Seven fixed pixel sizes in the type scale, every column fixed, and no LayoutTransform
/// anywhere — so the only lever anybody had was Windows scaling, which scales the game on the
/// same machine. This is the companion's own.
///
/// Four sizes rather than a slider: the type scale, the row heights and the fixed columns were
/// all designed against one of them, and a continuum of arbitrary multipliers is a continuum of
/// layouts nobody has ever looked at.
/// </remarks>
public sealed class InterfaceScaleTests
{
    [Fact]
    public void The_designed_size_is_one_of_the_sizes_on_offer()
    {
        // Or Reset would put the window somewhere it could never be stepped back to.
        Assert.Contains(1.0, ShellLayout.Scales);
    }

    [Fact]
    public void The_sizes_are_offered_smallest_first()
    {
        // The step is an index walk, so an unsorted list would make + smaller on some presses.
        Assert.Equal(ShellLayout.Scales.Order(), ShellLayout.Scales);
    }

    [Theory]
    [InlineData(0.9, 1.0)]
    [InlineData(1.0, 1.15)]
    [InlineData(1.15, 1.3)]
    public void Stepping_up_goes_to_the_next_size(double from, double expected)
    {
        Assert.Equal(expected, ShellLayout.StepScale(from, 1), 6);
    }

    [Theory]
    [InlineData(1.3, 1.15)]
    [InlineData(1.15, 1.0)]
    [InlineData(1.0, 0.9)]
    public void Stepping_down_goes_to_the_previous_size(double from, double expected)
    {
        Assert.Equal(expected, ShellLayout.StepScale(from, -1), 6);
    }

    [Fact]
    public void The_ends_stop_rather_than_wrapping()
    {
        // The same rule the floors follow. A key held down should not come back round to where
        // it started without saying so, and there is no size beyond the largest.
        Assert.Equal(2.0, ShellLayout.StepScale(2.0, 1), 6);
        Assert.Equal(0.9, ShellLayout.StepScale(0.9, -1), 6);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(double.NaN)]
    public void A_size_nobody_offers_is_snapped_to_one_that_is(double stored)
    {
        // The settings file is one somebody can open, and a hand-typed 4 would draw the rail
        // alone wider than a monitor — with no way to press anything that would undo it.
        Assert.Contains(ShellLayout.NearestScale(stored), ShellLayout.Scales);
    }

    [Fact]
    public void A_size_between_two_offered_ones_goes_to_the_nearer()
    {
        Assert.Equal(1.15, ShellLayout.NearestScale(1.13), 6);
        Assert.Equal(1.0, ShellLayout.NearestScale(1.02), 6);
    }

    [Fact]
    public void Stepping_from_a_size_nobody_offers_still_lands_on_one()
    {
        // Somebody who edited the file to 1.2 and then pressed + should get 1.3, not 2.2.
        Assert.Equal(1.3, ShellLayout.StepScale(1.2, 1), 6);
    }

    [Fact]
    public void A_layout_written_before_this_existed_opens_at_the_designed_size()
    {
        // Rather than at zero, which would draw nothing at all.
        Assert.Equal(1, ShellLayout.Default.Scale, 6);
    }

    [Fact]
    public void The_size_survives_a_window_that_could_not_be_used()
    {
        // Two different preferences. A stored size the shell cannot open at says nothing about
        // how large somebody wanted everything drawn, and discarding both would quietly undo a
        // setting because an unrelated one was wrong.
        var unusable = new ShellLayout(0, 0, null, null, false, true, 1.3);

        Assert.False(unusable.IsUsable);
        Assert.Equal(1.3, unusable.Scale, 6);
    }
}
