using System.Globalization;
using System.Reflection;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Localization;

public sealed class UiTextTests
{
    private static readonly IReadOnlyDictionary<string, UiString> English = new Dictionary<string, UiString>(StringComparer.Ordinal)
    {
        ["Greeting"] = new("Hello"),
        ["Farewell"] = new("Goodbye"),
        ["Raids"] = new("{0} raids", "{0} raid"),
        ["Value"] = new("{0:N0} roubles on {1}"),
    };

    [Fact]
    public void A_key_the_culture_lacks_shows_the_English_and_logs_once()
    {
        var log = new List<string>();
        var german = new Dictionary<string, UiString>(StringComparer.Ordinal) { ["Greeting"] = new("Hallo") };
        var strings = new UiStrings(CultureInfo.GetCultureInfo("de"), English, german, log.Add);

        Assert.Equal("Hallo", strings.Get("Greeting"));
        Assert.Equal("Goodbye", strings.Get("Farewell"));
        Assert.Equal("Goodbye", strings.Get("Farewell"));

        var entry = Assert.Single(log);
        Assert.Contains("'Farewell'", entry, StringComparison.Ordinal);
        Assert.Contains("de", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_English_lacks_shows_the_key_rather_than_nothing_and_logs_once()
    {
        var log = new List<string>();
        var strings = new UiStrings(CultureInfo.GetCultureInfo("en"), English, log: log.Add);

        Assert.Equal("Debrief.Nope", strings.Get("Debrief.Nope"));
        Assert.Equal("Debrief.Nope", strings.Format("Debrief.Nope", 1));

        Assert.Single(log);
    }

    [Theory]
    [InlineData("en", 0, "0 raids")]
    [InlineData("en", 1, "1 raid")]
    [InlineData("en", 2, "2 raids")]
    [InlineData("fr", 0, "0 raid")]
    [InlineData("fr", 1, "1 raid")]
    [InlineData("fr", 2, "2 raids")]
    [InlineData("ja", 1, "1 raids")]
    public void A_count_picks_one_or_other_by_the_culture_s_rule(string culture, long count, string expected)
    {
        var strings = new UiStrings(CultureInfo.GetCultureInfo(culture), English, English);

        Assert.Equal(expected, strings.Plural("Raids", count));
    }

    [Fact]
    public void Arguments_are_formatted_in_the_current_culture_not_the_interface_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var strings = new UiStrings(CultureInfo.GetCultureInfo("de"), English, English);

            Assert.Equal("450,000 roubles on Customs", strings.Format("Value", 450_000, "Customs"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_pseudo_locale_accents_lengthens_and_brackets_but_keeps_placeholders()
    {
        var pseudo = PseudoLocale.Transform("Deleted {0:N0} raids before {1}.");

        Assert.StartsWith("[Ďéľéţéď {0:N0} ŕáíďš ƀéƒöŕé {1}.", pseudo, StringComparison.Ordinal);
        Assert.EndsWith("]", pseudo, StringComparison.Ordinal);
        // Letters and punctuation outside placeholders: 24; a third more is at least 8.
        Assert.True(pseudo.Length >= "Deleted {0:N0} raids before {1}.".Length + 2 + 8, pseudo);
        Assert.StartsWith(
            "[Ďéľéţéď 3 ŕáíďš ƀéƒöŕé Tuesday.",
            string.Format(CultureInfo.InvariantCulture, pseudo, 3, "Tuesday"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_pseudo_locale_leaves_escaped_braces_as_literals()
    {
        Assert.StartsWith("[{{ö}}", PseudoLocale.Transform("{{o}}"), StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_the_pseudo_locale_transforms_every_English_entry()
    {
        var strings = UiText.Create(PseudoLocale.Name, _ => { });

        Assert.Equal(PseudoLocale.Name, strings.Name);
        Assert.Equal(PseudoLocale.Transform(UiText.English["Debrief.RaidHistory"].Other), strings.Get("Debrief.RaidHistory"));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-GB")]
    [InlineData("")]
    [InlineData("xx-not-a-culture")]
    public void A_culture_with_no_table_of_its_own_reads_English(string culture)
    {
        var log = new List<string>();
        var strings = UiText.Create(culture, log.Add);

        Assert.Equal(UiText.English["Debrief.RaidHistory"].Other, strings.Get("Debrief.RaidHistory"));
        Assert.DoesNotContain(log, entry => entry.Contains("has no", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_shipped_entry_has_English_text_and_every_translation_only_translates_English_keys()
    {
        Assert.Contains("en", UiText.Shipped());
        Assert.NotEmpty(UiText.English);
        Assert.All(UiText.English, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.Other), entry.Key);
            Assert.True(entry.Value.One is null || entry.Value.One.Length > 0, entry.Key);
        });

        foreach (var culture in UiText.Shipped().Where(name => name != "en"))
        {
            Assert.All(UiText.Load(culture)!.Keys, key => Assert.True(UiText.English.ContainsKey(key), $"{culture}: {key}"));
        }
    }

    [Fact]
    public void Every_Debrief_label_the_accessor_offers_has_an_English_value()
    {
        var log = new List<string>();
        using var scope = UiText.Scope(UiText.Create("en", log.Add));

        var members = typeof(DebriefText).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(string))
            .ToArray();
        Assert.True(members.Length > 150, $"only {members.Length} accessors");
        foreach (var member in members)
        {
            var arguments = member.GetParameters().Select(parameter => parameter.ParameterType switch
            {
                var type when type == typeof(string) => (object)"x",
                var type when type == typeof(RaidFactKind) => RaidFactKind.Manual,
                var type when type == typeof(object) => "x",
                var type => Activator.CreateInstance(type)!,
            }).ToArray();
            var text = (string)member.Invoke(null, arguments)!;
            Assert.False(string.IsNullOrWhiteSpace(text), member.Name);
        }

        Assert.Empty(log);
    }

    // [#314] Plan and Team followed Debrief onto the table. An accessor whose key is missing
    // from en.json would show the key itself in the running app; this catches it here instead.
    [Theory]
    [InlineData(typeof(PlanText), 150)]
    [InlineData(typeof(TeamText), 60)]
    [InlineData(typeof(IntelText), 300)]
    public void Every_label_a_workspace_accessor_offers_has_an_English_value(Type accessor, int atLeast)
    {
        var log = new List<string>();
        using var scope = UiText.Scope(UiText.Create("en", log.Add));

        var members = accessor.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(string))
            .ToArray();
        Assert.True(members.Length > atLeast, $"only {members.Length} accessors");
        foreach (var member in members)
        {
            var arguments = member.GetParameters().Select(parameter => parameter.ParameterType switch
            {
                var type when type == typeof(string) || type == typeof(object) => (object)"x",
                var type when type.IsEnum => Enum.GetValues(type).GetValue(0)!,
                var type => Activator.CreateInstance(type)!,
            }).ToArray();
            var text = (string)member.Invoke(null, arguments)!;
            Assert.False(string.IsNullOrWhiteSpace(text), member.Name);
        }

        Assert.Empty(log);
    }

    [Fact]
    public void A_scoped_table_does_not_leak_into_another_flow()
    {
        var pseudo = UiText.Create(PseudoLocale.Name, _ => { });
        using (UiText.Scope(pseudo))
        {
            Assert.StartsWith("[", DebriefText.RaidHistory, StringComparison.Ordinal);
        }

        Assert.NotEqual(PseudoLocale.Name, UiText.Current.Name);
    }
}
