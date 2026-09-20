using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>Which of a raid's values the player can correct by hand.</summary>
public enum RaidCorrectionField
{
    Side,
    Clock,
    Extracts,
}

/// <summary>
/// What the player has corrected by hand about the raid that is running, laid over what the
/// companion read for itself.
/// </summary>
/// <remarks>
/// <para>
/// Everything the companion knows about a raid is read: the side from a log line, the start from
/// a notification, the clock and the offered exits from a screenshot of the extract list. Each of
/// those can be missing (the companion was started mid-raid, the player never opened the list) or
/// wrong (a misread line), and until now the only remedy was to take another screenshot and hope.
/// </para>
/// <para>
/// A correction is not evidence, so it is not written into <see cref="RaidStateService"/>, which
/// would then have to remember what it had read underneath in order to give it back. It is an
/// overlay: <see cref="Apply"/> takes the automatic snapshot and returns the one the pages show,
/// the automatic one is never changed, and "return to automatic" is forgetting the overlay.
/// </para>
/// <para>
/// A correction belongs to one raid, exactly like the trail and the scanned extract list. It is
/// dropped when the automatic snapshot's raid id changes, and nothing is laid over a snapshot
/// that is not in a raid: a scav's side carried into the next PMC raid would count that raid down
/// from the wrong length, confidently.
/// </para>
/// </remarks>
public sealed class RaidManualCorrections
{
    /// <summary>What <see cref="RaidSnapshot.SideBasis"/> and an exit's source say for a value set here.</summary>
    public const string ManualBasis = "Set by you";

    private readonly object _gate = new();
    private Guid? _raidId;
    private string? _side;
    private (TimeSpan Clock, DateTimeOffset ReadUtc)? _clock;
    private DateTimeOffset? _startedUtc;
    private IReadOnlyList<string>? _extracts;

    /// <summary>A correction was made, undone, or dropped because the raid it belonged to is over.</summary>
    public event EventHandler? Changed;

    public bool IsManual(RaidCorrectionField field)
    {
        lock (_gate)
        {
            return field switch
            {
                RaidCorrectionField.Side => _side is not null,
                RaidCorrectionField.Clock => _clock is not null || _startedUtc is not null,
                RaidCorrectionField.Extracts => _extracts is not null,
                _ => false,
            };
        }
    }

    /// <summary>The exits the player said are offered, or null while the list is automatic.</summary>
    public IReadOnlyList<string>? ManualExtracts
    {
        get
        {
            lock (_gate)
            {
                return _extracts;
            }
        }
    }

    /// <summary>"PMC" or "Scav", the two strings <see cref="RaidTimer.LengthFor"/> understands.</summary>
    public void SetSide(string side)
    {
        var normalized = side?.Trim().ToLowerInvariant() switch
        {
            "pmc" => "PMC",
            "scav" => "Scav",
            _ => throw new ArgumentException("A raid is run as a PMC or as a scav.", nameof(side)),
        };
        lock (_gate)
        {
            _side = normalized;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The time the game shows as left, read by the player at <paramref name="readUtc"/>.
    /// </summary>
    /// <remarks>
    /// The same shape as a clock read off a screenshot, because it is the same claim: the game's
    /// own number at a known moment. It replaces a start set by hand, which is the weaker claim.
    /// </remarks>
    public void SetTimeLeft(TimeSpan left, DateTimeOffset readUtc)
    {
        if (left < TimeSpan.Zero || left > RaidResume.LongestRaid + RaidResume.LongestRaid)
        {
            throw new ArgumentOutOfRangeException(nameof(left), "No raid has that long left.");
        }

        lock (_gate)
        {
            _clock = (left, readUtc);
            _startedUtc = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reads what a player types for the time left: "23:10", "1:02:30", or "23" for minutes.
    /// </summary>
    public static bool TryParseTimeLeft(string? text, out TimeSpan left)
    {
        left = default;
        var parts = (text ?? string.Empty).Trim().Split(':');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        var numbers = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[index]))
            {
                return false;
            }
        }

        // Every part after the first is a base-sixty digit; the first is as large as the player says.
        if (numbers.Skip(1).Any(number => number > 59))
        {
            return false;
        }

        left = numbers.Length switch
        {
            1 => TimeSpan.FromMinutes(numbers[0]),
            2 => new TimeSpan(0, numbers[0], numbers[1]),
            _ => new TimeSpan(numbers[0], numbers[1], numbers[2]),
        };
        return left <= RaidResume.LongestRaid + RaidResume.LongestRaid;
    }

    /// <summary>When the raid began, for a raid whose beginning the companion did not see.</summary>
    public void SetStarted(DateTimeOffset startedUtc)
    {
        lock (_gate)
        {
            _startedUtc = startedUtc;
            _clock = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The exits this raid offers, by the names the map lists them under.</summary>
    public void SetExtracts(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var list = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (_gate)
        {
            _extracts = list;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ReturnToAutomatic(RaidCorrectionField field)
    {
        lock (_gate)
        {
            switch (field)
            {
                case RaidCorrectionField.Side:
                    _side = null;
                    break;
                case RaidCorrectionField.Clock:
                    _clock = null;
                    _startedUtc = null;
                    break;
                case RaidCorrectionField.Extracts:
                    _extracts = null;
                    break;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The snapshot the pages show: <paramref name="automatic"/> with this raid's corrections over it.
    /// </summary>
    /// <remarks>
    /// Returns <paramref name="automatic"/> itself when nothing is corrected, so a consumer that
    /// compares snapshots by reference sees no change where there is none.
    /// </remarks>
    public RaidSnapshot Apply(RaidSnapshot automatic)
    {
        ArgumentNullException.ThrowIfNull(automatic);
        bool dropped;
        RaidSnapshot result;
        lock (_gate)
        {
            dropped = automatic.RaidId != _raidId && HasAny;
            if (automatic.RaidId != _raidId)
            {
                _raidId = automatic.RaidId;
                _side = null;
                _clock = null;
                _startedUtc = null;
                _extracts = null;
            }

            result = automatic.State == RaidLifecycleState.InRaid && HasAny ? Overlay(automatic) : automatic;
        }

        if (dropped)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    private bool HasAny => _side is not null || _clock is not null || _startedUtc is not null || _extracts is not null;

    private RaidSnapshot Overlay(RaidSnapshot automatic)
    {
        var result = automatic;
        if (_side is { } side)
        {
            result = result with { Side = side, SideBasis = ManualBasis };
        }

        if (_clock is { } clock)
        {
            result = result with { RaidClock = clock.Clock, RaidClockReadUtc = clock.ReadUtc };
        }
        else if (_startedUtc is { } started)
        {
            // A reading always wins over a count (RaidTimer.Resolve), so a start set by hand has
            // to take the automatic reading away or it would change nothing on screen.
            result = result with { StartedUtc = started, RaidClock = null, RaidClockReadUtc = null };
        }

        if (_extracts is { } extracts)
        {
            result = result with
            {
                ActiveExtracts = extracts
                    .Select(name => new ActiveExtract($"manual:{name}", name, Confidence.Certain, ManualBasis))
                    .ToArray(),
                // Lines the scan could not match are about the scan the player has just overruled.
                ExtractLinesNotMatched = [],
            };
        }

        return result;
    }
}
