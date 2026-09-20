using System.Text.RegularExpressions;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.PlayerTime;

/// <summary>
/// A ratchet on the one rule: nothing outside <c>LocalTime</c> decides what clock a person reads.
/// </summary>
/// <remarks>
/// The bug this guards was not one wrong line but a missing rule. Each view model chose for itself,
/// so some pages called <c>ToLocalTime</c>, some printed <c>:u</c>, and the self-test stuck "UTC"
/// after a stored time, and every test passed on a UTC CI box where the two look identical. These
/// scans fail the moment a new call site goes around the helper, which is cheaper than another
/// screen quietly reading four hours out. They read source rather than behaviour on purpose: a
/// behavioural test can only cover the screens somebody remembered to construct.
/// </remarks>
public sealed class LocalTimeRuleContractTests
{
    private static readonly string[] PresentationProjects =
    [
        "TarkovCompanion.App",
        "TarkovCompanion.Application",
        "TarkovCompanion.Core",
        "TarkovCompanion.Infrastructure",
    ];

    [Fact]
    public void Nothing_converts_to_the_machine_zone_except_through_the_helper()
    {
        // The helper reads TimeZoneInfo.Local exactly once, and only there.
        AssertNoMatch(new Regex(@"\.ToLocalTime\(\)"), "convert with LocalTime, not ToLocalTime()");
        AssertNoMatch(new Regex(@"GetLocalNow\("), "convert with LocalTime, not TimeProvider.GetLocalNow()");
        AssertNoMatch(new Regex(@"DateTime(Offset)?\.Now\b"), "take UtcNow and convert with LocalTime");
        AssertNoMatch(
            new Regex(@"TimeZoneInfo\.Local\b"),
            "ask LocalTime.Zone for the player's zone",
            allowed: path => path.EndsWith("LocalTime.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void No_screen_prints_a_utc_clock_time_or_a_bare_utc_label()
    {
        AssertNoMatch(new Regex(@"ToString\(""u""\)|:u\}"), "the sortable-UTC format prints UTC to a local reader");
        AssertNoMatch(new Regex(@"\}\s*UTC\b"), "a time followed by UTC is a UTC time on a screen; use LocalTime");
    }

    private static void AssertNoMatch(Regex pattern, string instead, Func<string, bool>? allowed = null)
    {
        var offenders = new List<string>();
        foreach (var project in PresentationProjects)
        {
            var root = V2ShellTestData.RepositoryPath("src", project);
            foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(path);
                if ((extension is not ".cs" and not ".axaml")
                    || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || allowed?.Invoke(path) == true)
                {
                    continue;
                }

                var lines = File.ReadAllLines(path);
                for (var index = 0; index < lines.Length; index++)
                {
                    var line = lines[index];
                    var trimmed = line.TrimStart();
                    if (!trimmed.StartsWith("//", StringComparison.Ordinal)
                        && !trimmed.StartsWith("///", StringComparison.Ordinal)
                        && !trimmed.StartsWith('*')
                        && pattern.IsMatch(line))
                    {
                        offenders.Add($"{Path.GetRelativePath(V2ShellTestData.RepositoryPath("src"), path)}:{index + 1}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, $"{instead}:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }
}
