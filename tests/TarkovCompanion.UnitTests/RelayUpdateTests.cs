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

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"tarkov-relay-update-{Guid.NewGuid():N}");

    public RelayUpdateTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ItReadsTheBuildTheUpdaterRecorded()
    {
        File.WriteAllText(Path.Combine(_directory, "INSTALLED_SHA256"), Installed);
        var update = new RelayUpdate(_directory);

        var state = await update.ReadAsync(TimeProvider.System, CancellationToken.None);

        Assert.Equal(Installed, state.Installed);
        Assert.True(state.Available);
    }

    [Fact]
    public async Task ARefusedBuildIsReported()
    {
        // The one thing an operator cannot otherwise see. A relay that installed a build, found
        // it would not answer and rolled back is a relay that is behind and will stay behind
        // until a newer one is published, with nothing in the panel to say why.
        File.WriteAllText(Path.Combine(_directory, "INSTALLED_SHA256"), Installed);
        File.WriteAllText(Path.Combine(_directory, "REFUSED_SHA256"), Refused);
        var update = new RelayUpdate(_directory);

        var state = await update.ReadAsync(TimeProvider.System, CancellationToken.None);

        Assert.Equal(Refused, state.Refused);
    }

    [Fact]
    public async Task ABuildTheUpdaterHasNeverRecordedIsNotInvented()
    {
        // A relay installed by hand has no stamp. Saying nothing is the honest answer; saying
        // "up to date" would be a claim nothing supports.
        var update = new RelayUpdate(_directory);

        var state = await update.ReadAsync(TimeProvider.System, CancellationToken.None);

        Assert.Null(state.Installed);
        Assert.True(state.Available);
    }

    [Fact]
    public async Task AStampThatIsNotAChecksumIsIgnored()
    {
        File.WriteAllText(Path.Combine(_directory, "INSTALLED_SHA256"), "this is not a checksum");
        var update = new RelayUpdate(_directory);

        var state = await update.ReadAsync(TimeProvider.System, CancellationToken.None);

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
    public async Task AnAskThatHasNotStartedYetIsShown()
    {
        var update = new RelayUpdate(_directory);
        update.Request();

        var state = await update.ReadAsync(TimeProvider.System, CancellationToken.None);

        Assert.True(state.Requested);
    }

    [Fact]
    public async Task ARelayWithNoStateDirectoryCannotBeAsked()
    {
        // Every local run and every test. Saying so is better than a button that writes nothing
        // and reports success.
        var update = new RelayUpdate(null);

        Assert.False(update.Request());
        Assert.False((await update.ReadAsync(TimeProvider.System, CancellationToken.None)).Available);
    }

    [Fact]
    public async Task APublishedChecksumThatCannotBeReadIsNotTreatedAsUpToDate()
    {
        // A relay that cannot reach GitHub is a relay that is not updating, which is worth
        // knowing on the same page as the build it is stuck on.
        File.WriteAllText(Path.Combine(_directory, "INSTALLED_SHA256"), Installed);
        var update = new RelayUpdate(_directory);

        var state = await update.ReadAsync(TimeProvider.System, CancellationToken.None);

        Assert.Null(state.Published);
        Assert.NotNull(state.Detail);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
