using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.Profile;
using TarkovCompanion.Infrastructure.TarkovDevJson;
using TarkovCompanion.Infrastructure.TarkovTracker;

namespace TarkovCompanion.IntegrationTests;

public sealed class TarkovTrackerIntegrationTests
{
    private const string TestToken = "PVP_not-a-real-credential";
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 16, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    [Fact]
    public async Task SnapshotUsesSharedConflictAtomicApplyAndUndoBoundary()
    {
        await using var context = await Context.CreateAsync();
        await context.Store.ApplyAsync(new SetTaskStateMutation(
            context.Scope,
            context.Profile.Name,
            "task-contract",
            RecordedTaskState.Active,
            QuestProgressActor.User,
            "Manual",
            Guid.NewGuid(),
            Now), CancellationToken.None);
        await context.Store.ApplyAsync(new SetObjectiveProgressMutation(
            context.Scope,
            context.Profile.Name,
            "objective-find-item",
            RecordedObjectiveState.Completed,
            3,
            QuestProgressActor.User,
            "Manual",
            Guid.NewGuid(),
            Now), CancellationToken.None);

        var connected = await context.Integration.ConnectAsync(
            context.Scope,
            TestToken,
            CancellationToken.None);
        var refreshed = await context.Integration.RefreshPreviewAsync(
            context.Scope,
            TarkovTrackerRefreshKind.Manual,
            CancellationToken.None);
        var preview = refreshed.Preview;

        Assert.True(connected.Connected);
        Assert.Equal(QuestProgressImportSource.TarkovTracker, preview.Source);
        Assert.Contains("fetched time is not source edit time", preview.ProvenanceSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(TestToken, preview.ToString(), StringComparison.Ordinal);
        Assert.Single(preview.SafeProposals);
        var conflict = Assert.Single(preview.Conflicts);
        Assert.Equal("objective-find-item", conflict.EntityId);
        Assert.Equal(2, preview.Unresolved.Count);
        Assert.Contains(preview.Unresolved, value => value.EntityId == "unknown-task");
        Assert.Contains(preview.Unresolved, value =>
            value.EntityId == "objective-mark" &&
            value.Classification == QuestImportClassification.UnresolvedSourceRecord);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Exchange.ApplyImportAsync(
            preview,
            new Dictionary<string, QuestImportResolution>(),
            CancellationToken.None));
        var unchanged = await context.Store.GetAsync(context.Scope, CancellationToken.None);
        Assert.Equal(RecordedTaskState.Active, unchanged.Tasks["task-contract"].State);
        Assert.Equal(3, unchanged.Objectives["objective-find-item"].Count);

        var applied = await context.Exchange.ApplyImportAsync(
            preview,
            new Dictionary<string, QuestImportResolution>
            {
                [conflict.Key] = QuestImportResolution.UseIncoming,
            },
            CancellationToken.None);
        var changed = await context.Store.GetAsync(context.Scope, CancellationToken.None);
        Assert.Equal(2, applied.AppliedChangeCount);
        Assert.Equal(2, applied.UnresolvedCount);
        Assert.Equal(RecordedTaskState.Completed, changed.Tasks["task-contract"].State);
        Assert.Equal(2, changed.Objectives["objective-find-item"].Count);
        Assert.All(
            (await context.Store.GetJournalAsync(context.Scope, CancellationToken.None))
                .Where(value => value.CorrelationId == applied.ImportId),
            value => Assert.Equal("TarkovTracker read-only snapshot import", value.Source));

        var undone = await context.Exchange.UndoImportAsync(
            context.Scope,
            applied.ImportId,
            CancellationToken.None);
        var restored = await context.Store.GetAsync(context.Scope, CancellationToken.None);
        Assert.Equal(2, undone.RestoredChangeCount);
        Assert.Equal(RecordedTaskState.Active, restored.Tasks["task-contract"].State);
        Assert.Equal(3, restored.Objectives["objective-find-item"].Count);

        var disconnected = await context.Integration.DisconnectAsync(context.Scope, CancellationToken.None);
        Assert.False(disconnected.Connected);
        Assert.False(await context.Secrets.ExistsAsync(
            SecretReference(context.Scope),
            CancellationToken.None));
        Assert.Equal(["/token", "/progress"], context.Handler.Paths);
        Assert.All(context.Handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    [Fact]
    public async Task Etag304RepreviewsCachedSnapshotAndForegroundRefreshIsMinuteBounded()
    {
        await using var context = await Context.CreateAsync(returnNotModifiedAfterFirstProgress: true);
        await context.Integration.ConnectAsync(context.Scope, TestToken, CancellationToken.None);
        var first = await context.Integration.RefreshPreviewAsync(
            context.Scope,
            TarkovTrackerRefreshKind.Manual,
            CancellationToken.None);
        var second = await context.Integration.RefreshPreviewAsync(
            context.Scope,
            TarkovTrackerRefreshKind.Manual,
            CancellationToken.None);

        Assert.False(first.NotModified);
        Assert.True(second.NotModified);
        Assert.Equal(first.Preview.PayloadSha256, second.Preview.PayloadSha256);
        Assert.Equal("W/\"stage-5\"", context.Handler.IfNoneMatches.Last());
        var requestCount = context.Handler.Paths.Count;
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Integration.RefreshPreviewAsync(
                context.Scope,
                TarkovTrackerRefreshKind.Foreground,
                CancellationToken.None));
        Assert.Contains("once per minute", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(requestCount, context.Handler.Paths.Count);
    }

    [Fact]
    public async Task ExhaustedQuotaPausesRefreshUntilReportedResetWithoutAnotherRequest()
    {
        await using var context = await Context.CreateAsync(quotaExhausted: true);
        await context.Integration.ConnectAsync(context.Scope, TestToken, CancellationToken.None);
        var first = await context.Integration.RefreshPreviewAsync(
            context.Scope,
            TarkovTrackerRefreshKind.Manual,
            CancellationToken.None);

        Assert.Equal(0, first.Status.Quota.Remaining);
        Assert.Equal(Now.AddHours(1), first.Status.NextEligibleRefreshUtc);
        var requestCount = context.Handler.Paths.Count;
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Integration.RefreshPreviewAsync(
                context.Scope,
                TarkovTrackerRefreshKind.Manual,
                CancellationToken.None));
        Assert.Contains("paused until", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(requestCount, context.Handler.Paths.Count);
    }

    [Fact]
    public async Task FeatureOfflineAndUnavailableStorageStatesNeverCallApi()
    {
        var scope = new QuestProfileScope(
            Guid.Parse("b651f8be-977f-4bdd-a66f-3d1b2a9fb1c1"),
            GameMode.Regular,
            "generation-a");
        var api = new StubApiClient();
        var planner = new StubPlanner();
        var availableStore = new InMemoryIntegrationSecretStore(isAvailable: true);
        using var disabled = new TarkovTrackerIntegrationService(
            api,
            availableStore,
            planner,
            new() { Enabled = false });
        var disabledStatus = await disabled.GetStatusAsync(scope, CancellationToken.None);
        Assert.False(disabledStatus.CanConnect);
        await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.ConnectAsync(
            scope,
            TestToken,
            CancellationToken.None));

        using var offline = new TarkovTrackerIntegrationService(
            api,
            availableStore,
            planner,
            new() { Enabled = true, NetworkAccessEnabled = false });
        Assert.False((await offline.GetStatusAsync(scope, CancellationToken.None)).CanConnect);

        using var unavailable = new TarkovTrackerIntegrationService(
            api,
            new InMemoryIntegrationSecretStore(isAvailable: false),
            planner,
            new() { Enabled = true });
        var unavailableStatus = await unavailable.GetStatusAsync(scope, CancellationToken.None);
        Assert.False(unavailableStatus.SecureStorageAvailable);
        Assert.False(unavailableStatus.CanConnect);
        Assert.Equal(0, api.CallCount);
    }

    private static IntegrationSecretReference SecretReference(QuestProfileScope scope) => new(
        IntegrationSecretKind.TarkovTrackerProgressToken,
        scope.ProfileId,
        scope.GameMode,
        scope.Generation);

    private sealed class Context : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly JsonFilePlayerProfileService _profileService;
        private readonly TarkovTrackerApiClient _apiClient;

        private Context(
            string directory,
            PlayerProfile profile,
            JsonFilePlayerProfileService profileService,
            SqliteQuestProgressStore store,
            QuestProgressExchangeService exchange,
            TarkovTrackerIntegrationService integration,
            InMemoryIntegrationSecretStore secrets,
            ProgressHandler handler,
            TarkovTrackerApiClient apiClient)
        {
            _directory = directory;
            Profile = profile;
            _profileService = profileService;
            Store = store;
            Exchange = exchange;
            Integration = integration;
            Secrets = secrets;
            Handler = handler;
            _apiClient = apiClient;
        }

        internal PlayerProfile Profile { get; }

        internal QuestProfileScope Scope => new(Profile.Id, Profile.GameMode, Profile.ProfileGeneration);

        internal SqliteQuestProgressStore Store { get; }

        internal QuestProgressExchangeService Exchange { get; }

        internal TarkovTrackerIntegrationService Integration { get; }

        internal InMemoryIntegrationSecretStore Secrets { get; }

        internal ProgressHandler Handler { get; }

        internal static async Task<Context> CreateAsync(
            bool returnNotModifiedAfterFirstProgress = false,
            bool quotaExhausted = false)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"tarkov-tracker-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var factory = new SqliteConnectionFactory(new(Path.Combine(directory, "progress.db")));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            await ImportCatalogAsync(factory);
            var profile = new PlayerProfile(
                Guid.Parse("940d35d5-47a2-4a25-afb9-94145166d65b"),
                "Quest profile",
                GameMode.Regular,
                25,
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
                Now,
                "generation-a");
            var profileService = new JsonFilePlayerProfileService(
                new(Path.Combine(directory, "profile.json")),
                new ManualTimeProvider(Now));
            await profileService.SaveAsync(profile, CancellationToken.None);
            var store = new SqliteQuestProgressStore(factory);
            var exchange = new QuestProgressExchangeService(
                profileService,
                new SqliteQuestCatalog(factory),
                store,
                new SqliteQuestProgressImportStore(factory, new ManualTimeProvider(Now)),
                new ProjectQuestProgressJson(new("test"), new ManualTimeProvider(Now)),
                new());
            var handler = new ProgressHandler(returnNotModifiedAfterFirstProgress, quotaExhausted);
            var apiClient = new TarkovTrackerApiClient(
                handler,
                new() { Enabled = true },
                new ManualTimeProvider(Now));
            var secrets = new InMemoryIntegrationSecretStore(isAvailable: true);
            var integration = new TarkovTrackerIntegrationService(
                apiClient,
                secrets,
                exchange,
                new() { Enabled = true },
                new ManualTimeProvider(Now));
            return new(
                directory,
                profile,
                profileService,
                store,
                exchange,
                integration,
                secrets,
                handler,
                apiClient);
        }

        public ValueTask DisposeAsync()
        {
            Integration.Dispose();
            _apiClient.Dispose();
            _profileService.Dispose();
            SqliteConnection.ClearAllPools();
            TemporaryDirectory.Remove(_directory);
            return ValueTask.CompletedTask;
        }

        private static async Task ImportCatalogAsync(SqliteConnectionFactory factory)
        {
            var json = await FixtureJson.ReadAsync("tasks-contract.json");
            var envelope = JsonSerializer.Deserialize<TarkovDevEnvelope<TarkovDevTasksData>>(
                json,
                SerializerOptions) ?? throw new InvalidDataException("Quest fixture was null.");
            var response = new TarkovDevResponse<TarkovDevTasksData>(
                envelope.Data,
                json,
                Now,
                false,
                false,
                "\"stage-5\"",
                Now,
                json);
            var catalog = new TarkovDevQuestCatalogNormalizer().Normalize(
                response,
                GameMode.Regular,
                "en",
                Now);
            await new SqliteDataRefreshRepository(factory).RefreshTasksAsync(catalog, CancellationToken.None);
        }
    }

    private sealed class ProgressHandler(
        bool returnNotModifiedAfterFirstProgress,
        bool quotaExhausted) : HttpMessageHandler
    {
        private int _progressCalls;

        internal List<string> Paths { get; } = [];

        internal List<HttpMethod> Methods { get; } = [];

        internal List<string?> IfNoneMatches { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri?.AbsolutePath
                ?? throw new InvalidOperationException("Request URI was missing.");
            Paths.Add(path);
            Methods.Add(request.Method);
            IfNoneMatches.Add(request.Headers.IfNoneMatch.SingleOrDefault()?.ToString());
            if (path == "/token")
            {
                return Task.FromResult(Json($$$"""
                    {"success":true,"permissions":["GP"],"token":"{{{TestToken}}}","gameMode":"pvp"}
                    """));
            }

            if (path != "/progress")
            {
                throw new InvalidOperationException("Unsupported test route.");
            }

            _progressCalls++;
            HttpResponseMessage response;
            if (returnNotModifiedAfterFirstProgress && _progressCalls > 1)
            {
                response = new(HttpStatusCode.NotModified);
            }
            else
            {
                response = Json("""
                    {"success":true,"data":{"tasksProgress":[{"id":"task-contract","complete":true},{"id":"unknown-task","complete":true}],"taskObjectivesProgress":[{"id":"objective-find-item","complete":true,"count":2},{"id":"objective-mark","complete":true,"invalid":true}]},"meta":{"gameMode":"pvp","self":"not-mapped"}}
                    """);
            }

            response.Headers.ETag = EntityTagHeaderValue.Parse("W/\"stage-5\"");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", "1000");
            response.Headers.TryAddWithoutValidation(
                "X-RateLimit-Remaining",
                quotaExhausted ? "0" : (999 - _progressCalls).ToString());
            if (quotaExhausted)
            {
                response.Headers.TryAddWithoutValidation(
                    "X-RateLimit-Reset",
                    Now.AddHours(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class InMemoryIntegrationSecretStore(bool isAvailable) : IIntegrationSecretStore
    {
        private readonly Dictionary<IntegrationSecretReference, string> _values = [];

        public bool IsAvailable { get; } = isAvailable;

        public Task SaveAsync(
            IntegrationSecretReference reference,
            string secret,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAvailable)
            {
                throw new PlatformNotSupportedException();
            }

            _values[reference] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> LoadAsync(
            IntegrationSecretReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.TryGetValue(reference, out var secret);
            return Task.FromResult(secret);
        }

        public Task<bool> ExistsAsync(
            IntegrationSecretReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(IsAvailable && _values.ContainsKey(reference));
        }

        public Task DeleteAsync(
            IntegrationSecretReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(reference);
            return Task.CompletedTask;
        }
    }

    private sealed class StubApiClient : ITarkovTrackerApiClient
    {
        internal int CallCount { get; private set; }

        public Task<TarkovTrackerTokenValidation> ValidateTokenAsync(
            string token,
            GameMode expectedMode,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new TarkovTrackerTokenValidation(
                expectedMode,
                new(null, null, null)));
        }

        public Task<TarkovTrackerProgressFetch> GetProgressAsync(
            string token,
            GameMode expectedMode,
            string? etag,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("API should not be called by disabled feature states.");
        }
    }

    private sealed class StubPlanner : IQuestProgressImportPlanner
    {
        public Task<QuestProgressImportPreview> PreviewAsync(
            QuestProfileScope scope,
            QuestProgressImportSnapshot snapshot,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Planner should not be called by disabled feature states.");
    }
}
