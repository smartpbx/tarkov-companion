using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Application.Services.Input;

/// <summary>
/// Stands in for the platform hotkey service where global shortcuts do not exist.
/// </summary>
/// <remarks>
/// Registering the real service only on Windows would make every consumer optional. Failing
/// loudly here instead keeps the composition identical on both platforms and lets the caller
/// report an honest reason.
/// </remarks>
public sealed class UnavailableGlobalHotkeyService : IGlobalHotkeyService
{
    public event EventHandler? Pressed
    {
        add { }
        remove { }
    }

    public Task RegisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        return Task.FromException(
            new PlatformNotSupportedException("Global shortcuts are available on Windows only."));
    }

    public Task UnregisterAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
