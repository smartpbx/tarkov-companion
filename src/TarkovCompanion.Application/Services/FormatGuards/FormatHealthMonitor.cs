using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.Application.Services.FormatGuards;

/// <summary>How much has to be seen, and how much of it recognised, for each source.</summary>
/// <param name="Window">How many recent judged lines or names are counted.</param>
/// <param name="Minimum">How many have to be seen before any verdict at all.</param>
/// <param name="DegradedBelow">The recognised share under which the source is degraded.</param>
/// <param name="RecoveredAt">The share at which a degraded source is OK again (higher, so it does not flicker).</param>
public sealed record FormatWindow(int Window, int Minimum, double DegradedBelow, double RecoveredAt);

/// <summary>
/// [#712 0-3] Notices when the game starts writing its logs or screenshot names in a shape the
/// companion does not know, and says so, instead of raid detection quietly going wrong.
/// </summary>
/// <remarks>
/// <para>
/// Counts recognised against unrecognised shapes per source over a sliding window
/// (<see cref="EftLogLineShape"/> for the logs, <see cref="ScreenshotFilenameParser.Classify"/> for
/// names). Below <see cref="FormatWindow.DegradedBelow"/> the source is degraded; it is OK again
/// only at <see cref="FormatWindow.RecoveredAt"/>, so a mixed stretch does not flip it back and forth.
/// </para>
/// <para>
/// The game build comes from the log folder's name (and the header). A new build clears the
/// log windows, so the new build is judged on its own lines, and a degraded source then reads
/// "changed after update X" because it was last OK under another build. Degraded does not need a
/// new build, though: a hotfix that keeps the number and changes the lines is still caught.
/// </para>
/// <para>
/// Only shapes are looked at; nothing is kept but counts and the build number.
/// </para>
/// </remarks>
public sealed class FormatHealthMonitor
{
    public static readonly FormatWindow LogWindow = new(200, 40, 0.5, 0.8);
    public static readonly FormatWindow NotificationWindow = new(20, 5, 0.5, 0.8);
    public static readonly FormatWindow ScreenshotWindow = new(10, 3, 0.5, 0.8);

    private readonly TimeProvider _time;
    private readonly ILogger<FormatHealthMonitor>? _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<FormatSource, Tally> _tallies;
    private string? _gameVersion;
    private string? _lastPath;
    private string? _lastPathVersion;

    public FormatHealthMonitor(
        TimeProvider? time = null,
        ILogger<FormatHealthMonitor>? logger = null,
        IReadOnlyDictionary<FormatSource, FormatWindow>? windows = null)
    {
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _tallies = new()
        {
            [FormatSource.GameLog] = new(Pick(windows, FormatSource.GameLog, LogWindow)),
            [FormatSource.Notification] = new(Pick(windows, FormatSource.Notification, NotificationWindow)),
            [FormatSource.ScreenshotName] = new(Pick(windows, FormatSource.ScreenshotName, ScreenshotWindow)),
        };
    }

    /// <summary>Raised when a source's status or the game build changes, never on a count alone.</summary>
    public event EventHandler? Changed;

    public FormatHealthReport Current
    {
        get
        {
            lock (_gate)
            {
                return ReportLocked();
            }
        }
    }

    /// <summary>One line the log watcher read from <paramref name="path"/>, tailed or replayed.</summary>
    public void ObserveLogLine(string path, string? line)
    {
        ArgumentNullException.ThrowIfNull(path);
        var kind = EftLogLineShape.KindOf(path);
        if (kind == EftLogKind.Other)
        {
            return;
        }

        var header = EftLogLineShape.Header(line, kind);
        var notification = kind == EftLogKind.Backend ? EftLogLineShape.Notification(line) : LineShape.Neutral;
        if (header == LineShape.Neutral && notification == LineShape.Neutral)
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = NoteVersionLocked(VersionOf(path) ?? (header == LineShape.Recognised ? EftLogLineShape.HeaderVersion(line) : null));
            changed |= CountLocked(FormatSource.GameLog, header);
            changed |= CountLocked(FormatSource.Notification, notification);
        }

        if (changed)
        {
            Raise();
        }
    }

    /// <summary>One file the screenshot watcher found, by name.</summary>
    public void ObserveScreenshotName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var shape = ScreenshotFilenameParser.Classify(fileName) == ScreenshotNameKind.Unrecognized
            ? LineShape.Unrecognised
            : LineShape.Recognised;
        bool changed;
        lock (_gate)
        {
            changed = CountLocked(FormatSource.ScreenshotName, shape);
        }

        if (changed)
        {
            Raise();
        }
    }

    private string? VersionOf(string path)
    {
        if (string.Equals(path, _lastPath, StringComparison.Ordinal))
        {
            return _lastPathVersion;
        }

        _lastPath = path;
        _lastPathVersion = EftLogFolderGameVersionSource.ParseFolderName(Path.GetFileName(Path.GetDirectoryName(path)));
        return _lastPathVersion;
    }

    /// <summary>A new build starts the log sources over, keeping only the build each was last OK under.</summary>
    private bool NoteVersionLocked(string? version)
    {
        if (version is null || string.Equals(version, _gameVersion, StringComparison.Ordinal))
        {
            return false;
        }

        var previous = _gameVersion;
        _gameVersion = version;
        if (previous is not null)
        {
            // Screenshot names are kept: they arrive a few per raid, and "not read yet" for a whole
            // raid after every update would hide the one thing the row is for.
            _tallies[FormatSource.GameLog].Restart(_time.GetUtcNow());
            _tallies[FormatSource.Notification].Restart(_time.GetUtcNow());

            _logger?.LogInformation("Game build changed from {Previous} to {Version}; format health starts over.", previous, version);
        }

        return true;
    }

    private bool CountLocked(FormatSource source, LineShape shape)
    {
        if (shape == LineShape.Neutral)
        {
            return false;
        }

        var tally = _tallies[source];
        var before = tally.Status;
        tally.Add(shape == LineShape.Recognised, _time.GetUtcNow(), _gameVersion);
        if (tally.Status == before)
        {
            return false;
        }

        if (tally.Status == FormatHealthStatus.Degraded)
        {
            _logger?.LogWarning(
                "{Source} format not recognised: {Recognised} of the last {Seen} recognised (game {Version}, last OK under {LastOk}).",
                source,
                tally.Recognised,
                tally.Seen,
                _gameVersion ?? "unknown",
                tally.LastHealthyVersion ?? "none");
        }
        else
        {
            _logger?.LogInformation("{Source} format is {Status} (game {Version}).", source, tally.Status, _gameVersion ?? "unknown");
        }

        return true;
    }

    private FormatHealthReport ReportLocked() => new(
        [.. _tallies.Select(pair => new FormatSourceHealth(
            pair.Key,
            pair.Value.Status,
            pair.Value.Recognised,
            pair.Value.Seen - pair.Value.Recognised,
            _gameVersion,
            pair.Value.LastHealthyVersion,
            pair.Value.SinceUtc))],
        _gameVersion);

    private void Raise()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The watcher calling this is reading a file and must not stop for a subscriber.
            _logger?.LogWarning(exception, "A format-health subscriber failed.");
        }
    }

    private static FormatWindow Pick(IReadOnlyDictionary<FormatSource, FormatWindow>? windows, FormatSource source, FormatWindow fallback) =>
        windows is not null && windows.TryGetValue(source, out var window) ? window : fallback;

    /// <summary>A ring of the last N verdicts and the status they add up to.</summary>
    private sealed class Tally(FormatWindow window)
    {
        private readonly bool[] _ring = new bool[window.Window];
        private int _next;

        public int Seen { get; private set; }

        public int Recognised { get; private set; }

        public FormatHealthStatus Status { get; private set; }

        public string? LastHealthyVersion { get; private set; }

        public DateTimeOffset? SinceUtc { get; private set; }

        public void Add(bool recognised, DateTimeOffset nowUtc, string? version)
        {
            if (Seen == _ring.Length)
            {
                Recognised -= _ring[_next] ? 1 : 0;
            }
            else
            {
                Seen++;
            }

            _ring[_next] = recognised;
            Recognised += recognised ? 1 : 0;
            _next = (_next + 1) % _ring.Length;
            if (Seen < window.Minimum)
            {
                return;
            }

            var share = (double)Recognised / Seen;
            var status = share < window.DegradedBelow ? FormatHealthStatus.Degraded
                : share >= window.RecoveredAt ? FormatHealthStatus.Ok
                : Status == FormatHealthStatus.Unknown ? FormatHealthStatus.Ok
                : Status;
            if (status == FormatHealthStatus.Ok)
            {
                LastHealthyVersion = version ?? LastHealthyVersion;
            }

            if (status != Status)
            {
                Status = status;
                SinceUtc = nowUtc;
            }
        }

        public void Restart(DateTimeOffset nowUtc)
        {
            Array.Clear(_ring);
            _next = 0;
            Seen = 0;
            Recognised = 0;
            if (Status != FormatHealthStatus.Unknown)
            {
                Status = FormatHealthStatus.Unknown;
                SinceUtc = nowUtc;
            }
        }
    }
}
