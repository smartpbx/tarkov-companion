using System.Text.RegularExpressions;

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
/// </remarks>
public sealed partial class RelayPrivacyClaimsTests
{
    /// <summary>Every server source that names a file in the state directory.</summary>
    private static readonly string[] StateSources = ["Program.cs", "ProblemReports.cs", "RelayUpdate.cs"];

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
}
