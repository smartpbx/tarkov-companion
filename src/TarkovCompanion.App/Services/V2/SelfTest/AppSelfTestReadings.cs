using System.Net.Http.Json;
using System.Text.Json;
using TarkovCompanion.App.Services.V2;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Infrastructure.Diagnostics;
using TarkovCompanion.Infrastructure.TarkovDevJson;
using TarkovCompanion.App.Localization;

namespace TarkovCompanion.App.Services.V2.SelfTest;

/// <summary>
/// Reads the running application's own services for the self-test.
/// </summary>
/// <remarks>
/// The only part of the self-test that touches a real installation, and deliberately the
/// thinnest: every method converts services into records and draws no conclusion. The
/// conclusions are <see cref="SelfTestProbes"/>, which is why they can be tested on a machine
/// with no game, no relay and no tablet.
///
/// Read-only throughout. Discovery re-probes folders, the log and screenshot readers open files
/// for reading with full sharing, game data and the database are SELECTs, the relay gets one
/// GET of its health route, and the tablet answer comes from state this desktop already holds.
/// Nothing here refreshes, publishes, claims, writes or deletes.
/// </remarks>
public sealed class AppSelfTestReadings : ISelfTestReadings
{
    /// <summary>One health read, short enough that a dead relay is not a long wait.</summary>
    private static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(5);

    private readonly IEftInstallDiscoverySource? _discovery;
    private readonly IEftPathOverrideStore? _overrides;
    private readonly SelfTestFolderReader _folders;
    private readonly SelfTestLogReader _logs;
    private readonly SelfTestScreenshotWatch _screenshots;
    private readonly SqliteSelfTestReader _database;
    private readonly IGroupSettingsStore _groupSettings;
    private readonly IRuntimeStateStore _runtime;
    private readonly HttpClient _httpClient;
    private readonly DesktopCompanionAuthority? _authority;
    private readonly TabletMapSurfacePublisher? _publisher;
    private readonly Uri? _companionOrigin;
    private readonly string _gameMode;
    private readonly string _language;
    private readonly TimeProvider _clock;
    private readonly IProfileRuntimeContextService? _profileContext;

    public AppSelfTestReadings(
        SqliteSelfTestReader database,
        IGroupSettingsStore groupSettings,
        IRuntimeStateStore runtime,
        HttpClient httpClient,
        SelfTestFolderReader folders,
        SelfTestLogReader logs,
        SelfTestScreenshotWatch screenshots,
        string gameMode,
        string language,
        IEftInstallDiscoverySource? discovery = null,
        IEftPathOverrideStore? overrides = null,
        DesktopCompanionAuthority? authority = null,
        TabletMapSurfacePublisher? publisher = null,
        Uri? companionOrigin = null,
        TimeProvider? timeProvider = null,
        // #269: which mode and language's endpoints to report on. Without it, the fixed ones above.
        IProfileRuntimeContextService? profileContext = null)
    {
        _profileContext = profileContext;
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _groupSettings = groupSettings ?? throw new ArgumentNullException(nameof(groupSettings));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _folders = folders ?? throw new ArgumentNullException(nameof(folders));
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _screenshots = screenshots ?? throw new ArgumentNullException(nameof(screenshots));
        _gameMode = gameMode;
        _language = language;
        _discovery = discovery;
        _overrides = overrides;
        _authority = authority;
        _publisher = publisher;
        _companionOrigin = companionOrigin;
        _clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<SelfTestFolders> ReadFoldersAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _clock.GetUtcNow();
        if (_discovery is null)
        {
            return new(
                false,
                SetupText.ProbeFoldersNeedWindows,
                nowUtc,
                [],
                SetupText.ProbeFoldersNotWindows);
        }

        var snapshot = await _discovery.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var named = _overrides is null
            ? EftPathOverrides.None
            : (await _overrides.GetAsync(cancellationToken).ConfigureAwait(false)).Normalized();

        return new(
            true,
            SetupText.DiscoveryDetail(snapshot),
            snapshot.CheckedUtc,
            [
                Describe(SelfTestFolderPurpose.Install, snapshot.Paths.InstallRoot, SetupText.ProbeWhyInstall),
                Describe(
                    SelfTestFolderPurpose.Logs,
                    snapshot.Paths.LogRoot,
                    Matches(named.LogRoot, snapshot.Paths.LogRoot)
                        ? SetupText.ProbeWhyTyped
                        : SetupText.ProbeWhyFirstLogFolder),
                Describe(
                    SelfTestFolderPurpose.Screenshots,
                    snapshot.Paths.ScreenshotRoot,
                    Matches(named.ScreenshotRoot, snapshot.Paths.ScreenshotRoot)
                        ? SetupText.ProbeWhyTyped
                        : SetupText.ProbeWhyNewestScreenshot),
            ],
            snapshot.Status == EftInstallDiscoveryStatus.Unavailable ? SetupText.DiscoveryDetail(snapshot) : null);
    }

    public Task<SelfTestLogs> ReadLogsAsync(CancellationToken cancellationToken) =>
        _logs.ReadAsync(LastKnownLogRoot(), cancellationToken);

    public Task<SelfTestScreenshot> WatchScreenshotAsync(TimeSpan patience, CancellationToken cancellationToken) =>
        _screenshots.WatchAsync(
            LastKnownScreenshotRoot(),
            patience,
            _clock.LocalTimeZone.GetUtcOffset(_clock.GetUtcNow()),
            cancellationToken);

    public Task<SelfTestScreenshot> RecentScreenshotAsync(TimeSpan lookBack, CancellationToken cancellationToken) =>
        _screenshots.RecentAsync(
            LastKnownScreenshotRoot(),
            lookBack,
            _clock.LocalTimeZone.GetUtcOffset(_clock.GetUtcNow()),
            cancellationToken);

    public async Task<SelfTestGameData> ReadGameDataAsync(CancellationToken cancellationToken)
    {
        // The active profile's scope when there is one: the catalog is fetched for it, so its endpoints
        // are the ones whose age and size mean anything after a switch.
        var (gameMode, language) = _profileContext?.Current.CatalogScope is { } scope
            ? (TarkovDevDataRefreshOperation.ModeSlug(scope.GameMode), scope.Language)
            : (_gameMode, _language);
        var rows = await _database.ReadEndpointsAsync(gameMode, language, cancellationToken).ConfigureAwait(false);
        return new(
            gameMode,
            language,
            [.. rows.Select(row => new SelfTestEndpoint(
                row.SourceKey,
                row.Bytes,
                row.LastSuccessUtc,
                row.RecordCount,
                row.Status,
                Failure(row)))],
            _clock.GetUtcNow());
    }

    public async Task<SelfTestDatabase> ReadDatabaseAsync(CancellationToken cancellationToken)
    {
        var row = await _database.ReadDatabaseAsync(cancellationToken).ConfigureAwait(false);
        return new(
            row.Path,
            row.Bytes,
            row.Applied,
            [.. SqliteMigrationNames()],
            [.. row.Tables.Select(table => new SelfTestTable(table.Name, table.Rows))],
            _clock.GetUtcNow());
    }

    public async Task<SelfTestRelay> ReadRelayAsync(CancellationToken cancellationToken)
    {
        var settings = await _groupSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        var group = _runtime.Current.Group;
        var others = group.Members
            .Where(member => !string.Equals(member.Name, settings.DisplayName, StringComparison.Ordinal))
            .Select(member => new SelfTestSquadmate(member.Name, member.MapId, member.PositionAge, member.Since))
            .ToArray();
        var latency = Timed(group.PositionLatency);
        if (string.IsNullOrWhiteSpace(settings.ServerUri))
        {
            return new SelfTestRelay(
                false, null, false, null, null, null, null, null, null,
                group.IsSharing, settings.DisplayName, others, group.StaleSince, _clock.GetUtcNow())
            {
                PositionLatency = latency,
            };
        }

        var startedAt = _clock.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(settings.ServerUri), "health"));
            using var timeout = new CancellationTokenSource(RelayTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var response = await _httpClient.SendAsync(request, linked.Token).ConfigureAwait(false);
            var roundTrip = _clock.GetElapsedTime(startedAt);
            if (!response.IsSuccessStatusCode)
            {
                return Unreachable(
                    settings,
                    group,
                    others,
                    SetupText.ProbeRelayStatusCode((int)response.StatusCode, response.ReasonPhrase));
            }

            var health = await response.Content
                .ReadFromJsonAsync<RelayHealth>(cancellationToken: linked.Token)
                .ConfigureAwait(false);
            return new SelfTestRelay(
                true,
                settings.ServerUri,
                true,
                health?.Version,
                health?.Commit,
                health?.Protocol,
                health?.Rooms,
                health?.Members,
                roundTrip,
                group.IsSharing,
                settings.DisplayName,
                others,
                group.StaleSince,
                _clock.GetUtcNow())
            {
                PositionLatency = latency,
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or UriFormatException)
        {
            return Unreachable(settings, group, others, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unreachable(settings, group, others, SetupText.ProbeRelayTimeout(RelayTimeout.TotalSeconds));
        }
    }

    public Task<SelfTestTablet> ReadTabletAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nowUtc = _clock.GetUtcNow();
        if (_authority is null)
        {
            return Task.FromResult(new SelfTestTablet(
                false, null, [], false, null, null, 0, nowUtc,
                SetupText.ProbeTabletNoAuthority));
        }

        var devices = _authority.Snapshot.Devices
            .Select(device => new SelfTestDevice(
                device.DisplayName,
                device.Role.ToString(),
                device.Status.ToString(),
                device.LastUsedUtc))
            .ToArray();
        var surface = _publisher?.LastSurface;
        return Task.FromResult(new SelfTestTablet(
            true,
            _companionOrigin?.ToString(),
            devices,
            _publisher?.LastPublishedUtc is not null,
            _publisher?.LastPublishedUtc,
            surface?.MapName,
            surface?.Objects.Count ?? 0,
            nowUtc));
    }

    private SelfTestRelay Unreachable(
        GroupSharingSettings settings,
        GroupSnapshot group,
        IReadOnlyList<SelfTestSquadmate> others,
        string problem) =>
        new SelfTestRelay(
            true, settings.ServerUri, false, null, null, null, null, null, null,
            group.IsSharing, settings.DisplayName, others, group.StaleSince, _clock.GetUtcNow(), problem)
        {
            PositionLatency = Timed(group.PositionLatency),
        };

    /// <summary>Package 31's own measurement, carried across without being re-derived here.</summary>
    private static SelfTestPositionLatency Timed(GroupPositionLatencySnapshot latency) =>
        latency.HasSamples
            ? new(latency.Delivered, latency.SampleCount, latency.Median, latency.Slowest95, latency.Last)
            : SelfTestPositionLatency.None;

    private SelfTestFolderReading Describe(SelfTestFolderPurpose purpose, string? path, string why)
    {
        var state = _folders.Read(path);
        return new(purpose, path, why, state.Exists, state.NewestWriteUtc, state.Entries, state.Problem);
    }

    private string? LastKnownLogRoot() => _discovery?.Current.Paths.LogRoot ?? _runtime.Current.Observation.LogRoot;

    private string? LastKnownScreenshotRoot() =>
        _discovery?.Current.Paths.ScreenshotRoot ?? _runtime.Current.Observation.ScreenshotRoot;

    private static bool Matches(string? named, string? chosen) =>
        named is { Length: > 0 } && string.Equals(named, chosen, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why an endpoint has no rows, in the words the sync itself recorded.
    /// </summary>
    /// <remarks>
    /// "2 endpoint refresh(es) failed" with no names cost a day. The reason is already in the
    /// sync record; nothing had ever shown it.
    /// </remarks>
    private static string? Failure(SelfTestEndpointRow row) => row.ErrorSummary is { Length: > 0 } summary
        ? summary
        : row.Status is "refused" or "failed" or "error"
            ? SetupText.ProbeDataLastRefresh(row.Status)
            : null;

    private static IEnumerable<string> SqliteMigrationNames() =>
        Infrastructure.Persistence.SqliteMigrationLedger.Entries.Select(entry => entry.Id);

    /// <summary>The relay's own health route, which is the only thing this asks it for.</summary>
    private sealed record RelayHealth(
        string? Status,
        int? Protocol,
        string? Version,
        string? Commit,
        DateTimeOffset? StartedUtc,
        int? Rooms,
        int? Members);
}
