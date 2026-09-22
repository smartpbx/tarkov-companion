using TarkovCompanion.Application.Services.Intelligence;

namespace TarkovCompanion.UnitTests;

/// <summary>Loadout printed "Caliber556x45NATO" in its rows and issues; Ammo already did not.</summary>
public sealed class CaliberTextTests
{
    [Theory]
    [InlineData("Caliber556x45NATO", "556x45NATO")]
    [InlineData("caliber9x19PARA", "9x19PARA")]
    [InlineData("Caliber", "Caliber")]
    [InlineData("12/70", "12/70")]
    public void Only_the_upstream_prefix_is_dropped(string raw, string shown) =>
        Assert.Equal(shown, CaliberText.Describe(raw));
}
