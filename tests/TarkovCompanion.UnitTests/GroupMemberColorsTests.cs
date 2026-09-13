using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The colour that joins a name in the group panel to a dot on the map.
/// </summary>
/// <remarks>
/// Every member used to be drawn in the same ochre, so a map with three of them on it said
/// where three people were without saying which was which.
/// </remarks>
public sealed class GroupMemberColorsTests
{
    [Fact]
    public void EverybodyInAGroupGetsADifferentColour()
    {
        var assigned = GroupMemberColors.Assign(["Nate", "MaxGooner", "Clayton", "Rook", "Vos"]);

        Assert.Equal(5, assigned.Count);
        Assert.Equal(5, assigned.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(assigned.Values, color => Assert.Contains(color, GroupMemberColors.Palette));
    }

    /// <summary>
    /// The same name is the same colour on every machine and every launch.
    /// </summary>
    /// <remarks>
    /// The framework's string hash is randomised per process, so taking it would have made
    /// somebody a different colour on each restart and a different colour on each machine.
    /// Both break the only thing the colour is for.
    /// </remarks>
    [Fact]
    public void AColourDoesNotDependOnTheOrderNamesArriveIn()
    {
        var first = GroupMemberColors.Assign(["Nate", "MaxGooner", "Clayton"]);
        var second = GroupMemberColors.Assign(["Clayton", "Nate", "MaxGooner"]);

        Assert.Equal(first.OrderBy(pair => pair.Key, StringComparer.Ordinal), second.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void MoreMembersThanColoursDoesNotThrowOrLoseAnybody()
    {
        var names = Enumerable.Range(0, GroupMemberColors.Palette.Count + 4)
            .Select(index => "Member" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        var assigned = GroupMemberColors.Assign(names);

        Assert.Equal(names.Length, assigned.Count);
        Assert.All(names, name => Assert.True(assigned.ContainsKey(name)));
    }

    [Fact]
    public void UnnamedMembersAreLeftOutRatherThanGivenASlot()
    {
        var assigned = GroupMemberColors.Assign(["Nate", "", "  ", "Rook"]);

        Assert.Equal(2, assigned.Count);
        Assert.Equal(new[] { "Nate", "Rook" }, assigned.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Cyan is the player's own marker, so nobody else may be it.</summary>
    [Fact]
    public void NobodyIsTheColourOfYourOwnMarker() =>
        Assert.DoesNotContain("56B8C6", GroupMemberColors.Palette, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AlphaIsPrefixedSoAStaleMarkerFadesRatherThanChangesHue() =>
        Assert.Equal("#80E0B45C", GroupMemberColors.WithAlpha("E0B45C", "80"));
}
