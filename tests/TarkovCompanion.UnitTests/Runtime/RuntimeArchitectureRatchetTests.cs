using System.Text.RegularExpressions;
using TarkovCompanion.Application.Services.Execution;

namespace TarkovCompanion.UnitTests.Runtime;

public sealed partial class RuntimeArchitectureRatchetTests
{
    [Fact]
    public void OwnedRuntimePathsHaveNoUnsupervisedOrWallClockEscapeHatches()
    {
        foreach (var file in OwnedSourceFiles())
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("Task.Run(", source, StringComparison.Ordinal);
            Assert.DoesNotMatch(DiscardedTask(), source);
            Assert.DoesNotContain("CancellationToken.None", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DateTimeOffset.UtcNow", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CancelAfter(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Threading.Channels", source, StringComparison.Ordinal);
            foreach (Match delay in DelayCall().Matches(source))
            {
                Assert.Contains("timeProvider", delay.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void RuntimeOutboxIsClosedAndDoesNotAbsorbPairedDeviceCommands()
    {
        Assert.Equal(
            ["RaidStarted", "RaidEventRecorded", "RaidEnded"],
            Enum.GetNames<OutboxCommandKind>());
        Assert.DoesNotContain(
            typeof(OutboxItem).GetProperties(),
            property => property.Name.Contains("Coordinate", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Position", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Player", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(OutboxPayload).GetMethods(),
            method => method.IsPublic && method.Name.Contains("Typed", StringComparison.Ordinal));
    }

    private static IEnumerable<string> OwnedSourceFiles()
    {
        var root = RepositoryRoot();
        var execution = Path.Combine(root, "src", "TarkovCompanion.Application", "Services", "Execution");
        foreach (var file in Directory.EnumerateFiles(execution, "*.cs", SearchOption.AllDirectories))
        {
            yield return file;
        }

        var runtime = Path.Combine(root, "src", "TarkovCompanion.Application", "Services", "Runtime");
        foreach (var name in new[]
                 {
                     "ApplicationStartupCoordinator.cs",
                     "RaidActivityCoordinator.cs",
                     "RaidHistoryOutbox.cs",
                     "RuntimeState.cs",
                 })
        {
            yield return Path.Combine(runtime, name);
        }
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "TarkovCompanion.Application")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("The repository root was not found.");
    }

    [GeneratedRegex(@"Task\.Delay\((.*?)\)", RegexOptions.Singleline)]
    private static partial Regex DelayCall();

    [GeneratedRegex(@"(?m)\b_\s*=\s*(?!>)")]
    private static partial Regex DiscardedTask();
}
