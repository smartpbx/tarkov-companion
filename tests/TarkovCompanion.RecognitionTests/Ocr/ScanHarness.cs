using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Recognition;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests.Ocr;

/// <summary>Which pass of a scan an OCR request is.</summary>
public enum ScanPass
{
    RecognitionFrame,
    RecognitionContext,
    ExtractFrame,
    ExtractPanel,
    ContainerGrid,
    ContainerCell,
    Flea,
}

/// <summary>One screen a scan can be dispatched on, and the text a provider reads off it.</summary>
public enum ScanScene
{
    SingleItem,
    ExtractList,
    Container,
    FleaListings,
}

/// <summary>Something a scan did, when it started on the harness clock, and on which token.</summary>
internal sealed record ScanStage(string Name, TimeSpan StartedAt, CancellationToken Token);

/// <summary>
/// A scan through the real recognizer, coordinator, extract, container and flea recognizers and
/// <c>ScanUseCase</c>, over a scripted provider and in-memory repositories.
/// </summary>
/// <remarks>
/// The provider is scripted; everything that decides a scan's status and diagnostic is not. Every
/// provider pass and every lookup advances a manual clock by a configured cost before answering,
/// and records the token it was given, so a test can measure a frame's whole budget and prove that
/// every stage spent from the same one.
/// </remarks>
internal sealed class ScanHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset ObservedUtc = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly CanonicalItemResolverCache _catalog;

    public ScanHarness(ScanScene scene, TimeSpan? frameBudget = null)
    {
        Scene = scene;
        Image = CreateImage(scene);
        Clock = new ManualTimeProvider(ObservedUtc);
        Engine = new ScriptedScanEngine(this);
        Items = new CostedItemRepository(this, scene);
        Maps = new CostedMapDataService(this);
        RecommendationContext = new CostedRecommendationContextProvider(this);
        RaidState = new RaidStateService();
        RaidState.Apply(new RaidEvidence(
            RaidEvidenceKind.ManualOverride,
            ObservedUtc.AddMinutes(-1),
            "customs",
            RaidLifecycleState.InRaid,
            Confidence.Certain,
            "scan harness map"));
        Recorder = new ScanUseCaseTests.RecordingRaidActivity(RaidState);
        _catalog = new CanonicalItemResolverCache(new InMemoryRecognitionCatalogRepository(Catalog(scene)));
        UseCase = new ScanUseCase(
            new UnusedCapture(),
            new RecognitionService(new OcrCoordinator(Engine, new ScanContextDetector()), _catalog),
            new ExtractRecognitionService(Engine),
            new ContainerRecognitionService(Engine, _catalog, Items),
            new FleaRecognitionService(Engine),
            Maps,
            Recorder,
            Items,
            new RecommendationEngine(),
            RecommendationContext,
            Events,
            Publisher,
            timeProvider: Clock,
            frameOptions: new ScanFrameOptions { Timeout = frameBudget ?? TimeSpan.FromSeconds(30) });
    }

    public ScanScene Scene { get; }

    public CapturedImage Image { get; }

    public ManualTimeProvider Clock { get; }

    public ScriptedScanEngine Engine { get; }

    public CostedItemRepository Items { get; }

    public CostedMapDataService Maps { get; }

    public CostedRecommendationContextProvider RecommendationContext { get; }

    public RaidStateService RaidState { get; }

    public ScanUseCaseTests.RecordingRaidActivity Recorder { get; }

    public RecordingEvents Events { get; } = new();

    public LatestScanResultPublisher Publisher { get; } = new();

    public ScanUseCase UseCase { get; }

    /// <summary>Every provider pass and lookup, in the order the scan made them.</summary>
    public List<ScanStage> Stages { get; } = [];

    /// <summary>What each kind of pass or lookup costs on the manual clock.</summary>
    public Dictionary<string, TimeSpan> Costs { get; } = new(StringComparer.Ordinal);

    /// <summary>Runs as each stage starts, before its cost is spent.</summary>
    public Action<string>? OnStage { get; set; }

    public Task<ScanOutcome> ScanAsync(CancellationToken cancellationToken = default) =>
        UseCase.ScanImageAsync(Image, cancellationToken);

    public ValueTask DisposeAsync() => _catalog.DisposeAsync();

    /// <summary>Records a stage, spends its cost on the clock, then observes the token it was given.</summary>
    internal void Spend(string name, CancellationToken token)
    {
        lock (Stages)
        {
            Stages.Add(new(name, Clock.Elapsed, token));
        }

        OnStage?.Invoke(name);
        if (Costs.TryGetValue(name, out var cost) && cost > TimeSpan.Zero)
        {
            // Firing the frame's timer happens inside this call, as a real timer firing while the
            // stage ran would, so the stage then sees its own token cancelled.
            Clock.Advance(cost);
        }

        token.ThrowIfCancellationRequested();
    }

    /// <summary>The lines a provider reads off this scene for one pass.</summary>
    internal IReadOnlyList<OcrLine> LinesFor(ScanPass pass, OcrRequest request)
    {
        var lines = SceneLines(Scene);
        if (Scene == ScanScene.Container && pass == ScanPass.ContainerGrid && Engine.HideLastCellFromGridPass)
        {
            // The grid pass misses the third item, so its cell is read on its own.
            lines = [.. lines.Where(line => line.Text != "Power Cord")];
        }

        return request.Region is not { } region
            ? lines
            : [.. lines.Where(line => CapturedImagePixels.Intersects(region, line.Bounds))];
    }

    private static IReadOnlyList<OcrLine> SceneLines(ScanScene scene) => scene switch
    {
        ScanScene.SingleItem =>
        [
            new("INSPECT", new(100, 80, 150, 24), null),
            new("WEIGHT", new(100, 500, 150, 24), null),
            new("Graphics Card", new(300, 280, 250, 30), null),
        ],
        ScanScene.ExtractList =>
        [
            new("Find an extraction point 0:28:10", new(700, 20, 280, 20), new Confidence(0.96)),
            new("Road to Customs ACTIVE", new(700, 60, 280, 20), new Confidence(0.96)),
        ],
        ScanScene.Container =>
        [
            new("STASH", new(20, 20, 80, 20), new Confidence(0.95)),
            new("Wires x2", new(215, 145, 70, 22), new Confidence(0.95)),
            new("Graphics Card", new(415, 245, 80, 22), new Confidence(0.95)),
            new("Power Cord", new(510, 350, 80, 22), new Confidence(0.95)),
        ],
        ScanScene.FleaListings =>
        [
            new("FLEA MARKET", new(100, 40, 200, 24), new Confidence(0.95)),
            new("189 999 ₽ x3", new(1200, 300, 250, 24), new Confidence(0.92)),
            new("75,500 RUB", new(1200, 370, 220, 24), new Confidence(0.86)),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(scene)),
    };

    private static IReadOnlyList<CanonicalItemReference> Catalog(ScanScene scene) => scene switch
    {
        ScanScene.Container =>
        [
            new("wires", "Wires"),
            new("graphics-card", "Graphics Card"),
            new("power-cord", "Power Cord"),
        ],
        _ => [new("item-1", "Graphics Card")],
    };

    private static CapturedImage CreateImage(ScanScene scene)
    {
        var (width, height) = scene switch
        {
            ScanScene.ExtractList => (1000, 700),
            ScanScene.FleaListings => (1920, 1080),
            _ => (800, 600),
        };
        var pixels = new byte[checked(width * height)];
        if (scene == ScanScene.Container)
        {
            PaintContainer(pixels, width);
        }

        return new CapturedImage(
            pixels,
            width,
            height,
            width,
            PixelFormat.Gray8,
            ObservedUtc,
            "fixture://scan-harness/" + scene);
    }

    /// <summary>A 4x3 grid with occupied cells (0,0), (1,2) and (2,3), as the container tests draw it.</summary>
    private static void PaintContainer(byte[] pixels, int width)
    {
        Array.Fill(pixels, (byte)24);
        var grid = new ContainerGridSpec(new PixelRect(200, 120, 400, 300), 4, 3);
        foreach (var (row, column, value) in new[] { (0, 0, (byte)100), (1, 2, (byte)120), (2, 3, (byte)130) })
        {
            var left = grid.Bounds.X + ((grid.Bounds.Width * column) / grid.Columns);
            var right = grid.Bounds.X + ((grid.Bounds.Width * (column + 1)) / grid.Columns);
            var top = grid.Bounds.Y + ((grid.Bounds.Height * row) / grid.Rows);
            var bottom = grid.Bounds.Y + ((grid.Bounds.Height * (row + 1)) / grid.Rows);
            var horizontalMargin = Math.Max(1, (right - left) / 8);
            var verticalMargin = Math.Max(1, (bottom - top) / 8);
            for (var y = top + verticalMargin; y < bottom - verticalMargin; y++)
            {
                for (var x = left + horizontalMargin; x < right - horizontalMargin; x++)
                {
                    pixels[(y * width) + x] = value;
                }
            }
        }

        for (var column = 0; column <= grid.Columns; column++)
        {
            var x = grid.Bounds.X + ((grid.Bounds.Width * column) / grid.Columns);
            for (var y = grid.Bounds.Y; y <= grid.Bounds.Y + grid.Bounds.Height; y++)
            {
                pixels[(y * width) + x] = 220;
            }
        }

        for (var row = 0; row <= grid.Rows; row++)
        {
            var y = grid.Bounds.Y + ((grid.Bounds.Height * row) / grid.Rows);
            for (var x = grid.Bounds.X; x <= grid.Bounds.X + grid.Bounds.Width; x++)
            {
                pixels[(y * width) + x] = 220;
            }
        }
    }

    /// <summary>
    /// Answers each pass with the scene's lines, or with whatever a test substitutes for one pass.
    /// </summary>
    internal sealed class ScriptedScanEngine(ScanHarness harness) : IOcrEngine
    {
        private int _calls;

        /// <summary>Replaces the answer to one kind of pass, given the answer it would have had.</summary>
        public Dictionary<ScanPass, Func<OcrResult, OcrResult>> Substitutes { get; } = [];

        public List<ScanPass> Passes { get; } = [];

        public bool HideLastCellFromGridPass { get; set; }

        public Task<OcrResult> RecognizeAsync(
            CapturedImage image,
            OcrRequest request,
            CancellationToken cancellationToken)
        {
            var pass = Classify(_calls++, request);
            Passes.Add(pass);
            harness.Spend(pass.ToString(), cancellationToken);
            var clean = new OcrResult(harness.LinesFor(pass, request), TimeSpan.FromMilliseconds(5), "scripted-scan");
            return Task.FromResult(Substitutes.TryGetValue(pass, out var substitute) ? substitute(clean) : clean);
        }

        private static ScanPass Classify(int index, OcrRequest request) => index switch
        {
            0 => ScanPass.RecognitionFrame,
            1 when request.Context != ScanContext.Unknown => ScanPass.RecognitionContext,
            _ => request.Context switch
            {
                ScanContext.ExtractList when request.Region is null => ScanPass.ExtractFrame,
                ScanContext.ExtractList => ScanPass.ExtractPanel,
                ScanContext.Container when request.Region is { Width: > 150 } => ScanPass.ContainerGrid,
                ScanContext.Container => ScanPass.ContainerCell,
                ScanContext.FleaListings => ScanPass.Flea,
                _ => throw new InvalidOperationException($"Unexpected OCR request {index}: {request}"),
            },
        };
    }

    internal sealed class CostedItemRepository(ScanHarness harness, ScanScene scene) : IItemRepository
    {
        private static readonly DataProvenance Provenance = new("fixture", ObservedUtc);

        public bool HasPrice { get; set; } = true;

        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken)
        {
            harness.Spend("item", cancellationToken);
            return Task.FromResult<ItemDefinition?>(new(
                itemId,
                "Graphics Card",
                "GPU",
                string.Empty,
                ItemCategory.Barter,
                new(2, 1),
                true,
                null,
                null,
                null,
                null,
                null,
                new HashSet<string>(StringComparer.Ordinal),
                Provenance));
        }

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken)
        {
            harness.Spend("price", cancellationToken);
            var value = scene == ScanScene.Container && itemId == "wires" ? 12_000L : 200_000L;
            return Task.FromResult<ItemPriceSnapshot?>(HasPrice
                ? new ItemPriceSnapshot(value, [], value, value, value, Provenance)
                : null);
        }
    }

    internal sealed class CostedMapDataService(ScanHarness harness) : IMapDataService
    {
        public Task<MapDefinition?> GetAsync(string mapId, CancellationToken cancellationToken)
        {
            harness.Spend("map", cancellationToken);
            var provenance = new DataProvenance("fixture", ObservedUtc);
            return Task.FromResult<MapDefinition?>(new MapDefinition(
                "customs",
                "Customs",
                null,
                null,
                [],
                [new MapExtract("road", "customs", "Road to Customs", null, null, provenance)],
                null,
                provenance));
        }
    }

    internal sealed class CostedRecommendationContextProvider(ScanHarness harness) : IScanRecommendationContextProvider
    {
        public bool HasContext { get; set; } = true;

        public int Calls { get; private set; }

        public Task<RecommendationContext?> GetAsync(
            ItemDefinition item,
            RecognitionCandidate recognition,
            CancellationToken cancellationToken)
        {
            Calls++;
            harness.Spend("context", cancellationToken);
            return Task.FromResult<RecommendationContext?>(HasContext
                ? new(false, 0, 0, 0, false, EventItemState.Unknown, null, null, recognition.Confidence)
                : null);
        }
    }

    internal sealed class RecordingEvents : IScanEventRepository
    {
        public List<ScanEventMetadata> Saved { get; } = [];

        public Task SaveAsync(ScanEventMetadata scanEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved.Add(scanEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedCapture : IScreenCaptureService
    {
        public Task<CapturedImage> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The harness scans images it was given.");
    }
}

/// <summary>A clock that moves only when told to, and fires the timers it created as it passes them.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _elapsed;

    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return _elapsed;
            }
        }
    }

    /// <summary>The due time of every timer created, in order: the budgets anything started on this clock.</summary>
    public List<TimeSpan> TimersCreated { get; } = [];

    public override DateTimeOffset GetUtcNow() => start + Elapsed;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Elapsed.Ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            TimersCreated.Add(dueTime);
            _timers.Add(timer);
            timer.Schedule(_elapsed, dueTime);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _elapsed += by;
            due = [.. _timers.Where(timer => timer.DueAt is { } at && at <= _elapsed)];
            foreach (var timer in due)
            {
                timer.DueAt = null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        public TimeSpan? DueAt { get; set; }

        public void Schedule(TimeSpan now, TimeSpan dueTime) =>
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                Schedule(clock._elapsed, dueTime);
            }

            return true;
        }

        public void Dispose() => clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
