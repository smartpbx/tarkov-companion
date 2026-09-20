using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Personalization;
using TarkovCompanion.Core.Domain.Personalization;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>
/// Stores the appearance record in <c>Config/preferences.json</c>.
/// </summary>
/// <remarks>
/// Fails to <see cref="WorkspacePreferences.Default"/> rather than to nothing, like the shell
/// layout store beside it: the worst outcome of a lost appearance file is a companion that looks
/// the way it always did, and that is recoverable from inside the application. A file whose
/// <c>schemaVersion</c> is newer than this build is also read as the default, because guessing at
/// a shape written by a later version is how a half-applied theme happens.
///
/// Enums are written as names, not numbers. This file is one somebody can open, and a diff that
/// says <c>"theme": "HighContrast"</c> is worth more than one that says <c>3</c> — and renumbering
/// an enum later must not silently repaint somebody's window.
/// </remarks>
public sealed class JsonFileWorkspacePreferenceStore(string settingsPath) : IWorkspacePreferenceStore
{
    private const int MaximumSettingsBytes = 8 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<WorkspacePreferences> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (document is null || document.SchemaVersion > WorkspacePreferences.SchemaVersion)
            {
                return WorkspacePreferences.Default;
            }

            return new WorkspacePreferences(
                document.Theme ?? WorkspacePreferences.Default.Theme,
                document.ColorVision ?? WorkspacePreferences.Default.ColorVision,
                document.TextScalePercent ?? WorkspacePreferences.Default.TextScalePercent,
                document.Density ?? WorkspacePreferences.Default.Density,
                document.ReduceMotion ?? WorkspacePreferences.Default.ReduceMotion,
                document.FocusAlwaysVisible ?? WorkspacePreferences.Default.FocusAlwaysVisible)
                .Normalized();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(WorkspacePreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalized = preferences.Normalized();
            var document = new PreferenceDocument(
                WorkspacePreferences.SchemaVersion,
                normalized.Theme,
                normalized.ColorVision,
                normalized.TextScalePercent,
                normalized.Density,
                normalized.ReduceMotion,
                normalized.FocusAlwaysVisible);
            await AtomicJsonFile.WriteAsync(
                settingsPath,
                JsonSerializer.Serialize(document, JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An appearance that could not be written is not worth failing a click over; the
            // window is already drawn the way the player asked.
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PreferenceDocument?> ReadOrDefaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists || info.Length > MaximumSettingsBytes)
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<PreferenceDocument>(text, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException)
        {
            return null;
        }
    }

    /// <remarks>
    /// Every field nullable so a file written by an earlier shape, or by hand with one key in it,
    /// reads as "that choice was never made" rather than as the zero of its type — which for the
    /// text scale would be a window drawn at nothing.
    /// </remarks>
    private sealed record PreferenceDocument(
        int SchemaVersion,
        AppearanceTheme? Theme,
        ColorVisionMode? ColorVision,
        int? TextScalePercent,
        InterfaceDensity? Density,
        bool? ReduceMotion,
        bool? FocusAlwaysVisible);
}
