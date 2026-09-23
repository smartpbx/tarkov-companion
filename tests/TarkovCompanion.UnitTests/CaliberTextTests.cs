using TarkovCompanion.Application.Services.Intelligence;

namespace TarkovCompanion.UnitTests;

/// <summary>The identifiers are every caliber measured in the 2026-09-14 seed catalog.</summary>
public sealed class CaliberTextTests
{
    [Theory]
    [InlineData("Caliber1143x23ACP", ".45 ACP")]
    [InlineData("Caliber127x33", ".50 AE")]
    [InlineData("Caliber127x55", "12.7x55mm")]
    [InlineData("Caliber127x99", ".50 BMG")]
    [InlineData("Caliber12g", "12/70")]
    [InlineData("Caliber20g", "20/70")]
    [InlineData("Caliber20x1mm", "20x1mm")]
    [InlineData("Caliber23x75", "23x75mm")]
    [InlineData("Caliber26x75", "26x75mm")]
    [InlineData("Caliber366TKM", ".366 TKM")]
    [InlineData("Caliber40mmRU", "40mm")]
    [InlineData("Caliber40x46", "40x46mm")]
    [InlineData("Caliber46x30", "4.6x30mm")]
    [InlineData("Caliber545x39", "5.45x39mm")]
    [InlineData("Caliber556x45NATO", "5.56x45mm NATO")]
    [InlineData("Caliber57x28", "5.7x28mm")]
    [InlineData("Caliber58x42", "5.8x42mm")]
    [InlineData("Caliber68x51", "6.8x51mm")]
    [InlineData("Caliber725", "72.5mm")]
    [InlineData("Caliber762x25TT", "7.62x25mm TT")]
    [InlineData("Caliber762x35", ".300 Blackout")]
    [InlineData("Caliber762x39", "7.62x39mm")]
    [InlineData("Caliber762x51", "7.62x51mm")]
    [InlineData("Caliber762x54R", "7.62x54mm R")]
    [InlineData("Caliber784x49", ".308 ME")]
    [InlineData("Caliber86x70", ".338 Lapua Magnum")]
    [InlineData("Caliber93x64", "9.3x64mm")]
    [InlineData("Caliber9x18PM", "9x18mm PM")]
    [InlineData("Caliber9x18PMM", "9x18mm PMM")]
    [InlineData("Caliber9x19PARA", "9x19mm Parabellum")]
    [InlineData("Caliber9x21", "9x21mm")]
    [InlineData("Caliber9x33R", ".357 Magnum")]
    [InlineData("Caliber9x39", "9x39mm")]
    [InlineData("12/70", "12/70")]
    public void Every_seed_catalog_caliber_has_a_player_facing_name(string raw, string shown)
    {
        var described = CaliberText.Describe(raw);

        Assert.Equal(shown, described);
        Assert.False(described.StartsWith("Caliber", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_future_identifier_uses_the_catalog_ammunition_name_without_leaking_the_raw_id()
    {
        Assert.Equal("6.5x50mm", CaliberText.Describe("Caliber65x50Future", "6.5x50mm Future FMJ"));
        Assert.Equal("Unknown caliber", CaliberText.Describe("CaliberFuture"));
    }
}
