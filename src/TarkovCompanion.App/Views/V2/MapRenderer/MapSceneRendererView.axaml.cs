using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.Views.V2.MapRenderer;

/// <summary>A secondary gesture on bare map: where it landed, and whether Shift was held.</summary>
/// <remarks>
/// [V2 rough package 46] Shift is carried rather than resolved here because which mark a modifier
/// means is the host's business, not the renderer's. The renderer only reports the gesture.
/// </remarks>
/// <param name="IsMenu">Ctrl was held: the host offers a menu of what to place instead (#289).</param>
public readonly record struct MapPlanGesture(MapScenePoint Point, bool IsSecondary, bool IsMenu = false);

/// <summary>Responsive pointer, touch, and keyboard handoff for the canonical map view.</summary>
public sealed partial class MapSceneRendererView : UserControl
{
    private const double DragThreshold = 4;

    private bool _pointerDown;
    private bool _dragging;
    private bool _viewportEventsAttached;
    private Point _pointerStart;
    private string? _appliedLayoutMode;
    /// <summary>Whether the press that started this gesture was a plain left click, which may select.</summary>
    private bool _pressSelects;
    /// <summary>[#286] A line is being drawn: the pointer's path so far, in viewport pixels.</summary>
    private List<Point>? _ink;
    /// <summary>
    /// #938: the same path as scene points, each converted with the camera of the moment it was
    /// drawn. Converting the whole stroke on release used the camera it ended on, so a wheel zoom,
    /// a Follow recentre or a tablet pan mid-stroke moved or scaled the line that was saved and
    /// sent to the squad, while the pixel preview still looked right to the one drawing it.
    /// </summary>
    private readonly List<MapScenePoint> _inkScene = [];
    /// <summary>[#286] Space is held, so a left-drag pans even in Draw mode.</summary>
    private bool _spaceHeld;
    private TopLevel? _keyboardSource;

    /// <summary>The least a pointer moves, in pixels, before a drawn line takes another point.</summary>
    private const double InkStep = 2;

    /// <summary>
    /// [#286] Draw mode: a left-drag draws a line rather than panning.
    /// </summary>
    /// <remarks>
    /// Set by the host (the Raid cockpit owns the mode). A middle-drag pans in either mode, and so
    /// does a left-drag with Space held, so drawing never costs the player their pan.
    /// </remarks>
    public static readonly StyledProperty<bool> IsDrawingProperty =
        AvaloniaProperty.Register<MapSceneRendererView, bool>(nameof(IsDrawing));

    public bool IsDrawing
    {
        get => GetValue(IsDrawingProperty);
        set => SetValue(IsDrawingProperty, value);
    }

    /// <summary>[#286] A line was drawn in Draw mode, as scene points in the order drawn.</summary>
    public event EventHandler<IReadOnlyList<MapScenePoint>>? StrokeDrawn;

    /// <summary>[#286] Escape was pressed in Draw, Inspect or Route mode: the host goes back to Navigate.</summary>
    public event EventHandler? ModeEscaped;

    /// <summary>
    /// [#286] Inspect and Route modes: a plain left click is the host's (<see cref="ModeClicked"/>)
    /// and never selects or clears a selection; a left-drag still pans.
    /// </summary>
    public static readonly StyledProperty<bool> IsClickModeProperty =
        AvaloniaProperty.Register<MapSceneRendererView, bool>(nameof(IsClickMode));

    public bool IsClickMode
    {
        get => GetValue(IsClickModeProperty);
        set => SetValue(IsClickModeProperty, value);
    }

    /// <summary>[#286] A plain left click in Inspect or Route mode, at that scene point.</summary>
    public event EventHandler<MapScenePoint>? ModeClicked;

    /// <summary>
    /// The host places the presentation and floor controls itself, so nothing is floated over the plan for them.
    /// </summary>
    /// <remarks>
    /// Reported on 2026-09-20: "the top-left view-mode box ... sits on top of the map awkwardly".
    /// On a tall map (Factory, Reserve) the pill covered the plan's top-left corner, and one of
    /// Factory's extracts with it. A host with a strip of its own puts a
    /// <see cref="MapPresentationControls"/> there and sets this; every other host keeps the pill.
    /// </remarks>
    public static readonly StyledProperty<bool> DocksPresentationProperty =
        AvaloniaProperty.Register<MapSceneRendererView, bool>(nameof(DocksPresentation));

    public bool DocksPresentation
    {
        get => GetValue(DocksPresentationProperty);
        set => SetValue(DocksPresentationProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocksPresentationProperty && PresentationPill is not null)
        {
            PresentationPill.IsVisible = !DocksPresentation;
        }

        if (change.Property == IsDrawingProperty)
        {
            if (!IsDrawing)
            {
                EndInk();
            }

            Cursor = IsDrawing || IsClickMode ? new Cursor(StandardCursorType.Cross) : null;
        }

        if (change.Property == IsClickModeProperty)
        {
            Cursor = IsDrawing || IsClickMode ? new Cursor(StandardCursorType.Cross) : null;
        }
    }

    public MapSceneRendererView()
    {
        // InitializeComponent, not AvaloniaXamlLoader.Load: only the generated method assigns the
        // x:Name fields (PlanViewport, RendererBody, ...). With Load alone every one stayed null,
        // so the responsive layout and viewport sizing below silently never ran and the plan kept
        // its default 1000x700 canvas beside an empty details column.
        InitializeComponent();
        PresentationPill.IsVisible = !DocksPresentation;
        DataContextChanged += RendererDataContextChanged;
    }

    /// <summary>
    /// A plain click (not a drag or a pan) landed on the plan, at that scene point.
    /// </summary>
    /// <remarks>
    /// Raised whatever the click hit, selection included, so a host arming "place a mark here"
    /// does not have to duplicate this view's own hit-testing to find out where the pointer was.
    /// A host that is not placing anything simply leaves this unhandled.
    /// </remarks>
    public event EventHandler<MapScenePoint>? PlanClicked;

    /// <summary>
    /// A right-click (or equivalent secondary gesture) landed on an object on the plan.
    /// </summary>
    /// <remarks>
    /// Raised only when the gesture actually hit something; a right-click on bare map raises
    /// nothing, so a host wiring "right-click removes a mark" never has to check what a bare-map
    /// right-click used to do before this existed.
    /// </remarks>
    public event EventHandler<MapSceneObjectId>? MarkerRightClicked;

    /// <summary>
    /// A right-click (or equivalent secondary gesture) landed on bare map, and whether Shift was held.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 46] The other half of <see cref="MarkerRightClicked"/>, and deliberately
    /// exclusive with it: exactly one of the two is raised per right-click, so the host cannot
    /// place a mark on top of the one it was asked to remove. What was under the pointer wins,
    /// and the hit area is the marker's own, not the pixel.
    /// </remarks>
    public event EventHandler<MapPlanGesture>? PlanRightClicked;

    /// <summary>Wires named XAML controls only after they exist in the visual tree.</summary>
    /// <remarks>
    /// Avalonia can finish the code-behind constructor before generated <c>x:Name</c> fields are
    /// assigned. Subscribing to PlanViewport in the constructor made the packaged gallery exit
    /// before it showed a window even though compiled-XAML and unit builds succeeded.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        if (!_viewportEventsAttached && PlanViewport is not null)
        {
            _viewportEventsAttached = true;
            SizeChanged += RendererSizeChanged;
            PlanViewport.SizeChanged += PlanViewportSizeChanged;
        }

        UpdateResponsiveLayout();
        UpdateViewport();

        // [#286] Space and Escape are read from the whole window, tunnelling and whatever
        // handled them: the plan is a Border and never has focus, so its own KeyDown would only
        // ever hear them after something inside it had been clicked with the keyboard.
        _keyboardSource = TopLevel.GetTopLevel(this);
        _keyboardSource?.AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _keyboardSource?.AddHandler(KeyUpEvent, WindowKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        if (_keyboardSource is { } source)
        {
            source.RemoveHandler(KeyDownEvent, WindowKeyDown);
            source.RemoveHandler(KeyUpEvent, WindowKeyUp);
            _keyboardSource = null;
        }

        _spaceHeld = false;
        EndInk();
        if (_viewportEventsAttached)
        {
            SizeChanged -= RendererSizeChanged;
            if (PlanViewport is not null)
            {
                PlanViewport.SizeChanged -= PlanViewportSizeChanged;
            }

            _viewportEventsAttached = false;
        }

        base.OnDetachedFromVisualTree(eventArgs);
    }

    private void RendererDataContextChanged(object? sender, EventArgs eventArgs)
    {
        // A host can swap in a new renderer view model while this view is already laid out at
        // its final size (the Raid workspace does, on its first scene). Neither SizeChanged
        // handler fires then, so the new model would keep its default canvas size and the
        // details column this model may not want. Re-apply both now and once more after layout.
        UpdateResponsiveLayout();
        UpdateViewport();
        Dispatcher.UIThread.Post(() =>
        {
            UpdateResponsiveLayout();
            UpdateViewport();
        }, DispatcherPriority.Loaded);
    }

    private void RendererSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        UpdateResponsiveLayout();
        UpdateViewport();
    }

    private void UpdateResponsiveLayout()
    {
        if (RendererHeader is null || RendererTitle is null || RendererCommands is null ||
            RendererBody is null || PlanViewport is null || DetailsPanel is null || RendererOverlay is null)
        {
            return;
        }

        // Only a host that hides the details column (the Raid workspace) gets the responsive,
        // fill-the-host layout. With the details column the view sits in a vertical scroller
        // where the plan's height comes from the canvas, so sizing the canvas from the plan grew
        // it by a pixel on every pass (a layout cycle), and that host's verified gallery layout
        // and cluster ids were recorded against the fixed default canvas. It keeps that layout.
        if (DataContext is not MapSceneRendererViewModel { ShowsDetailsPanel: false })
        {
            return;
        }

        // Re-assigning definitions invalidates the Grid even when nothing changed, and this runs
        // from SizeChanged; apply only when the mode actually differs, so it cannot feed itself.
        const string Mode = "fill";
        if (_appliedLayoutMode == Mode)
        {
            return;
        }

        _appliedLayoutMode = Mode;
        RendererBody.ColumnDefinitions = new("*");
        RendererBody.RowDefinitions = new("*");
        RendererOverlay.ColumnDefinitions = new("*");
        // The map is the whole view: the plan takes the host's finite height instead of letting
        // the canvas's previous size decide it inside a scroller.
        if (RendererScroll is not null)
        {
            RendererScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
    }

    private void PlanViewportSizeChanged(object? sender, SizeChangedEventArgs eventArgs) => UpdateViewport();

    private void UpdateViewport()
    {
        // See UpdateResponsiveLayout: only the fill-the-host layout has a plan height that does
        // not depend on the canvas itself.
        if (PlanViewport is not null && _appliedLayoutMode is not null &&
            DataContext is MapSceneRendererViewModel { ShowsDetailsPanel: false } renderer)
        {
            renderer.SetViewportSize(PlanViewport.Bounds.Width, PlanViewport.Bounds.Height);
        }
    }

    private void PlanPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        var current = eventArgs.GetCurrentPoint(PlanViewport);
        if (current.Properties.IsRightButtonPressed)
        {
            // Checked here rather than through the same TrySelectAt path a left click uses: a
            // right click never changes selection, only asks what, if anything, it landed on.
            if (DataContext is MapSceneRendererViewModel rightClickRenderer)
            {
                var position = eventArgs.GetPosition(PlanViewport);
                // [#929] Only an object the host gives a right-click meaning; anything else drawn
                // there (a traffic circle, an extract, a squadmate) lets the press place a mark.
                if (rightClickRenderer.TryHitRightClickTargetAt(position.X, position.Y, out var objectId))
                {
                    // [V2 rough package 46] Removal beats placement: something under the pointer
                    // ends the gesture here, so the same press can never also drop a mark.
                    MarkerRightClicked?.Invoke(this, objectId);
                    eventArgs.Handled = true;
                }
                else if (rightClickRenderer.TryScenePointAt(position.X, position.Y, out var scenePoint))
                {
                    PlanRightClicked?.Invoke(
                        this,
                        new(
                            scenePoint,
                            eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift),
                            eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control)));
                    // Handled either way, so nothing further up opens a context menu over the
                    // plan and swallows the gesture the next time.
                    eventArgs.Handled = true;
                }
            }

            return;
        }

        // [#286] Draw mode: a plain left-drag draws, whatever it starts on. Space held, or the
        // middle button, pans as always.
        if (current.Properties.IsLeftButtonPressed && IsDrawing && !_spaceHeld)
        {
            _ink = [eventArgs.GetPosition(PlanViewport)];
            _inkScene.Clear();
            CaptureInkPoint(_ink[0]);
            if (DrawingInk is not null)
            {
                DrawingInk.Points = [.. _ink];
                DrawingInk.IsVisible = true;
            }

            eventArgs.Pointer.Capture(PlanViewport);
            eventArgs.Handled = true;
            return;
        }

        var middle = current.Properties.IsMiddleButtonPressed;
        if (!middle && (!current.Properties.IsLeftButtonPressed ||
            (!IsClickMode && (eventArgs.Source as StyledElement)?.DataContext is MapSceneRendererObjectViewModel)))
        {
            return;
        }

        // A middle press, or a left press in Draw mode with Space held, only ever pans.
        _pressSelects = !middle && !IsDrawing;
        _pointerDown = true;
        _dragging = false;
        _pointerStart = eventArgs.GetPosition(PlanViewport);
        // V2 rough package 20: the drag is live from here. The plan follows the pointer through
        // the canvas's RenderTransform, and only the camera it ends on reaches the scene.
        (DataContext as MapSceneRendererViewModel)?.BeginPan();
        eventArgs.Pointer.Capture(PlanViewport);
        eventArgs.Handled = true;
    }

    private void PlanPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (_ink is { } ink)
        {
            var at = eventArgs.GetPosition(PlanViewport);
            var last = ink[^1];
            if (Math.Abs(at.X - last.X) >= InkStep || Math.Abs(at.Y - last.Y) >= InkStep)
            {
                ink.Add(at);
                CaptureInkPoint(at);
                if (DrawingInk is not null)
                {
                    DrawingInk.Points = [.. ink];
                }
            }

            eventArgs.Handled = true;
            return;
        }

        if (!_pointerDown)
        {
            return;
        }

        var current = eventArgs.GetPosition(PlanViewport);
        var delta = current - _pointerStart;
        if (!_dragging && Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        _dragging = true;
        // The total offset from where the pointer went down, not the step since the last move:
        // a coalesced or dropped move then cannot leave the plan drifting behind the pointer.
        (DataContext as MapSceneRendererViewModel)?.UpdatePan(delta.X, delta.Y);
        eventArgs.Handled = true;
    }

    private void MapMarkerPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        if (DataContext is MapSceneRendererViewModel renderer &&
            (sender as StyledElement)?.DataContext is MapSceneRendererObjectViewModel { ObjectId: { } id })
        {
            renderer.HoverRequirementObject(id);
        }
    }

    private void MapMarkerPointerExited(object? sender, PointerEventArgs eventArgs) =>
        (DataContext as MapSceneRendererViewModel)?.HoverRequirementObject(null);

    /// <summary>A drag that ends without a release (another control takes the pointer) puts the
    /// plan back where it was rather than leaving it mid-drag.</summary>
    /// <remarks>
    /// [Issue 551] "if i zoom in on a map, and then try to pan it, it snaps back to where i
    /// zoomed". Reported three times, and answered twice in the view model, where it never was.
    /// Releasing the button gives the capture back, giving it back raises this event before
    /// <see cref="PlanPointerReleased"/> has got as far as committing, and this handler used to
    /// cancel the drag unconditionally: so every drag in the running app was abandoned on release
    /// and the plan went back to the camera the drag began from, which after a zoom is "where I
    /// zoomed". At the fitted zoom there is nowhere to pan to, which is why it only ever showed
    /// zoomed in; and every test drove the view model directly, where there is no capture.
    /// A release clears <c>_pointerDown</c> first, so only a capture lost while the button is
    /// still down cancels. MapPanGestureTests drags the real view with a real pointer.
    /// </remarks>
    private void PlanPointerCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs)
    {
        // [#286] A line whose capture went elsewhere mid-stroke is dropped, not half-kept.
        EndInk();
        if (!_pointerDown)
        {
            return;
        }

        _pointerDown = false;
        _dragging = false;
        (DataContext as MapSceneRendererViewModel)?.CancelPan();
    }

    private void PlanPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (_ink is { } ink)
        {
            // Cleared before the capture is released: giving it back raises CaptureLost, which
            // would otherwise drop the line that is being finished here.
            _ink = null;
            var released = eventArgs.GetPosition(PlanViewport);
            ink.Add(released);
            CaptureInkPoint(released);
            List<MapScenePoint> points = [.. _inkScene];
            eventArgs.Pointer.Capture(null);
            EndInk();
            FinishStroke(points);
            eventArgs.Handled = true;
            return;
        }

        if (!_pointerDown)
        {
            return;
        }

        _pointerDown = false;
        eventArgs.Pointer.Capture(null);
        var current = eventArgs.GetPosition(PlanViewport);
        if (DataContext is MapSceneRendererViewModel renderer)
        {
            if (_dragging)
            {
                renderer.UpdatePan(current.X - _pointerStart.X, current.Y - _pointerStart.Y);
                renderer.CommitPan();
            }
            else if (!_pressSelects)
            {
                renderer.CancelPan();
            }
            else if (IsClickMode)
            {
                // [#286] Inspect and Route: the click is the mode's, and a selection stays as it was.
                renderer.CancelPan();
                if (renderer.TryScenePointAt(current.X, current.Y, out var modePoint))
                {
                    ModeClicked?.Invoke(this, modePoint);
                }
            }
            else
            {
                renderer.CancelPan();
                if (!renderer.TrySelectAt(current.X, current.Y))
                {
                    renderer.ClearSelection();
                }

                if (renderer.TryScenePointAt(current.X, current.Y, out var point))
                {
                    PlanClicked?.Invoke(this, point);
                }
            }
        }

        _dragging = false;
        eventArgs.Handled = true;
    }

    private void PlanPointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs)
    {
        if (DataContext is not MapSceneRendererViewModel renderer || eventArgs.Delta.Y == 0)
        {
            return;
        }

        // Zoom follows the pointer: the place under the cursor stays under it.
        var position = eventArgs.GetPosition(PlanViewport);
        renderer.RequestZoomAt(eventArgs.Delta.Y, position.X, position.Y);
        eventArgs.Handled = true;
    }

    private void RendererKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (DataContext is not MapSceneRendererViewModel renderer)
        {
            return;
        }

        if (eventArgs.Key == Key.Escape && renderer.HasSelection)
        {
            renderer.ClearSelection();
            eventArgs.Handled = true;
            return;
        }

        // Plain arrows, digits, and letters retain their normal focus, scrolling, and assistive
        // technology behavior. Map shortcuts are explicit Alt combinations.
        if (eventArgs.KeyModifiers != KeyModifiers.Alt)
        {
            return;
        }

        switch (eventArgs.Key)
        {
            case Key.F:
                renderer.FitPlanCommand.Execute(null);
                break;
            case Key.D1:
                renderer.RequestMode(MapSceneMode.Flat2D);
                break;
            case Key.D2:
                renderer.RequestMode(MapSceneMode.FloorStack2D);
                break;
            case Key.D3:
                renderer.RequestMode(MapSceneMode.Interior3D);
                break;
            default:
                return;
        }

        eventArgs.Handled = true;
    }

    /// <summary>[#286] One drawn pixel as a scene point, with the camera as it is now (#938).</summary>
    private void CaptureInkPoint(Point pixel)
    {
        if (DataContext is MapSceneRendererViewModel renderer &&
            renderer.TryScenePointAt(pixel.X, pixel.Y, out var point))
        {
            _inkScene.Add(point);
        }
    }

    /// <summary>[#286] Hands the stroke's scene points to the host.</summary>
    private void FinishStroke(IReadOnlyList<MapScenePoint> points)
    {
        if (points.Count >= 2)
        {
            StrokeDrawn?.Invoke(this, points);
        }
    }

    private void EndInk()
    {
        _ink = null;
        _inkScene.Clear();
        if (DrawingInk is not null)
        {
            DrawingInk.IsVisible = false;
            DrawingInk.Points = [];
        }
    }

    private void WindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Space)
        {
            _spaceHeld = true;
            return;
        }

        if (eventArgs.Key == Key.Escape && (IsDrawing || IsClickMode) && !eventArgs.Handled)
        {
            EndInk();
            ModeEscaped?.Invoke(this, EventArgs.Empty);
            eventArgs.Handled = true;
        }
    }

    private void WindowKeyUp(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Space)
        {
            _spaceHeld = false;
        }
    }
}
