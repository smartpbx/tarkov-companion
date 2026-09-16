using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

public sealed class MapSceneRendererHostContractTests
{
    [Fact]
    public void Gallery_flags_are_known_and_select_both_packaged_scenarios()
    {
        var ordinary = AppCommandLine.Parse(["--map-renderer-gallery"]);
        var largeText = AppCommandLine.Parse(["--map-renderer-gallery", "--map-renderer-large-text"]);

        Assert.True(ordinary.MapRendererGallery);
        Assert.False(ordinary.MapRendererLargeText);
        Assert.True(largeText.MapRendererGallery);
        Assert.True(largeText.MapRendererLargeText);
        Assert.Empty(largeText.UnknownOptions);
    }

    [Fact]
    public void Packaged_gallery_composes_the_real_renderer_and_is_selected_by_application_startup()
    {
        var app = Read("src", "TarkovCompanion.App", "App.axaml.cs");
        var gallery = Read("src", "TarkovCompanion.App", "Views", "V2", "MapRenderer", "MapSceneRendererGalleryWindow.axaml");
        var galleryCode = Read(
            "src",
            "TarkovCompanion.App",
            "Views",
            "V2",
            "MapRenderer",
            "MapSceneRendererGalleryWindow.axaml.cs");

        Assert.Contains("new MapSceneRendererGalleryWindow(options.MapRendererLargeText)", app, StringComparison.Ordinal);
        Assert.Contains("public MapSceneRendererGalleryWindow()", galleryCode, StringComparison.Ordinal);
        Assert.Contains(": this(largeText: false)", galleryCode, StringComparison.Ordinal);
        Assert.Contains("<map:MapSceneRendererView DataContext=\"{Binding Renderer}\" />", gallery, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"320\"", gallery, StringComparison.Ordinal);
        Assert.Contains("ScaleX=\"{Binding InterfaceScale}\"", gallery, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_defers_named_control_event_wiring_until_the_visual_tree_exists()
    {
        var view = Read(
            "src",
            "TarkovCompanion.App",
            "Views",
            "V2",
            "MapRenderer",
            "MapSceneRendererView.axaml.cs");

        var constructorStart = view.IndexOf("public MapSceneRendererView()", StringComparison.Ordinal);
        var attachStart = view.IndexOf("protected override void OnAttachedToVisualTree", StringComparison.Ordinal);
        Assert.True(constructorStart >= 0 && attachStart > constructorStart);
        var constructor = view[constructorStart..attachStart];
        Assert.DoesNotContain("PlanViewport.", constructor, StringComparison.Ordinal);
        Assert.Contains("PlanViewport.SizeChanged += PlanViewportSizeChanged", view, StringComparison.Ordinal);
        Assert.Contains("PlanViewport.SizeChanged -= PlanViewportSizeChanged", view, StringComparison.Ordinal);
        Assert.Contains("if (PlanViewport is not null", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_has_search_paging_touch_targets_and_live_text_peers()
    {
        var view = Read("src", "TarkovCompanion.App", "Views", "V2", "MapRenderer", "MapSceneRendererView.axaml");

        foreach (var id in new[]
                 {
                     "v2-map-renderer", "v2-map-plan", "v2-map-search", "v2-map-page-previous",
                     "v2-map-page-next", "v2-map-page-status", "v2-map-live-assertive", "v2-map-live-polite",
                 })
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", view, StringComparison.Ordinal);
        }

        Assert.Contains("AutomationProperties.LiveSetting=\"Assertive\"", view, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", view, StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"MinHeight\" Value=\"{DynamicResource V2.Target.Touch}\" />",
            view,
            StringComparison.Ordinal);
        Assert.Contains("<ScrollViewer HorizontalScrollBarVisibility=\"Disabled\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("<Viewbox", view, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Renderer_uses_invokable_clusters_and_defers_nullable_selection_bindings()
    {
        var view = Read("src", "TarkovCompanion.App", "Views", "V2", "MapRenderer", "MapSceneRendererView.axaml");

        Assert.Contains("ItemsSource=\"{Binding PointMarkers}\"", view, StringComparison.Ordinal);
        Assert.Contains("<ToggleButton Classes=\"v2-map-marker\"", view, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ClusterMarkers}\"", view, StringComparison.Ordinal);
        Assert.Contains("<Button Classes=\"v2-map-marker cluster\"", view, StringComparison.Ordinal);
        Assert.Contains("<ContentControl Content=\"{Binding SelectedObject}\">", view, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding SelectedObject.", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_reflow_and_presentation_do_not_use_machine_ambient_choices()
    {
        var view = Read("src", "TarkovCompanion.App", "Views", "V2", "MapRenderer", "MapSceneRendererView.axaml.cs");
        var presentation = Read("src", "TarkovCompanion.App", "ViewModels", "V2", "MapRenderer", "MapSceneRendererPresentation.cs");
        var renderer = Read("src", "TarkovCompanion.App", "ViewModels", "V2", "MapRenderer", "MapSceneRendererViewModel.cs");

        Assert.Contains("NarrowHeaderWidth = 600", view, StringComparison.Ordinal);
        Assert.Contains("CompactWidth = 860", view, StringComparison.Ordinal);
        Assert.Contains("RendererBody.ColumnDefinitions", view, StringComparison.Ordinal);
        Assert.DoesNotContain("ToLocalTime", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentCulture", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentCulture", renderer, StringComparison.Ordinal);
        Assert.Contains("TimeZoneInfo.ConvertTime(value, TimeZone)", presentation, StringComparison.Ordinal);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(V2ShellTestData.RepositoryPath(segments));
}
