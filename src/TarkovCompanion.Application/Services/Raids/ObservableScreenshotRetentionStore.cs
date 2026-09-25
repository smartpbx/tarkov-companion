namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// The screenshot-tidying store, with an event for whoever holds a copy of its value.
/// </summary>
/// <remarks>
/// [#902] Setup's Reset everything and Import wrote this store directly, and the window kept the
/// copy it read at startup: the next press of "Keep screenshots for" stepped from the old span and
/// wrote it back, undoing the reset. Every writer goes through here, so the copy hears about it.
/// </remarks>
public sealed class ObservableScreenshotRetentionStore(IScreenshotRetentionStore inner) : IScreenshotRetentionStore
{
    private readonly IScreenshotRetentionStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>Raised after a save, on the thread that saved.</summary>
    public event EventHandler<ScreenshotRetentionSettings>? Changed;

    public Task<ScreenshotRetentionSettings> GetAsync(CancellationToken cancellationToken) =>
        _inner.GetAsync(cancellationToken);

    public async Task SaveAsync(ScreenshotRetentionSettings settings, CancellationToken cancellationToken)
    {
        await _inner.SaveAsync(settings, cancellationToken).ConfigureAwait(true);
        Changed?.Invoke(this, settings);
    }
}
