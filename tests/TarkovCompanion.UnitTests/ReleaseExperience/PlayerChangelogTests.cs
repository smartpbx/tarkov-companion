using TarkovCompanion.Application.Services.ReleaseExperience;

namespace TarkovCompanion.UnitTests.ReleaseExperience;

public sealed class PlayerChangelogTests
{
    [Fact]
    public void Reads_the_named_build_and_tolerates_future_fields()
    {
        var changelog = PlayerChangelog.Parse("""
            {
              "schemaVersion": 1,
              "future": true,
              "releases": [
                {
                  "version": "2.0.42",
                  "changes": ["See the route before the raid."],
                  "anotherFutureField": "ignored"
                }
              ]
            }
            """);

        var release = Assert.IsType<PlayerChangelogRelease>(changelog.Find("2.0.42"));
        Assert.Equal(["See the route before the raid."], release.Changes);
        Assert.Null(changelog.Find("2.0.41"));
    }

    [Fact]
    public void Refuses_an_unbounded_player_line()
    {
        var line = new string('x', PlayerChangelog.MaximumChangeLength + 1);
        var json = $$"""{"schemaVersion":1,"releases":[{"version":"2.0.42","changes":["{{line}}"]}]}""";

        var error = Assert.Throws<InvalidDataException>(() => PlayerChangelog.Parse(json));

        Assert.Contains("at most", error.Message, StringComparison.Ordinal);
    }
}
