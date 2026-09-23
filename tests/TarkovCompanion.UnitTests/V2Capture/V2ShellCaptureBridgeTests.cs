using System.Collections.Immutable;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.Profiles;
using TarkovCompanion.UnitTests.V2Shell;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// What a capture actually reaches the player with (#287).
/// </summary>
/// <remarks>
/// The bridge used to resolve every review automatically and arm with an entirely empty context.
/// Both are wiring, not policy, and both were invisible from inside either side: the shell
/// rendered an attention panel nothing ever filled, and the coordinator carried seven null context
/// fields to every handoff. These tests hold the two halves.
/// </remarks>
public sealed class V2ShellCaptureBridgeTests
{
    private static readonly CaptureSessionId Session = new(Guid.Parse("30000000-0000-0000-0000-000000000287"));

    [Fact]
    public async Task ADisagreeingReviewStopsForThePlayerRatherThanResolvingItself()
    {
        await using var fixture = await Fixture.CreateAsync();

        fixture.Arm(ScanIntent.Stash);
        fixture.Sessions.RaiseReview(Review(ScanIntent.Stash, RecognizedContext.Flea, disagreement: true));

        var attention = fixture.Shell.CaptureState.Attention;
        Assert.NotNull(attention);
        Assert.Equal(V2CaptureAttentionKind.IntentMismatch, attention.Kind);
        Assert.Equal(RecognizedContext.Flea, attention.DetectedContext);
        Assert.Empty(fixture.Sessions.Reviewed);
        Assert.Equal(
            [V2CaptureResolutionKind.Skip, V2CaptureResolutionKind.AnalyzeAsArmed, V2CaptureResolutionKind.AnalyzeAsDetected],
            fixture.Shell.CaptureAttentionActions.Select(action => action.Resolution));
    }

    [Fact]
    public async Task AnUnplaceableScreenStopsTooAndOffersRetry()
    {
        await using var fixture = await Fixture.CreateAsync();

        fixture.Arm(ScanIntent.Auto);
        fixture.Sessions.RaiseReview(Review(ScanIntent.Auto, detected: null, disagreement: false));

        Assert.Equal(V2CaptureAttentionKind.UnknownContext, fixture.Shell.CaptureState.Attention?.Kind);
        Assert.Contains(
            V2CaptureResolutionKind.Retry,
            fixture.Shell.CaptureAttentionActions.Select(action => action.Resolution));
    }

    [Fact]
    public async Task AnAgreeingReviewStillResolvesWithoutInterrupting()
    {
        await using var fixture = await Fixture.CreateAsync();

        fixture.Arm(ScanIntent.Stash);
        fixture.Sessions.RaiseReview(Review(ScanIntent.Stash, RecognizedContext.Stash, disagreement: false));

        Assert.Null(fixture.Shell.CaptureState.Attention);
        Assert.Equal(CaptureReviewAction.UseDetected, Assert.Single(fixture.Sessions.Reviewed).Action);
    }

    [Fact]
    public async Task TheDisagreementActionsReachTheCoordinator()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Arm(ScanIntent.Stash);
        fixture.Sessions.RaiseReview(Review(ScanIntent.Stash, RecognizedContext.Flea, disagreement: true));

        fixture.Shell.CaptureAttentionActions
            .Single(action => action.Resolution == V2CaptureResolutionKind.AnalyzeAsDetected)
            .InvokeCommand.Execute(null);

        Assert.Equal(CaptureReviewAction.UseDetected, Assert.Single(fixture.Sessions.Reviewed).Action);
    }

    [Fact]
    public async Task ArmingCarriesWhereTheCaptureWasTakenFrom()
    {
        await using var fixture = await Fixture.CreateAsync();

        fixture.Arm(ScanIntent.Auto);

        var context = Assert.Single(fixture.Sessions.Armed).Context;
        Assert.False(string.IsNullOrWhiteSpace(context.ActiveWorkspace));
        Assert.Equal(V2NavigationContext.ThisDesktop, context.InitiatingDevice);
    }

    [Fact]
    public async Task AnIdentifiedItemBecomesAReviewNamingItsAlternates()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Arm(ScanIntent.Auto);

        await fixture.Intel.AcceptAsync(
            IdentifiedHandoff(
                new CaptureIdentifiedItem("item-a", "Graphics card", new Confidence(0.82), "line"),
                new CaptureIdentifiedItem("item-b", "Graphics tablet", new Confidence(0.61), "line")),
            CancellationToken.None);

        var review = fixture.Shell.CaptureState.Review;
        Assert.NotNull(review);
        Assert.Contains("Graphics card", review.Summary, StringComparison.Ordinal);
        Assert.Contains("Graphics tablet", review.Summary, StringComparison.Ordinal);
        Assert.Equal(RecognizedContext.Item, review.DetectedContext);
        Assert.Contains("intel/item-a", fixture.Shell.Router.CurrentAddress, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Correct result" was acknowledged and dropped. A wrong item is put right by picking the
    /// candidate it was, which opens that item and says who chose it.
    /// </summary>
    [Fact]
    public async Task PickingAnotherCandidateOpensThatItemAndSaysThePlayerChoseIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Arm(ScanIntent.Auto);
        await fixture.Intel.AcceptAsync(
            IdentifiedHandoff(
                new CaptureIdentifiedItem("item-a", "Graphics card", new Confidence(0.82), "line"),
                new CaptureIdentifiedItem("item-b", "Graphics tablet", new Confidence(0.61), "line")),
            CancellationToken.None);

        Assert.DoesNotContain(fixture.Shell.CaptureReviewActions, action => action.Resolution == V2CaptureResolutionKind.Correct);
        Assert.Equal(2, fixture.Shell.CaptureReviewCandidates.Count);
        Assert.True(fixture.Shell.CaptureReviewCandidates[0].IsChosen);
        fixture.Shell.CaptureReviewCandidates[1].ChooseCommand.Execute(null);

        Assert.Contains("intel/item-b", fixture.Shell.Router.CurrentAddress, StringComparison.Ordinal);
        Assert.Equal("Graphics tablet · chosen by you", fixture.Shell.CaptureState.Review!.Summary);
        Assert.True(fixture.Shell.CaptureReviewCandidates[1].IsChosen);
    }

    [Theory]
    [InlineData(ScanIntent.HealthAndCharacter)]
    [InlineData(ScanIntent.ExtractsAndMap)]
    public async Task AnIntentNothingReadsIsShownAsSuchAndCannotBeArmed(ScanIntent intent)
    {
        await using var fixture = await Fixture.CreateAsync();

        var offered = fixture.Shell.CaptureIntents.Single(item => item.Intent == intent);
        fixture.Arm(intent);

        Assert.False(offered.IsSupported);
        Assert.EndsWith("not supported yet", offered.Label, StringComparison.Ordinal);
        Assert.Empty(fixture.Sessions.Armed);
    }

    [Fact]
    public async Task FakePipelineStreamAddsPendingRowsThenFinalResultReusesAndOrdersThem()
    {
        await using var fixture = await Fixture.CreateAsync();
        var correlation = CaptureCorrelationId.New();
        fixture.Progress.Start(correlation);
        fixture.Progress.Match(correlation, Named(0, 0, "item-a", "First matched"));
        fixture.Progress.Match(correlation, Named(0, 1, "item-b", "Second matched"));

        var live = Assert.IsType<TarkovCompanion.App.ViewModels.V2.LootScan.LootScanViewModel>(fixture.Shell.LootScanResult);
        Assert.All(live.Decisions, row => Assert.True(row.IsPending));
        var first = live.Decisions[0];
        var second = live.Decisions[1];

        live.Reconcile(Final(correlation,
            Decision(Named(0, 1, "item-b", "Second matched"), LootScanVerdict.Leave),
            Decision(Named(0, 0, "item-a", "First matched"), LootScanVerdict.Review)));

        Assert.Same(second, live.Decisions[0]);
        Assert.Same(first, live.Decisions[1]);
        Assert.Equal(["LEAVE", "REVIEW"], live.Decisions.Select(row => row.VerdictLabel));
        Assert.All(live.Decisions, row => Assert.False(row.IsPending));
    }

    [Fact]
    public async Task FakePipelineIgnoresCancelledAndSupersededScanItems()
    {
        await using var fixture = await Fixture.CreateAsync();
        var oldCorrelation = CaptureCorrelationId.New();
        var currentCorrelation = CaptureCorrelationId.New();
        fixture.Progress.Start(oldCorrelation);
        fixture.Progress.Match(oldCorrelation, Named(0, 0, "old", "Old item"));
        fixture.Progress.Start(currentCorrelation);
        fixture.Progress.Match(currentCorrelation, Named(0, 0, "new", "New item"));
        fixture.Progress.Match(oldCorrelation, Named(0, 1, "late", "Late old item"));
        fixture.Progress.Stop(currentCorrelation);
        fixture.Progress.Match(currentCorrelation, Named(0, 1, "cancelled", "After cancel"));

        var live = Assert.IsType<TarkovCompanion.App.ViewModels.V2.LootScan.LootScanViewModel>(fixture.Shell.LootScanResult);
        Assert.Equal(currentCorrelation, live.CorrelationId);
        Assert.Equal("Scan stopped", live.StatusLabel);
        Assert.Equal("New item", Assert.Single(live.Decisions).Name);
    }

    private static LootScanDecision Decision(GridCellObservation cell, LootScanVerdict verdict) =>
        new(cell.Anchor, cell.Item, verdict, [new LootScanReason("fixture", "Fixture decision")]);

    private static LootScanResult Final(CaptureCorrelationId correlation, params LootScanDecision[] decisions) =>
        new(
            correlation.ToString(),
            Session,
            correlation,
            new CaptureContextMetadata("raid", null, "customs", null, null, null, "desktop"),
            "shot-1",
            0,
            new string('a', 64),
            new string('a', 64),
            "desktop",
            Now,
            new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current, "fixture"),
            decisions,
            [],
            []);

    private static GridCellObservation Named(int row, int column, string id, string name)
    {
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "fixture://progress",
            Now,
            new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.97),
            new ProducerIdentity("fixture", "1"));
        EvidencedValue<T> Known<T>(string field, T value, EvidenceRegion? bounds = null) =>
            new(field, value, new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current), provenance, bounds);
        var item = new RecognizedItem(
            Known("id", id),
            Known("name", name),
            Known<int?>("quantity", 1),
            Known<int?>("width", 1),
            Known<int?>("height", 1),
            Known<bool?>("rotated", false),
            new EvidencedValue<bool?>("fir", null, new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current), provenance),
            Known("condition", ItemConditionReading.NotApplicable));
        var bounds = new EvidenceRegion(column * 64, row * 64, 64, 64, EvidenceCoordinateSpace.SourcePixels);
        return new($"cell-{row}-{column}", new GridCellAddress(row, column), Known("item", item, bounds));
    }

    private static CaptureHandoffRequest IdentifiedHandoff(params CaptureIdentifiedItem[] identified) =>
        new(
            Session,
            "shot-1",
            new CaptureAnalysis("result", RecognizedContext.Item, false, true, null, new Confidence(0.82), Identified: identified),
            CaptureContextMetadata.Empty,
            CaptureCorrelationId.New(),
            CaptureSourceKind.GameWrittenScreenshot,
            V2ShellTestData.Now,
            V2ShellTestData.Now,
            null,
            CaptureDeliveryKind.WatchedFile,
            new EvidenceProvenance(
                EvidenceSourceClass.GameWrittenScreenshot,
                "fixture://capture",
                V2ShellTestData.Now,
                EvidenceConfidence.Unscored,
                new ProducerIdentity("fixture", "1")),
            0,
            CaptureReviewAction.UseDetected,
            ScanIntent.Auto,
            new CaptureCorrection(CaptureReviewAction.UseDetected, ScanIntent.Auto, RecognizedContext.Item, 0, V2ShellTestData.Now, "fixture"));

    private static CaptureReviewRequest Review(ScanIntent intent, RecognizedContext? detected, bool disagreement) =>
        new(
            Session,
            "shot-1",
            0,
            intent,
            detected,
            disagreement,
            0,
            V2ShellTestData.Now.AddMinutes(1),
            CaptureCorrelationId.New());

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _config = V2ShellTestData.TemporaryDirectory();

        private Fixture(
            V2ShellViewModel shell,
            FakeSessions sessions,
            IntelCaptureHandoff intel,
            FakeLootProgress progress,
            V2ShellCaptureBridge bridge)
        {
            Shell = shell;
            Sessions = sessions;
            Intel = intel;
            Progress = progress;
            Bridge = bridge;
        }

        public V2ShellViewModel Shell { get; }

        public FakeSessions Sessions { get; }

        public IntelCaptureHandoff Intel { get; }

        public FakeLootProgress Progress { get; }

        private V2ShellCaptureBridge Bridge { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var config = V2ShellTestData.TemporaryDirectory();
            var shell = new V2ShellViewModel(
                V2ShellMode.VariantB,
                config,
                new RuntimeStateStore(new RuntimeOptions(true, true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5))));
            var sessions = new FakeSessions();
            var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
            var runtime = new ProfileRuntimeContextService(profiles);
            await runtime.InitializeAsync(CancellationToken.None);
            var intel = new IntelCaptureHandoff();
            var progress = new FakeLootProgress();
            var bridge = new V2ShellCaptureBridge(
                shell,
                sessions,
                new LootScanCaptureHandoff(runtime, new InventoryGridReconstructor(), new LootScanDecisionService()),
                intel,
                new WorkspaceOrigin(
                    new WorkspaceId(Guid.Parse("10000000-0000-0000-0000-000000000287")),
                    new CompanionDeviceId(Guid.Parse("10000000-0000-0000-0000-000000000288")),
                    WorkspaceOriginKind.DesktopApplication,
                    "desktop"),
                lootRecognitionProgress: progress);
            return new(shell, sessions, intel, progress, bridge);
        }

        public void Arm(ScanIntent intent)
        {
            Shell.CaptureIntents.Single(offered => offered.Intent == intent).SelectCommand.Execute(null);
            Shell.ArmCaptureCommand.Execute(null);
        }

        public async ValueTask DisposeAsync()
        {
            Bridge.Dispose();
            await Shell.DisposeAsync();
            try
            {
                Directory.Delete(_config, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FakeLootProgress : ILootScanRecognitionProgressSource
    {
        public event EventHandler<LootScanRecognitionStarted>? LootRecognitionStarted;

        public event EventHandler<LootScanItemMatched>? LootItemMatched;

        public event EventHandler<LootScanRecognitionStopped>? LootRecognitionStopped;

        public void Start(CaptureCorrelationId correlation) => LootRecognitionStarted?.Invoke(this, new(
            Session,
            "shot-1",
            correlation,
            0,
            new CaptureContextMetadata("raid", null, "customs", null, null, null, "desktop"),
            new string('a', 64),
            Now));

        public void Match(CaptureCorrelationId correlation, GridCellObservation cell) => LootItemMatched?.Invoke(this, new(
            Session,
            "shot-1",
            correlation,
            0,
            cell));

        public void Stop(CaptureCorrelationId correlation) => LootRecognitionStopped?.Invoke(this, new(
            Session,
            "shot-1",
            correlation,
            0,
            WasCancelled: true));
    }

    private sealed class FakeSessions : ICaptureSessionService
    {
        public event EventHandler? Changed;

        public event EventHandler<CaptureReviewRequestedEventArgs>? ReviewRequested;

        public event EventHandler<CaptureAcceptedEventArgs>? Accepted;

        public List<CaptureArmRequest> Armed { get; } = [];

        public List<(string Origin, CaptureReviewAction Action)> Reviewed { get; } = [];

        public CaptureSessionServiceSnapshot Snapshot { get; } = CaptureSessionServiceSnapshot.Empty;

        public CaptureArmReceipt Arm(CaptureArmRequest request)
        {
            Armed.Add(request);
            Changed?.Invoke(this, EventArgs.Empty);
            return new(true, request.Request.SessionId, "accepted");
        }

        public ValueTask<CaptureQueueReceipt> EnqueueAsync(CaptureSubmission submission, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void RaiseReview(CaptureReviewRequest review) =>
            ReviewRequested?.Invoke(this, new(review));

        public bool TryReview(
            CaptureSessionId sessionId,
            string artifactId,
            int decodeRevision,
            CaptureReviewAction action,
            string origin)
        {
            Reviewed.Add((origin, action));
            return true;
        }

        public bool Cancel(CaptureSessionId sessionId, string origin)
        {
            Reviewed.Add((origin, CaptureReviewAction.Cancel));
            return true;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Unused() => Accepted?.Invoke(this, null!);
    }
}
