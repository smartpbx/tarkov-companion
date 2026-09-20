using TarkovCompanion.Application.Services.Intelligence;

namespace TarkovCompanion.UnitTests.Intel;

/// <summary>
/// The tokens here are the 2026-09-14 catalog's own, not invented ones: a rule for comparing
/// calibers is only as good as the strings it is tried on.
/// </summary>
public sealed class CaliberKeyTests
{
    /// <summary>Every caliber a round carries in the real catalog: 31 of them.</summary>
    private static readonly string[] RealAmmunitionCalibers =
    [
        "Caliber1143x23ACP", "Caliber127x33", "Caliber127x55", "Caliber127x99", "Caliber12g", "Caliber20g",
        "Caliber20x1mm", "Caliber23x75", "Caliber26x75", "Caliber366TKM", "Caliber40mmRU", "Caliber40x46",
        "Caliber46x30", "Caliber545x39", "Caliber556x45NATO", "Caliber57x28", "Caliber58x42", "Caliber68x51",
        "Caliber762x25TT", "Caliber762x35", "Caliber762x39", "Caliber762x51", "Caliber762x54R", "Caliber784x49",
        "Caliber86x70", "Caliber93x64", "Caliber9x18PM", "Caliber9x19PARA", "Caliber9x21", "Caliber9x33R",
        "Caliber9x39",
    ];

    [Fact]
    public void The_Klin_fires_the_rounds_it_fires()
    {
        // The PP-9 Klin is the one item written Caliber9x18PMM; all fourteen 9x18 rounds, the one
        // named "PMM PstM" among them, are Caliber9x18PM. It was told it could fire none of them.
        Assert.True(CaliberKey.Matches("Caliber9x18PMM", "Caliber9x18PM"));
        Assert.True(CaliberKey.Matches("Caliber9x18PM", "Caliber9x18PMM"));
    }

    [Fact]
    public void No_two_real_calibers_become_one()
    {
        // The danger in any folding: 7.62x39, x51 and x54R, or 9x18, 9x19, 9x21 and 9x39, read
        // as the same thing, and a rifle is told it may chamber another rifle's round.
        var keys = RealAmmunitionCalibers.Select(CaliberKey.Of).ToArray();

        Assert.Equal(RealAmmunitionCalibers.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("Caliber545x39", "Caliber556x45NATO")]
    [InlineData("Caliber762x39", "Caliber762x51")]
    [InlineData("Caliber762x51", "Caliber762x54R")]
    [InlineData("Caliber9x18PM", "Caliber9x19PARA")]
    [InlineData("Caliber9x18PMM", "Caliber9x19PARA")]
    [InlineData("Caliber9x19PARA", "Caliber9x21")]
    [InlineData("Caliber12g", "Caliber20g")]
    public void Different_calibers_still_do_not_match(string weapon, string ammunition)
    {
        Assert.False(CaliberKey.Matches(weapon, ammunition));
    }

    [Theory]
    [InlineData("Caliber545x39", "caliber545x39")]
    [InlineData("Caliber545x39", "545x39")]
    [InlineData("Caliber762x54R", "7.62x54R")]
    [InlineData("Caliber762x54R", " 7.62 x 54 r ")]
    [InlineData("Caliber9x18PM", "9x18-PM")]
    public void The_same_caliber_written_another_way_matches(string catalog, string written)
    {
        Assert.True(CaliberKey.Matches(catalog, written));
    }

    [Fact]
    public void Every_real_caliber_matches_itself()
    {
        Assert.All(RealAmmunitionCalibers, caliber => Assert.True(CaliberKey.Matches(caliber, caliber)));
    }
}
