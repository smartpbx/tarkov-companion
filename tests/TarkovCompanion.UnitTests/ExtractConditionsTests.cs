using System.Text.Json;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What an exit asks of you before it will let you out.
/// </summary>
/// <remarks>
/// Clayton: "clicking an extract should pull up a picture of it and maybe any important
/// instructions for it too". The picture has no source. The instructions have been in the
/// synced payload the whole time, and all that was read from it was the faction.
/// </remarks>
public sealed class ExtractConditionsTests
{
    private const string Roubles = "5449016a4bdc2d6f028b456f";

    [Theory]
    [InlineData("pmc", "PMC only")]
    [InlineData("scav", "Scav only")]
    [InlineData("shared", "Either side")]
    public void WhoMayTakeIt(string faction, string expected) =>
        Assert.Equal(expected, Describe($$"""{"faction":"{{faction}}"}"""));

    /// <summary>
    /// A switch is the difference between an exit and a trip.
    /// </summary>
    [Fact]
    public void AnExitBehindASwitchSaysSo() =>
        Assert.Equal(
            "Scav only · Needs a switch",
            Describe("""{"faction":"scav","switches":["ae4bdfc1fc5b30100701158b56ae4d20840e0550"]}"""));

    [Fact]
    public void TwoSwitchesAreCounted() =>
        Assert.Equal("Needs 2 switches", Describe("""{"switches":["a","b"]}"""));

    /// <summary>The payload carries both forms and they say the same thing.</summary>
    [Fact]
    public void TheSingularSwitchFieldCountsToo() =>
        Assert.Equal("Needs a switch", Describe("""{"switch":"ae4bdfc1"}"""));

    /// <summary>
    /// Money is a decision about whether it is worth it; a key is a decision about whether
    /// you have one, and the two have to read differently.
    /// </summary>
    [Fact]
    public void AVehicleExtractIsPricedInMoney() =>
        Assert.Equal(
            "PMC only · Costs 20,000 ₽",
            Describe(
                $$"""{"faction":"pmc","transferItem":{"item":"{{Roubles}}","count":20000}}""",
                id => id == Roubles ? "Roubles" : null));

    [Fact]
    public void ASecretExitNamesWhatItWants() =>
        Assert.Equal(
            "Needs Bunker key",
            Describe(
                """{"transferItem":{"item":"key-1","count":1}}""",
                id => id == "key-1" ? "Bunker key" : null));

    /// <summary>An item the catalog has never synced still costs something.</summary>
    [Fact]
    public void AnUnknownItemIsStillACost() =>
        Assert.Equal("Needs an item", Describe("""{"transferItem":{"item":"key-1","count":1}}"""));

    /// <summary>
    /// An exit with nothing to say gets nothing, because "no conditions" and "we did not
    /// look" read identically and only one of them is true.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"name":"RUAF Roadblock","position":{"x":1,"y":2,"z":3}}""")]
    public void AnExitWithNoConditionsSaysNothing(string json) =>
        Assert.Null(Describe(json));

    [Fact]
    public void TheItemsAnExitWantsCanBeLookedUpFirst()
    {
        using var document = JsonDocument.Parse("""{"transferItem":{"item":"key-1","count":1}}""");

        Assert.Equal(["key-1"], ExtractConditions.TransferItemIds(document.RootElement).ToArray());
    }

    [Fact]
    public void AnExitThatWantsNothingHasNothingToLookUp()
    {
        using var document = JsonDocument.Parse("""{"faction":"pmc"}""");

        Assert.Empty(ExtractConditions.TransferItemIds(document.RootElement));
    }

    private static string? Describe(string json, Func<string, string?>? itemName = null)
    {
        using var document = JsonDocument.Parse(json);
        return ExtractConditions.Describe(document.RootElement, itemName);
    }
}
