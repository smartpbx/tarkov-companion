using System.Text.RegularExpressions;

namespace TarkovCompanion.UnitTests.Release;

/// <summary>
/// [#279] The Windows run has to be able to stop a build being published.
/// </summary>
/// <remarks>
/// Branch protection is a repository setting; no test can read it and no workflow can enforce
/// it. What a test can hold is the list in <c>scripts/require-checks.sh</c>, which is what an
/// operator applies and what <c>scripts/release/capture_controls.py</c> is compared against.
///
/// `windows-verify` was a required merge check twice and was removed twice. It catches a real
/// fault — "the packaged application does not open a window", three times in one day — but as
/// a merge gate on a trunk that required current branches it cost a Windows run per pull
/// request per merge. The gate is kept where the harm is instead: the workflow runs on every
/// push to <c>main</c>, and <c>publish.yml</c> takes nothing but a successful run of it. The
/// first test below holds that trigger in place, because removing it would leave nothing at
/// all between a broken package and a machine that installs it.
///
/// The names are also checked against the workflows, because a required context that names no
/// job never reports at all: every pull request then waits forever for a check that will never
/// arrive, which looks like an outage rather than a typo.
/// </remarks>
public sealed class RequiredChecksTests
{
    private static readonly string[] WorkflowFiles =
    [
        ".github/workflows/ci.yml",
        ".github/workflows/windows-verify.yml",
    ];

    [Fact]
    public void The_packaged_Windows_run_still_runs_on_every_push_to_main()
    {
        var workflow = ReadRepositoryFile(".github/workflows/windows-verify.yml");

        Assert.Matches(
            new Regex(@"^on:\n(?:(?!^\S).*\n)*?  push:\n    branches:\n      - main\n", RegexOptions.Multiline),
            workflow.Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_pull_request_does_not_wait_for_a_Windows_run()
    {
        Assert.DoesNotContain("windows-verify", DeclaredChecks());
        Assert.DoesNotContain("checks", DeclaredChecks());
    }

    [Fact]
    public void Every_required_check_names_a_job_that_exists()
    {
        var jobs = WorkflowFiles.SelectMany(JobIdsIn).ToHashSet(StringComparer.Ordinal);

        foreach (var check in DeclaredChecks())
        {
            Assert.True(
                jobs.Contains(check),
                $"'{check}' is required to merge but no job in {string.Join(" or ", WorkflowFiles)} " +
                "produces a check by that name, so every pull request would wait for it forever.");
        }
    }

    /// <summary>The list as <c>scripts/require-checks.sh</c> states it.</summary>
    private static IReadOnlyList<string> DeclaredChecks()
    {
        var script = ReadRepositoryFile("scripts/require-checks.sh");
        var declaration = Regex.Match(script, @"^REQUIRED=\(([^)]*)\)", RegexOptions.Multiline);
        Assert.True(declaration.Success, "scripts/require-checks.sh no longer declares REQUIRED=( ... ).");

        var checks = declaration.Groups[1].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(checks);
        return checks;
    }

    /// <summary>Top-level job ids, which are the check names GitHub reports for a workflow.</summary>
    private static IEnumerable<string> JobIdsIn(string relativePath)
    {
        var inJobs = false;
        foreach (var line in ReadRepositoryFile(relativePath).Split('\n'))
        {
            if (line.StartsWith("jobs:", StringComparison.Ordinal))
            {
                inJobs = true;
                continue;
            }

            if (!inJobs || line.Length == 0)
            {
                continue;
            }

            // A new top-level key ends the jobs block; a job id is indented exactly two spaces.
            if (!char.IsWhiteSpace(line[0]))
            {
                inJobs = false;
                continue;
            }

            var job = Regex.Match(line, @"^  ([A-Za-z0-9_-]+):\s*(#.*)?$");
            if (job.Success)
            {
                yield return job.Groups[1].Value;
            }
        }
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
            {
                var path = Path.Combine(directory.FullName, relativePath);
                Assert.True(File.Exists(path), $"{relativePath} is not where this test expects it.");
                return File.ReadAllText(path);
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test output.");
    }
}
