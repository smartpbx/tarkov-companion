using System.Globalization;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One language Setup offers: a culture that has a string table of its own.</summary>
public sealed record SetupLanguageChoice(string CultureName, string Label);

/// <summary>
/// Setup › Game &amp; Profile's interface language (#314): the cultures this build ships a table for,
/// written to Config/interface-language.json and taken up at the next start.
/// </summary>
/// <remarks>
/// Only shipped tables are offered, because a culture without one would read English and look
/// like the picker did nothing. The pseudo-locale is a developer's tool for finding untranslated
/// and clipped copy, so it is offered in developer mode only. Views read their words once when
/// they load (see <see cref="UiText"/>), which is why the choice says "Restart to apply" rather
/// than pretending to switch in place.
/// </remarks>
public sealed class SetupLanguageViewModel : BindableViewModel
{
    private readonly string _configDirectory;
    private readonly string _running;
    private string _status = string.Empty;

    public SetupLanguageViewModel(string configDirectory, bool developerMode)
        : this(configDirectory, Offered(UiText.Shipped(), developerMode), UiText.CultureName)
    {
    }

    public SetupLanguageViewModel(string configDirectory, IReadOnlyList<SetupLanguageChoice> offered, string running)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentNullException.ThrowIfNull(offered);
        _configDirectory = configDirectory;
        _running = running ?? string.Empty;
        Choices = [.. offered.Select(choice => new V2AppearanceChoiceViewModel(
            "language-" + choice.CultureName.ToLowerInvariant(),
            () => choice.Label,
            () => Choose(choice.CultureName)))];
        CultureNames = [.. offered.Select(choice => choice.CultureName)];
        MarkCurrent(Chosen(UiCulturePreference.ReadFile(configDirectory), _running));
    }

    public string Heading => SetupText.LanguageHeading;

    public string Hint => SetupText.LanguageHint;

    public IReadOnlyList<V2AppearanceChoiceViewModel> Choices { get; }

    private IReadOnlyList<string> CultureNames { get; }

    /// <summary>"Restart to apply" once the saved language is not the one running; empty otherwise.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => Status.Length > 0;

    /// <summary>The cultures with a table, English first, the pseudo-locale last and only for a developer.</summary>
    public static IReadOnlyList<SetupLanguageChoice> Offered(IEnumerable<string> shipped, bool developerMode)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        var cultures = shipped
            .Where(name => !string.IsNullOrWhiteSpace(name) && !PseudoLocale.Is(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name.Equals("en", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new SetupLanguageChoice(name, NativeName(name)))
            .ToList();
        if (developerMode)
        {
            cultures.Add(new SetupLanguageChoice(PseudoLocale.Name, SetupText.LanguagePseudo));
        }

        return cultures;
    }

    private void Choose(string cultureName)
    {
        try
        {
            UiCulturePreference.Write(_configDirectory, cultureName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("warning/UiText", $"The interface language could not be saved: {exception.Message}");
            Status = SetupText.LanguageNotSaved;
            return;
        }

        MarkCurrent(cultureName);
    }

    private void MarkCurrent(string cultureName)
    {
        foreach (var (choice, name) in Choices.Zip(CultureNames))
        {
            choice.IsCurrent = string.Equals(name, cultureName, StringComparison.OrdinalIgnoreCase);
        }

        Status = string.Equals(cultureName, _running, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : SetupText.LanguageRestart;
    }

    /// <summary>The saved choice, else the one running (a system culture with no table runs English).</summary>
    private string Chosen(string? saved, string running)
    {
        if (!string.IsNullOrWhiteSpace(saved)
            && CultureNames.FirstOrDefault(name => string.Equals(name, saved.Trim(), StringComparison.OrdinalIgnoreCase)) is { } match)
        {
            return match;
        }

        return CultureNames.FirstOrDefault(name => string.Equals(name, running, StringComparison.OrdinalIgnoreCase)) ?? "en";
    }

    private static string NativeName(string cultureName)
    {
        try
        {
            var native = CultureInfo.GetCultureInfo(cultureName).NativeName;
            return native.Length > 0 ? char.ToUpper(native[0], CultureInfo.GetCultureInfo(cultureName)) + native[1..] : cultureName;
        }
        catch (CultureNotFoundException)
        {
            return cultureName;
        }
    }
}
