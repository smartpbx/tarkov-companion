using Microsoft.Extensions.Logging;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>
/// What the desktop's side of the relay link did, in the desktop's own log, at most once a minute
/// per kind of event.
/// </summary>
/// <remarks>
/// [#693] A paired tablet sat on "Reconnecting to the desktop" for twenty minutes and the desktop's
/// logs, back to the first build that had a relay, held not one line about the relay, its owner
/// session, or the tablet: "never saw the ticket", "saw it and ignored it" and "was not on the
/// relay at all" all looked the same. These lines say which.
///
/// Nothing here writes a key, a credential, a pairing code, a ticket id (a ticket id is enough to
/// read the code the desktop answers it with) or a device's name. Outcomes and HTTP statuses only.
/// A poll runs every two seconds, so each kind of line is written once per
/// <see cref="Window"/> and the next one says how many were held back.
/// </remarks>
public sealed class RelayLinkLog
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset WrittenUtc, int Held)> _last = new(StringComparer.Ordinal);

    public RelayLinkLog(ILogger logger, TimeProvider? clock = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Writes <paramref name="message"/> unless a line of the same kind was written within <see cref="Window"/>.</summary>
    public void Write(string kind, string message, LogLevel level = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var now = _clock.GetUtcNow();
        int held;
        lock (_gate)
        {
            if (_last.TryGetValue(kind, out var last) && now - last.WrittenUtc < Window)
            {
                _last[kind] = (last.WrittenUtc, last.Held + 1);
                return;
            }

            held = _last.TryGetValue(kind, out last) ? last.Held : 0;
            _last[kind] = (now, 0);
        }

        var line = held > 0 ? $"Relay link: {message} ({held} more like it in the last minute.)" : "Relay link: " + message;
        _logger.Log(level, "{RelayLinkEvent}", line);
    }
}
