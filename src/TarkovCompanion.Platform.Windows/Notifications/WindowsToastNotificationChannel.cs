#if WINDOWS
using Microsoft.Toolkit.Uwp.Notifications;
#endif
using TarkovCompanion.Application.Services.Notifications;

namespace TarkovCompanion.Platform.Windows.Notifications;

/// <summary>The narrow, replaceable call into the Windows notification toolkit.</summary>
public interface IWindowsToastSink
{
    bool IsAvailable { get; }

    void Show(string title, string body);
}

/// <summary>Shows the privacy-safe notification through Windows rather than inside Avalonia.</summary>
/// <remarks>
/// The application layer decides whether the player is in a raid or inside quiet hours and hands
/// this type only lock-screen-safe text. Keeping the toolkit behind <see cref="IWindowsToastSink"/>
/// makes the platform seam fixture-testable without trying to open Notification Center in CI.
/// </remarks>
public sealed class WindowsToastNotificationChannel(IWindowsToastSink? sink = null) : INativeNotificationChannel
{
    private readonly IWindowsToastSink _sink = sink ?? new ToolkitWindowsToastSink();

    public bool IsAvailable => _sink.IsAvailable;

    public void Show(LockScreenNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!IsAvailable)
        {
            return;
        }

        try
        {
            _sink.Show(notification.Title, notification.Body);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A disabled Windows notification service is not an application-startup failure. The
            // tray still counts the same request, so it remains visible when the player looks.
        }
    }

    private sealed class ToolkitWindowsToastSink : IWindowsToastSink
    {
#if WINDOWS
        public bool IsAvailable => OperatingSystem.IsWindows();

        public void Show(string title, string body) => new ToastContentBuilder()
            .AddText(title)
            .AddText(body)
            .Show();
#else
        // Linux compiles this target to exercise the platform contract without taking a WinRT
        // dependency. The installed Windows build resolves the target above instead.
        public bool IsAvailable => false;

        public void Show(string title, string body)
        {
        }
#endif
    }
}
