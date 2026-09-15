using System.Reflection;
using System.Text.RegularExpressions;
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
public sealed partial class RelayUpdateTests : IDisposable
{
    private const string Installed = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Refused = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string Published = "3333333333333333333333333333333333333333333333333333333333333333";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"tarkov-relay-update-{Guid.NewGuid():N}");

    public RelayUpdateTests()
    {
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Status);
    }

    /// <summary>The relay's own state directory, which it writes.</summary>
    private string State => Path.Combine(_root, "tarkov-group");

    /// <summary>The updater's status directory, which the relay only reads.</summary>
    private string Status => Path.Combine(_root, "tarkov-group-update-status");

    [Fact]
    public void ItReadsTheBuildTheUpdaterRecorded()
    {
        File.WriteAllText(Path.Combine(Status, "INSTALLED_SHA256"), Installed);
        var update = new RelayUpdate(State, Status);

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
        File.WriteAllText(Path.Combine(Status, "INSTALLED_SHA256"), Installed);
        File.WriteAllText(Path.Combine(Status, "REFUSED_SHA256"), Refused);
        var update = new RelayUpdate(State, Status);

        var state = update.Read();

        Assert.Equal(Refused, state.Refused);
    }

    [Fact]
    public void ARolledBackBuildIsReportedAsRefusedAndNotAsInstalled()
    {
        // RISK-RELAY-UPDATE-STATE. The updater used to write the new installed stamp before the
        // health check, so after a rollback the panel named the refused build as the one running.
        // It now leaves the previous stamp in place; this is the state the panel must read then.
        File.WriteAllText(Path.Combine(Status, "INSTALLED_SHA256"), Installed);
        File.WriteAllText(Path.Combine(Status, "PUBLISHED_SHA256"), Refused);
        File.WriteAllText(Path.Combine(Status, "REFUSED_SHA256"), Refused);
        var update = new RelayUpdate(State, Status);

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
        var update = new RelayUpdate(State, Status);

        var state = update.Read();

        Assert.Null(state.Installed);
        Assert.True(state.Available);
    }

    [Fact]
    public void AStampThatIsNotAChecksumIsIgnored()
    {
        File.WriteAllText(Path.Combine(Status, "INSTALLED_SHA256"), "this is not a checksum");
        var update = new RelayUpdate(State, Status);

        var state = update.Read();

        Assert.Null(state.Installed);
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("1111111111111111111111111111111111111111111111111111111111111111extra")]
    public void AStampThatIsNotCanonicalLowercaseSha256IsIgnored(string value)
    {
        File.WriteAllText(Path.Combine(Status, "INSTALLED_SHA256"), value);

        var state = new RelayUpdate(State, Status).Read();

        Assert.Null(state.Installed);
    }

    [Fact]
    public void ARedirectedStatusStampIsIgnored()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Path.Combine(_root, "forged");
        File.WriteAllText(target, Installed);
        File.CreateSymbolicLink(Path.Combine(Status, "INSTALLED_SHA256"), target);

        Assert.Null(new RelayUpdate(State, Status).Read().Installed);
    }

    [Fact]
    public void StampsTheRelayCouldWriteItselfAreNotReported()
    {
        // The relay's own directory once held the updater's stamps, so a compromised relay could
        // make its panel name any build it liked. Those names in that directory now mean nothing.
        foreach (var name in new[] { "INSTALLED_SHA256", "PUBLISHED_SHA256", "REFUSED_SHA256" })
        {
            File.WriteAllText(Path.Combine(State, name), Installed);
        }

        var state = new RelayUpdate(State, Status).Read();

        Assert.Null(state.Installed);
        Assert.Null(state.Published);
        Assert.Null(state.Refused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStatusDirectoryInsideTheRelaysOwnIsNotTrusted(bool nested)
    {
        var status = nested ? Path.Combine(State, "status") : State;
        Directory.CreateDirectory(status);
        File.WriteAllText(Path.Combine(status, "INSTALLED_SHA256"), Installed);
        File.WriteAllText(Path.Combine(status, "PUBLISHED_SHA256"), Published);

        var state = new RelayUpdate(State, status).Read();

        Assert.Null(state.Installed);
        Assert.Null(state.Published);
        Assert.NotNull(state.Detail);
    }

    [Fact]
    public void AStatusDirectoryRedirectedIntoTheRelaysOwnIsNotTrusted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var redirected = Path.Combine(_root, "redirected-status");
        Directory.CreateSymbolicLink(redirected, State);
        File.WriteAllText(Path.Combine(State, "INSTALLED_SHA256"), Installed);

        var state = new RelayUpdate(State, redirected).Read();

        Assert.Null(state.Installed);
        Assert.NotNull(state.Detail);
    }

    [Fact]
    public void AskingWritesTheFileThePathUnitWatches()
    {
        // The whole mechanism. This process runs unprivileged and must not be able to start a
        // unit; it writes one file in the directory it already owns, and systemd does the rest.
        var update = new RelayUpdate(State, Status);

        Assert.True(update.Request());

        Assert.True(File.Exists(Path.Combine(State, "UPDATE_NOW")));
        Assert.False(File.Exists(Path.Combine(Status, "UPDATE_NOW")));
    }

    [Fact]
    public void AskingDoesNotFollowAnExistingRequestMarkerLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Path.Combine(_root, "request-target");
        File.WriteAllText(target, "unchanged");
        File.CreateSymbolicLink(Path.Combine(State, "UPDATE_NOW"), target);

        Assert.False(new RelayUpdate(State, Status).Request());
        Assert.Equal("unchanged", File.ReadAllText(target));
    }

    [Fact]
    public void AnAskThatHasNotStartedYetIsShown()
    {
        var update = new RelayUpdate(State, Status);
        update.Request();

        var state = update.Read();

        Assert.True(state.Requested);
    }

    [Fact]
    public void ARelayWithNoStateDirectoryCannotBeAsked()
    {
        // Every local run and every test. Saying so is better than a button that writes nothing
        // and reports success.
        var update = new RelayUpdate(null, Status);

        Assert.False(update.Request());
        Assert.False(update.Read().Available);
    }

    [Fact]
    public void WithoutAnAuthenticatedDecisionNothingIsReportedAsPublished()
    {
        // Absence means the updater has not verified a signed decision. The panel must not fill
        // the gap with a checksum of its own finding and turn that into "up to date".
        File.WriteAllText(Path.Combine(Status, "INSTALLED_SHA256"), Installed);
        var update = new RelayUpdate(State, Status);

        var state = update.Read();

        Assert.Null(state.Published);
        Assert.NotNull(state.Detail);
    }

    [Fact]
    public void ItReportsThePublishedBuildTheUpdaterAuthenticated()
    {
        File.WriteAllText(Path.Combine(Status, "INSTALLED_SHA256"), Installed);
        File.WriteAllText(Path.Combine(Status, "PUBLISHED_SHA256"), Published + "\n");
        var update = new RelayUpdate(State, Status);

        var state = update.Read();

        Assert.Equal(Published, state.Published);
        Assert.Null(state.Detail);
    }

    [Fact]
    public void ThePanelCannotBeGivenAWayToAskTheNetwork()
    {
        // Structural: the only inputs are two directories. An HttpClient parameter coming back
        // would be the unauthenticated second opinion this class used to fetch.
        var constructors = typeof(RelayUpdate).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        var parameters = Assert.Single(constructors).GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.All(parameters, parameter => Assert.Equal(typeof(string), parameter.ParameterType));
    }

    [Theory]
    [InlineData("deploy/group-server/README.md")]
    [InlineData("docs/RELEASES.md")]
    public void TheRunbookNamesEveryStatusFileThePanelReads(string runbook)
    {
        // The status files left the relay's state directory, so the privacy runbook check that
        // lists that directory no longer sees them. This keeps them written down where they went.
        var source = File.ReadAllText(RepositoryFile("src/TarkovCompanion.GroupServer/RelayUpdate.cs"));
        var names = StatusFileName().Matches(source).Select(match => match.Groups["name"].Value).Distinct().ToArray();
        var text = File.ReadAllText(RepositoryFile(runbook));

        Assert.Equal(new[] { "PUBLISHED_SHA256", "INSTALLED_SHA256", "REFUSED_SHA256" }, names);
        Assert.All(names, name => Assert.Contains(name, text, StringComparison.Ordinal));
        Assert.Contains("/var/lib/tarkov-group-update-status", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusDirectoryTheRelayReadsIsTheOneTheUpdaterWrites()
    {
        var program = File.ReadAllText(RepositoryFile("src/TarkovCompanion.GroupServer/Program.cs"));
        var updater = File.ReadAllText(RepositoryFile("deploy/group-server/tarkov-group-update.sh"));

        Assert.Contains("\"/var/lib/tarkov-group-update-status\"", program, StringComparison.Ordinal);
        Assert.Contains("TARKOV_UPDATE_STATUS:-/var/lib/tarkov-group-update-status}", updater, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string RepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
            {
                return Path.Combine(directory.FullName, relativePath);
            }
        }

        throw new FileNotFoundException($"Could not locate the repository above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(@"\bReadStatus\(""(?<name>[^""]+)""\)", RegexOptions.CultureInvariant)]
    private static partial Regex StatusFileName();
}
