namespace TarkovCompanion.App.ViewModels.V2.Setup;

public sealed partial class V2SetupWorkspaceViewModel
{
    /// <summary>[#314] Game &amp; Profile's interface language, or null in a shell built without a Config folder.</summary>
    public SetupLanguageViewModel? Language { get; private set; }

    public bool HasLanguage => Language is not null;

    public void AttachLanguage(SetupLanguageViewModel language)
    {
        Language = language ?? throw new ArgumentNullException(nameof(language));
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(HasLanguage));
    }
}
