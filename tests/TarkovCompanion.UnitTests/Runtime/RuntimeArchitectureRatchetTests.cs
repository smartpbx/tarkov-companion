using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

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
            Assert.DoesNotContain("DateTime.UtcNow", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DateTime.Now", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CancelAfter(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Thread.Sleep(", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".GetAwaiter().GetResult()", source, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Threading.Channels", source, StringComparison.Ordinal);
            foreach (Match delay in DelayCall().Matches(source))
            {
                Assert.Contains("timeProvider", delay.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Disposing a registration waits for its callback, and the pending-cancellation callback
    /// takes the scheduler lock. Doing it while holding that lock is a deadlock waiting to
    /// happen; only the non-blocking unregister is allowed.
    /// </summary>
    [Fact]
    public void TheSchedulerNeverDisposesACancellationRegistration()
    {
        var source = File.ReadAllText(Path.Combine(ExecutionDirectory(), "BackgroundWorkSupervisor.cs"));

        Assert.DoesNotContain("Registration.Dispose()", source, StringComparison.Ordinal);
        Assert.Contains(".Unregister()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShutdownHasNoTerminalStateThatClaimsUnfinishedWorkEnded()
    {
        Assert.DoesNotContain("StopTimedOut", Enum.GetNames<BackgroundWorkState>());
        Assert.DoesNotContain("StopTimedOut", Enum.GetNames<FeatureLifecycleState>());
        Assert.Contains(
            nameof(SupervisorStopResult.UnfinishedOperations),
            typeof(SupervisorStopResult).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public void RuntimeOutboxCommandsArePinnedClosedAndBatchAccepted()
    {
        // Values are persisted by a durable store, so a new command is appended, never inserted.
        Assert.Equal(
            [
                ("RaidStarted", 1),
                ("RaidStateRecorded", 2),
                ("RaidEnded", 3),
                ("RaidPositionRecorded", 4),
                ("RaidExtractsRecorded", 5),
                ("RaidScanRecorded", 6),
                ("RaidSaleRecorded", 7),
                ("RaidQuestRecorded", 8),
            ],
            Enum.GetValues<OutboxCommandKind>().Select(kind => (kind.ToString(), (int)kind)));
        Assert.DoesNotContain(
            typeof(OutboxItem).GetProperties(),
            property => property.Name.Contains("Coordinate", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Position", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Player", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(OutboxPayload).GetMethods(),
            method => method.IsPublic && method.Name.Contains("Typed", StringComparison.Ordinal));
        Assert.Contains(
            typeof(IOutboxStore).GetMethods(),
            method => method.Name == nameof(IOutboxStore.EnqueueBatchAsync));
        var snapshotProperties = typeof(OutboxSnapshot).GetProperties().Select(property => property.Name).ToArray();
        Assert.Contains(nameof(OutboxSnapshot.LastPumpFault), snapshotProperties);
        Assert.Contains(nameof(OutboxSnapshot.DeadLetters), snapshotProperties);
        Assert.Contains(nameof(OutboxSnapshot.LastAcceptanceFault), snapshotProperties);
    }

    /// <summary>
    /// The outbox once carried a generic JSON string inside a typed payload, which let exact
    /// coordinates and screenshot filenames past its privacy boundary. Every payload is now a
    /// closed record of plain values, and this keeps it that way.
    /// </summary>
    [Fact]
    public void RaidHistoryPayloadsCarryNoGenericJsonOrScreenshotNames()
    {
        var payloads = typeof(RaidHistoryOutbox)
            .GetNestedTypes(BindingFlags.NonPublic)
            .Where(type => type.Name.EndsWith("Payload", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            [
                "EndedPayload",
                "ExtractPayload",
                "ExtractsPayload",
                "PositionPayload",
                "QuestPayload",
                "SalePayload",
                "ScanPayload",
                "StartedPayload",
                "StatePayload",
            ],
            payloads.Select(type => type.Name).Order(StringComparer.Ordinal));
        foreach (var payload in payloads)
        {
            foreach (var property in payload.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.True(
                    IsClosedPayloadType(property.PropertyType, payloads),
                    $"{payload.Name}.{property.Name} is not a closed payload value.");
                foreach (var forbidden in new[] { "Json", "Filename", "FileName", "Path", "Screenshot", "Url", "Uri" })
                {
                    Assert.DoesNotContain(forbidden, property.Name, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void RaidHistoryCommandsAreAClosedHierarchyWithNoGenericEventRoute()
    {
        Assert.All(
            typeof(RaidHistoryCommand).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            constructor => Assert.True(constructor.IsPrivate));
        var kinds = typeof(RaidHistoryCommand)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Where(type => type.IsSubclassOf(typeof(RaidHistoryCommand)))
            .ToArray();
        Assert.Equal(Enum.GetValues<OutboxCommandKind>().Length, kinds.Length);
        Assert.All(kinds, kind =>
        {
            Assert.True(kind.IsSealed);
            Assert.False(kind.IsNestedPublic);
        });
        Assert.Contains(
            typeof(IAtLeastOnceRaidHistoryService).GetMethods(),
            method => method.Name == nameof(IAtLeastOnceRaidHistoryService.AcceptAsync));
        Assert.DoesNotContain(
            typeof(IAtLeastOnceRaidHistoryService).GetMethods().SelectMany(method => method.GetParameters()),
            parameter => parameter.ParameterType == typeof(string));
    }

    /// <summary>
    /// A field added to one of these records would be silently dropped by its reviewed codec.
    /// Failing here sends whoever adds it to the codec, and to the question of whether the new
    /// field may cross the outbox at all.
    /// </summary>
    [Fact]
    public void RecordsCrossingTheOutboxStillMatchTheirReviewedCodecs()
    {
        AssertShape<RaidHistoryEntry>("EndedUtc", "Id", "MapId", "Mode", "Notes", "Outcome", "ProfileId", "StartedUtc");
        AssertShape<RaidEvidence>(
            "Confidence", "EventId", "Kind", "MapId", "ObservedUtc", "ResumesSession", "Side", "SideBasis",
            "StartsNewRaid", "SuggestedState", "Summary");
        AssertShape<ActiveExtract>("Confidence", "ExtractId", "Name", "Source");
        AssertShape<ScanExecutionResult>(
            "CanonicalItemId", "Confidence", "Detail", "IsAvailable", "ItemName", "ObservedUtc", "Recommendation",
            "Source", "Succeeded", "ValuePerSlotRoubles", "ValueRoubles");
        AssertShape<FleaSaleObservation>("Count", "HandbookItemId", "ObservedUtc", "OfferId");
        AssertShape<QuestStatusObservation>("EventId", "ObservedUtc", "State", "TaskId");
        AssertShape<ScreenshotPosition>(
            "DuplicateIndex", "Filename", "HeadingDegrees", "InGameTime", "Orientation", "Position", "Timestamp");
        AssertShape<WorldPosition>("X", "Y", "Z");
        AssertShape<QuaternionOrientation>("W", "X", "Y", "Z");
    }

    [Fact]
    public void ADurableRaidTransitionIsAcceptedBeforeItIsCommittedOrPublished()
    {
        var source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "RaidActivityCoordinator.cs"));
        var accept = source.IndexOf("outbox.AcceptAsync(commands", StringComparison.Ordinal);
        var commit = source.IndexOf("staged.Commit(stage)", StringComparison.Ordinal);
        var publish = source.IndexOf("Publish(current)", StringComparison.Ordinal);

        Assert.True(accept > 0, "The durable transition no longer accepts its commands.");
        Assert.True(commit > accept, "The raid state is committed before its record was accepted.");
        Assert.True(publish > commit, "The raid state is published before it was committed.");
        Assert.DoesNotContain("raidStateService.Apply(", source, StringComparison.Ordinal);
    }

    private static void AssertShape<T>(params string[] expected) =>
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            typeof(T)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanWrite)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));

    private static bool IsClosedPayloadType(Type type, Type[] payloads)
    {
        if (type.IsArray)
        {
            return payloads.Contains(type.GetElementType());
        }

        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(object)
            || underlying == typeof(JsonElement)
            || underlying == typeof(JsonDocument)
            || typeof(JsonNode).IsAssignableFrom(underlying))
        {
            return false;
        }

        return underlying.IsEnum
            || underlying == typeof(string)
            || underlying == typeof(bool)
            || underlying == typeof(int)
            || underlying == typeof(long)
            || underlying == typeof(double)
            || underlying == typeof(Guid)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(TimeSpan);
    }

    private static IEnumerable<string> OwnedSourceFiles()
    {
        foreach (var file in Directory.EnumerateFiles(ExecutionDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            yield return file;
        }

        foreach (var name in new[]
                 {
                     "ApplicationStartupCoordinator.cs",
                     "RaidActivityCoordinator.cs",
                     "RaidHistoryCommand.cs",
                     "RaidHistoryOutbox.cs",
                     "RuntimeState.cs",
                 })
        {
            yield return Path.Combine(RuntimeDirectory(), name);
        }
    }

    private static string ExecutionDirectory() =>
        Path.Combine(RepositoryRoot(), "src", "TarkovCompanion.Application", "Services", "Execution");

    private static string RuntimeDirectory() =>
        Path.Combine(RepositoryRoot(), "src", "TarkovCompanion.Application", "Services", "Runtime");

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
