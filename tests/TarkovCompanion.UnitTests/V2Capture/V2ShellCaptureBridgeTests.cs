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
using TarkovCompanion.Core.Domain.Profiles;
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

        private Fixture(V2ShellViewModel shell, FakeSessions sessions, IntelCaptureHandoff intel, V2ShellCaptureBridge bridge)
        {
            Shell = shell;
            Sessions = sessions;
            Intel = intel;
            Bridge = bridge;
        }

        public V2ShellViewModel Shell { get; }

        public FakeSessions Sessions { get; }

        public IntelCaptureHandoff Intel { get; }

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
            var bridge = new V2ShellCaptureBridge(
                shell,
                sessions,
                new LootScanCaptureHandoff(runtime, new InventoryGridReconstructor(), new LootScanDecisionService()),
                intel,
                new WorkspaceOrigin(
                    new WorkspaceId(Guid.Parse("10000000-0000-0000-0000-000000000287")),
                    new CompanionDeviceId(Guid.Parse("10000000-0000-0000-0000-000000000288")),
                    WorkspaceOriginKind.DesktopApplication,
                    "desktop"));
            return new(shell, sessions, intel, bridge);
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
