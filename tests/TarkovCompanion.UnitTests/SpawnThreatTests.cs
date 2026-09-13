using Avalonia;
using Avalonia.Collections;
using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Drawing where the other players started, and fading it as the raid goes on.
/// </summary>
public sealed class SpawnThreatTests
{
    [Fact]
    public void Fades_with_the_raid()
    {
        // Where everybody spawned is the most useful thing on the map at the start of a raid
        // and noise by the middle of it.
        var full = Threat(1);
        var half = Threat(0.5);
        var gone = Threat(0);

        Assert.StartsWith("#FF", full.FillColor, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("#80", half.FillColor, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("#00", gone.FillColor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Draws_the_line_quieter_than_the_marker()
    {
        // The marker is the place; the line is only which way. Eight of them at full strength
        // would be a map of lines rather than a map with lines on it.
        var threat = Threat(1);

        Assert.NotEqual(threat.FillColor, threat.LineColor);
        Assert.EndsWith("E05C5C", threat.LineColor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Is_red_which_nothing_else_on_this_map_is()
    {
        // Extracts are cyan, ochre and sage by faction; group marks are violet; the player is
        // cyan. A place somebody else may be standing right now earns its own colour.
        Assert.EndsWith("E05C5C", Threat(1).FillColor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Draws_no_line_when_the_player_has_not_been_seen()
    {
        var unknown = new SpawnThreatViewModel("Spawn", 10, 10, [], 1);

        Assert.False(unknown.HasLine);
        Assert.True(Threat(1).HasLine);
    }

    private static SpawnThreatViewModel Threat(double strength) =>
        new("Spawn", 10, 10, [new Point(10, 10), new Point(80, 80)], strength);
}
