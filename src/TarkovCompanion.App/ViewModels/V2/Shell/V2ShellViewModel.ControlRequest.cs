namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// Attaches <see cref="ControlRequestPromptViewModel"/> to the shell, the smallest edit this
/// needed to make a tablet's control request visible wherever the player is (#562).
/// </summary>
/// <remarks>
/// A separate partial rather than a change to the main constructor: <c>_companionPairing</c> is
/// already set (or left null, under the render/test constructor) by the time anything reads this
/// property, so there is nothing to wire at construction — only a class to attach, the same shape
/// <c>V2ShellViewModel.StartupFaults.cs</c> already uses for <see cref="LoadFaultNoticeViewModel"/>.
/// </remarks>
public sealed partial class V2ShellViewModel
{
    private ControlRequestPromptViewModel? _controlRequestPrompt;

    /// <summary>Visible on every V2 page while a paired tablet is asking to drive this desktop.</summary>
    public ControlRequestPromptViewModel ControlRequestPrompt =>
        _controlRequestPrompt ??= new ControlRequestPromptViewModel(_companionPairing);
}
