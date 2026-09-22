using System.Text.Json;
using TarkovCompanion.Application.Services.Shell;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>Stores the V2 window's normal rectangle for each monitor configuration.</summary>
public sealed class JsonFileDesktopWindowPlacementStore(string settingsPath) : IDesktopWindowPlacementStore
{
    private const int MaximumSettingsBytes = 32 * 1024;
    private const int MaximumMonitors = 16;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<DesktopWindowPlacementState> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return DesktopWindowPlacementState.Empty;
            }

            var json = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<PlacementDocument>(json, JsonOptions);
            var monitors = document?.Monitors?
                .Where(IsUsable)
                .DistinctBy(placement => placement.MonitorKey, StringComparer.Ordinal)
                .Take(MaximumMonitors)
                .ToArray() ?? [];
            return new(document?.ActiveMonitorKey, monitors);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return DesktopWindowPlacementState.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DesktopWindowPlacementState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = new PlacementDocument(
                state.ActiveMonitorKey,
                state.Monitors.Where(IsUsable).Take(MaximumMonitors).ToArray());
            await AtomicJsonFile.WriteAsync(
                settingsPath,
                JsonSerializer.Serialize(document, JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Placement is a convenience. A locked settings directory must not prevent shutdown.
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsUsable(MonitorWindowPlacement placement) =>
        !string.IsNullOrWhiteSpace(placement.MonitorKey) && placement.MonitorKey.Length <= 256 &&
        double.IsFinite(placement.WidthPixels) && placement.WidthPixels > 0 &&
        double.IsFinite(placement.HeightPixels) && placement.HeightPixels > 0 &&
        double.IsFinite(placement.LeftOffsetPixels) && double.IsFinite(placement.TopOffsetPixels);

    private sealed record PlacementDocument(
        string? ActiveMonitorKey,
        IReadOnlyList<MonitorWindowPlacement>? Monitors);
}
