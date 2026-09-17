namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>
/// Decides whether the map's floating chrome should be shown or faded, without touching a
/// window, a timer or a pointer.
/// </summary>
/// <remarks>
/// On a second monitor the mouse is usually in the game, not on the companion, so the toolbars
/// and expanders sit on screen doing nothing but covering the map underneath them. Fading them
/// out after a few idle seconds and bringing them back the moment the pointer returns is the
/// ask; a player mid-keystroke in a dropdown or reading the Layers key is not idle just because
/// the mouse has not moved.
///
/// The three inputs — the pointer being over the map, something asking to be kept visible
/// (an open dropdown, an expanded Layers panel, keyboard focus inside an overlay), and the
/// passage of time — are read by the view and fed in here so the fade/no-fade decision itself
/// is a handful of comparisons against an injected clock, checkable without a live UI.
/// </remarks>
public sealed class MapControlsIdleState
{
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);

    private bool _pointerInside = true;
    private bool _keepVisible;
    private DateTimeOffset? _idleSince;

    public bool IsVisible { get; private set; } = true;

    /// <summary>The pointer is over the map page again; chrome comes back immediately.</summary>
    public void PointerEntered(DateTimeOffset now)
    {
        _pointerInside = true;
        _idleSince = null;
        IsVisible = true;
    }

    /// <summary>The pointer has left the map page; the idle clock starts unless something else keeps chrome up.</summary>
    public void PointerExited(DateTimeOffset now)
    {
        _pointerInside = false;
        if (!_keepVisible)
        {
            _idleSince = now;
        }
    }

    /// <summary>
    /// An open dropdown, an expanded Layers panel, or keyboard focus inside an overlay is
    /// asking chrome to stay up regardless of the pointer or the clock.
    /// </summary>
    public void SetKeepVisible(bool keepVisible, DateTimeOffset now)
    {
        if (_keepVisible == keepVisible)
        {
            return;
        }

        _keepVisible = keepVisible;
        if (keepVisible)
        {
            _idleSince = null;
            IsVisible = true;
        }
        else if (!_pointerInside)
        {
            // Released with the pointer already away: the idle clock starts now, not from
            // whenever the pointer originally left minutes ago.
            _idleSince = now;
        }
    }

    /// <summary>Re-evaluates visibility against the clock. Cheap enough to call on a short timer.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (_pointerInside || _keepVisible)
        {
            IsVisible = true;
            return;
        }

        IsVisible = _idleSince is not { } idleSince || now - idleSince < IdleDelay;
    }
}
