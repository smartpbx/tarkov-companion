using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>Behavioral checks over the real shell VM, without composing a second V1 runtime.</summary>
public sealed class V2ShellViewModelTests : IDisposable
{
    private readonly string _config = V2ShellTestData.TemporaryDirectory();
    private readonly FixedClock _clock = new(V2ShellTestData.Now);

    public void Dispose() => Directory.Delete(_config, recursive: true);

    [Theory]
    [InlineData(null, true)]
    [InlineData("CurrentPage", true)]
    [InlineData("WindowTitle", false)]
    [InlineData("Status", false)]
    public void Legacy_host_feed_only_refreshes_for_page_context(string? propertyName, bool expected)
    {
        Assert.Equal(
            expected,
            V2ShellViewModel.ShouldRefreshLegacyContext(isLegacyRoot: true, propertyName));
        Assert.True(V2ShellViewModel.ShouldRefreshLegacyContext(isLegacyRoot: false, propertyName));
    }

    [Fact]
    public async Task Capture_and_health_modals_cannot_mutate_the_disabled_page_behind_them()
    {
        await using var shell = CreateShell();
        shell.CaptureCommand.Execute(null);
        var address = shell.Router.CurrentAddress;

        var consumed = shell.HandleKey(new("2", Control: true, Alt: false, Shift: false));

        Assert.True(consumed);
        Assert.Equal(address, shell.Router.CurrentAddress);
        Assert.Equal(V2ShellDialogKind.Capture, shell.ActiveDialog);
        Assert.Contains("Close", shell.AssertiveAnnouncement, StringComparison.Ordinal);

        shell.HandleKey(new("K", Control: true, Alt: false, Shift: false));
        Assert.Equal(V2ShellDialogKind.Commands, shell.ActiveDialog);

        shell.HandleKey(new("2", Control: true, Alt: false, Shift: false));
        Assert.Equal(V2ShellDialogKind.None, shell.ActiveDialog);
        Assert.Equal("#/raid", shell.Router.CurrentAddress);
    }

    [Fact]
    public async Task Typed_capture_state_renders_one_correlation_progress_actions_and_review()
    {
        await using var shell = CreateShell();
        var sessionId = new CaptureSessionId(Guid.Parse("20000000-0000-0000-0000-000000000267"));
        var request = new CaptureSessionRequest(
            sessionId,
            ScanIntent.Loot,
            Origin(),
            V2ShellTestData.Now);
        CaptureStageProgress[] progress =
        [
            new(sessionId, 0, CaptureSessionStage.Armed, V2ShellTestData.Now),
            new(sessionId, 1, CaptureSessionStage.AwaitingCapture, V2ShellTestData.Now.AddSeconds(1)),
            new(sessionId, 2, CaptureSessionStage.Settling, V2ShellTestData.Now.AddSeconds(2), "shot-4", 0, 10),
            new(sessionId, 3, CaptureSessionStage.Decoding, V2ShellTestData.Now.AddSeconds(3), "shot-4", 0, 35),
            new(sessionId, 4, CaptureSessionStage.DetectingContext, V2ShellTestData.Now.AddSeconds(4), "shot-4", 0, 60),
            new(sessionId, 5, CaptureSessionStage.AwaitingReview, V2ShellTestData.Now.AddSeconds(5), "shot-4", 0, 100),
        ];
        var session = new CaptureSessionSnapshot(
            request,
            progress,
            new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current));
        var attention = new V2CaptureAttention(
            V2CaptureAttentionKind.IntentMismatch,
            sessionId,
            "shot-4",
            0,
            ScanIntent.Loot,
            new StateRevision(19),
            "desktop",
            RecognizedContext.Stash);
        var review = new V2CaptureReview(
            sessionId,
            "shot-4",
            0,
            ScanIntent.Stash,
            RecognizedContext.Stash,
            V2ShellTestData.Now,
            "38 stacks found; review 4 uncertain rows",
            "game screenshot · local recognizer");

        shell.UpdateCaptureState(new(
            ScanIntent.Loot,
            new StateRevision(19),
            "desktop",
            session,
            attention,
            review));

        Assert.Equal(9, shell.CaptureIntents.Count);
        Assert.Equal(progress.Length, shell.CaptureProgressItems.Count);
        Assert.All(shell.CaptureProgressItems, item => Assert.Equal(sessionId.Value.ToString("D"), item.Reference));
        Assert.Equal(sessionId.Value.ToString("D"), shell.Router.Context.CaptureCorrelationId);
        Assert.True(shell.HasCaptureAttention);
        Assert.Contains(shell.CaptureAttentionActions, item => item.Resolution == V2CaptureResolutionKind.AnalyzeAsDetected);
        Assert.True(shell.HasCaptureReview);
        Assert.Contains(shell.CaptureReviewActions, item => item.Resolution == V2CaptureResolutionKind.Correct);
        Assert.Equal("1 capture needs a decision", shell.CaptureLabel);

        V2CaptureArmRequest? armRequest = null;
        shell.CaptureArmRequested += (_, requestEvent) => armRequest = requestEvent;
        shell.CaptureIntents.Single(item => item.Intent == ScanIntent.Ammo).SelectCommand.Execute(null);
        shell.UpdateCaptureState(new(
            ScanIntent.Loot,
            new StateRevision(19),
            "desktop",
            session,
            attention,
            review));
        Assert.Equal(ScanIntent.Ammo, shell.SelectedCaptureIntent);
        shell.ArmCaptureCommand.Execute(null);
        Assert.Equal(ScanIntent.Ammo, armRequest?.Intent);
        Assert.Equal(19L, armRequest?.BasedOnRevision.Value);

        V2CaptureResolutionRequest? resolutionRequest = null;
        shell.CaptureResolutionRequested += (_, requestEvent) => resolutionRequest = requestEvent;
        shell.CaptureAttentionActions
            .Single(item => item.Resolution == V2CaptureResolutionKind.AnalyzeAsDetected)
            .InvokeCommand.Execute(null);
        Assert.Equal(sessionId, resolutionRequest?.SessionId);
        Assert.Equal(ScanIntent.Stash, resolutionRequest?.Intent);
    }

    [Fact]
    public void Capture_attention_rejects_a_mismatch_without_detected_context_and_a_false_unknown()
    {
        var sessionId = new CaptureSessionId(Guid.Parse("20000000-0000-0000-0000-000000000267"));

        Assert.Throws<ArgumentException>(() => new V2CaptureAttention(
            V2CaptureAttentionKind.IntentMismatch,
            sessionId,
            "shot-4",
            0,
            ScanIntent.Loot,
            new StateRevision(19),
            "desktop"));
        Assert.Throws<ArgumentException>(() => new V2CaptureAttention(
            V2CaptureAttentionKind.UnknownContext,
            sessionId,
            "shot-4",
            0,
            ScanIntent.Loot,
            new StateRevision(19),
            "desktop",
            RecognizedContext.Stash));
    }

    [Theory]
    [InlineData(V2CaptureAttentionKind.UnknownContext, V2CaptureResolutionKind.AnalyzeAsSelected)]
    [InlineData(V2CaptureAttentionKind.StillWriting, V2CaptureResolutionKind.Retry)]
    [InlineData(V2CaptureAttentionKind.Duplicate, V2CaptureResolutionKind.AnalyzeAgain)]
    [InlineData(V2CaptureAttentionKind.DeviceRace, V2CaptureResolutionKind.ArmSelectedIntent)]
    public async Task Every_capture_decision_case_has_typed_visible_recovery(
        V2CaptureAttentionKind kind,
        V2CaptureResolutionKind expected)
    {
        await using var shell = CreateShell();
        var sessionId = new CaptureSessionId(Guid.Parse("20000000-0000-0000-0000-000000000267"));
        var isRace = kind == V2CaptureAttentionKind.DeviceRace;
        var attention = new V2CaptureAttention(
            kind,
            isRace ? null : sessionId,
            isRace ? null : "shot-5",
            isRace ? null : 4,
            ScanIntent.Loot,
            new StateRevision(8),
            "tablet",
            kind == V2CaptureAttentionKind.UnknownContext ? null : RecognizedContext.Stash);

        shell.UpdateCaptureState(new(
            ScanIntent.Loot,
            new StateRevision(8),
            "tablet",
            attention: attention));

        Assert.False(string.IsNullOrWhiteSpace(shell.CaptureAttentionHeading));
        Assert.Contains(shell.CaptureAttentionActions, action => action.Resolution == expected);
    }

    [Fact]
    public async Task Failed_reset_keeps_current_state_visible_and_retry_only_reports_success_after_delete()
    {
        var attempts = 0;
        var saveAttempts = 0;
        Task Reset(CancellationToken _)
        {
            var attempt = Interlocked.Increment(ref attempts);
            return attempt == 1
                ? Task.FromException(new IOException("fixture file is locked"))
                : Task.CompletedTask;
        }

        await using var shell = CreateShell(
            save: (_, _) =>
            {
                Interlocked.Increment(ref saveAttempts);
                return Task.CompletedTask;
            },
            reset: Reset);
        shell.GoTo(V2Routes.Raid);
        shell.TogglePin();
        var address = shell.Router.CurrentAddress;

        await shell.ResetPreviewCommand.ExecuteAsync();

        Assert.Equal(address, shell.Router.CurrentAddress);
        Assert.NotEmpty(shell.Pins);
        Assert.True(shell.HasPersistenceFailure);
        Assert.Equal("Retry reset", shell.PersistenceRetryLabel);

        var savesBefore = Volatile.Read(ref saveAttempts);
        shell.ToggleCaptureShortcut();
        await WaitUntilAsync(() => Volatile.Read(ref saveAttempts) > savesBefore);
        Assert.True(shell.HasPersistenceFailure);
        Assert.Equal("Retry reset", shell.PersistenceRetryLabel);

        shell.RetryPersistenceCommand.Execute(null);
        await WaitUntilAsync(() =>
            Volatile.Read(ref attempts) == 2 &&
            !shell.HasPersistenceFailure &&
            shell.Pins.Count == 0 &&
            shell.Router.CurrentAddress == "#/home" &&
            shell.PoliteAnnouncement.Contains("reset", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("#/home", shell.Router.CurrentAddress);
        Assert.Empty(shell.Recents);
        Assert.Contains("reset", shell.PoliteAnnouncement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_background_save_stays_visible_while_retry_is_in_flight_and_clears_on_success()
    {
        var attempts = 0;
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Save(V2ShellPreviewState _, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1)
            {
                throw new UnauthorizedAccessException("fixture folder is read-only");
            }

            retryStarted.TrySetResult();
            await releaseRetry.Task.WaitAsync(cancellationToken);
        }

        await using var shell = CreateShell(save: Save);
        shell.TogglePin();
        await WaitUntilAsync(() => shell.HasPersistenceFailure);

        Assert.Equal("Retry save", shell.PersistenceRetryLabel);
        Assert.Contains("not saved", shell.PersistenceFailure, StringComparison.OrdinalIgnoreCase);

        shell.RetryPersistenceCommand.Execute(null);
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            Assert.True(shell.HasPersistenceFailure);
            Assert.True(shell.PersistenceRetryPending);
            Assert.False(shell.CanRetryPersistence);
            Assert.Equal("Trying again…", shell.PersistenceRetryLabel);
            Assert.Contains("Trying preview storage again", shell.PoliteAnnouncement, StringComparison.Ordinal);
        }
        finally
        {
            // Never strand the persistence worker if an assertion above fails; shell disposal
            // intentionally waits for admitted durable work.
            releaseRetry.TrySetResult();
        }

        await WaitUntilAsync(() =>
            Volatile.Read(ref attempts) >= 2 &&
            !shell.HasPersistenceFailure &&
            shell.PoliteAnnouncement.Contains("saving again", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("saving again", shell.PoliteAnnouncement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_background_save_retry_remains_retryable_until_a_later_success()
    {
        var attempts = 0;
        Task Save(V2ShellPreviewState _, CancellationToken __)
        {
            var attempt = Interlocked.Increment(ref attempts);
            return attempt <= 2
                ? Task.FromException(new UnauthorizedAccessException($"fixture failure {attempt}"))
                : Task.CompletedTask;
        }

        await using var shell = CreateShell(save: Save);
        shell.TogglePin();
        await WaitUntilAsync(() => shell.HasPersistenceFailure && shell.CanRetryPersistence);

        shell.RetryPersistenceCommand.Execute(null);
        await WaitUntilAsync(() =>
            Volatile.Read(ref attempts) >= 2 &&
            shell.HasPersistenceFailure &&
            !shell.PersistenceRetryPending &&
            shell.CanRetryPersistence &&
            shell.PersistenceFailure.Contains("failure 2", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("failure 2", shell.PersistenceFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Retry save", shell.PersistenceRetryLabel);

        shell.RetryPersistenceCommand.Execute(null);
        // The save delegate increments before its result crosses the UI apply boundary. Waiting
        // for attempt two alone can still observe failure one's fully retryable state.
        await WaitUntilAsync(() =>
            Volatile.Read(ref attempts) >= 3 &&
            !shell.HasPersistenceFailure &&
            shell.PoliteAnnouncement.Contains("saving again", StringComparison.OrdinalIgnoreCase));

        Assert.False(shell.PersistenceRetryPending);
        Assert.False(shell.CanRetryPersistence);
    }

    [Fact]
    public async Task Header_context_ages_the_observed_raid_clock_and_preserves_continuity()
    {
        var runtime = new TestRuntimeStore(V2ShellTestData.Snapshot() with
        {
            Raid = new(
                Guid.Parse("30000000-0000-0000-0000-000000000267"),
                RaidLifecycleState.InRaid,
                "customs",
                V2ShellTestData.Now.AddMinutes(-10),
                V2ShellTestData.Now,
                Confidence.Unknown,
                null,
                [],
                false)
            {
                RaidClock = TimeSpan.FromMinutes(35),
                RaidClockReadUtc = V2ShellTestData.Now.AddMinutes(-5),
            },
        });
        await using var shell = CreateShell(runtime: runtime);

        shell.UpdateContinuity("plan-a", "objective-b", "item-c", null, "paired-tablet");

        Assert.Contains("30:00 left", shell.RaidContextLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("35:00", shell.RaidContextLabel, StringComparison.Ordinal);
        Assert.Contains("plan-a", shell.PlanContextLabel, StringComparison.Ordinal);
        Assert.Contains("objective-b", shell.PlanContextLabel, StringComparison.Ordinal);
        Assert.Contains("paired-tablet", shell.DeviceContextLabel, StringComparison.Ordinal);
        Assert.Contains("item-c", shell.CapturePrior, StringComparison.Ordinal);

        runtime.Update(snapshot => snapshot with
        {
            Raid = snapshot.Raid with { State = RaidLifecycleState.PostRaid },
        });
        Assert.Contains("Post-raid", shell.RaidContextLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("left", shell.RaidContextLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Suggestions_are_source_labelled_filterable_and_open_planned_item_intel()
    {
        await using var shell = CreateShell();
        shell.GoTo(V2Routes.Items);
        shell.TogglePin();
        shell.UpdatePlannedSuggestions(
        [
            new("item-ledx", "LEDX", "Private Clinic", "Find one in raid"),
        ]);

        Assert.Contains(shell.SuggestionItems, item => item.Kind == V2ShellSuggestionKind.Pinned && item.Provenance == "Pinned by you");
        Assert.Contains(shell.SuggestionItems, item => item.Kind == V2ShellSuggestionKind.Recent && item.Provenance == "Opened recently");
        var planned = Assert.Single(shell.SuggestionItems, item => item.Kind == V2ShellSuggestionKind.Planned);
        Assert.Contains("Private Clinic", planned.Provenance, StringComparison.Ordinal);
        Assert.Equal(4, shell.BrowseCategories.Count);

        shell.SuggestionFilters.Single(filter => filter.Kind == V2ShellSuggestionKind.Planned).SelectCommand.Execute(null);
        Assert.All(shell.FilteredSuggestionItems, item => Assert.Equal(V2ShellSuggestionKind.Planned, item.Kind));

        planned.OpenCommand.Execute(null);
        Assert.Equal("item-ledx", shell.Router.Current.SelectedEntity);
        Assert.Contains("item-ledx", shell.Router.CurrentAddress, StringComparison.Ordinal);
    }

    private V2ShellViewModel CreateShell(
        TestRuntimeStore? runtime = null,
        Func<V2ShellPreviewState, CancellationToken, Task>? save = null,
        Func<CancellationToken, Task>? reset = null) =>
        new(
            V2ShellMode.VariantB,
            _config,
            runtime ?? new TestRuntimeStore(V2ShellTestData.Snapshot()),
            _clock,
            save,
            reset);

    private static WorkspaceOrigin Origin() => new(
        new WorkspaceId(Guid.Parse("10000000-0000-0000-0000-000000000267")),
        new CompanionDeviceId(Guid.Parse("10000000-0000-0000-0000-000000000268")),
        WorkspaceOriginKind.DesktopApplication,
        "desktop");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class TestRuntimeStore(ApplicationRuntimeSnapshot current) : IRuntimeStateStore
    {
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067

        public ApplicationRuntimeSnapshot Current { get; private set; } = current;

        public void Update(Func<ApplicationRuntimeSnapshot, ApplicationRuntimeSnapshot> update)
        {
            Current = update(Current);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
