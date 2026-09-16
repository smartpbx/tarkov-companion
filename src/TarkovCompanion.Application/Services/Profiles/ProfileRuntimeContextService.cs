using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Profiles;
using CatalogGameMode = TarkovCompanion.Core.Common.GameMode;

namespace TarkovCompanion.Application.Services.Profiles;

public enum ProfileRuntimeContextState
{
    Uninitialized,
    NoActiveProfile,
    UnknownGameMode,
    Ready,
}

/// <summary>
/// The exact profile-owned scope a catalog consumer uses. The public API uses <c>regular</c> for
/// PvP and primary language subtags for translated endpoints, but neither value is a fallback:
/// both are derived from the selected persisted profile context.
/// </summary>
public sealed record ProfileCatalogScope
{
    private ProfileCatalogScope(ProfileContext context, CatalogGameMode gameMode, string language)
    {
        Context = context;
        GameMode = gameMode;
        Language = language;
    }

    public ProfileContext Context { get; }

    public CatalogGameMode GameMode { get; }

    public string Language { get; }

    public SyncRequest ToSyncRequest(bool force = false) => new(GameMode, Language, force);

    internal static ProfileCatalogScope? From(ProfileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var gameMode = context.Mode switch
        {
            ProfileGameMode.Pvp => CatalogGameMode.Regular,
            ProfileGameMode.Pve => CatalogGameMode.Pve,
            ProfileGameMode.Seasonal => CatalogGameMode.PvpSeason,
            ProfileGameMode.Unknown => (CatalogGameMode?)null,
            _ => throw new ArgumentOutOfRangeException(nameof(context), "Profile game mode is not defined."),
        };
        if (gameMode is null)
        {
            return null;
        }

        // json.tarkov.dev translation documents are keyed by language, while the profile keeps
        // the complete display locale. Selecting the primary subtag preserves that distinction:
        // pt-BR requests Portuguese data and the UI can still format in the full Brazilian locale.
        var localeLanguage = context.Locale.Language;
        var separator = localeLanguage.IndexOf('-');
        var language = (separator < 0 ? localeLanguage : localeLanguage[..separator]).ToLowerInvariant();
        return new(context, gameMode.Value, language);
    }
}

public sealed record ProfileRuntimeContextSnapshot
{
    internal ProfileRuntimeContextSnapshot(
        bool isInitialized,
        ProfileWorkspaceSnapshot workspace,
        ProfileRuntimeContextState state,
        ProfileCatalogScope? catalogScope,
        string code,
        string detail)
    {
        IsInitialized = isInitialized;
        Workspace = workspace;
        State = state;
        CatalogScope = catalogScope;
        Code = code;
        Detail = detail;
    }

    public long Revision => Workspace.Revision;

    public bool IsInitialized { get; }

    public ProfileWorkspaceSnapshot Workspace { get; }

    public long WorkspaceRevision => Workspace.Revision;

    public ProfileRecord? ActiveProfile => Workspace.ActiveProfileId is null ? null : Workspace.ActiveProfile;

    public ProfileRuntimeContextState State { get; }

    public ProfileCatalogScope? CatalogScope { get; }

    public string Code { get; }

    public string Detail { get; }
}

public sealed record ProfileRuntimeContextChanged(ProfileRuntimeContextSnapshot Snapshot);

public interface IProfileRuntimeContextService
{
    ProfileRuntimeContextSnapshot Current { get; }

    event Action<ProfileRuntimeContextChanged>? ContextChanged;

    Task<ProfileRuntimeContextSnapshot> InitializeAsync(CancellationToken cancellationToken);

    Task<ProfileRuntimeContextSnapshot> RefreshAsync(CancellationToken cancellationToken);

    Task<ProfileRuntimeContextSnapshot> SwitchAsync(Guid profileId, CancellationToken cancellationToken);

    Task<ProfileRuntimeContextSnapshot> UpdateActiveProgressAsync(
        ProfileRuntimeContextSnapshot expected,
        ProfileProgress progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Turns the durable workspace into the one revisioned context consumed by profile-aware runtime
/// services. Publication happens only after the workspace compare-and-swap commits. Mode and
/// language are projected from that same immutable snapshot, so a rapid switch cannot pair one
/// profile's progress with another profile's catalog request.
/// </summary>
public sealed class ProfileRuntimeContextService : IProfileRuntimeContextService, IDisposable
{
    private readonly ProfileContextService _profiles;
    private readonly ILogger<ProfileRuntimeContextService> _logger;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<ProfileRuntimeContextChanged> _pendingChanges = new();
    private ProfileRuntimeContextSnapshot _current = Uninitialized();
    private int _publishingChanges;
    private bool _disposed;

    public ProfileRuntimeContextService(
        ProfileContextService profiles,
        ILogger<ProfileRuntimeContextService>? logger = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _logger = logger ?? NullLogger<ProfileRuntimeContextService>.Instance;
        _profiles.ContextChanged += OnWorkspaceChanged;
    }

    public event Action<ProfileRuntimeContextChanged>? ContextChanged;

    public ProfileRuntimeContextSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public async Task<ProfileRuntimeContextSnapshot> InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current.IsInitialized)
            {
                return Current;
            }

            var workspace = await _profiles.GetAsync(cancellationToken).ConfigureAwait(false);
            return Accept(workspace, initialize: true);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<ProfileRuntimeContextSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var workspace = await _profiles.GetAsync(cancellationToken).ConfigureAwait(false);
        return Accept(workspace, initialize: true);
    }

    public async Task<ProfileRuntimeContextSnapshot> SwitchAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (profileId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(profileId), "A profile id is required.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var workspace = await _profiles.SwitchAsync(profileId, cancellationToken).ConfigureAwait(false);
        return ForCommittedWorkspace(workspace);
    }

    public async Task<ProfileRuntimeContextSnapshot> UpdateActiveProgressAsync(
        ProfileRuntimeContextSnapshot expected,
        ProfileProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(progress);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!expected.IsInitialized)
        {
            throw new InvalidOperationException("Load profile context before saving progress.");
        }

        var active = expected.ActiveProfile
            ?? throw new InvalidOperationException("Select a profile before saving progress.");
        var workspace = await _profiles.UpdateActiveProgressAsync(
                new(expected.WorkspaceRevision, active.Context.Identity, progress),
                cancellationToken)
            .ConfigureAwait(false);
        return ForCommittedWorkspace(workspace);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _profiles.ContextChanged -= OnWorkspaceChanged;
        _initializationGate.Dispose();
    }

    private void OnWorkspaceChanged(ProfileContextChanged change) =>
        Accept(change.Snapshot, initialize: true);

    private ProfileRuntimeContextSnapshot Accept(ProfileWorkspaceSnapshot workspace, bool initialize)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ProfileRuntimeContextChanged change;
        ProfileRuntimeContextSnapshot result;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current.IsInitialized && workspace.Revision < _current.WorkspaceRevision)
            {
                return _current;
            }

            if (_current.IsInitialized == initialize &&
                workspace.Revision == _current.WorkspaceRevision)
            {
                return _current;
            }

            result = Build(initialize, workspace);
            _current = result;
            change = new(result);
            // Accepted under the same lock that advances the published workspace revision. A reentrant
            // subscriber can commit another profile switch, but its publication queues behind
            // the one currently being delivered instead of overtaking the remaining subscribers.
            _pendingChanges.Enqueue(change);
        }

        PublishPendingChanges();
        return result;
    }

    private ProfileRuntimeContextSnapshot ForCommittedWorkspace(ProfileWorkspaceSnapshot workspace)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A subscriber may immediately commit another switch while this switch's ordered
            // notification is being delivered. Return the exact workspace this call committed,
            // even when Current has already advanced beyond it; never republish or regress it.
            return workspace.Revision == _current.WorkspaceRevision
                ? _current
                : Build(true, workspace);
        }
    }

    private static ProfileRuntimeContextSnapshot Build(
        bool isInitialized,
        ProfileWorkspaceSnapshot workspace)
    {
        if (!isInitialized)
        {
            return new(
                false,
                workspace,
                ProfileRuntimeContextState.Uninitialized,
                null,
                "profile-context-uninitialized",
                "Profile context has not been loaded yet.");
        }

        if (workspace.ActiveProfileId is null)
        {
            return new(
                true,
                workspace,
                ProfileRuntimeContextState.NoActiveProfile,
                null,
                "profile-context-not-selected",
                "Create or select a profile before using profile-scoped features.");
        }

        var active = workspace.ActiveProfile;
        var catalog = ProfileCatalogScope.From(active.Context);
        if (catalog is null)
        {
            return new(
                true,
                workspace,
                ProfileRuntimeContextState.UnknownGameMode,
                null,
                "profile-context-mode-unknown",
                "Choose PvP, PvE, or Seasonal mode for the active profile before loading game data.");
        }

        return new(
            true,
            workspace,
            ProfileRuntimeContextState.Ready,
            catalog,
            "profile-context-ready",
            "The active profile context is ready.");
    }

    private void PublishPendingChanges()
    {
        while (Interlocked.CompareExchange(ref _publishingChanges, 1, 0) == 0)
        {
            try
            {
                while (_pendingChanges.TryDequeue(out var change))
                {
                    Deliver(change);
                }
            }
            finally
            {
                Volatile.Write(ref _publishingChanges, 0);
            }

            if (_pendingChanges.IsEmpty)
            {
                return;
            }
        }
    }

    private void Deliver(ProfileRuntimeContextChanged change)
    {
        if (ContextChanged is not { } handlers)
        {
            return;
        }

        foreach (Action<ProfileRuntimeContextChanged> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(change);
            }
            catch (Exception exception)
            {
                // The durable workspace already committed. One presentation subscriber must not
                // make a successful profile switch look failed to the caller or starve its peers.
                _logger.LogWarning(
                    exception,
                    "A runtime profile-context subscriber failed after publication {Revision}.",
                    change.Snapshot.Revision);
            }
        }
    }

    private static ProfileRuntimeContextSnapshot Uninitialized() => new(
        false,
        new ProfileWorkspaceSnapshot(0, null, []),
        ProfileRuntimeContextState.Uninitialized,
        null,
        "profile-context-uninitialized",
        "Profile context has not been loaded yet.");
}
