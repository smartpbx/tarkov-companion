using System;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.Services.V2;

/// <summary>A camera the desktop's map is easing toward: plan centre and zoom.</summary>
public readonly record struct EasedCamera(double X, double Y, double Zoom);

/// <summary>
/// Glides the desktop's map to where a paired tablet in Control has put it, instead of jumping.
/// </summary>
/// <remarks>
/// [#604] A tablet streams its moves about sixteen times a second. Each one set straight on the
/// camera is a small jump, and a person watching the desk sees a stutter at that rate. Each new
/// target restarts a short ease from wherever the camera is now, so a steady drag reads as one
/// smooth movement a little behind the finger, and the last target is always where it stops.
/// Runs on the UI thread only; a step is one camera request, the same the desktop's own wheel makes.
/// </remarks>
public sealed class DesktopViewportEase : IDisposable
{
    /// <summary>How long one ease takes. Short enough to feel attached to the finger.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(120);

    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    private readonly DispatcherTimer _timer;
    private MapSceneRendererViewModel? _renderer;
    private EasedCamera _from;
    private EasedCamera _to;
    private DateTimeOffset _startedUtc;
    private readonly TimeProvider _clock;

    public DesktopViewportEase(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _timer = new DispatcherTimer(Frame, DispatcherPriority.Render, (_, _) => Tick());
    }

    /// <summary>Whether a glide is under way, so the caller does not echo its steps back out.</summary>
    public bool IsMoving => _timer.IsEnabled;

    /// <summary>Starts (or restarts) a glide from the camera now showing to <paramref name="target"/>.</summary>
    public void MoveTo(MapSceneRendererViewModel renderer, EasedCamera target)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        var camera = renderer.Scene.View.Camera;
        _renderer = renderer;
        _from = new EasedCamera(camera.CenterX, camera.CenterY, camera.Zoom);
        _to = target;
        _startedUtc = _clock.GetUtcNow();
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }

        Tick();
    }

    private void Tick()
    {
        if (_renderer is not { } renderer)
        {
            _timer.Stop();
            return;
        }

        var fraction = (_clock.GetUtcNow() - _startedUtc) / Duration;
        if (fraction < 0)
        {
            // [#799] The clock was set back mid-glide; finish it rather than tick for hours.
            fraction = 1;
        }

        var camera = At(_from, _to, fraction);
        renderer.ShowCamera(new MapScenePoint(camera.X, camera.Y), camera.Zoom);
        if (fraction >= 1)
        {
            _timer.Stop();
        }
    }

    /// <summary>
    /// Where the glide is at <paramref name="fraction"/> of its way: eased out (fast, then
    /// settling), zoom in proportion rather than in steps of the same size so a zoom-in and a
    /// zoom-out feel alike, and exactly the target from 1 on.
    /// </summary>
    public static EasedCamera At(EasedCamera from, EasedCamera to, double fraction)
    {
        if (!double.IsFinite(fraction) || fraction >= 1)
        {
            return to;
        }

        var t = Math.Max(0, fraction);
        var eased = 1 - Math.Pow(1 - t, 3);
        var zoom = from.Zoom > 0 && to.Zoom > 0
            ? from.Zoom * Math.Pow(to.Zoom / from.Zoom, eased)
            : to.Zoom;
        return new EasedCamera(
            from.X + ((to.X - from.X) * eased),
            from.Y + ((to.Y - from.Y) * eased),
            zoom);
    }

    public void Dispose()
    {
        _timer.Stop();
        _renderer = null;
    }
}
