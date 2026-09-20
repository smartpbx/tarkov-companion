using Avalonia;
using Avalonia.Controls;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Plan;

namespace TarkovCompanion.App.Views.V2.Plan;

/// <summary>
/// V2 rough package 32: places the objectives panel where more of the map gets drawn.
/// </summary>
/// <remarks>
/// The arithmetic is <see cref="MapPanelLayout"/>; this only applies its answer and feeds it the
/// sizes this view actually uses, so the decision and the draw cannot disagree. It runs on every
/// size change, which is what makes a window size nobody has tried behave like the ones that have
/// been: no breakpoint list, no per-monitor special case.
/// </remarks>
public sealed partial class PlanWorkspaceView : UserControl
{
    /// <summary>The least the panel is worth drawing under the map.</summary>
    /// <remarks>
    /// The map's heading, a row of objectives, and Open in Raid. It is also what the placement
    /// arithmetic is told the arrangement costs, so moving the panel has to buy at least this
    /// much map to be worth doing. What the panel actually gets is everything the map does not
    /// need, which at 1920x1080 is more height than it had beside the map, in three columns
    /// instead of one.
    /// </remarks>
    private const double PanelMinimumHeightBelow = 320;

    /// <summary>The map card's own margins, which are not available to the plan.</summary>
    private const double MapCardMargins = 32;

    /// <summary>How wide Requirements and Open in Raid are when they stand beside the objectives.</summary>
    private const double SidePieceWidthBelow = 380;

    /// <summary>What the panel takes from the width when it sits beside the map.</summary>
    private const double PanelWidthBeside = 440;

    private MapPanelPlacement? _placement;
    private PlanWorkspaceViewModel? _watchedPlan;
    private MapSceneRendererViewModel? _watchedRenderer;

    public PlanWorkspaceView()
    {
        // InitializeComponent, not AvaloniaXamlLoader.Load: only the generated method assigns the
        // x:Name fields this needs. See the same note in MapSceneRendererView.
        InitializeComponent();
        SizeChanged += (_, _) => ApplyPanelPlacement();
        DataContextChanged += (_, _) => WatchPlan();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        WatchPlan();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        Watch(plan: null, renderer: null);
        base.OnDetachedFromVisualTree(eventArgs);
    }

    /// <summary>
    /// Follows the map preview, and the plan shape inside it, because neither arrives with the
    /// view model.
    /// </summary>
    /// <remarks>
    /// The workspace is laid out long before it has a map: the preview is built when the scene is
    /// rebuilt, and the plan's real shape only after its artwork has decoded. Deciding once, on
    /// the size the window happened to be at construction, put the panel beside a map whose shape
    /// was still a guess and left it there — which is how the first attempt at this looked right
    /// in one render and wrong in the next.
    /// </remarks>
    private void WatchPlan()
    {
        var plan = DataContext as PlanWorkspaceViewModel;
        Watch(plan, plan?.MapPreview);
        ApplyPanelPlacement();
    }

    private void Watch(PlanWorkspaceViewModel? plan, MapSceneRendererViewModel? renderer)
    {
        if (!ReferenceEquals(_watchedPlan, plan))
        {
            if (_watchedPlan is not null)
            {
                _watchedPlan.PropertyChanged -= PlanPropertyChanged;
            }

            _watchedPlan = plan;
            if (_watchedPlan is not null)
            {
                _watchedPlan.PropertyChanged += PlanPropertyChanged;
            }
        }

        if (ReferenceEquals(_watchedRenderer, renderer))
        {
            return;
        }

        if (_watchedRenderer is not null)
        {
            _watchedRenderer.PropertyChanged -= RendererPropertyChanged;
        }

        _watchedRenderer = renderer;
        if (_watchedRenderer is not null)
        {
            _watchedRenderer.PropertyChanged += RendererPropertyChanged;
        }
    }

    private void PlanPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is null or nameof(PlanWorkspaceViewModel.MapPreview))
        {
            Watch(_watchedPlan, _watchedPlan?.MapPreview);
            ApplyPanelPlacement();
        }
    }

    private void RendererPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is null or nameof(MapSceneRendererViewModel.PlanAspect))
        {
            ApplyPanelPlacement();
        }
    }

    private void ApplyPanelPlacement()
    {
        if (PlanBody is null || PlanPanel is null || PlanMapCard is null)
        {
            return;
        }

        // The plan's own shape, not the shape it happens to be drawn at right now: the second
        // would make this depend on its own previous answer.
        var aspect = _watchedRenderer?.PlanAspect ?? double.NaN;
        var available = Bounds.Size;
        var forMapAndPanel = available.Width - PlanBody.ColumnDefinitions[0].ActualWidth;
        var placement = MapPanelLayout.Choose(
            forMapAndPanel,
            available.Height,
            PanelWidthBeside,
            PanelMinimumHeightBelow,
            aspect);

        if (placement == MapPanelPlacement.Below)
        {
            // The card takes the height the plan can actually fill and the panel takes the rest,
            // so neither of them is a box with nothing in it.
            var mapHeight = Math.Min(
                available.Height - PanelMinimumHeightBelow,
                ((forMapAndPanel - MapCardMargins) / aspect) + MapCardMargins);
            if (_placement != placement)
            {
                Grid.SetColumn(PlanPanel, 1);
                Grid.SetRow(PlanPanel, 1);
                Grid.SetRowSpan(PlanPanel, 1);
                PlanPanel.Width = double.NaN;
                PlanPanel.Height = double.NaN;
                PlanBody.RowDefinitions = new RowDefinitions("Auto,*");
                // The map's name is the heading of the card directly above it here, so the panel
                // does not say it a second time and spends the rows on objectives instead.
                SetPanelHeadingVisible(false);
                ArrangePanel(sideBySide: true);
            }

            PlanMapCard.Height = Math.Max(0, mapHeight);
            _placement = placement;
            return;
        }

        if (_placement == placement)
        {
            return;
        }

        _placement = placement;
        Grid.SetColumn(PlanPanel, 2);
        Grid.SetRow(PlanPanel, 0);
        Grid.SetRowSpan(PlanPanel, 2);
        PlanPanel.Width = PanelWidthBeside;
        PlanPanel.Height = double.NaN;
        PlanMapCard.Height = double.NaN;
        PlanBody.RowDefinitions = new RowDefinitions("*,Auto");
        SetPanelHeadingVisible(true);
        ArrangePanel(sideBySide: false);
    }

    /// <summary>
    /// Where Requirements and Open in Raid sit inside the panel: under the objectives, or beside them.
    /// </summary>
    /// <remarks>
    /// Reported at 1920x1080 with the panel under the map: "only the Objectives (N) heading shows
    /// and the rows have no height". The panel is about 320 tall there, and stacked in one column
    /// the Requirements card and the button took their full height first, leaving the objectives'
    /// starred row some 95 pixels for a heading and a clipped line. Under the map the panel is
    /// three times as wide as it is beside it, so the two fixed pieces go in a column of their
    /// own on the right and the objectives keep the whole height.
    /// </remarks>
    private void ArrangePanel(bool sideBySide)
    {
        if (PlanObjectivesCard is null || PlanRequirementsCard is null || PlanPanelActions is null)
        {
            return;
        }

        Grid.SetColumnSpan(PlanObjectivesCard, sideBySide ? 1 : 2);
        Grid.SetRowSpan(PlanObjectivesCard, sideBySide ? 3 : 1);
        PlanObjectivesCard.Margin = sideBySide ? new(16, 8, 0, 16) : new(16, 0, 16, 12);

        Grid.SetColumn(PlanRequirementsCard, sideBySide ? 1 : 0);
        Grid.SetColumnSpan(PlanRequirementsCard, sideBySide ? 1 : 2);
        // The starred row beside the objectives, not the Auto row under them.
        Grid.SetRow(PlanRequirementsCard, sideBySide ? 1 : 2);
        PlanRequirementsCard.Width = sideBySide ? SidePieceWidthBelow : double.NaN;
        PlanRequirementsCard.Margin = sideBySide ? new(16, 8, 16, 12) : new(16, 0, 16, 12);
        PlanRequirementsCard.VerticalAlignment = sideBySide
            ? Avalonia.Layout.VerticalAlignment.Top
            : Avalonia.Layout.VerticalAlignment.Stretch;

        Grid.SetColumn(PlanPanelActions, sideBySide ? 1 : 0);
        Grid.SetColumnSpan(PlanPanelActions, sideBySide ? 1 : 2);
        PlanPanelActions.Width = sideBySide ? SidePieceWidthBelow : double.NaN;
    }

    private void SetPanelHeadingVisible(bool visible)
    {
        if (PlanPanelHeading is not null)
        {
            PlanPanelHeading.IsVisible = visible;
        }
    }

    /// <summary>
    /// Records the player's level, through the event rather than a two-way binding: the board is
    /// re-read after a save, which rebuilds the spinner's value, and a two-way binding would
    /// read that rebuild as another edit.
    /// </summary>
    private void LevelChanged(object? sender, NumericUpDownValueChangedEventArgs eventArgs)
    {
        if (eventArgs.NewValue is { } level && DataContext is PlanWorkspaceViewModel viewModel)
        {
            _ = viewModel.SetPlayerLevelAsync(level);
        }
    }

    private void TraderLevelChanged(object? sender, NumericUpDownValueChangedEventArgs eventArgs)
    {
        if (sender is NumericUpDown { Tag: string traderId } &&
            eventArgs.NewValue is { } level &&
            DataContext is PlanWorkspaceViewModel viewModel)
        {
            _ = viewModel.SetTraderLevelAsync(traderId, level);
        }
    }
}
