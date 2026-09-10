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

namespace TarkovCompanion.RecognitionTests;

public sealed class ScanUseCaseTests
{
    private static readonly DateTimeOffset ObservedUtc =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SingleItemFlowsFromCaptureThroughRecommendationAndMetadataPublication()
    {
        var candidate = Candidate("item-1", "Graphics Card", 0.96);
        var harness = new Harness(new(ScanContext.SingleItem, [candidate], ObservedUtc));

        var outcome = await harness.UseCase.ScanAsync(harness.Request, CancellationToken.None);

        Assert.Equal(ScanCompletionStatus.Complete, outcome.Status);
        Assert.Equal("item-1", outcome.Recognition.Selected?.CanonicalId);
        Assert.NotNull(outcome.Recommendation);
        Assert.Single(harness.Events.Saved);
        Assert.Equal("item-1", harness.Events.Saved[0].ResolvedItemId);
        Assert.Equal(new PixelRect(0, 0, 800, 600), harness.Events.Saved[0].SourceGeometry);
        Assert.Same(outcome, harness.Publisher.Current);
        Assert.Contains(outcome.Evidence, evidence =>
            evidence.Code == "capture" && evidence.Detail.Contains("pixels were not persisted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractScanUsesCurrentMapAndPublishesOnlyActiveObservationsToRaidState()
    {
        var harness = new Harness(new(ScanContext.ExtractList, [], ObservedUtc, "extract_context"));
        harness.RaidState.Apply(new(
            RaidEvidenceKind.ManualOverride,
            ObservedUtc.AddMinutes(-1),
            "customs",
            RaidLifecycleState.InRaid,
            Confidence.Certain,
            "test map"));
        harness.Extracts.Result = new(
            [new ActiveExtract("road", "Road to Customs", new Confidence(0.95), "fixture")],
            [
                new("road", "Road to Customs", ExtractStatus.Active, new Confidence(0.95), "fixture", ObservedUtc),
                new("dorms", "Dorms V-EX", ExtractStatus.Closed, new Confidence(0.94), "fixture", ObservedUtc),
            ],
            [],
            [],
            true);

        var outcome = await harness.UseCase.ScanAsync(harness.Request, CancellationToken.None);

        Assert.Equal(ScanCompletionStatus.Complete, outcome.Status);
        Assert.Equal(1, harness.Extracts.Calls);
        Assert.Equal(["road"], harness.RaidState.Current.ActiveExtracts.Select(extract => extract.ExtractId));
        Assert.Contains(outcome.Evidence, evidence => evidence.Code == "extract_closed");
    }

    [Fact]
    public async Task ContainerScanPublishesHonestPartialResult()
    {
        var harness = new Harness(new(ScanContext.Container, [], ObservedUtc));
        harness.Containers.Result = new(
            [Candidate("wires", "Wires", 0.88)],
            12_000,
            1,
            ["wires"],
            new Confidence(0.88),
            [new(0, 1, new(100, 0, 100, 100), "occupied_without_candidate", [])],
            [],
            true,
            "container_partial");

        var outcome = await harness.UseCase.ScanAsync(harness.Request, CancellationToken.None);

        Assert.Equal(ScanCompletionStatus.Partial, outcome.Status);
        Assert.Equal(1, harness.Containers.Calls);
        Assert.True(outcome.Container?.IsPartial);
        Assert.Equal("container_partial", harness.Events.Saved[0].DiagnosticCode);
        Assert.Equal("wires", Assert.Single(harness.Events.Saved[0].Candidates).CanonicalId);
    }

    [Fact]
    public async Task FleaScanPublishesVisibleRowsWithoutPerformingMarketActions()
    {
        var harness = new Harness(new(ScanContext.FleaListings, [], ObservedUtc));
        harness.Flea.Result = new(
            [new(75_500, 2, new Confidence(0.90), new(500, 200, 160, 24))],
            ObservedUtc,
            new Confidence(0.90),
            true);

        var outcome = await harness.UseCase.ScanAsync(harness.Request, CancellationToken.None);

        Assert.Equal(ScanCompletionStatus.Complete, outcome.Status);
        Assert.Equal(1, harness.Flea.Calls);
        Assert.Contains(outcome.Evidence, evidence =>
            evidence.Code == "flea_rows" &&
            evidence.Detail.Contains("no market action was performed", StringComparison.Ordinal));
    }

    private static RecognitionCandidate Candidate(string id, string name, double confidence) => new(
        id,
        name,
        new Confidence(confidence),
        "fixture",
        new PixelRect(100, 100, 200, 30));

    private sealed class Harness
    {
        private readonly ItemDefinition _item;
        private readonly ItemPriceSnapshot _price;

        public Harness(RecognitionResult recognition)
        {
            var provenance = new DataProvenance("fixture", ObservedUtc);
            _item = new(
                "item-1",
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
                provenance);
            _price = new(
                200_000,
                [],
                195_000,
                190_000,
                205_000,
                provenance);
            var image = new CapturedImage(
                new byte[800 * 600],
                800,
                600,
                800,
                PixelFormat.Gray8,
                ObservedUtc,
                "fixture://scan-use-case");
            Capture = new(image);
            Recognition = new(recognition);
            Extracts = new();
            Containers = new();
            Flea = new();
            RaidState = new();
            Events = new();
            Publisher = new();
            var items = new StaticItemRepository(_item, _price);
            UseCase = new(
                Capture,
                Recognition,
                Extracts,
                Containers,
                Flea,
                new StaticMapDataService(CreateMap(provenance)),
                RaidState,
                items,
                new RecommendationEngine(),
                new StaticRecommendationContextProvider(),
                Events,
                Publisher);
        }

        public ScanUseCase UseCase { get; }

        public CaptureRequest CaptureRequest { get; } = new("eft", null, false, "test");

        public ScanRequest Request => new(CaptureRequest);

        public StaticCapture Capture { get; }

        public StaticRecognition Recognition { get; }

        public TrackingExtractService Extracts { get; }

        public TrackingContainerService Containers { get; }

        public TrackingFleaService Flea { get; }

        public RaidStateService RaidState { get; }

        public RecordingScanEventRepository Events { get; }

        public LatestScanResultPublisher Publisher { get; }

        private static MapDefinition CreateMap(DataProvenance provenance) => new(
            "customs",
            "Customs",
            null,
            null,
            [],
            [new MapExtract("road", "customs", "Road to Customs", null, null, provenance)],
            null,
            provenance);
    }

    private sealed class StaticCapture(CapturedImage image) : IScreenCaptureService
    {
        public Task<CapturedImage> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(image);
        }
    }

    private sealed class StaticRecognition(RecognitionResult result) : IRecognitionService
    {
        public Task<RecognitionResult> RecognizeAsync(CapturedImage image, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class TrackingExtractService : IExtractRecognitionService
    {
        public int Calls { get; private set; }

        public ExtractRecognitionResult Result { get; set; } = new([], [], [], [], true);

        public Task<ExtractRecognitionResult> RecognizeAsync(
            CapturedImage image,
            MapDefinition currentMap,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class TrackingContainerService : IContainerRecognitionService
    {
        public int Calls { get; private set; }

        public ContainerScanResult Result { get; set; } = new(
            [], 0, 0, [], Confidence.Unknown, [], [], true, "not-configured");

        public Task<ContainerScanResult> RecognizeAsync(CapturedImage image, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class TrackingFleaService : IFleaRecognitionService
    {
        public int Calls { get; private set; }

        public FleaRecognitionResult Result { get; set; } = new(
            [], ObservedUtc, Confidence.Unknown, true, "not-configured");

        public Task<FleaRecognitionResult> RecognizeAsync(CapturedImage image, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class StaticMapDataService(MapDefinition map) : IMapDataService
    {
        public Task<MapDefinition?> GetAsync(string mapId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<MapDefinition?>(map.Id == mapId ? map : null);
        }
    }

    private sealed class StaticItemRepository(ItemDefinition item, ItemPriceSnapshot price) : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(item.Id == itemId ? item : null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(item.Id == itemId ? price : null);
    }

    private sealed class StaticRecommendationContextProvider : IScanRecommendationContextProvider
    {
        public Task<RecommendationContext?> GetAsync(
            ItemDefinition item,
            RecognitionCandidate recognition,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<RecommendationContext?>(new(
                false,
                0,
                0,
                0,
                false,
                EventItemState.Unknown,
                null,
                null,
                recognition.Confidence));
        }
    }

    private sealed class RecordingScanEventRepository : IScanEventRepository
    {
        public List<ScanEventMetadata> Saved { get; } = [];

        public Task SaveAsync(ScanEventMetadata scanEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved.Add(scanEvent);
            return Task.CompletedTask;
        }
    }
}
