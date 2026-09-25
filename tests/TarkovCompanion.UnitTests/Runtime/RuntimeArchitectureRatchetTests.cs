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
    /// The GDI window-capture stack was retired (#316), and this is what stops it coming back by
    /// default. Scans read the screenshots the game writes; a capturing service returns only as
    /// a deliberate decision, with pixel, time and memory bounds, and this test is then edited
    /// with it.
    /// </summary>
    [Fact]
    public void TheWindowCaptureStackStaysRetired()
    {
        var root = RepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(root, "src", "TarkovCompanion.App", "Services", "AppComposition.cs"));

        Assert.False(Directory.Exists(Path.Combine(root, "src", "TarkovCompanion.Platform.Windows", "Capture")));
        Assert.DoesNotContain("GdiScreenCaptureService", composition, StringComparison.Ordinal);
        Assert.Contains(
            "AddSingleton<IScreenCaptureService, UnavailableScreenCaptureService>",
            composition,
            StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src", "TarkovCompanion.Platform.Windows"), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("BitBlt", source, StringComparison.Ordinal);
            Assert.DoesNotContain("GetWindowDC", source, StringComparison.Ordinal);
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
        Assert.True(typeof(IOutboxStore).GetMethod(nameof(IOutboxStore.RenewLeaseAsync))!.IsAbstract);
        Assert.True(typeof(IOutboxStore).GetMethod(nameof(IOutboxStore.ResolveDeadLetterAsync))!.IsAbstract);
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
        // EndsUnreported, LogSession, RaidKey, RaidLastSeenUtc and RaidStartedUtc (#568) are on the
        // record and deliberately NOT in the codec. They steer the raid state in memory, before
        // anything is queued: which raid a line belongs to, and when a raid nobody reported over
        // began and was last seen. What they decide does cross, on the commands that carry it (a
        // new raid's start, an end with its outcome and notes). A stored state event therefore has
        // no short id; if something comes to need it there, it goes into the codec first.
        // EndsOnlyARaidWithoutId (#892) is the same kind: it only decides, in memory, whether a
        // profile reload ends the raid.
        AssertShape<RaidEvidence>(
            "Confidence", "EndsOnlyARaidWithoutId", "EndsUnreported", "EventId", "Kind", "LoadSeconds", "LogSession", "MapId", "ObservedUtc",
            "RaidKey", "RaidLastSeenUtc", "RaidStartedUtc", "ResumesSession", "Side", "SideBasis",
            "StartsNewRaid", "SuggestedState", "Summary");
        AssertShape<ActiveExtract>("Confidence", "ExtractId", "Name", "Source");
        // DetailPhrase (#314) is on the record and deliberately NOT in the codec: it is the
        // scanner-state line in the interface language, for this machine's screen. The codec keeps
        // carrying the fixed English Detail, so what is queued never depends on the language.
        AssertShape<ScanExecutionResult>(
            "CanonicalItemId", "Confidence", "Detail", "DetailPhrase", "IsAvailable", "ItemName", "ObservedUtc", "Recommendation",
            "Source", "Succeeded", "ValuePerSlotRoubles", "ValueRoubles");
        // WrittenUtc (#314) is on the record and deliberately NOT in the codec: it only tells the
        // flea-sold notification a replayed sale from a new one, in memory, before anything is queued.
        AssertShape<FleaSaleObservation>("Count", "HandbookItemId", "ObservedUtc", "OfferId", "WrittenUtc");
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

    [Fact]
    public void RuntimeStateReplacementIsLinearizedBeforeSubscriberNotification()
    {
        var source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "RuntimeState.cs"));
        var update = source.IndexOf("public void Update(", StringComparison.Ordinal);
        var notificationGate = source.IndexOf("lock (_notificationGate)", update, StringComparison.Ordinal);
        var stateGate = source.IndexOf("lock (_gate)", notificationGate, StringComparison.Ordinal);
        var invoke = source.IndexOf("handler(this, EventArgs.Empty)", stateGate, StringComparison.Ordinal);

        Assert.True(notificationGate > update, "Runtime updates no longer enter the publication gate.");
        Assert.True(stateGate > notificationGate, "State can be replaced before publication is serialized.");
        Assert.True(invoke > stateGate, "Subscribers are no longer notified inside the publication gate.");
    }

    [Fact]
    public void LifecycleDrainsStartedWrappersBeforeReleasingTheStartupTokenLink()
    {
        var source = File.ReadAllText(Path.Combine(ExecutionDirectory(), "FeatureLifecycleCoordinator.cs"));
        var phase = source.IndexOf("private async Task StartPhaseAsync", StringComparison.Ordinal);
        var drain = source.IndexOf("Task.WhenAll(running.Values.Select(ObserveAsync))", phase, StringComparison.Ordinal);
        var start = source.IndexOf(
            "public async Task<FeatureLifecycleSnapshot> StartAsync",
            StringComparison.Ordinal);
        var finallyBlock = source.IndexOf("finally", start, StringComparison.Ordinal);
        var dispose = source.IndexOf("linked.Dispose();", finallyBlock, StringComparison.Ordinal);

        Assert.True(drain > phase, "Cancelled startup can abandon sibling feature wrappers.");
        Assert.True(finallyBlock > 0, "The startup token link has no guaranteed release path.");
        Assert.True(dispose > finallyBlock, "The startup token link is no longer released by its owner.");
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
            return type.GetElementType() is { } element && payloads.Contains(element);
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
