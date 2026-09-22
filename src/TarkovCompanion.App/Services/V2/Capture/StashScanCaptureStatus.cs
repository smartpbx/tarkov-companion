namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>
/// Carries capture outcomes that belong on the Stash page rather than only in a diagnostic log.
/// </summary>
/// <remarks>
/// Capture handoff runs independently of the workspace. Keeping the last notice here means a
/// skipped capture is still explained when the player opens Stash after the capture panel closes.
/// </remarks>
public sealed class StashScanCaptureStatus
{
    public const string NoActiveProfileMessage =
        "Stash scan skipped because no profile is active. Select a profile in Setup, then try again.";

    private string? _lastMessage;

    public string? LastMessage => Volatile.Read(ref _lastMessage);

    public event EventHandler? Changed;

    public void ReportNoActiveProfile()
    {
        Volatile.Write(ref _lastMessage, NoActiveProfileMessage);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
