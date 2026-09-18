using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// [V2 rough package 39] How long until somebody who started at another spawn area could be
/// here. The rule under test is that this is a band and never a figure: the map cannot see
/// anybody, so anything narrower than "somewhere between these two" would be invented.
/// </summary>
public sealed class SpawnReachTests
{
    [Fact]
    public void A_reach_is_always_a_band_or_an_approximation_never_a_bare_number()
    {
        foreach (var metres in new double[] { 10, 40, 90, 150, 220, 300, 600 })
        {
            var reach = SpawnReach.Describe(metres);
            Assert.Contains("away", reach, StringComparison.Ordinal);
            Assert.True(
                reach.Contains('–', StringComparison.Ordinal) ||
                reach.StartsWith("about ", StringComparison.Ordinal),
                $"{metres} m produced '{reach}', which reads as an exact figure.");
        }
    }

    [Fact]
    public void The_band_brackets_the_sprint_and_the_careful_advance()
    {
        // 150 m: about 27 seconds flat out, about 75 moving carefully. The band has to contain
        // both, and it is rounded outwards, so it may be wider and may never be narrower.
        var reach = SpawnReach.Describe(150);

        Assert.Equal("20–90 s away", reach);
    }

    [Fact]
    public void A_walk_of_minutes_is_said_in_minutes()
    {
        Assert.Equal("90 s – 5 min away", SpawnReach.Describe(500));
        Assert.Equal("45 s – 3 min away", SpawnReach.Describe(250));
        Assert.Equal("2–7 min away", SpawnReach.Describe(800));
    }

    [Fact]
    public void Standing_on_it_is_not_a_walk_at_all()
    {
        Assert.Equal("about 10 s away", SpawnReach.Describe(0));
    }

    [Fact]
    public void The_near_end_of_a_band_is_never_faster_than_anybody_can_run()
    {
        // Rounding the sprint end outwards must not turn it into a claim nobody could meet: the
        // rung it lands on has to be within a rung of the real sprint, not a ladder step below it.
        foreach (var metres in new double[] { 60, 150, 220, 300 })
        {
            var sprintSeconds = metres / SpawnReach.SprintMetresPerSecond;
            var floor = LowerBoundSeconds(SpawnReach.Describe(metres));
            Assert.True(floor <= sprintSeconds, $"{metres} m claims a slower sprint than the model's own.");
            Assert.True(floor >= sprintSeconds * 0.6, $"{metres} m rounded the sprint down to something impossible.");
        }
    }

    [Fact]
    public void A_distance_that_is_not_a_distance_says_nothing()
    {
        Assert.Equal(string.Empty, SpawnReach.Describe(double.NaN));
        Assert.Equal(string.Empty, SpawnReach.Describe(-1));
    }

    [Fact]
    public void Further_is_never_sooner()
    {
        var previousFloor = 0;
        foreach (var metres in Enumerable.Range(0, 60).Select(step => step * 12.0))
        {
            var reach = SpawnReach.Describe(metres);
            Assert.NotEqual(string.Empty, reach);
            // The lower end of the band can only ever move outwards as the distance grows.
            var floor = LowerBoundSeconds(reach);
            Assert.True(floor >= previousFloor, $"{metres} m came back sooner than the step before it.");
            previousFloor = floor;
        }
    }

    /// <summary>The first number in the band, in seconds, whichever unit it was written in.</summary>
    private static int LowerBoundSeconds(string reach)
    {
        var text = reach.Replace("about ", string.Empty, StringComparison.Ordinal);
        var head = text.Split('–')[0].Trim();
        var digits = new string(head.TakeWhile(char.IsAsciiDigit).ToArray());
        var value = int.Parse(digits, System.Globalization.CultureInfo.CurrentCulture);
        // "1–5 min away": the unit lives on the far end of the band when both ends share it.
        return text.Contains("min", StringComparison.Ordinal) && !head.Contains('s', StringComparison.Ordinal)
            ? value * 60
            : value;
    }
}
