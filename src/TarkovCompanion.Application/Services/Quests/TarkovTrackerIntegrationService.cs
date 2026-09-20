using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Quests;

public sealed class TarkovTrackerIntegrationService : ITarkovTrackerIntegrationService, IDisposable
{
    public const string ContractVersion =
        "OpenAPI 2.5.0 @ 443d9fd73f0f88cac1623206fe79ba122ab9b1fb";

    private readonly ITarkovTrackerApiClient _apiClient;
    private readonly IIntegrationSecretStore _secretStore;
    private readonly IQuestProgressImportPlanner _planner;
    private readonly TarkovTrackerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<IntegrationSecretReference, SessionState> _sessions = [];

    public TarkovTrackerIntegrationService(
        ITarkovTrackerApiClient apiClient,
        IIntegrationSecretStore secretStore,
        IQuestProgressImportPlanner planner,
        TarkovTrackerOptions options,
        TimeProvider? timeProvider = null)
    {
        _apiClient = apiClient;
        _secretStore = secretStore;
        _planner = planner;
        _options = options;
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TarkovTrackerIntegrationStatus> GetStatusAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        var reference = Reference(scope);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = GetSession(reference);
            var connected = _secretStore.IsAvailable &&
                await _secretStore.ExistsAsync(reference, cancellationToken).ConfigureAwait(false);
            return Status(scope, session, connected);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TarkovTrackerIntegrationStatus> ConnectAsync(
        QuestProfileScope scope,
        string token,
        CancellationToken cancellationToken)
    {
        var reference = Reference(scope);
        EnsureConnectAvailable();
        TarkovTrackerTokenPolicy.ValidateForMode(token, scope.GameMode);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var validation = await _apiClient.ValidateTokenAsync(token, scope.GameMode, cancellationToken)
                .ConfigureAwait(false);
            await _secretStore.SaveAsync(reference, token, cancellationToken).ConfigureAwait(false);
            var session = new SessionState
            {
                LastCheckedUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
                Quota = validation.Quota,
            };
            ApplyQuotaBackoff(session);
            _sessions[reference] = session;
            return Status(scope, session, connected: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TarkovTrackerIntegrationStatus> DisconnectAsync(
        QuestProfileScope scope,
        CancellationToken cancellationToken)
    {
        var reference = Reference(scope);
        if (!_secretStore.IsAvailable)
        {
            throw new InvalidOperationException(
                "Protected storage is unavailable, so no TarkovTracker credential can be managed here.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _secretStore.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
            _sessions.Remove(reference);
            return Status(scope, new SessionState(), connected: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TarkovTrackerRefreshResult> RefreshPreviewAsync(
        QuestProfileScope scope,
        TarkovTrackerRefreshKind kind,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var reference = Reference(scope);
        EnsureConnectAvailable();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = GetSession(reference);
            var token = await _secretStore.LoadAsync(reference, cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                throw new InvalidOperationException("TarkovTracker is not connected for this exact profile scope.");
            }

            if (session.RequiresReconnect)
            {
                throw new InvalidOperationException(
                    "TarkovTracker rejected the saved credential; reconnect before refreshing.");
            }

            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            if (session.NextEligibleRefreshUtc is { } nextEligible && nextEligible > now)
            {
                throw new InvalidOperationException(
                    $"TarkovTracker refresh is paused until {LocalTime.Moment(nextEligible)} by quota or failure backoff.");
            }

            if (kind == TarkovTrackerRefreshKind.Foreground &&
                session.LastCheckedUtc is { } lastChecked &&
                now - lastChecked < _options.MinimumForegroundRefreshInterval)
            {
                var nextForeground = lastChecked + _options.MinimumForegroundRefreshInterval;
                throw new InvalidOperationException(
                    $"Foreground TarkovTracker refresh is limited to once per minute; retry after {LocalTime.Moment(nextForeground)}.");
            }

            TarkovTrackerProgressFetch fetched;
            try
            {
                fetched = await _apiClient.GetProgressAsync(
                    token,
                    scope.GameMode,
                    session.ETag,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TarkovTrackerApiException exception)
            {
                session.LastCheckedUtc = now;
                if (exception.Failure is TarkovTrackerApiFailure.Unauthorized or TarkovTrackerApiFailure.Forbidden)
                {
                    session.RequiresReconnect = true;
                }
                else if (exception.Failure == TarkovTrackerApiFailure.RateLimited)
                {
                    session.NextEligibleRefreshUtc = exception.RetryAfterUtc ?? now + TimeSpan.FromMinutes(1);
                }
                else if (exception.Failure is TarkovTrackerApiFailure.ServerError or
                         TarkovTrackerApiFailure.Timeout or
                         TarkovTrackerApiFailure.Transport)
                {
                    session.NextEligibleRefreshUtc = now + _options.TransientFailureBackoff;
                }

                throw;
            }

            session.LastCheckedUtc = now;
            session.Quota = fetched.Quota;
            session.ETag = fetched.ETag;
            if (!fetched.NotModified)
            {
                session.Snapshot = fetched.Snapshot
                    ?? throw new InvalidOperationException("TarkovTracker returned no progress snapshot.");
            }
            else if (session.Snapshot is null)
            {
                throw new InvalidOperationException(
                    "TarkovTracker returned 304 without a prior local snapshot; reconnect and refresh again.");
            }

            ApplyQuotaBackoff(session);
            var snapshot = session.Snapshot;
            var preview = await _planner.PreviewAsync(
                scope,
                Normalize(snapshot),
                cancellationToken).ConfigureAwait(false);
            return new(preview, fetched.NotModified, Status(scope, session, connected: true));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static QuestProgressImportSnapshot Normalize(TarkovTrackerProgressSnapshot snapshot)
    {
        var tasks = snapshot.Tasks.Select(value =>
        {
            var unresolved = value.Invalid
                ? "TarkovTracker marked this task record invalid; it was retained but not applied."
                : value.Complete && value.Failed
                    ? "TarkovTracker returned contradictory complete and failed task flags; the record was retained but not applied."
                    : null;
            RecordedTaskState? state = unresolved is not null
                ? null
                : value.Failed
                    ? RecordedTaskState.Failed
                    : value.Complete
                        ? RecordedTaskState.Completed
                        : RecordedTaskState.NotStarted;
            return new QuestProgressImportTask(value.Id, state, unresolved);
        }).ToArray();
        var objectives = snapshot.Objectives.Select(value => new QuestProgressImportObjective(
            value.Id,
            value.Invalid
                ? null
                : value.Complete
                    ? RecordedObjectiveState.Completed
                    : RecordedObjectiveState.InProgress,
            value.Count,
            value.Invalid
                ? "TarkovTracker marked this objective record invalid; it was retained but not applied."
                : null)).ToArray();
        return new(
            QuestProgressImportSource.TarkovTracker,
            snapshot.GameMode,
            null,
            snapshot.PayloadSha256,
            ContractVersion,
            snapshot.FetchedUtc,
            "TarkovTracker GET /progress snapshot from canonical api.tarkovtracker.org; fetched time is not source edit time",
            tasks,
            objectives,
            [],
            []);
    }

    private TarkovTrackerIntegrationStatus Status(
        QuestProfileScope scope,
        SessionState session,
        bool connected) => new(
            _options.Enabled,
            _options.NetworkAccessEnabled,
            _secretStore.IsAvailable,
            connected,
            session.RequiresReconnect,
            scope.GameMode,
            session.LastCheckedUtc,
            session.Snapshot?.FetchedUtc,
            session.NextEligibleRefreshUtc,
            session.Quota);

    private void ApplyQuotaBackoff(SessionState session)
    {
        if (session.Quota.Remaining == 0 && session.Quota.ResetUtc is { } resetUtc)
        {
            session.NextEligibleRefreshUtc = resetUtc;
        }
        else
        {
            session.NextEligibleRefreshUtc = null;
        }
    }

    private SessionState GetSession(IntegrationSecretReference reference)
    {
        if (!_sessions.TryGetValue(reference, out var session))
        {
            session = new SessionState();
            _sessions.Add(reference, session);
        }

        return session;
    }

    private void EnsureConnectAvailable()
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("The optional TarkovTracker integration feature is disabled.");
        }

        if (!_options.NetworkAccessEnabled)
        {
            throw new InvalidOperationException(
                "The optional TarkovTracker integration is unavailable while network access is disabled.");
        }

        if (!_secretStore.IsAvailable)
        {
            throw new InvalidOperationException(
                "The optional TarkovTracker integration is disabled because protected secret storage is unavailable.");
        }
    }

    private static IntegrationSecretReference Reference(QuestProfileScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ProfileId == Guid.Empty || !Enum.IsDefined(scope.GameMode) ||
            string.IsNullOrWhiteSpace(scope.Generation) || scope.Generation.Length > 128 ||
            !scope.Generation.Equals(scope.Generation.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The TarkovTracker profile scope is invalid.", nameof(scope));
        }

        return new(
            IntegrationSecretKind.TarkovTrackerProgressToken,
            scope.ProfileId,
            scope.GameMode,
            scope.Generation);
    }

    private sealed class SessionState
    {
        internal bool RequiresReconnect { get; set; }

        internal DateTimeOffset? LastCheckedUtc { get; set; }

        internal DateTimeOffset? NextEligibleRefreshUtc { get; set; }

        internal string? ETag { get; set; }

        internal TarkovTrackerProgressSnapshot? Snapshot { get; set; }

        internal TarkovTrackerQuota Quota { get; set; } = new(null, null, null);
    }
}
