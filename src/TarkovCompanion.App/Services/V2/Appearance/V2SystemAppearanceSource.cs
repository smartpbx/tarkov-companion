using Avalonia.Platform;

namespace TarkovCompanion.App.Services.V2.Appearance;

/// <summary>The operating-system appearance values and their runtime change signal.</summary>
/// <remarks>
/// Avalonia's platform event supplies a snapshot, but the application deliberately reads through
/// <see cref="Read"/> again when it fires. On Windows the notification can represent dark mode,
/// contrast mode, or both, and resolving from the current platform state avoids applying an old
/// snapshot after two settings changed close together.
/// </remarks>
internal interface IV2SystemAppearanceSource : IDisposable
{
    event EventHandler? Changed;

    PlatformColorValues? Read();
}

/// <summary>Adapts Avalonia platform settings without exposing them to the appearance policy.</summary>
internal sealed class AvaloniaV2SystemAppearanceSource : IV2SystemAppearanceSource
{
    private readonly IPlatformSettings? _settings;

    public AvaloniaV2SystemAppearanceSource(IPlatformSettings? settings)
    {
        _settings = settings;
        if (_settings is not null)
        {
            _settings.ColorValuesChanged += OnColorValuesChanged;
        }
    }

    public event EventHandler? Changed;

    public PlatformColorValues? Read() => _settings?.GetColorValues();

    public void Dispose()
    {
        if (_settings is not null)
        {
            _settings.ColorValuesChanged -= OnColorValuesChanged;
        }
    }

    private void OnColorValuesChanged(object? sender, PlatformColorValues values) =>
        Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>A read-only source for headless render hosts that have no platform settings.</summary>
internal sealed class DelegateV2SystemAppearanceSource(Func<PlatformColorValues?> read) : IV2SystemAppearanceSource
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public PlatformColorValues? Read() => read();

    public void Dispose()
    {
    }
}
