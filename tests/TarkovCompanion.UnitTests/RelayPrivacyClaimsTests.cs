using System.Text.RegularExpressions;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What operators and players are told the relay keeps, checked against what it writes.
/// </summary>
/// <remarks>
/// The operations runbook said the relay stored "the squad's waypoints, and nothing else" while
/// the same state directory held the room registry, every problem report as sent, and the
/// updater's stamps. The Settings caption and the hourly issue text both promised a report had
/// no coordinates while its log tail named screenshots in full. Nobody reads a runbook against
/// the server's source, so this does; the open gaps are RISK-REPORT-REDACTION and the #317
/// relay audit.
///
/// The observed party data had the same problem after its first correction. The server README
/// and the contract remarks said an observation was handed only to the person it named, while
/// GroupRooms.Read returns every surviving entry to every member's exchange and to a keyed
/// GET /state, and the desktop fills other members' kit from them. The open gap is
/// RISK-RELAY-OBSERVED-DATA-POLICY (#310).
/// </remarks>
public sealed partial class RelayPrivacyClaimsTests
{
    /// <summary>Every server source that names a file in the state directory.</summary>
    private static readonly string[] StateSources = ["Program.cs", "ProblemReports.cs", "RelayUpdate.cs"];

    /// <summary>
    /// A member who is not described, and a second screen that is not a member, both receive an
    /// observation about somebody else in the room.
    /// </summary>
    /// <remarks>
    /// Measures what the claims below describe rather than trusting them. If the relay ever hands
    /// an entry only to the player it names, this fails on purpose, and the README, contract and
    /// client remarks must change with it.
    /// </remarks>
    [Fact]
    public void ObservedPartyDataReachesEveryRoomKeyHolderNotOnlyThePersonItNames()
    {
        var rooms = new GroupRooms(TimeProvider.System);
        rooms.Publish("room", "Geo", Member("Geo"));
        rooms.Publish("room", "Max", Member("Max"));
        rooms.Publish("room", "Clay", Member("Clay") with
        {
            Observed = [new("Geo", ["Slick", "Altyn"]) { Level = 42, Side = "Usec" }],
        });

        // Max's own POST /state answer, and the second screen's GET /state read.
        var toMax = rooms.Read("room", "Max").Members.SelectMany(member => member.Observed).ToArray();
        var toSecondScreen = rooms.Read("room", string.Empty).Members.SelectMany(member => member.Observed).ToArray();

        Assert.Equal("Geo", Assert.Single(toMax).Name);
        Assert.Equal(42, Assert.Single(toSecondScreen).Level);

        // The second read above is only GET /state while the route still asks for everybody.
        var program = File.ReadAllText(RepositoryFile("src/TarkovCompanion.GroupServer/Program.cs"));
        Assert.Contains("rooms.Read(room, exceptMemberKey: string.Empty)", program, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("src/TarkovCompanion.GroupServer/README.md")]
    [InlineData("src/TarkovCompanion.GroupServer/GroupContracts.cs")]
    [InlineData("src/TarkovCompanion.Application/Services/Group/GroupKitMirror.cs")]
    [InlineData("src/TarkovCompanion.Application/Services/Group/GroupSessionService.cs")]
    public void NothingSaysObservedPartyDataReachesOnlyThePersonItNames(string path)
    {
        var prose = Prose(File.ReadAllText(RepositoryFile(path)));

        Assert.DoesNotMatch(RecipientOnly(), prose);
        Assert.Matches(EveryKeyHolder(), prose);
    }

    [Theory]
    [InlineData("docs/OPERATIONS.md")]
    [InlineData("deploy/group-server/README.md")]
    public void ARunbookNamesEveryFileTheRelayKeepsInItsStateDirectory(string runbook)
    {
        var kept = StateFileNames();
        var text = File.ReadAllText(RepositoryFile(runbook));

        // A pattern that stopped matching the source would pass everything below vacuously.
        Assert.Contains("marks.json", kept);
        Assert.Contains("rooms.json", kept);
        Assert.Contains("reports", kept);
        Assert.All(kept, name => Assert.Contains(name, text, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("src/TarkovCompanion.App/Views/Pages/SettingsView.axaml")]
    [InlineData(".github/workflows/relay-watch.yml")]
    public void NothingShownAboutAReportPromisesItHasNoCoordinates(string path)
    {
        // Only the words somebody is shown, a XAML attribute or a quoted line of issue text,
        // and not the comments beside them explaining why the words changed.
        var shown = ShownText().Matches(File.ReadAllText(RepositoryFile(path)))
            .Select(match => match.Groups["text"].Value)
            .Where(text => text.Contains("coordinates", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(shown);
        Assert.All(shown, text => Assert.DoesNotMatch(Denial(), text));
    }

    /// <summary>The file names the relay's source writes into, or reads from, its state directory.</summary>
    private static string[] StateFileNames() =>
    [
        .. StateSources
            .Select(file => File.ReadAllText(RepositoryFile($"src/TarkovCompanion.GroupServer/{file}")))
            .SelectMany(source => StateFileName().Matches(source))
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal),
    ];

    private static GroupMemberState Member(string name) =>
        new(name, "bigmap", "InRaid", "pmc", 1, 2, 90, 0, [], []);

    /// <summary>A file's text with comment markers removed and every line break collapsed.</summary>
    /// <remarks>A claim wrapped across two remark lines has to read as one sentence to be matched.</remarks>
    private static string Prose(string text) =>
        Whitespace().Replace(CommentMarker().Replace(text, " "), " ");

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

    [GeneratedRegex(@"\b(?:StorePath|Read)\(""(?<name>[^""]+)""\)|Path\.Combine\([^,()]+,\s*""(?<name>[^""]+)""\)", RegexOptions.CultureInvariant)]
    private static partial Regex StateFileName();

    [GeneratedRegex(@"(?:Text|Tip)=""(?<text>[^""]*)""|^\s*""(?<text>[^""]*)""\s*\\?\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ShownText();

    [GeneratedRegex(@"\b(?:no|never|nor|without)\b[^.]*\bcoordinates\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Denial();

    /// <summary>The phrasings that said an observation reached only the player it described.</summary>
    [GeneratedRegex(@"only ever handed back to the person|hands them to those people|handed to those people by name|reach(?:es)? the screen of the person it is about|onto the screen of the person it is about|receives theirs whatever|nothing here reaches anybody who was not already", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RecipientOnly();

    [GeneratedRegex(@"\b(?:every|any)(?:one|body)?\s+(?:holder\s+of\s+the\s+room\s+key|holding\s+the\s+room\s+key|key\s+holder)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EveryKeyHolder();

    [GeneratedRegex(@"^\s*(?:///?|\*)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex CommentMarker();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
