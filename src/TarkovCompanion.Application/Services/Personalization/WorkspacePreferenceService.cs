using TarkovCompanion.Core.Domain.Personalization;

namespace TarkovCompanion.Application.Services.Personalization;

/// <summary>Where the appearance record is kept between launches.</summary>
public interface IWorkspacePreferenceStore
{
    Task<WorkspacePreferences> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(WorkspacePreferences preferences, CancellationToken cancellationToken);
}

/// <summary>
/// The one place that holds the current appearance and tells everybody when it changes.
/// </summary>
/// <remarks>
/// The view model that offers the choice and the adapter that paints it are in different layers
/// and neither owns the other, so a change has to travel through something. An event rather than
/// a callback list because there are two listeners already — the applier and the Setup page — and
/// a third (the tablet's own appearance) is expected.
///
/// <para>
/// Writes are fire-and-forget from the caller's point of view but serialized here: a player
/// stepping the text scale four times quickly must not have the fourth press lose to the second
/// write finishing late. <see cref="Current"/> moves immediately so the window redraws at once;
/// the file catches up.
/// </para>
/// </remarks>
public sealed class WorkspacePreferenceService(IWorkspacePreferenceStore store)
{
    private readonly IWorkspacePreferenceStore _store = store
        ?? throw new ArgumentNullException(nameof(store));
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Raised after <see cref="Current"/> has already changed.</summary>
    public event EventHandler<WorkspacePreferences>? Changed;

    /// <summary>What the app is drawn with right now.</summary>
    public WorkspacePreferences Current { get; private set; } = WorkspacePreferences.Default;

    /// <summary>Whether the stored record has been read yet.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Reads the stored record and announces it, once per process.</summary>
    public async Task<WorkspacePreferences> LoadAsync(CancellationToken cancellationToken)
    {
        var stored = (await _store.GetAsync(cancellationToken).ConfigureAwait(false)).Normalized();
        IsLoaded = true;
        Current = stored;
        Changed?.Invoke(this, stored);
        return stored;
    }

    /// <summary>Applies a change now and persists it in the background.</summary>
    public Task UpdateAsync(WorkspacePreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var wanted = preferences.Normalized();
        if (IsLoaded && wanted == Current)
        {
            return Task.CompletedTask;
        }

        IsLoaded = true;
        Current = wanted;
        Changed?.Invoke(this, wanted);
        return PersistAsync(wanted, cancellationToken);
    }

    /// <summary>Applies one field's change, leaving the rest of the record alone.</summary>
    public Task UpdateAsync(
        Func<WorkspacePreferences, WorkspacePreferences> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return UpdateAsync(change(Current), cancellationToken);
    }

    private async Task PersistAsync(WorkspacePreferences preferences, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(preferences, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
