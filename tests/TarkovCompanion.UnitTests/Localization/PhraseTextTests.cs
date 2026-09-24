using System.Globalization;
using System.Reflection;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.UnitTests.Localization;

public sealed class PhraseTextTests
{
    [PhraseCodes("Test.Phrase")]
    private enum TestCode
    {
        Plain,
        WithNumber,
        Raids,
        Outer,
    }

    private static readonly IReadOnlyDictionary<string, UiString> Table = new Dictionary<string, UiString>(StringComparer.Ordinal)
    {
        ["Test.Phrase.Plain"] = new("plain words"),
        ["Test.Phrase.WithNumber"] = new("{0:N0} roubles for {1}"),
        ["Test.Phrase.Raids"] = new("{0} raids on {1}", "{0} raid on {1}"),
        ["Test.Phrase.Outer"] = new("because {0}"),
    };

    /// <summary>Every assembly a phrase code may live in: Core, Application and the App.</summary>
    public static IEnumerable<Type> PhraseEnums() =>
        new[] { typeof(Phrase).Assembly, typeof(TarkovCompanion.Application.Services.Maps.SpawnReach).Assembly, typeof(UiText).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsEnum && type.GetCustomAttribute<PhraseCodesAttribute>() is not null);

    [Fact]
    public void Every_phrase_code_has_English_and_every_English_key_under_a_prefix_is_a_code()
    {
        var enums = PhraseEnums().ToArray();
        Assert.NotEmpty(enums);
        foreach (var type in enums)
        {
            var prefix = type.GetCustomAttribute<PhraseCodesAttribute>()!.KeyPrefix + ".";
            var names = Enum.GetNames(type);
            foreach (var name in names)
            {
                Assert.True(
                    UiText.English.TryGetValue(prefix + name, out var english) && !string.IsNullOrWhiteSpace(english.Other),
                    $"{type.Name}.{name} has no English value at {prefix}{name}.");
            }

            var stray = UiText.English.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && !key[prefix.Length..].Contains('.'))
                .Where(key => !names.Contains(key[prefix.Length..], StringComparer.Ordinal))
                .ToArray();
            Assert.True(stray.Length == 0, $"{type.Name}: keys that name no code: {string.Join(", ", stray)}");
        }
    }

    [Fact]
    public void No_two_phrase_enums_share_a_prefix()
    {
        var prefixes = PhraseEnums().Select(type => type.GetCustomAttribute<PhraseCodesAttribute>()!.KeyPrefix).ToArray();
        Assert.Equal(prefixes.Length, prefixes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_phrase_is_said_with_its_numbers_in_the_current_culture_and_nested_phrases_first()
    {
        using var scope = UiText.Scope(new UiStrings(CultureInfo.GetCultureInfo("en"), Table));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("plain words", PhraseText.Say(TestCode.Plain));
            Assert.Equal("12,345 roubles for Salewa", PhraseText.Say(new Phrase(TestCode.WithNumber, 12345L, "Salewa")));
            Assert.Equal("because plain words", PhraseText.Say(new Phrase(TestCode.Outer, TestCode.Plain)));
            Assert.Equal("because 5 roubles for x", PhraseText.Say(new Phrase(TestCode.Outer, new Phrase(TestCode.WithNumber, 5, "x"))));
            Assert.Equal("1 raid on Customs", PhraseText.Say(Phrase.Counted(TestCode.Raids, 1, "Customs")));
            Assert.Equal("3 raids on Customs", PhraseText.Say(Phrase.Counted(TestCode.Raids, 3, "Customs")));

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("12.345 roubles for Salewa", PhraseText.Say(new Phrase(TestCode.WithNumber, 12345L, "Salewa")));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Two_phrases_with_the_same_code_and_arguments_are_equal()
    {
        Assert.Equal(new Phrase(TestCode.WithNumber, 5, "x"), new Phrase(TestCode.WithNumber, 5, "x"));
        Assert.NotEqual(new Phrase(TestCode.WithNumber, 5, "x"), new Phrase(TestCode.WithNumber, 6, "x"));
        Assert.NotEqual(Phrase.Counted(TestCode.Raids, 1), Phrase.Counted(TestCode.Raids, 2));
        Assert.Equal(new Phrase(TestCode.Plain).GetHashCode(), new Phrase(TestCode.Plain).GetHashCode());
    }

    [Fact]
    public void A_code_of_an_unmarked_enum_cannot_be_said()
    {
        Assert.Throws<ArgumentException>(() => PhraseText.Say(DayOfWeek.Monday));
    }
}

public sealed class UnitTextTests
{
    [Theory]
    [InlineData(0L, "₽0")]
    [InlineData(950L, "₽950")]
    [InlineData(1_500L, "₽1.5k")]
    [InlineData(9_999L, "₽10k")]
    [InlineData(45_300L, "₽45k")]
    [InlineData(1_240_000L, "₽1.2M")]
    [InlineData(-12_000L, "₽-12k")]
    public void Short_roubles_read_as_before_in_English(long value, string expected)
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        Assert.Equal(expected, UnitText.RoublesShort(value, CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Short_roubles_can_keep_the_full_number_below_a_floor()
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        var culture = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("₽5,000", UnitText.RoublesShort(5_000, culture, shortFrom: 10_000));
        Assert.Equal("₽12k", UnitText.RoublesShort(12_000, culture, shortFrom: 10_000));
    }

    [Fact]
    public void Units_follow_the_culture_for_digits_and_the_table_for_the_suffix()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");
        using (UiText.Scope(UiText.Create("en", _ => { })))
        {
            Assert.Equal("₽12.345", UnitText.Roubles(12_345, german));
            Assert.Equal("₽1,5k", UnitText.RoublesShort(1_500, german));
            Assert.Equal("1,25 kg", UnitText.Kilograms(1.25, german));
            Assert.Equal("12k", UnitText.Thousands(12, CultureInfo.GetCultureInfo("en-US")));
        }

        using (UiText.Scope(UiText.Create(PseudoLocale.Name, _ => { })))
        {
            var pseudo = UnitText.Kilograms(2, CultureInfo.GetCultureInfo("en-US"));
            Assert.Contains("2", pseudo, StringComparison.Ordinal);
            Assert.NotEqual("2 kg", pseudo);
        }
    }
}
