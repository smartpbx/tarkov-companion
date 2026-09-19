namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// What #292 adds to Setup, handed over as one object: the data detail, the About and Data &amp; Privacy
/// pages, and the displays. One hand-off instead of four keeps the shell's constructor to a single new
/// parameter, and keeps them together, which is how the page uses them.
/// </summary>
public sealed class SetupAdminViewModel(
    SetupDataDetailViewModel data,
    SetupInfoPageViewModel about,
    SetupInfoPageViewModel dataPrivacy,
    SetupDisplaysViewModel displays)
{
    public SetupDataDetailViewModel Data { get; } = data ?? throw new ArgumentNullException(nameof(data));

    public SetupInfoPageViewModel About { get; } = about ?? throw new ArgumentNullException(nameof(about));

    public SetupInfoPageViewModel DataPrivacy { get; } = dataPrivacy ?? throw new ArgumentNullException(nameof(dataPrivacy));

    public SetupDisplaysViewModel Displays { get; } = displays ?? throw new ArgumentNullException(nameof(displays));
}
