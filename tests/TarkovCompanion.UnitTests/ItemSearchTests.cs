using System.Text;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Answering "what is this worth" without the tablet downloading the catalog.
/// </summary>
/// <remarks>
/// #217's step 3. The items file is 16,713,367 bytes, 1,887,826 gzipped, for 5,320 items, so
/// the question travels to the relay rather than the catalog travelling to the sofa.
///
/// Two files, because one has no names in it: every item's <c>name</c> is the token
/// <c>"{id} Name"</c>, and <c>items_en</c> is what turns it into a name. All 5,320 resolve
/// through it.
/// </remarks>
public sealed class ItemSearchTests
{
    [Fact]
    public void NamesAreResolvedThroughTheTranslationFile()
    {
        var index = ItemSearch.Build(Bytes(Items), Bytes(Names));

        Assert.Contains(index, item => item.Name == "Salewa first aid kit" && item.ShortName == "Salewa");
    }

    [Fact]
    public void AnItemWhoseNameDoesNotResolveKeepsItsToken()
    {
        // A row reading "5447a9cd… Name" is a visible gap somebody can report. A dropped row is
        // a search that quietly does not work.
        var index = ItemSearch.Build(Bytes(Items), Bytes("{}"));

        Assert.Contains(index, item => item.Name == "item-salewa Name");
    }

    [Fact]
    public void WithNoTranslationFileAtAllItStillIndexes()
    {
        Assert.NotEmpty(ItemSearch.Build(Bytes(Items), []));
    }

    [Fact]
    public void EveryWordHasToMatch()
    {
        // The failure the first version had: "factory key" matched nothing, because the item is
        // called "Factory emergency exit key" and the typed text is not in it contiguously.
        var found = Find("factory key");

        Assert.Equal("Factory emergency exit key", Assert.Single(found).Name);
    }

    [Fact]
    public void AnItemMatchingOnlyHalfTheQueryIsNotAnAnswer()
    {
        var found = Find("factory lantern");

        Assert.Empty(found);
    }

    [Fact]
    public void AnExactShortNameWins()
    {
        // The short name is what the game prints in a stash grid, so typing one exactly is
        // somebody naming the thing rather than describing it.
        var found = Find("salewa");

        Assert.Equal("Salewa first aid kit", found[0].Name);
    }

    [Fact]
    public void TheWordBeatsAShortNameThatMerelyStartsWithTheSameLetters()
    {
        // "kit" means the word. KITECO's short name starts with those three letters and is not
        // what anybody was asking for.
        var found = Find("kit");

        Assert.Equal("Sewing kit", found[0].Name);
    }

    [Fact]
    public void OneLetterIsNotASearch()
    {
        // Otherwise the first keystroke asks for the catalog.
        Assert.Empty(Find("k"));
        Assert.Empty(Find(""));
        Assert.Empty(Find(null));
    }

    [Fact]
    public void APriceThatUpstreamDoesNotStateIsNotInvented()
    {
        var found = Find("lantern");

        var item = Assert.Single(found);
        Assert.Null(item.Flea);
        Assert.Equal(400, item.Base);
    }

    [Fact]
    public void AShapeItDoesNotRecogniseCostsTheSearchAndNothingElse()
    {
        Assert.Empty(ItemSearch.Build(Bytes("{\"nothing\":true}"), []));
        Assert.Empty(ItemSearch.Build(Bytes("{\"data\":{}}"), []));
    }

    private static IReadOnlyList<FoundItem> Find(string? query) =>
        ItemSearch.Find(ItemSearch.Build(Bytes(Items), Bytes(Names)), query);

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private const string Items =
        "{\"data\":{\"items\":[" +
        "{\"id\":\"item-salewa\",\"name\":\"item-salewa Name\",\"shortName\":\"item-salewa ShortName\",\"avg24hPrice\":31747,\"basePrice\":15090}," +
        "{\"id\":\"item-factory\",\"name\":\"item-factory Name\",\"shortName\":\"item-factory ShortName\",\"avg24hPrice\":0,\"basePrice\":9000}," +
        "{\"id\":\"item-kiteco\",\"name\":\"item-kiteco Name\",\"shortName\":\"item-kiteco ShortName\",\"basePrice\":500}," +
        "{\"id\":\"item-sewing\",\"name\":\"item-sewing Name\",\"shortName\":\"item-sewing ShortName\",\"avg24hPrice\":29395}," +
        "{\"id\":\"item-lantern\",\"name\":\"item-lantern Name\",\"shortName\":\"item-lantern ShortName\",\"basePrice\":400}" +
        "]}}";

    private const string Names =
        "{\"item-salewa Name\":\"Salewa first aid kit\",\"item-salewa ShortName\":\"Salewa\"," +
        "\"item-factory Name\":\"Factory emergency exit key\",\"item-factory ShortName\":\"Factory\"," +
        "\"item-kiteco Name\":\"Ballistic plate\",\"item-kiteco ShortName\":\"KITECO\"," +
        "\"item-sewing Name\":\"Sewing kit\",\"item-sewing ShortName\":\"Sewing\"," +
        "\"item-lantern Name\":\"Lantern\",\"item-lantern ShortName\":\"Lantern\"}";
}
