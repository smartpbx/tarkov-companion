using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests;

public sealed class RaidObservationServiceTests
{
    [Fact]
    public async Task ReportsThatTheGameWasNotFoundWhenNoFolderExists()
    {
        using var harness = new Harness(new(null, null, null, Confidence.Unknown));

        await harness.RunUntilAsync(state => state.Detail.Contains("not found", StringComparison.OrdinalIgnoreCase));

        var observation = harness.Store.Current.Observation;
        Assert.False(observation.IsObserving);
        Assert.Contains("not found", observation.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TurnsLogEvidenceIntoRaidState()
    {
        using var harness = new Harness(new(@"C:\EFT", @"C:\EFT\Logs", null, new Confidence(0.8)));
        harness.LogEvidence.Add(new(
            RaidEvidenceKind.LogLine,
            DateTimeOffset.UnixEpoch,
            "customs",
            RaidLifecycleState.InRaid,
            new Confidence(0.95),
            "Matched a map line."));

        await harness.RunUntilAsync(_ => harness.Store.Current.Raid.MapId is not null);

        var raid = harness.Store.Current.Raid;
        Assert.Equal("customs", raid.MapId);
        Assert.Equal(RaidLifecycleState.InRaid, raid.State);
        Assert.True(harness.Store.Current.Observation.IsWatchingLogs);
        Assert.False(harness.Store.Current.Observation.IsWatchingScreenshots);
    }

    [Fact]
    public async Task TurnsAScreenshotFilenameIntoALastKnownPosition()
    {
        // Built with the running platform's separator: Path.GetFileName only recognises
        // backslashes on Windows, so a hardcoded Windows path would not be split on Linux.
        var screenshotRoot = Path.Combine("eft", "Screenshots");
        using var harness = new Harness(new("eft", null, screenshotRoot, new Confidence(0.8)));
        harness.ScreenshotPaths.Add(Path.Combine(screenshotRoot, "shot.png"));

        await harness.RunUntilAsync(_ => harness.Store.Current.Raid.LastKnownPosition is not null);

        var position = harness.Store.Current.Raid.LastKnownPosition;
        Assert.NotNull(position);
        Assert.Equal("shot.png", position.Filename);
        Assert.True(harness.Store.Current.Observation.IsWatchingScreenshots);
    }

    [Fact]
    public async Task FilenamePositionDoesNotWaitForCaptureAdmissionAndLegacyScanStillRuns()
    {
        var screenshotRoot = Path.Combine("eft", "Screenshots");
        var capture = new BlockingCaptureSessionService();
        var scan = new CountingScanUseCase();
        using var harness = new Harness(
            new("eft", null, screenshotRoot, new Confidence(0.8)),
            imageLoader: new StubImageLoader(),
            scanUseCase: scan,
            captureSessions: capture);
        harness.ScreenshotPaths.Add(Path.Combine(screenshotRoot, "shot.png"));

        await harness.RunUntilAsync(_ => harness.Store.Current.Raid.LastKnownPosition is not null);
        Assert.Equal(0, scan.ImageScans);

        capture.Release();
        await UntilAsync(() => scan.ImageScans == 1);
        Assert.NotNull(capture.Submission);
        Assert.Equal(CaptureDeliveryKind.WatchedFile, capture.Submission!.DeliveryKind);
        Assert.Equal(CaptureSourceKind.GameWrittenScreenshot, capture.Submission.Source.SourceKind);
    }

    [Fact]
    public async Task DoesNotObserveInDemoMode()
    {
        using var harness = new Harness(
            new(@"C:\EFT", @"C:\EFT\Logs", @"C:\EFT\Screenshots", new Confidence(0.9)),
            demoMode: true);

        harness.Service.Start();
        await Task.Delay(50, CancellationToken.None);

        Assert.False(harness.Store.Current.Observation.IsObserving);
        Assert.Contains("Demo mode", harness.Store.Current.Observation.Detail, StringComparison.Ordinal);
        Assert.Empty(harness.WatchedLogRoots);
    }

    private sealed class Harness : IDisposable
    {
        public Harness(
            EftPaths paths,
            bool demoMode = false,
            IScreenshotImageLoader? imageLoader = null,
            IScanUseCase? scanUseCase = null,
            ICaptureSessionService? captureSessions = null)
        {
            var options = new RuntimeOptions(
                demoMode,
                Offline: true,
                GameMode.Regular,
                "en",
                TimeSpan.FromHours(9),
                TimeSpan.FromMinutes(5));
            Store = new RuntimeStateStore(options);
            var raidState = new RaidStateService();
            var coordinator = new RaidActivityCoordinator(
                raidState,
                new RecordingRaidHistory(),
                new StubProfileService(),
                Store);
            Service = new(
                new StubPathLocator(paths),
                new StubLogWatcher(this),
                new StubScreenshotWatcher(this),
                new StubFilenameParser(),
                coordinator,
                Squad,
                FleaSales,
                Store,
                options,
                NullLogger<RaidObservationService>.Instance,
                imageLoader: imageLoader,
                scanUseCase: scanUseCase,
                captureSessions: captureSessions);
        }

        public SquadStateService Squad { get; } = new();

        public FleaSaleStateService FleaSales { get; } = new();

        public RuntimeStateStore Store { get; }

        public RaidObservationService Service { get; }

        public List<RaidEvidence> LogEvidence { get; } = [];

        public List<string> ScreenshotPaths { get; } = [];

        public List<string> WatchedLogRoots { get; } = [];

        /// <summary>Starts observation and waits, briefly, for the expected state.</summary>
        public async Task RunUntilAsync(Func<EftObservationState, bool> condition)
        {
            Service.Start();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (condition(Store.Current.Observation))
                {
                    return;
                }

                await Task.Delay(10, CancellationToken.None);
            }

            Assert.Fail($"Observation never reached the expected state. Detail: {Store.Current.Observation.Detail}");
        }

        public void Dispose() => Service.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(5, CancellationToken.None);
        }

        Assert.True(condition());
    }

    private sealed class StubImageLoader : IScreenshotImageLoader
    {
        public Task<CapturedImage?> LoadAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<CapturedImage?>(new(
                new byte[16],
                2,
                2,
                8,
                PixelFormat.Bgra8888,
                DateTimeOffset.UnixEpoch,
                "raid-observation-fixture"));
    }

    private sealed class CountingScanUseCase : IScanUseCase
    {
        public int ImageScans => Volatile.Read(ref _imageScans);

        private int _imageScans;

        public Task<ScanOutcome> ScanAsync(ScanRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Outcome());

        public Task<ScanOutcome> ScanImageAsync(CapturedImage image, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _imageScans);
            return Task.FromResult(Outcome());
        }

        private static ScanOutcome Outcome() => new(
            Guid.NewGuid(),
            ScanCompletionStatus.Partial,
            ScanContext.Unknown,
            DateTimeOffset.UnixEpoch,
            new(ScanContext.Unknown, [], DateTimeOffset.UnixEpoch, "fixture"),
            null,
            null,
            null,
            null,
            [],
            "fixture");
    }

    private sealed class BlockingCaptureSessionService : ICaptureSessionService
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler? Changed { add { } remove { } }

        public event EventHandler<CaptureReviewRequestedEventArgs>? ReviewRequested { add { } remove { } }

        public event EventHandler<CaptureAcceptedEventArgs>? Accepted { add { } remove { } }

        public CaptureSessionServiceSnapshot Snapshot => CaptureSessionServiceSnapshot.Empty;

        public CaptureSubmission? Submission { get; private set; }

        public CaptureArmReceipt Arm(CaptureArmRequest request) =>
            new(false, request.Request.SessionId, "fixture_not_armed");

        public async ValueTask<CaptureQueueReceipt> EnqueueAsync(
            CaptureSubmission submission,
            CancellationToken cancellationToken)
        {
            Submission = submission;
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(
                0,
                CaptureQueueDisposition.Accepted,
                submission.CorrelationId,
                submission.SubmittedUtc,
                "fixture_accepted");
        }

        public bool TryReview(
            CaptureSessionId sessionId,
            string artifactId,
            CaptureReviewAction action,
            string origin) => false;

        public bool Cancel(CaptureSessionId sessionId, string origin) => false;

        public void Release() => _release.TrySetResult();

        public ValueTask DisposeAsync()
        {
            _release.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubPathLocator(EftPaths paths) : IEftPathLocator
    {
        public Task<EftPaths> FindAsync(CancellationToken cancellationToken) => Task.FromResult(paths);
    }

    private sealed class StubLogWatcher(Harness harness) : IEftLogWatcher
    {
        public async IAsyncEnumerable<RaidEvidence> WatchAsync(
            string logRoot,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            harness.WatchedLogRoots.Add(logRoot);
            foreach (var evidence in harness.LogEvidence)
            {
                yield return evidence;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class StubScreenshotWatcher(Harness harness) : IScreenshotWatcher
    {
        public async IAsyncEnumerable<string> WatchAsync(
            string screenshotRoot,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var path in harness.ScreenshotPaths)
            {
                yield return path;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class StubFilenameParser : IScreenshotFilenameParser
    {
        public bool TryParse(string filename, TimeSpan localUtcOffset, out ScreenshotPosition? position)
        {
            // The real parser records the bare name whatever it is handed, and the service
            // now hands it a full path so the file's own timestamp can be read.
            position = new(
                DateTimeOffset.UnixEpoch,
                new WorldPosition(1, 2, 3),
                new QuaternionOrientation(0, 0, 0, 1),
                90,
                null,
                null,
                Path.GetFileName(filename));
            return true;
        }

        public bool TryParseFile(string path, TimeSpan localUtcOffset, out ScreenshotPosition? position) =>
            TryParse(path, localUtcOffset, out position);
    }

    private sealed class StubProfileService : IPlayerProfileService
    {
        private static readonly PlayerProfile Profile = new(
            Guid.Parse("2e9b9a6c-6d7a-4c61-8a68-1b2b1bd2a0f1"),
            "Observation profile",
            GameMode.Regular,
            1,
            Faction.Usec,
            null,
            new Dictionary<string, int>(),
            new HashSet<string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            new HashSet<string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, EventItemState>(),
            new Dictionary<string, string>(),
            DateTimeOffset.UnixEpoch);

        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Profile);

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => Task.FromResult("{}");

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) =>
            Task.FromResult(Profile);
    }

    private sealed class RecordingRaidHistory : IRaidHistoryService
    {
        public Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken) =>
            Task.FromResult(raid.Id);

        public Task RecordEventAsync(
            Guid raidId,
            string type,
            DateTimeOffset timestampUtc,
            string payloadJson,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EndAsync(
            Guid raidId,
            DateTimeOffset endUtc,
            string? outcome,
            string? notes,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidHistoryEntry>>([]);

        public Task<IReadOnlyList<string>> ListEventPayloadsAsync(
            Guid raidId,
            string type,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(
            string mapId,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidTrail>>([]);

        public Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(
            Guid raidId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScreenshotPosition>>([]);

        public Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
