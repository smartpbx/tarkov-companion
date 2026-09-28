using System.Text.Json;
using TarkovCompanion.Application.Services.Shell;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>Durable, bounded calibrations for explicitly requested external window captures.</summary>
public sealed class JsonFileCaptureTargetCalibrationStore(string settingsPath) : ICaptureTargetCalibrationStore
{
    private const int MaximumSettingsBytes = 64 * 1024;
    private const int MaximumProfiles = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CaptureTargetCalibrationState> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CaptureTargetCalibrationProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!CaptureTargetCalibration.IsUsable(profile))
        {
            throw new ArgumentException("Capture calibration is outside the bounded target contract.", nameof(profile));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var profiles = current.Profiles
                .Where(candidate => candidate.Key != profile.Key)
                .Append(profile)
                .OrderByDescending(candidate => candidate.UpdatedUtc)
                .Take(MaximumProfiles)
                .ToArray();
            await WriteAsync(profiles, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(CaptureTargetCalibrationKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync(current.Profiles.Where(profile => profile.Key != key).ToArray(), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CaptureTargetCalibrationState> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return CaptureTargetCalibrationState.Empty;
            }

            var json = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<CalibrationDocument>(json, JsonOptions);
            return new((document?.Profiles ?? [])
                .Where(CaptureTargetCalibration.IsUsable)
                .DistinctBy(profile => profile.Key)
                .Take(MaximumProfiles)
                .ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return CaptureTargetCalibrationState.Empty;
        }
    }

    private Task WriteAsync(IReadOnlyList<CaptureTargetCalibrationProfile> profiles, CancellationToken cancellationToken) =>
        AtomicJsonFile.WriteAsync(
            settingsPath,
            JsonSerializer.Serialize(new CalibrationDocument(profiles), JsonOptions),
            cancellationToken);

    private sealed record CalibrationDocument(IReadOnlyList<CaptureTargetCalibrationProfile>? Profiles);
}
