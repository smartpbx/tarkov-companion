using System.Text.Json;
using TarkovCompanion.Application.Services.Shell;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>
/// Stores where the window was, in <c>Config/shell.json</c>.
/// </summary>
/// <remarks>
/// Fails to the default rather than to nothing, like the retention store beside it and unlike
/// the group one. The directions differ because the risks do: a lost group setting must not
/// start sharing, while a lost window position must not stop the window opening. The worst
/// outcome here is a companion that opens where it always used to.
/// </remarks>
public sealed class JsonFileShellLayoutStore(string settingsPath) : IShellLayoutStore
{
    private const int MaximumSettingsBytes = 4 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ShellLayout> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return ShellLayout.Default;
            }

            var layout = new ShellLayout(
                document.Width,
                document.Height,
                document.Left,
                document.Top,
                document.Maximized,
                document.RailCollapsed);

            // A size the shell cannot use is discarded, but the rail's own state is kept: those
            // are two different preferences and one being unusable says nothing about the other.
            return layout.IsUsable
                ? layout
                : ShellLayout.Default with { IsRailCollapsed = document.RailCollapsed };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ShellLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = new LayoutDocument(
                layout.Width,
                layout.Height,
                layout.Left,
                layout.Top,
                layout.IsMaximized,
                layout.IsRailCollapsed);
            await AtomicJsonFile.WriteAsync(
                settingsPath,
                JsonSerializer.Serialize(document, JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A window position that could not be written is not worth failing a shutdown over.
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LayoutDocument?> ReadOrDefaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LayoutDocument>(text, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException)
        {
            return null;
        }
    }

    private sealed record LayoutDocument(
        double Width,
        double Height,
        double? Left,
        double? Top,
        bool Maximized,
        bool RailCollapsed);
}
