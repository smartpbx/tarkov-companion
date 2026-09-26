using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.Application.Services.Readiness;

/// <summary>One thing the companion needs before it can answer anything in a raid.</summary>
public enum ReadinessItemKind
{
    GameLogs,
    Screenshots,
    GameData,
    Squad,

    /// <summary>The game's logs or screenshot names changed shape (#945). Listed only while it is so.</summary>
    GameFormat,
}

public enum ReadinessItemState
{
    Ready,

    /// <summary>Still being looked for or downloaded: not yet a problem.</summary>
    Checking,

    /// <summary>Looked for and not found, or a download that did not arrive.</summary>
    Missing,

    /// <summary>Game data cannot come because Local only is on, and none is stored yet.</summary>
    LocalOnly,
    Failed,

    /// <summary>Not needed to play: squad sharing.</summary>
    Optional,
}

/// <summary>The one-tap fix the strip offers for an item.</summary>
public enum ReadinessFix
{
    None,
    ChooseLogFolder,
    ChooseScreenshotFolder,
    RetryData,
    OpenDataNetwork,
    OpenSquad,

    /// <summary>Setup › Get ready, which keeps the detail.</summary>
    OpenDetails,

    /// <summary>Setup › Updates &amp; Diagnostics, where the format-health row (#945) says what changed.</summary>
    OpenFormatHealth,
}

public sealed record ReadinessItem(ReadinessItemKind Kind, ReadinessItemState State, ReadinessFix Fix, bool Required)
{
    /// <summary>Wants the player: the strip is shown while any required item is like this.</summary>
    public bool Blocks => Required && State is ReadinessItemState.Missing or ReadinessItemState.Failed or ReadinessItemState.LocalOnly;
}

/// <summary>What the strip is decided from, gathered from the runtime snapshot and the network policy.</summary>
public sealed record ReadinessStripInput(
    bool IsDemoMode,
    EftObservationState Observation,
    bool DatabaseReady,
    RuntimeDataState Data,
    bool LocalOnly,
    bool SquadSharing,
    FormatHealthReport? Format)
{
    public static ReadinessStripInput From(ApplicationRuntimeSnapshot snapshot, bool localOnly, FormatHealthReport? format)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(
            snapshot.IsDemoMode,
            snapshot.Observation,
            snapshot.DatabaseReady,
            snapshot.Data,
            localOnly || snapshot.IsOffline,
            snapshot.Group.IsSharing,
            format);
    }
}

public sealed record ReadinessStripState(IReadOnlyList<ReadinessItem> Items, bool IsVisible, FormatHealthReport? Format)
{
    public static ReadinessStripState Hidden { get; } = new([], false, null);

    public ReadinessItem? For(ReadinessItemKind kind) => Items.FirstOrDefault(item => item.Kind == kind);
}

/// <summary>
/// [#712 1-13] The slim "Game logs ✓ · Screenshots ✓ · Game data ✓ · Squad (optional)" line at the
/// top of the Raid page, decided.
/// </summary>
/// <remarks>
/// <para>
/// It is there only while something needs the player, and gone once everything required is
/// green; Setup › Get ready keeps the full detail either way. That is what keeps it off the screen
/// of every existing player: their folders are found (or named) and their catalog is stored, so
/// nothing blocks and nothing is drawn.
/// </para>
/// <para>
/// "Still looking" and "still downloading" do not block. On every launch the folders are unknown
/// for the moment before discovery answers, and a strip that appeared for that moment would
/// flash at every existing player each time they started the app.
/// </para>
/// <para>
/// Demo mode replays a fixture, and a machine that is not Windows cannot watch the game at all;
/// neither has anything the player could fix, so neither shows the strip.
/// </para>
/// </remarks>
public static class ReadinessStripRules
{
    public static ReadinessStripState Evaluate(ReadinessStripInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.IsDemoMode || !input.Observation.IsSupported || input.Data.Availability == DataAvailability.DemoFixture)
        {
            return ReadinessStripState.Hidden;
        }

        var observation = input.Observation;
        var items = new List<ReadinessItem>(5)
        {
            new(
                ReadinessItemKind.GameLogs,
                observation.IsWatchingLogs ? ReadinessItemState.Ready
                    : observation.Searched ? ReadinessItemState.Missing : ReadinessItemState.Checking,
                observation.IsWatchingLogs ? ReadinessFix.None : ReadinessFix.ChooseLogFolder,
                Required: true),
            new(
                ReadinessItemKind.Screenshots,
                observation.IsWatchingScreenshots ? ReadinessItemState.Ready
                    : observation.Searched ? ReadinessItemState.Missing : ReadinessItemState.Checking,
                observation.IsWatchingScreenshots ? ReadinessFix.None : ReadinessFix.ChooseScreenshotFolder,
                Required: true),
            GameData(input),
            new(
                ReadinessItemKind.Squad,
                input.SquadSharing ? ReadinessItemState.Ready : ReadinessItemState.Optional,
                input.SquadSharing ? ReadinessFix.None : ReadinessFix.OpenSquad,
                Required: false),
        };

        if (input.Format is { IsDegraded: true })
        {
            items.Add(new(ReadinessItemKind.GameFormat, ReadinessItemState.Failed, ReadinessFix.OpenFormatHealth, Required: true));
        }

        return new(items, items.Any(item => item.Blocks), input.Format);
    }

    private static ReadinessItem GameData(ReadinessStripInput input)
    {
        var data = input.Data;

        // Anything stored is usable, however old: an old catalog still answers, and Get ready
        // says how old it is. Only an empty one blocks.
        if (data.ItemCount > 0)
        {
            return new(ReadinessItemKind.GameData, ReadinessItemState.Ready, ReadinessFix.None, Required: true);
        }

        // Local only is the player's own choice, and the first-launch download respects it: the
        // strip says why there is no data and where the switch is, rather than offering a retry
        // that is not allowed to go anywhere.
        if (input.LocalOnly)
        {
            return new(ReadinessItemKind.GameData, ReadinessItemState.LocalOnly, ReadinessFix.OpenDataNetwork, Required: true);
        }

        var state = data.Availability switch
        {
            DataAvailability.Error => ReadinessItemState.Failed,
            DataAvailability.Refreshing => ReadinessItemState.Checking,
            _ when !input.DatabaseReady => ReadinessItemState.Checking,
            DataAvailability.Unavailable => ReadinessItemState.Missing,
            _ => ReadinessItemState.Checking,
        };
        return new(
            ReadinessItemKind.GameData,
            state,
            state == ReadinessItemState.Checking ? ReadinessFix.None : ReadinessFix.RetryData,
            Required: true);
    }
}

/// <summary>
/// [#712 1-13] Where a folder the player picked should point, for the folder they meant.
/// </summary>
/// <remarks>
/// Asked for the game's logs, most people pick the game's own folder, not the Logs folder inside
/// it; asked for screenshots, some pick "Escape from Tarkov" under Documents rather than its
/// Screenshots folder. Either way the folder they meant is one level down and exists, so that is
/// the one saved.
/// </remarks>
public static class ChosenGameFolder
{
    public static string Resolve(ReadinessItemKind kind, string picked, Func<string, bool> directoryExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(picked);
        ArgumentNullException.ThrowIfNull(directoryExists);
        var trimmed = picked.Trim().TrimEnd('\\', '/');
        var child = kind switch
        {
            ReadinessItemKind.GameLogs => "Logs",
            ReadinessItemKind.Screenshots => "Screenshots",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only the logs and screenshots folders are chosen."),
        };

        if (string.Equals(Path.GetFileName(trimmed), child, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var inside = Path.Combine(trimmed, child);
        return directoryExists(inside) ? inside : trimmed;
    }
}
