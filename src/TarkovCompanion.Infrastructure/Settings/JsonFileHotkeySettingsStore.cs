using System.Text.Json;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Input;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>
/// Stores the user's chosen shortcut as readable text in <c>Config/hotkeys.json</c>.
/// </summary>
/// <remarks>
/// The file is meant to be legible and hand-editable, so the binding is written the way it is
/// displayed ("Ctrl + Alt + S") rather than as key codes. An unreadable or unparseable file
/// falls back to the default rather than blocking startup.
/// </remarks>
public sealed class JsonFileHotkeySettingsStore(string settingsPath) : IHotkeySettingsStore
{
    private const int MaximumSettingsBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<HotkeyBinding> GetScanBindingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return HotkeyBinding.TryParse(document?.Scan, out var binding) && binding.IsValid(out _)
                ? binding
                : HotkeyBinding.DefaultScan;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveScanBindingAsync(HotkeyBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.IsValid(out var reason))
        {
            throw new ArgumentException(reason, nameof(binding));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(new(binding.DisplayName), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HotkeyDocument?> ReadOrDefaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            await using var stream = new FileStream(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return stream.Length > MaximumSettingsBytes
                ? null
                : await JsonSerializer
                    .DeserializeAsync<HotkeyDocument>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task WriteAsync(HotkeyDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(settingsPath))
            ?? throw new InvalidOperationException("The hotkey settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = settingsPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, settingsPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record HotkeyDocument(string? Scan);
}
