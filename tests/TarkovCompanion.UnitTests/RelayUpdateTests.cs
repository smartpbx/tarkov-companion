using System.Reflection;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What build the relay is on, and asking it for a newer one.
/// </summary>
/// <remarks>
/// The relay has updated itself every half hour for a while and could say nothing about it. A
/// relay running an old build looked exactly like one running the newest, and one that installed
/// a build, failed its health check and rolled back looked like both: the updater records the
/// refusal so it does not loop, and nothing surfaced it.
/// </remarks>
public sealed class RelayUpdateTests : IDisposable
{
    private const string Installed = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Refused = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string Published = "3333333333333333333333333333333333333333333333333333333333333333";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"tarkov-relay-update-{Guid.NewGuid():N}");

    public RelayUpdateTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ItReadsTheBuildTheUpdaterRecorded()
    {
        File.WriteAllText(Path.Combine(_directory, "INSTALLED_SHA256"), Installed);
        var update = new RelayUpdate(_directory);

        var state = update.Read();

        Assert.Equal(Installed, state.Installed);
        Assert.True(state.Available);
    }

    [Fact]
    public void ARefusedBuildIsReported()
    {
        // The one thing an operator cannot otherwise see. A relay that installed a build, found
        // it would not answer and rolled back is a relay that is behind and will stay behind
        // until the ring publishes a new decision, with nothing in the panel to say why.
        File.WriteAllText(Path.Combine(_directory, "INSTALLED_SHA256"), Installed);
        File.WriteAllText(Path.Combine(_directory, "REFUSED_SHA256"), Refused);
        var update = new RelayUpdate(_directory);

        var state = update.Read();

        Assert.Equal(Refused, state.Refused);
    }

    [Fact]
    public void ARolledBackBuildIsReportedAsRefusedAndNotAsInstalled()
    {
        // RISK-RELAY-UPDATE-STATE. The updater used to write the new installed stamp before the
        // health check, so after a rollback the panel named the refused build as the one running.
        // It now leaves the previous stamp in place; this is the state the panel must read then.
        File.WriteAllText(Path.Combine(_directory, "INSTALLED_SHA256"), Installed);
        File.WriteAllText(Path.Combine(_directory, "PUBLISHED_SHA256"), Refused);
        File.WriteAllText(Path.Combine(_directory, "REFUSED_SHA256"), Refused);
        var update = new RelayUpdate(_directory);

        var state = update.Read();

        Assert.Equal(Installed, state.Installed);
        Assert.Equal(Refused, state.Published);
        Assert.Equal(state.Published, state.Refused);
        Assert.NotEqual(state.Installed, state.Published);
    }

    [Fact]
    public void ABuildTheUpdaterHasNeverRecordedIsNotInvented()
    {
        // A relay installed by hand has no stamp. Saying nothing is the honest answer; saying
        // "up to date" would be a claim nothing supports.
        var update = new RelayUpdate(_directory);

        var state = update.Read();

        Assert.Null(state.Installed);
        Assert.True(state.Available);
    }

    [Fact]
    public void AStampThatIsNotAChecksumIsIgnored()
    {
        File.WriteAllText(Path.Combine(_directory, "INSTALLED_SHA256"), "this is not a checksum");
        var update = new RelayUpdate(_directory);

        var state = update.Read();

        Assert.Null(state.Installed);
    }

    [Fact]
    public void AskingWritesTheFileThePathUnitWatches()
    {
        // The whole mechanism. This process runs unprivileged and must not be able to start a
        // unit; it writes one file in the directory it already owns, and systemd does the rest.
        var update = new RelayUpdate(_directory);

        Assert.True(update.Request());

        Assert.True(File.Exists(Path.Combine(_directory, "UPDATE_NOW")));
    }

    [Fact]
    public void AnAskThatHasNotStartedYetIsShown()
    {
        var update = new RelayUpdate(_directory);
        update.Request();

        var state = update.Read();

        Assert.True(state.Requested);
    }

    [Fact]
    public void ARelayWithNoStateDirectoryCannotBeAsked()
    {
        // Every local run and every test. Saying so is better than a button that writes nothing
        // and reports success.
        var update = new RelayUpdate(null);

        Assert.False(update.Request());
        Assert.False(update.Read().Available);
    }

    [Fact]
    public void WithoutAnAuthenticatedDecisionNothingIsReportedAsPublished()
    {
        // Absence means the updater has not verified a signed decision. The panel must not fill
        // the gap with a checksum of its own finding and turn that into "up to date".
        File.WriteAllText(Path.Combine(_directory, "INSTALLED_SHA256"), Installed);
        var update = new RelayUpdate(_directory);

        var state = update.Read();

        Assert.Null(state.Published);
        Assert.NotNull(state.Detail);
    }

    [Fact]
    public void ItReportsThePublishedBuildTheUpdaterAuthenticated()
    {
        File.WriteAllText(Path.Combine(_directory, "INSTALLED_SHA256"), Installed);
        File.WriteAllText(Path.Combine(_directory, "PUBLISHED_SHA256"), Published + "\n");
        var update = new RelayUpdate(_directory);

        var state = update.Read();

        Assert.Equal(Published, state.Published);
        Assert.Null(state.Detail);
    }

    [Fact]
    public void ThePanelCannotBeGivenAWayToAskTheNetwork()
    {
        // Structural: the only input is the state directory. An HttpClient parameter coming back
        // would be the unauthenticated second opinion this class used to fetch.
        var constructors = typeof(RelayUpdate).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        var parameters = Assert.Single(constructors).GetParameters();
        Assert.Equal(typeof(string), Assert.Single(parameters).ParameterType);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
