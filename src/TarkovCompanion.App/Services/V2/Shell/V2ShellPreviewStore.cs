using System.Text.Json;
using System.Text;
using TarkovCompanion.Application.Services.Shell;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>Where a preview window was left, in device-independent pixels.</summary>
public sealed record V2ShellWindowPlacement(double Width, double Height, double? Left, double? Top, bool IsMaximized)
{
    public const double MinimumWidth = 360;
    public const double MinimumHeight = 480;
    public const double MaximumDimension = 16_384;
    public const double MaximumCoordinateMagnitude = 1_000_000;

    public bool IsUsable =>
        double.IsFinite(Width) && double.IsFinite(Height) &&
        Width is >= MinimumWidth and <= MaximumDimension &&
        Height is >= MinimumHeight and <= MaximumDimension &&
        (Left is null) == (Top is null) &&
        IsCoordinate(Left) && IsCoordinate(Top);

    /// <summary>Moves a stranded preview back to a screen with a grabbable title bar.</summary>
    /// <remarks>
    /// Preview state is deliberately separate from V1's layout file, but an unplugged monitor
    /// has the same failure mode in both shells: a perfectly valid saved window nobody can reach.
    /// Keep deliberate edge overlap, and move only a window with no usable overlap at all.
    ///
    /// [#881] <paramref name="frameHeight"/> is what the window adds above and below its client
    /// area (title bar and borders), and <paramref name="scaling"/> is device pixels per
    /// device-independent pixel. <see cref="Width"/> and <see cref="Height"/> are the client's
    /// size in device-independent pixels, while the screens and <see cref="Left"/>/<see cref="Top"/>
    /// are device pixels; comparing the two directly let a window whose client alone matched the
    /// work area keep its title bar's height of client under the taskbar.
    /// </remarks>
    public V2ShellWindowPlacement ClampTo(IReadOnlyList<ScreenBounds> screens, double frameHeight = 0, double scaling = 1)
    {
        ArgumentNullException.ThrowIfNull(screens);
        frameHeight = double.IsFinite(frameHeight) && frameHeight > 0 ? frameHeight : 0;
        scaling = double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
        if (screens.Count == 0 || Left is not { } left || Top is not { } top)
        {
            return this;
        }

        const double grabbable = 80;
        foreach (var screen in screens)
        {
            if (left + Width - grabbable > screen.Left &&
                left + grabbable < screen.Right &&
                top + Height > screen.Top &&
                top + grabbable < screen.Bottom)
            {
                return FitVertically(screen, frameHeight, scaling);
            }
        }

        var home = screens[0];
        var availableWidth = Math.Max(0, home.Right - home.Left);
        var availableHeight = Math.Max(0, home.Bottom - home.Top);
        var clampedWidth = Math.Max(MinimumWidth, Math.Min(Width, availableWidth));
        var clampedHeight = Math.Max(MinimumHeight, Math.Min(Height, availableHeight / scaling - frameHeight));
        return this with
        {
            Left = home.Left + Math.Max(0, Math.Min(40, (availableWidth - clampedWidth) / 2)),
            Top = home.Top + Math.Max(0, Math.Min(40, (availableHeight - (clampedHeight + frameHeight) * scaling) / 2)),
            Width = clampedWidth,
            Height = clampedHeight,
        };
    }

    /// <summary>
    /// Keeps the whole height of a reachable window inside the screen's work area.
    /// </summary>
    /// <remarks>
    /// Hanging off the side of a screen is something people do on purpose and is left alone.
    /// Hanging off the bottom is not: the work area ends where the taskbar begins, and a window
    /// saved 1080 tall on a 1080p screen (work area about 1032) keeps its last 48 pixels under it.
    /// That is where the navigation rail keeps Setup, reported as "the settings gear is clipped".
    /// The window is made no taller than the work area and moved up until it ends inside it.
    ///
    /// [#881] "The whole height" includes the title bar. This fit once compared the client's
    /// height alone with the work area, so a window it had "fitted" (client 1032 tall at the top of
    /// a 1032 work area) still ended a title bar lower, and the gear stayed half under the taskbar
    /// every launch. The client now gets the work area less the frame, in the same units.
    /// </remarks>
    private V2ShellWindowPlacement FitVertically(ScreenBounds screen, double frameHeight, double scaling)
    {
        if (Top is not { } top)
        {
            return this;
        }

        var available = Math.Max(0, screen.Bottom - screen.Top);
        var height = Math.Max(MinimumHeight, Math.Min(Height, available / scaling - frameHeight));
        var outer = (height + frameHeight) * scaling;
        var fittedTop = Math.Max(screen.Top, Math.Min(top, Math.Floor(screen.Bottom - outer)));
        return height == Height && fittedTop == top ? this : this with { Height = height, Top = fittedTop };
    }

    private static bool IsCoordinate(double? coordinate) =>
        coordinate is null ||
        double.IsFinite(coordinate.Value) && Math.Abs(coordinate.Value) <= MaximumCoordinateMagnitude;
}

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
    public const int MaxEntityLength = V2AddressCodec.MaxItemLength;
    public const int MaxFocusTargetLength = V2ShellIdentifier.MaxLength;

    public int Schema { get; init; } = CurrentSchema;

    public string Variant { get; init; } = string.Empty;

    public string? Address { get; init; }

    public string? SelectedEntity { get; init; }

    public string? FocusTarget { get; init; }

    public IReadOnlyList<string> Recents { get; init; } = [];

    public IReadOnlyList<string> Pins { get; init; } = [];

    public V2ShellWindowPlacement? Window { get; init; }

    public bool CaptureShortcutEnabled { get; init; } = true;

    /// <summary>[V2 rough package 46] How much of the navigation rail was showing: labels, icons or hidden.</summary>
    public string? NavigationRail { get; init; }

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
        MaxDepth = 16,
    };

    private readonly V2ShellMode _mode;
    private readonly V2AddressCodec _addresses;
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
        _addresses = new(V2ShellVariants.For(mode), V2RouteRegistry.Default);
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
        _gate.Wait();
        try
        {
            return LoadCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    private V2PreviewLoad LoadCore()
    {
        V2ShellPreviewState? state;
        try
        {
            if (!File.Exists(FilePath))
            {
                return new(V2ShellPreviewState.For(_mode), V2PreviewLoadOutcome.FirstLaunch, null);
            }

            state = ReadBounded();
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
        var normalized = NormalizeForSave(state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException($"Preview state is larger than the {MaximumBytes}-byte storage limit.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicJsonFile.WriteAsync(
                FilePath,
                Encoding.UTF8.GetString(bytes),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets this variant's navigation state, and only this variant's.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(FilePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsTrustworthy(V2ShellPreviewState state) =>
        state.Schema == V2ShellPreviewState.CurrentSchema &&
        string.Equals(state.Variant, _mode.ToToken(), StringComparison.Ordinal) &&
        IsCanonicalAddress(state.Address, optional: true) &&
        IsBoundedIdentifier(state.SelectedEntity, V2ShellPreviewState.MaxEntityLength) &&
        IsBoundedFocusTarget(state.FocusTarget) &&
        state.Recents is { Count: <= V2ShellPreviewState.MaxRecents } &&
        state.Pins is { Count: <= V2ShellPreviewState.MaxPins } &&
        state.Recents.Concat(state.Pins).All(address => IsCanonicalAddress(address, optional: false)) &&
        state.Recents.Distinct(StringComparer.Ordinal).Count() == state.Recents.Count &&
        state.Pins.Distinct(StringComparer.Ordinal).Count() == state.Pins.Count &&
        (state.Window is null || state.Window.IsUsable);

    private V2ShellPreviewState? ReadBounded()
    {
        using var stream = new FileStream(
            FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumBytes)
        {
            return null;
        }

        var bytes = new byte[MaximumBytes + 1];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = stream.Read(bytes, read, bytes.Length - read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return read > MaximumBytes
            ? null
            : JsonSerializer.Deserialize<V2ShellPreviewState>(bytes.AsSpan(0, read), JsonOptions);
    }

    private V2ShellPreviewState NormalizeForSave(V2ShellPreviewState state)
    {
        if (!IsCanonicalAddress(state.Address, optional: true))
        {
            throw new ArgumentException("The preview address is not canonical for this variant.", nameof(state));
        }

        if (!IsBoundedIdentifier(state.SelectedEntity, V2ShellPreviewState.MaxEntityLength) ||
            !IsBoundedFocusTarget(state.FocusTarget))
        {
            throw new ArgumentException("The preview selection or focus target is not a bounded shell identifier.", nameof(state));
        }

        if (state.Window is { IsUsable: false })
        {
            throw new ArgumentException("The preview window placement is outside its finite bounds.", nameof(state));
        }

        return state with
        {
            Schema = V2ShellPreviewState.CurrentSchema,
            Variant = _mode.ToToken(),
            Recents = CanonicalEntries(state.Recents, V2ShellPreviewState.MaxRecents, nameof(state.Recents)),
            Pins = CanonicalEntries(state.Pins, V2ShellPreviewState.MaxPins, nameof(state.Pins)),
        };
    }

    private IReadOnlyList<string> CanonicalEntries(IReadOnlyList<string> entries, int limit, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalized = entries
            .Take(limit)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Any(address => !IsCanonicalAddress(address, optional: false)))
        {
            throw new ArgumentException("A remembered address is not canonical for this variant.", parameterName);
        }

        return normalized;
    }

    private bool IsCanonicalAddress(string? address, bool optional)
    {
        if (address is null)
        {
            return optional;
        }

        if (address.Length is 0 or > MaximumAddressLength)
        {
            return false;
        }

        var parsed = _addresses.Parse(address);
        return parsed.Location is { } location &&
            string.Equals(_addresses.Format(location), address, StringComparison.Ordinal);
    }

    private static bool IsBoundedIdentifier(string? value, int maximumLength) =>
        value is null ||
        value.Length <= maximumLength && V2AddressCodec.IsValidItem(value);

    private static bool IsBoundedFocusTarget(string? value) =>
        value is null ||
        value.Length is > 0 and <= V2ShellPreviewState.MaxFocusTargetLength &&
        value is not "." and not ".." &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.');
}
