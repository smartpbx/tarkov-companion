using Avalonia;
using Avalonia.Controls;
using TarkovCompanion.Application.Services.Shell;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.Services.Windowing;

public interface IDesktopWindowPlacementController
{
    event EventHandler? CurrentDisplayChanged;

    string? CurrentDisplayId { get; }

    Task MoveToAsync(string displayId, CancellationToken cancellationToken = default);
}

/// <summary>Connects pure per-monitor placement rules to the one desktop window.</summary>
/// <remarks>
/// Windows monitor discovery remains behind <see cref="IMonitorService"/>. This class only sees
/// descriptors and Avalonia window events, which keeps work-area, missing-monitor and DPI rules
/// fixture-testable without loading User32 on the development host.
/// </remarks>
public sealed class DesktopWindowPlacementController(
    IMonitorService monitors,
    IDesktopWindowPlacementStore store) : IDesktopWindowPlacementController, IAsyncDisposable
{
    private readonly Dictionary<string, MonitorWindowPlacement> _placements = new(StringComparer.Ordinal);
    private Window? _window;
    private bool _loaded;
    private bool _applying;
    private string? _activeMonitorKey;
    private long _saveVersion;
    private Task _lastSave = Task.CompletedTask;

    public event EventHandler? CurrentDisplayChanged;

    public string? CurrentDisplayId { get; private set; }

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_window is not null)
        {
            throw new InvalidOperationException("Window placement is already attached.");
        }

        _window = window;
        window.Opened += WindowOpened;
        window.Closing += WindowClosing;
        window.PositionChanged += WindowBoundsChanged;
        window.SizeChanged += WindowBoundsChanged;
        window.ScalingChanged += WindowScalingChanged;
        window.Screens.Changed += ScreensChanged;
    }

    public async Task MoveToAsync(string displayId, CancellationToken cancellationToken = default)
    {
        if (_window is null || string.IsNullOrWhiteSpace(displayId))
        {
            return;
        }

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(true);
        var displays = await monitors.GetDisplaysAsync(cancellationToken).ConfigureAwait(true);
        var targetDisplay = displays.FirstOrDefault(display => display.Id == displayId);
        if (targetDisplay is null)
        {
            return;
        }

        CaptureCurrent(displays);
        var current = _activeMonitorKey is { } key && _placements.TryGetValue(key, out var placement)
            ? placement
            : null;
        var targetKey = DesktopWindowPlacement.MonitorKey(targetDisplay);
        var wanted = _placements.TryGetValue(targetKey, out var remembered)
            ? remembered
            : DesktopWindowPlacement.ForFallback(current, targetDisplay, _window.Width, _window.Height);
        var (frameWidth, frameHeight) = FramePixels(targetDisplay);
        Apply(DesktopWindowPlacement.Restore(wanted, targetDisplay, frameWidth, frameHeight));
        CaptureCurrent(displays);
        QueueSave();
    }

    /// <summary>
    /// [#881 follow-up] The title bar and borders around the client, in the display's pixels;
    /// zero until the window has a frame (it has one by Opened on Windows).
    /// </summary>
    private (double Width, double Height) FramePixels(DisplayDescriptor display)
    {
        if (_window?.FrameSize is not { } frame)
        {
            return (0, 0);
        }

        var client = _window.ClientSize;
        var scale = display.Scale > 0 && double.IsFinite(display.Scale) ? display.Scale : 1;
        return (Math.Max(0, frame.Width - client.Width) * scale, Math.Max(0, frame.Height - client.Height) * scale);
    }

    private async void WindowOpened(object? sender, EventArgs eventArgs) =>
        await ReconcileAsync(restoreRemembered: true).ConfigureAwait(true);

    private async void ScreensChanged(object? sender, EventArgs eventArgs) =>
        await ReconcileAsync(restoreRemembered: false).ConfigureAwait(true);

    private async void WindowScalingChanged(object? sender, EventArgs eventArgs) =>
        await ReconcileAsync(restoreRemembered: false, preservePhysicalSize: true).ConfigureAwait(true);

    private void WindowBoundsChanged(object? sender, EventArgs eventArgs)
    {
        if (_applying || _window is null || !_loaded)
        {
            return;
        }

        CaptureAndSaveAsync();
    }

    private async void CaptureAndSaveAsync()
    {
        try
        {
            var displays = await monitors.GetDisplaysAsync(CancellationToken.None).ConfigureAwait(true);
            CaptureCurrent(displays);
            QueueSave();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Display changes are best effort; the next event or launch retries them.
        }
    }

    private void WindowClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (!_loaded)
        {
            return;
        }

        CaptureAndSaveAsync();
    }

    private async Task ReconcileAsync(bool restoreRemembered, bool preservePhysicalSize = false)
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            await EnsureLoadedAsync(CancellationToken.None).ConfigureAwait(true);
            var displays = await monitors.GetDisplaysAsync(CancellationToken.None).ConfigureAwait(true);
            if (displays.Count == 0)
            {
                return;
            }

            var scale = CurrentScale(displays);
            var current = DesktopWindowPlacement.DisplayAt(
                displays,
                _window.Position.X,
                _window.Position.Y,
                _window.Width * scale,
                _window.Height * scale);
            var rememberedDisplay = restoreRemembered && _activeMonitorKey is { } activeKey
                ? displays.FirstOrDefault(display => DesktopWindowPlacement.MonitorKey(display) == activeKey)
                : null;
            var wantedKey = current is null ? _activeMonitorKey : null;
            var targetDisplay = rememberedDisplay ?? current ?? DesktopWindowPlacement.PreferredDisplay(displays, wantedKey);
            if (targetDisplay is null)
            {
                return;
            }

            var targetKey = DesktopWindowPlacement.MonitorKey(targetDisplay);
            MonitorWindowPlacement placement;
            if (restoreRemembered && _placements.TryGetValue(targetKey, out var remembered))
            {
                placement = remembered;
            }
            else if (current is null)
            {
                var previous = _activeMonitorKey is { } active && _placements.TryGetValue(active, out var stranded)
                    ? stranded
                    : null;
                placement = DesktopWindowPlacement.ForFallback(previous, targetDisplay, _window.Width, _window.Height);
            }
            else if (_activeMonitorKey is { } active && active != targetKey &&
                     _placements.TryGetValue(active, out var previous))
            {
                // Crossing a DPI boundary can make Avalonia change its logical Width before this
                // callback runs. Carry the last physical size across instead of capturing that
                // transient logical rectangle as the new preference.
                placement = DesktopWindowPlacement.ForFallback(previous, targetDisplay, _window.Width, _window.Height);
            }
            else if (preservePhysicalSize && _placements.TryGetValue(targetKey, out var physical))
            {
                placement = physical;
            }
            else
            {
                placement = DesktopWindowPlacement.Capture(
                    targetDisplay,
                    _window.Width,
                    _window.Height,
                    _window.Position.X,
                    _window.Position.Y,
                    _window.WindowState == WindowState.Maximized);
            }

            var (frameWidth, frameHeight) = FramePixels(targetDisplay);
            Apply(DesktopWindowPlacement.Restore(placement, targetDisplay, frameWidth, frameHeight));
            CaptureCurrent(displays);
            QueueSave();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A monitor query must never prevent the shell from opening or remaining usable.
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        var state = await store.GetAsync(cancellationToken).ConfigureAwait(true);
        _placements.Clear();
        foreach (var placement in state.Monitors)
        {
            _placements[placement.MonitorKey] = placement;
        }

        _activeMonitorKey = state.ActiveMonitorKey;
        _loaded = true;
    }

    private void Apply(WindowPlacementTarget target)
    {
        if (_window is null)
        {
            return;
        }

        _applying = true;
        try
        {
            _window.WindowState = WindowState.Normal;
            _window.Width = target.Width;
            _window.Height = target.Height;
            _window.Position = new PixelPoint(target.Left, target.Top);
            if (target.IsMaximized)
            {
                _window.WindowState = WindowState.Maximized;
            }

            _activeMonitorKey = target.MonitorKey;
            SetCurrentDisplay(target.MonitorId);
        }
        finally
        {
            _applying = false;
        }
    }

    private void CaptureCurrent(IReadOnlyList<DisplayDescriptor> displays)
    {
        if (_window is null || displays.Count == 0)
        {
            return;
        }

        var scale = CurrentScale(displays);
        var display = DesktopWindowPlacement.DisplayAt(
            displays,
            _window.Position.X,
            _window.Position.Y,
            _window.Width * scale,
            _window.Height * scale);
        if (display is null)
        {
            return;
        }

        var key = DesktopWindowPlacement.MonitorKey(display);
        if (_window.WindowState == WindowState.Maximized && _placements.TryGetValue(key, out var normal))
        {
            _placements[key] = normal with { IsMaximized = true };
        }
        else
        {
            _placements[key] = DesktopWindowPlacement.Capture(
                display,
                _window.Width,
                _window.Height,
                _window.Position.X,
                _window.Position.Y,
                _window.WindowState == WindowState.Maximized);
        }

        _activeMonitorKey = key;
        SetCurrentDisplay(display.Id);
    }

    private double CurrentScale(IReadOnlyList<DisplayDescriptor> displays)
    {
        if (_window is null)
        {
            return 1;
        }

        var atOrigin = displays.FirstOrDefault(display =>
            _window.Position.X >= display.Bounds.X &&
            _window.Position.X < display.Bounds.X + display.Bounds.Width &&
            _window.Position.Y >= display.Bounds.Y &&
            _window.Position.Y < display.Bounds.Y + display.Bounds.Height);
        return atOrigin is { Scale: > 0 } ? atOrigin.Scale : _window.RenderScaling;
    }

    private void SetCurrentDisplay(string? displayId)
    {
        if (CurrentDisplayId == displayId)
        {
            return;
        }

        CurrentDisplayId = displayId;
        CurrentDisplayChanged?.Invoke(this, EventArgs.Empty);
    }

    private void QueueSave()
    {
        var version = Interlocked.Increment(ref _saveVersion);
        _lastSave = SaveAfterDelayAsync(version);
    }

    private async Task SaveAfterDelayAsync(long version)
    {
        await Task.Delay(200).ConfigureAwait(false);
        if (version != Interlocked.Read(ref _saveVersion))
        {
            return;
        }

        await store.SaveAsync(Snapshot(), CancellationToken.None).ConfigureAwait(false);
    }

    private DesktopWindowPlacementState Snapshot() =>
        new(_activeMonitorKey, _placements.Values.ToArray());

    public async ValueTask DisposeAsync()
    {
        if (_window is { } window)
        {
            window.Opened -= WindowOpened;
            window.Closing -= WindowClosing;
            window.PositionChanged -= WindowBoundsChanged;
            window.SizeChanged -= WindowBoundsChanged;
            window.ScalingChanged -= WindowScalingChanged;
            window.Screens.Changed -= ScreensChanged;
        }

        Interlocked.Increment(ref _saveVersion);
        if (_loaded)
        {
            await store.SaveAsync(Snapshot(), CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await _lastSave.ConfigureAwait(false);
        }
    }
}
