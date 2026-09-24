using System.Text.Json;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.UnitTests.Localization;

public sealed class SetupLanguageViewModelTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "tc-language-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_config))
        {
            Directory.Delete(_config, recursive: true);
        }
    }

    [Fact]
    public void A_player_is_offered_only_the_languages_with_a_table()
    {
        var offered = SetupLanguageViewModel.Offered(["de", "en", PseudoLocale.Name], developerMode: false);

        Assert.Equal(["en", "de"], offered.Select(choice => choice.CultureName));
        Assert.Equal("English", offered[0].Label);
    }

    [Fact]
    public void A_developer_is_also_offered_the_pseudo_locale_last()
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));

        var offered = SetupLanguageViewModel.Offered(["en"], developerMode: true);

        Assert.Equal(["en", PseudoLocale.Name], offered.Select(choice => choice.CultureName));
        Assert.Equal("Pseudo (for testing)", offered[^1].Label);
    }

    [Fact]
    public void This_build_ships_English()
    {
        Assert.Contains("en", UiText.Shipped());
        Assert.Contains(SetupLanguageViewModel.Offered(UiText.Shipped(), developerMode: false), choice => choice.CultureName == "en");
    }

    [Fact]
    public void Choosing_a_language_writes_the_preference_file_and_asks_for_a_restart()
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        var language = new SetupLanguageViewModel(_config, SetupLanguageViewModel.Offered(["en"], developerMode: true), running: "en");
        Assert.False(language.HasStatus);
        Assert.True(language.Choices[0].IsCurrent);

        language.Choices[1].ChooseCommand.Execute(null);

        using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_config, UiCulturePreference.FileName))))
        {
            Assert.Equal(PseudoLocale.Name, document.RootElement.GetProperty("culture").GetString());
        }

        Assert.Equal(PseudoLocale.Name, UiCulturePreference.ReadFile(_config));
        Assert.True(language.Choices[1].IsCurrent);
        Assert.False(language.Choices[0].IsCurrent);
        Assert.Equal("Restart to apply", language.Status);

        language.Choices[0].ChooseCommand.Execute(null);

        Assert.Equal("en", UiCulturePreference.ReadFile(_config));
        Assert.False(language.HasStatus);
    }

    [Fact]
    public void A_saved_choice_that_is_not_running_yet_still_shows_as_chosen()
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        UiCulturePreference.Write(_config, PseudoLocale.Name);

        var language = new SetupLanguageViewModel(_config, SetupLanguageViewModel.Offered(["en"], developerMode: true), running: "en");

        Assert.True(language.Choices[1].IsCurrent);
        Assert.Equal("Restart to apply", language.Status);
    }
}
