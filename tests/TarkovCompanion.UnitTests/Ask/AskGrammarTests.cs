using TarkovCompanion.Application.Services.Ask;

namespace TarkovCompanion.UnitTests.Ask;

public sealed class AskGrammarTests
{
    [Theory]
    [InlineData("what do I need for Gunsmith 5", AskIntent.Needs, "gunsmith 5")]
    [InlineData("What do I need for Lavatory 2?", AskIntent.Needs, "lavatory 2")]
    [InlineData("requirements for Gunsmith Master - Part 5", AskIntent.Needs, "gunsmith master - part 5")]
    [InlineData("Lavatory level 2 needs", AskIntent.Needs, "lavatory level 2")]
    [InlineData("where is Dorms 314 key used", AskIntent.ItemUses, "dorms 314 key")]
    [InlineData("is LEDX needed for anything", AskIntent.ItemUses, "ledx")]
    [InlineData("what's the LEDX used for?", AskIntent.ItemUses, "ledx")]
    [InlineData("what does dorm 314 key open", AskIntent.ItemUses, "dorm 314 key")]
    [InlineData("where can I buy a Gas analyzer", AskIntent.ItemSources, "gas analyzer")]
    [InlineData("who sells salewa", AskIntent.ItemSources, "salewa")]
    [InlineData("how do I get a graphics card", AskIntent.ItemSources, "graphics card")]
    [InlineData("best extract from here", AskIntent.BestExtract, "")]
    [InlineData("nearest exfil?", AskIntent.BestExtract, "")]
    public void Reads_each_shape_and_its_subject(string text, AskIntent intent, string subject)
    {
        var question = AskGrammar.Parse(text);

        Assert.NotNull(question);
        Assert.Equal(intent, question.Intent);
        Assert.Equal(subject, question.Subject);
    }

    [Theory]
    [InlineData("best 5.45 for class 4", "5.45", 4, null)]
    [InlineData("best 7.62×39 under ₽1k", "7.62x39", null, 1000L)]
    [InlineData("what 9x19 ammo goes through level 3 armor", "9x19", 3, null)]
    [InlineData("best 12/70 vs class 2 under 800", "12/70", 2, 800L)]
    public void Reads_ammunition_questions_with_class_and_price(string text, string caliber, int? armorClass, long? cap)
    {
        var question = AskGrammar.Parse(text);

        Assert.NotNull(question);
        Assert.Equal(AskIntent.Ammo, question.Intent);
        Assert.Equal(caliber, question.Subject);
        Assert.Equal(armorClass, question.ArmorClass);
        Assert.Equal(cap, question.PriceCapRoubles);
    }

    [Theory]
    [InlineData("theme")]
    [InlineData("local only")]
    [InlineData("raid")]
    [InlineData("quiet hours")]
    public void A_command_word_is_not_a_question(string text)
    {
        Assert.False(AskGrammar.LooksLikeQuestion(text));
        Assert.Null(AskGrammar.Parse(text));
    }

    [Theory]
    [InlineData("what is the meaning of life")]
    [InlineData("?who is winning")]
    [InlineData("how many raids did I do?")]
    public void An_unknown_question_is_still_a_question_but_has_no_shape(string text)
    {
        Assert.True(AskGrammar.LooksLikeQuestion(text));
        Assert.Null(AskGrammar.Parse(text));
    }

    [Fact]
    public void A_key_number_is_not_read_as_a_caliber()
    {
        // "300" and "366" are calibers; a room number with no ammunition word is an item.
        var question = AskGrammar.Parse("what does dorm 338 key open");

        Assert.NotNull(question);
        Assert.Equal(AskIntent.ItemUses, question.Intent);
    }
}
