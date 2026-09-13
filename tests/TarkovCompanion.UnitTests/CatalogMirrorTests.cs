using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What the group server will and will not fetch on the group's behalf.
/// </summary>
public sealed class CatalogMirrorTests
{
    [Theory]
    [InlineData("regular", "items")]
    [InlineData("regular", "maps")]
    [InlineData("pve", "tasks")]
    [InlineData("pve", "hideout")]
    public void Serves_the_upstream_surface_the_client_actually_uses(string mode, string endpoint) =>
        Assert.True(CatalogMirror.IsAllowed(mode, endpoint));

    [Theory]
    // An open proxy on a public address is somebody else's bandwidth bill, and "it only
    // forwards to one host" stops being true the first time the path is built from input.
    [InlineData("regular", "../../etc/passwd")]
    [InlineData("..", "items")]
    [InlineData("regular", "items?callback=http://elsewhere")]
    [InlineData("https://elsewhere.invalid", "items")]
    [InlineData("regular", "")]
    [InlineData("", "items")]
    [InlineData("REGULAR", "items")]
    [InlineData("regular", "Items")]
    [InlineData("seasonal", "items")]
    [InlineData("regular", "everything")]
    public void Refuses_anything_that_is_not_on_the_list(string mode, string endpoint) =>
        Assert.False(CatalogMirror.IsAllowed(mode, endpoint));

    [Fact]
    public void Refuses_a_missing_path_rather_than_throwing()
    {
        Assert.False(CatalogMirror.IsAllowed(null, "items"));
        Assert.False(CatalogMirror.IsAllowed("regular", null));
    }
}
