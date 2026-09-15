using System.Text.Json;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>Where a preview window was left, in device-independent pixels.</summary>
public sealed record V2ShellWindowPlacement(double Width, double Height, double? Left, double? Top, bool IsMaximized);

/// <summary>
/// Everything a provisional shell remembers between launches, and nothing more.
/// </summary>
/// <remarks>
/// Navigation state only: the place, what was selected and focused there, recents, pins, the
/// window, and whether the Capture shortcut is switched on. Profile, raid, capture, marks, plans
/// and the V1 layout are owned elsewhere and never pass through here, so resetting this file — or
/// deleting the whole preview — cannot change any of them.
/// </remarks>
public sealed record V2ShellPreviewState
{
    public const int CurrentSchema = 1;
    public const int MaxRecents = 10;
    public const int MaxPins = 20;

    public int Schema { get; init; } = CurrentSchema;

    public string Variant { get; init; } = string.Empty;

    public string? Address { get; init; }

    public string? SelectedEntity { get; init; }

    public string? FocusTarget { get; init; }

    public IReadOnlyList<string> Recents { get; init; } = [];

    public IReadOnlyList<string> Pins { get; init; } = [];

    public V2ShellWindowPlacement? Window { get; init; }

    public bool CaptureShortcutEnabled { get; init; } = true;

    public static V2ShellPreviewState For(V2ShellMode mode) => new() { Variant = mode.ToToken() };
}

public enum V2PreviewLoadOutcome
{
    /// <summary>Nothing was remembered: this variant has never been opened here.</summary>
    FirstLaunch = 1,

    Restored,

    /// <summary>The file could not be trusted. It was set aside and the preview starts clean.</summary>
    ResetAfterCorruption,
}

public sealed record V2PreviewLoad(V2ShellPreviewState State, V2PreviewLoadOutcome Outcome, string? SetAsidePath);

/// <summary>
/// One JSON file per variant under <c>Config/v2-shell-preview/</c>.
/// </summary>
/// <remarks>
/// Namespaced by variant because the variants are being compared: a participant who used A and
/// then B must not find B opening on A's address, and a corrupt B file must not cost A anything.
/// The directory is the preview's own. <c>shell.json</c>, which V1 keeps its window and scale in,
/// is never read or written from here, so switching back to <c>--ui-shell legacy</c> finds the V1
/// window exactly where V1 left it.
///
/// A file that fails to parse is set aside rather than deleted, following the settings stores
/// beside it: "the preview reset" is easier to believe with the old file still there.
/// </remarks>
public sealed class V2ShellPreviewStore
{
    public const string DirectoryName = "v2-shell-preview";
    public const int MaximumBytes = 16 * 1024;
    private const int MaximumAddressLength = V2AddressCodec.MaxAddressLength;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly V2ShellMode _mode;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public V2ShellPreviewStore(string configDirectory, V2ShellMode mode, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        if (!mode.IsPreview())
        {
            throw new ArgumentException("Only a V2 preview has preview state; the legacy shell keeps its own.", nameof(mode));
        }

        _mode = mode;
        _timeProvider = timeProvider ?? TimeProvider.System;
        DirectoryPath = Path.Combine(Path.GetFullPath(configDirectory), DirectoryName);
        FilePath = Path.Combine(DirectoryPath, $"{mode.ToToken()}.json");
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    /// <summary>
    /// Reads what this variant remembered. Synchronous and small, because it decides the first page
    /// the window opens on, and a window that opens on the landing page and then jumps is worse.
    /// </summary>
    public V2PreviewLoad Load()
    {
        V2ShellPreviewState? state;
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists)
            {
                return new(V2ShellPreviewState.For(_mode), V2PreviewLoadOutcome.FirstLaunch, null);
            }

            state = info.Length > MaximumBytes
                ? null
                : JsonSerializer.Deserialize<V2ShellPreviewState>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as corrupt: nothing is moved, and the preview starts clean
            // for this launch only.
            return new(V2ShellPreviewState.For(_mode), V2PreviewLoadOutcome.FirstLaunch, null);
        }
        catch (JsonException)
        {
            state = null;
        }

        if (state is not null && IsTrustworthy(state))
        {
            return new(state, V2PreviewLoadOutcome.Restored, null);
        }

        var aside = AtomicJsonFile.SetAside(FilePath, _timeProvider.GetUtcNow());
        return new(V2ShellPreviewState.For(_mode), V2PreviewLoadOutcome.ResetAfterCorruption, aside);
    }

    public async Task SaveAsync(V2ShellPreviewState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = state with
        {
            Schema = V2ShellPreviewState.CurrentSchema,
            Variant = _mode.ToToken(),
            Recents = state.Recents.Take(V2ShellPreviewState.MaxRecents).ToArray(),
            Pins = state.Pins.Take(V2ShellPreviewState.MaxPins).ToArray(),
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.WriteAsync(
                FilePath,
                JsonSerializer.Serialize(normalized, JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing where a preview was left is not worth interrupting anybody for.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets this variant's navigation state, and only this variant's.</summary>
    public void Reset()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private bool IsTrustworthy(V2ShellPreviewState state) =>
        state.Schema == V2ShellPreviewState.CurrentSchema &&
        string.Equals(state.Variant, _mode.ToToken(), StringComparison.Ordinal) &&
        state.Address is null or { Length: <= MaximumAddressLength } &&
        state.Recents is { Count: <= V2ShellPreviewState.MaxRecents } &&
        state.Pins is { Count: <= V2ShellPreviewState.MaxPins } &&
        state.Recents.Concat(state.Pins).All(address => address is { Length: > 0 and <= MaximumAddressLength }) &&
        (state.Window is null || IsUsable(state.Window));

    private static bool IsUsable(V2ShellWindowPlacement window) =>
        double.IsFinite(window.Width) && double.IsFinite(window.Height) &&
        window.Width >= 320 && window.Height >= 320 &&
        (window.Left is null || double.IsFinite(window.Left.Value)) &&
        (window.Top is null || double.IsFinite(window.Top.Value));
}
