using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Where marker names end up once they have to avoid each other.
/// </summary>
/// <remarks>
/// Reported with a screenshot: on Customs "Sniper Roadblock" sat across the marker above it
/// and across the name below it. Names were drawn at a fixed offset under their own disc with
/// no knowledge of their neighbours, and the only mitigation was hiding all of them below a
/// zoom threshold.
/// </remarks>
public sealed class MapLabelLayoutTests
{
    private const double Height = 17;

    [Fact]
    public void NamesWithRoomAroundThemStayUnderTheirOwnDisc()
    {
        var slots = MapLabelLayout.Arrange(
            [Label(0, 0), Label(400, 0), Label(0, 300)],
            zoom: 1);

        Assert.Equal([0, 0, 0], slots);
    }

    /// <summary>Two markers close enough that one name would cover the other's disc.</summary>
    [Fact]
    public void ANameStepsAsideRatherThanCoveringTheMarkerBelowIt()
    {
        var slots = MapLabelLayout.Arrange([Label(0, 0), Label(0, 25)], zoom: 1);

        Assert.NotEqual(MapLabelLayout.Hidden, slots[0]);
        Assert.NotEqual(MapLabelLayout.Hidden, slots[1]);
        Assert.NotEqual(slots[0], slots[1]);
    }

    /// <summary>
    /// The arrangement is a function of zoom, because the names do not shrink with the map.
    /// </summary>
    /// <remarks>
    /// Two markers that sit apart at full size are on top of each other at a quarter of it,
    /// while their names are the same width either way. This is the whole reason the old fixed
    /// offset could not work.
    /// </remarks>
    [Fact]
    public void PullingTheMapBackChangesWhoCollidesWithWhom()
    {
        IReadOnlyList<MapLabelCandidate> labels = [Label(0, 0), Label(120, 0)];

        Assert.Equal([0, 0], MapLabelLayout.Arrange(labels, zoom: 1));
        Assert.NotEqual([0, 0], MapLabelLayout.Arrange(labels, zoom: 0.1));
    }

    [Fact]
    public void NoTwoNamesEverOverlapHoweverCrowdedItIs()
    {
        var labels = Enumerable.Range(0, 14)
            .Select(index => Label(index % 3 * 6, index * 3, width: 140))
            .ToArray();

        var slots = MapLabelLayout.Arrange(labels, zoom: 1);

        var placed = new List<(double Left, double Top, double Right, double Bottom)>();
        for (var index = 0; index < labels.Length; index++)
        {
            if (slots[index] == MapLabelLayout.Hidden)
            {
                continue;
            }

            var rect = RectFor(labels[index], slots[index]);
            Assert.All(placed, other => Assert.False(
                rect.Left < other.Right && rect.Right > other.Left &&
                rect.Top < other.Bottom && rect.Bottom > other.Top,
                "two names overlap"));
            placed.Add(rect);
        }

        Assert.NotEmpty(placed);
    }

    /// <summary>
    /// When something has to be dropped, the extract the player was offered is not it.
    /// </summary>
    [Fact]
    public void TheNameThatMattersMostSurvivesTheCrush()
    {
        var labels = Enumerable.Range(0, 14)
            .Select(index => Label(0, index * 3, width: 160, priority: index == 13 ? 2 : 0))
            .ToArray();

        var slots = MapLabelLayout.Arrange(labels, zoom: 1);

        Assert.NotEqual(MapLabelLayout.Hidden, slots[13]);
        Assert.Contains(MapLabelLayout.Hidden, slots);
    }

    /// <summary>Before a zoom is known, nothing is moved rather than everything guessed at.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void AnUnknownZoomLeavesEveryNameWhereItWas(double zoom) =>
        Assert.Equal([0, 0], MapLabelLayout.Arrange([Label(0, 0), Label(0, 1)], zoom));

    [Fact]
    public void NothingToArrangeIsNotAnError() =>
        Assert.Empty(MapLabelLayout.Arrange([], zoom: 1));

    private static MapLabelCandidate Label(double x, double y, double width = 60, int priority = 0) =>
        new(x, y, width, Height, priority);

    private static (double Left, double Top, double Right, double Bottom) RectFor(
        MapLabelCandidate label,
        int slot)
    {
        var top = label.CenterY + MapLabelLayout.TopOffsetFor(slot, label.Height);
        return (label.CenterX - (label.Width / 2), top, label.CenterX + (label.Width / 2), top + label.Height);
    }
}
