using System.Text.Json;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Personal;

/// <summary>
/// Reads what <see cref="PersonalPace"/> and <see cref="PersonalExits"/> need from the player's own
/// recorded raids, for a page that is not Debrief (#712 2-4: the Raid page's Now panel).
/// </summary>
/// <remarks>
/// Exits count from finished, not archived raids, as in Debrief's pattern line: one still running
/// has no exit yet, and an archived raid was set aside on purpose. Pace is the player's and not the
/// map's, so every map's newest trails are read for it.
/// </remarks>
public sealed class PersonalHistory(IRaidHistoryService history)
{
    /// <summary>Raids per map read for trails; the newest say how the player moves now (as Debrief).</summary>
    public const int TrailRaids = 40;

    private readonly IRaidHistoryService _history = history ?? throw new ArgumentNullException(nameof(history));

    /// <summary>The measured pace, or null while too few moving legs are recorded.</summary>
    public async Task<WalkPace?> PaceAsync(CancellationToken cancellationToken)
    {
        // Every map played, as Debrief's pace does: a trail's legs are moving time whatever the raid's ending.
        var raids = await _history.ListAsync(cancellationToken).ConfigureAwait(false);
        var trails = new List<IReadOnlyList<ScreenshotPosition>>();
        foreach (var mapId in raids.Select(raid => raid.MapId).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mapTrails = await _history.ListTrailsForMapAsync(mapId, TrailRaids, cancellationToken).ConfigureAwait(false);
            trails.AddRange(mapTrails.Select(trail => trail.Positions));
        }

        return PersonalPace.Measure(trails);
    }

    /// <summary>Times each exit was used on this map (and side, when known), by exit name.</summary>
    public async Task<IReadOnlyDictionary<string, int>> ExitUsesAsync(string mapId, string? side, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        var raids = new List<PersonalRaid>();
        foreach (var raid in await FinishedAsync(mapId, cancellationToken).ConfigureAwait(false))
        {
            var used = RaidExtractUsed.Latest(await _history
                .ListEventPayloadsAsync(raid.Id, RaidExtractUsed.EventType, cancellationToken).ConfigureAwait(false));
            if (used is null)
            {
                continue;
            }

            var raidSide = side is null
                ? null
                : Side(await _history.ListEventPayloadsAsync(raid.Id, "state", cancellationToken).ConfigureAwait(false));
            raids.Add(new PersonalRaid(raid.Id, raid.MapId, raidSide, raid.StartedUtc, raid.Outcome, used));
        }

        return PersonalExits.Uses(raids, mapId, side);
    }

    /// <summary>The newest "Side" a raid's state events carry (Debrief reads it the same way).</summary>
    internal static string? Side(IEnumerable<string> statePayloads)
    {
        string? side = null;
        foreach (var payload in statePayloads)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("Side", out var element)
                    && element.ValueKind == JsonValueKind.String
                    && element.GetString() is { Length: > 0 } value)
                {
                    side = value;
                }
            }
            catch (JsonException)
            {
                // One bad row must not hide the side every other row carries.
            }
        }

        return side;
    }

    private async Task<IReadOnlyList<Core.Domain.Raids.RaidHistoryEntry>> FinishedAsync(string mapId, CancellationToken cancellationToken)
    {
        var finished = new List<Core.Domain.Raids.RaidHistoryEntry>();
        foreach (var raid in await _history.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (raid.EndedUtc is null || !string.Equals(raid.MapId, mapId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!RaidArchive.IsArchived(await _history
                    .ListEventPayloadsAsync(raid.Id, RaidArchive.EventType, cancellationToken).ConfigureAwait(false)))
            {
                finished.Add(raid);
            }
        }

        return finished;
    }
}
