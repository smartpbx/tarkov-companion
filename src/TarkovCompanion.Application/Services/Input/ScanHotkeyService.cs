using Microsoft.Extensions.Logging;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Input;

namespace TarkovCompanion.Application.Services.Input;

public sealed record HotkeyRegistrationState(
    HotkeyBinding Binding,
    bool IsRegistered,
    bool IsSupported,
    string Detail);

/// <summary>
/// Owns the one global shortcut the companion registers, and keeps it in step with the
/// user's choice.
/// </summary>
/// <remarks>
/// The point of the shortcut is that the player never leaves the game to use the companion,
/// so registration failure has to be reported rather than swallowed: a shortcut another
/// application already owns simply does nothing, and the player would have no way to know why.
/// </remarks>
public sealed class ScanHotkeyService : IAsyncDisposable
{
    private readonly IGlobalHotkeyService _hotkeys;
    private readonly IHotkeySettingsStore _store;
    private readonly ILogger<ScanHotkeyService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _registered;
    private bool _disposed;

    public ScanHotkeyService(
        IGlobalHotkeyService hotkeys,
        IHotkeySettingsStore store,
        ILogger<ScanHotkeyService> logger)
    {
        _hotkeys = hotkeys;
        _store = store;
        _logger = logger;
        _hotkeys.Pressed += OnPressed;
        Current = new(HotkeyBinding.DefaultScan, false, OperatingSystem.IsWindows(), "The shortcut has not been applied yet.");
    }

    /// <summary>Raised on the hotkey service's own thread. Marshal before touching the UI.</summary>
    public event EventHandler? Triggered;

    public HotkeyRegistrationState Current { get; private set; }

    /// <summary>Registers the stored shortcut, or reports why it could not be registered.</summary>
    public async Task<HotkeyRegistrationState> InitializeAsync(CancellationToken cancellationToken)
    {
        var binding = await _store.GetScanBindingAsync(cancellationToken).ConfigureAwait(false);
        return await ApplyAsync(binding, persist: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Registers a shortcut and, when asked, remembers it for next time.</summary>
    public async Task<HotkeyRegistrationState> ApplyAsync(
        HotkeyBinding binding,
        bool persist,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!binding.IsValid(out var reason))
        {
            return Current = new(binding, false, OperatingSystem.IsWindows(), reason);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return Current = new(
                    binding,
                    false,
                    false,
                    "Global shortcuts are a Windows feature; the shortcut is stored but not active here.");
            }

            await UnregisterCoreAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _hotkeys.RegisterAsync(binding.ToGesture(), cancellationToken).ConfigureAwait(false);
                _registered = true;
                Current = new(binding, true, true, $"{binding.DisplayName} is active while the companion is running.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not register the {Binding} shortcut.", binding.DisplayName);
                Current = new(
                    binding,
                    false,
                    true,
                    $"{binding.DisplayName} could not be registered. Another application is probably already using it. Choose a different combination.");
            }

            if (persist)
            {
                await _store.SaveScanBindingAsync(binding, cancellationToken).ConfigureAwait(false);
            }

            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hotkeys.Pressed -= OnPressed;
        try
        {
            await UnregisterCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The global shortcut could not be released cleanly.");
        }

        _gate.Dispose();
    }

    private async Task UnregisterCoreAsync(CancellationToken cancellationToken)
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        await _hotkeys.UnregisterAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnPressed(object? sender, EventArgs arguments) => Triggered?.Invoke(this, EventArgs.Empty);
}
