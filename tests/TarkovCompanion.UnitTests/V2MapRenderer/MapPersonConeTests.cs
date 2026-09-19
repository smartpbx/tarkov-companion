using System.Text.RegularExpressions;
using System.Xml.Linq;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [V2 rough package 46] The facing cone turns about the dot it belongs to.
/// </summary>
/// <remarks>
/// Reported as "the facing cones are not attached to his or his teammates' dots". The cone is a
/// <c>Path</c> whose geometry is written with its apex at (22,22) — the centre of the 44-pixel
/// marker box — and turned by the recorded heading about <c>RenderTransformOrigin="50%,50%"</c>.
/// A Path with no explicit size measures to its own geometry, which for this cone is 36 by 22, so
/// "50%" meant (18,11) and the turn swung the apex off the dot by up to 23 pixels. V1 never showed
/// it because V1 turns a Panel that fills the marker square.
///
/// Two things have to stay true for the cone to stay attached, and neither can be seen from the
/// other file: the geometry's apex is the centre of the box, and the markup gives the Path that
/// box. Layout itself needs a rendering platform, so what is checked here is the pair.
/// </remarks>
public sealed class MapPersonConeTests
{
    private const string ViewPath = "src/TarkovCompanion.App/Views/V2/MapRenderer/MapSceneRendererView.axaml";

    [Fact]
    public void The_cone_apex_is_the_centre_of_the_marker_box()
    {
        var cone = MapSceneRendererObjectViewModel.PersonConeGeometry;
        var apex = Regex.Match(cone, @"^M\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)");
        Assert.True(apex.Success, $"The cone geometry does not start with a move: '{cone}'.");
        Assert.Equal(MapSceneRendererViewModel.MarkerExtent / 2, double.Parse(apex.Groups[1].Value), 3);
        Assert.Equal(MapSceneRendererViewModel.MarkerExtent / 2, double.Parse(apex.Groups[2].Value), 3);
    }

    [Fact]
    public void The_cone_path_is_given_the_whole_marker_box_to_turn_in()
    {
        // Read from the markup, because this is a markup fault: a Path that is not told its size
        // takes its geometry's, and then the centre it turns about is not the apex.
        var view = XDocument.Load(RepositoryFile(ViewPath));
        var cone = view.Descendants()
            .Single(element =>
                element.Name.LocalName == "Path" &&
                element.Attribute("Classes")?.Value.Contains("v2-map-person-cone", StringComparison.Ordinal) == true);
        Assert.Equal(MapSceneRendererViewModel.MarkerExtent, double.Parse(cone.Attribute("Width")!.Value), 3);
        Assert.Equal(MapSceneRendererViewModel.MarkerExtent, double.Parse(cone.Attribute("Height")!.Value), 3);
        Assert.Equal("None", cone.Attribute("Stretch")!.Value);
        Assert.Equal("50%,50%", cone.Attribute("RenderTransformOrigin")!.Value);
    }

    private static string RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, relativePath);
        Assert.True(File.Exists(path), $"{relativePath} is not where this test expects it.");
        return path;
    }
}
