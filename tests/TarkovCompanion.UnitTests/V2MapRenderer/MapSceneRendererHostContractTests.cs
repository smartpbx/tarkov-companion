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
        var offline = AppCommandLine.Parse(["--map-renderer-gallery", "--map-renderer-loot-offline"]);

        Assert.True(ordinary.MapRendererGallery);
        Assert.False(ordinary.MapRendererLargeText);
        Assert.False(ordinary.MapRendererLootOffline);
        Assert.True(largeText.MapRendererGallery);
        Assert.True(largeText.MapRendererLargeText);
        Assert.True(offline.MapRendererLootOffline);
        Assert.Empty(largeText.UnknownOptions);
        Assert.Empty(offline.UnknownOptions);
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

        Assert.Contains("options.MapRendererLootOffline", app, StringComparison.Ordinal);
        Assert.Contains("public MapSceneRendererGalleryWindow()", galleryCode, StringComparison.Ordinal);
        Assert.Contains(": this(largeText: false, lootOffline: false)", galleryCode, StringComparison.Ordinal);
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
        // The mode segment, the floor ladder and the notices are MapPresentationControls, which
        // the renderer's pill hosts; together the two files are the renderer's surface.
        var view = Read("src", "TarkovCompanion.App", "Views", "V2", "MapRenderer", "MapSceneRendererView.axaml") +
            Read("src", "TarkovCompanion.App", "Views", "V2", "MapRenderer", "MapPresentationControls.axaml");

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
        // [#574] The desktop control height since the owner asked for smaller controls; the
        // tablet page keeps the touch size.
        Assert.Contains(
            "<Setter Property=\"MinHeight\" Value=\"{DynamicResource V2.Target.Desktop}\" />",
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
        Assert.Contains("<ContentControl Content=\"{Binding SelectedObject}\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding SelectedObject.", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_exposes_high_value_preset_filters_list_and_typed_details_to_uia()
    {
        var view = Read("src", "TarkovCompanion.App", "Views", "V2", "MapRenderer", "MapSceneRendererView.axaml");
        var renderer = Read(
            "src", "TarkovCompanion.App", "ViewModels", "V2", "MapRenderer", "MapSceneRendererViewModel.cs");
        var loot = Read(
            "src", "TarkovCompanion.App", "ViewModels", "V2", "MapRenderer", "HighValueLootLayerViewModel.cs");

        foreach (var id in new[]
                 {
                     "v2-map-loot-preset", "v2-map-loot-heading", "v2-map-loot-legend",
                     "v2-map-loot-state", "v2-map-loot-page-status", "v2-map-loot-page-next",
                     "v2-map-loot-selection-live", "v2-map-loot-category-more",
                     "v2-map-loot-floor-more", "v2-map-loot-freshness",
                     "v2-map-loot-category-active",
                     "v2-map-loot-dataset", "v2-map-loot-source",
                     "v2-map-loot-confidence", "v2-map-loot-source-coverage",
                 })
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", view, StringComparison.Ordinal);
        }

        Assert.Contains("HighValueLootLayerPreset.Create", renderer, StringComparison.Ordinal);
        Assert.Contains("requestedRevision == scene.Revision", renderer, StringComparison.Ordinal);
        Assert.Contains("Map.Loot.PresetConflict", renderer, StringComparison.Ordinal);
        Assert.Contains("HazardsLayerId", renderer, StringComparison.Ordinal);
        Assert.Contains("MaximumLootPresetPreservedLayers", renderer, StringComparison.Ordinal);
        Assert.Contains("HighValueLootFilterRequested", renderer, StringComparison.Ordinal);
        Assert.Contains("HighValueLootFilterRequest", renderer, StringComparison.Ordinal);
        Assert.Contains("result.MapId", renderer, StringComparison.Ordinal);
        Assert.Contains("result.TransformVersion", renderer, StringComparison.Ordinal);
        Assert.Contains("result.AppliedFilter", renderer, StringComparison.Ordinal);
        Assert.Contains("VisibleObjectIds", renderer, StringComparison.Ordinal);
        Assert.Contains("MaximumRenderedFilterOptions", loot, StringComparison.Ordinal);
        Assert.Contains("MaximumFilterOptionsRead", loot, StringComparison.Ordinal);
        Assert.Contains("Map.Loot.ValueUnknown", loot, StringComparison.Ordinal);
        Assert.Contains("Map.Loot.FloorUnknown", loot, StringComparison.Ordinal);
        Assert.Contains("Map.Loot.ListOnly", loot, StringComparison.Ordinal);
        Assert.Contains("Map.Loot.ValueProfileUtility", loot, StringComparison.Ordinal);
        Assert.Contains("Map.Loot.ValuePerSquareSingle", loot, StringComparison.Ordinal);
        Assert.Contains("Map.Loot.ConfidenceKind", loot, StringComparison.Ordinal);
        Assert.Contains("Map.Loot.SourceCoverage", loot, StringComparison.Ordinal);
        Assert.DoesNotContain("v2-map-loot-waypoint", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_reflow_and_presentation_do_not_use_machine_ambient_choices()
    {
        var view = Read("src", "TarkovCompanion.App", "Views", "V2", "MapRenderer", "MapSceneRendererView.axaml.cs");
        var presentation = Read("src", "TarkovCompanion.App", "ViewModels", "V2", "MapRenderer", "MapSceneRendererPresentation.cs");
        var renderer = Read("src", "TarkovCompanion.App", "ViewModels", "V2", "MapRenderer", "MapSceneRendererViewModel.cs");

        // The width-driven compact reflow for hosts with a details column never ran (the view's
        // x:Name fields were unassigned) and was removed with the layout-cycle fix; reflow now
        // applies once, only for a host without a details column.
        Assert.Contains("RendererBody.ColumnDefinitions", view, StringComparison.Ordinal);
        Assert.Contains("ShowsDetailsPanel: false", view, StringComparison.Ordinal);
        Assert.Contains("_appliedLayoutMode == Mode", view, StringComparison.Ordinal);
        Assert.DoesNotContain("ToLocalTime", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentCulture", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentCulture", renderer, StringComparison.Ordinal);
        Assert.Contains("TimeZoneInfo.ConvertTime(value, TimeZone)", presentation, StringComparison.Ordinal);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(V2ShellTestData.RepositoryPath(segments));
}
