using System.Runtime.CompilerServices;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Platform.Windows.Watching;

/// <summary>
/// Reports screenshots the game has written, by looking at the folder rather than by
/// subscribing to it.
/// </summary>
/// <remarks>
/// This started out on <see cref="FileSystemWatcher"/> and its notifications never arrived on
/// a real installation, exactly as they never arrived for the game's logs. Polling costs one
/// directory listing and cannot miss a file, so the same approach is used here.
///
/// How often it looks depends on whether anybody is waiting. A second when nobody is, and a
/// quarter of a second during a raid with the group sharing, when four other maps are waiting on
/// the next screenshot and the poll is the whole of what they wait. The folder itself has the
/// last word: the interval is never less than ten times the last listing took, so a folder large
/// enough to be expensive is looked at less often rather than costing a core.
///
/// Every file is reported twice. The first report is its name, on the poll that first sees the
/// entry, because the player's coordinates are in the name and are complete the moment it
/// exists. The second is the file itself, once its size has stopped changing and it opens with
/// a whole image envelope, which is what anything decoding pixels has to wait for. One signal
/// for both made the position wait on the picture: two probes a second apart before a marker
/// could move, for a number already on disk.
///
/// Screenshots already on disk when watching starts are not replayed, with one exception: a
/// file written in the couple of minutes before startup is still worth reporting, because the
/// alternative is losing the shot the player took while the companion was restarting. Anything
/// older is left alone, so that yesterday's screenshot cannot announce a raid that is over.
/// </remarks>
public sealed class WindowsScreenshotWatcher(
    bool developerMode = false,
    TimeSpan? pollInterval = null,
    TimeProvider? timeProvider = null,
    int requiredStableProbes = 2,
    long maximumEncodedBytes = 64L * 1024 * 1024,
    int maximumTrackedFiles = 16_384,
    // Optional so every existing caller — and every platform without a runtime state store —
    // gets exactly the one-second poll it always had.
    IScreenshotWatchPacer? pacer = null,
    TimeSpan? attentivePollInterval = null)
    : IScreenshotWatcher
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How often the folder is looked at while somebody is waiting for a screenshot.</summary>
    /// <remarks>
    /// A quarter of a second. The name is complete the moment the entry appears, so the poll is
    /// now the whole of the wait before a squadmate's marker moves: at one second it was most of
    /// the measured p95. Four times a second costs four directory listings a second and is only
    /// paid during a raid with the group on.
    /// </remarks>
    private static readonly TimeSpan DefaultAttentivePollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The most of its time this watcher will spend listing, as one part in this many.
    /// </summary>
    /// <remarks>
    /// A screenshot folder is normally a few dozen files and a listing is well under a
    /// millisecond, so nothing here applies. A folder somebody has pointed at by mistake, or
    /// years of screenshots nobody deleted, is a different thing: listing it takes a stat per
    /// file, and polling that four times a second would be a core spent on looking. So the
    /// interval is never less than ten times the last listing took — the listing cost is capped
    /// at a tenth of the watcher, whatever the folder turns out to hold.
    /// </remarks>
    private const int ListingDutyCycle = 10;

    /// <summary>Below this a listing is free and the duty cycle has nothing to say about it.</summary>
    private static readonly TimeSpan NegligibleListing = TimeSpan.FromMilliseconds(5);

    /// <summary>However slow the folder is, it is still looked at this often.</summary>
    /// <remarks>
    /// Thirty seconds. The backing off exists to stop a pathological folder costing a core, not
    /// to stop watching it: a player whose screenshots land somewhere enormous should see their
    /// marker late rather than never.
    /// </remarks>
    private static readonly TimeSpan SlowestPollInterval = TimeSpan.FromSeconds(30);
    private const FileAttributes CloudPlaceholderAttributes =
        FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

    /// <summary>How far back a file already on disk at startup is still considered new.</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    private readonly TimeSpan _pollInterval = pollInterval ?? DefaultPollInterval;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _attentivePollInterval = Shorter(
        attentivePollInterval ?? DefaultAttentivePollInterval,
        pollInterval ?? DefaultPollInterval);
    private readonly int _requiredStableProbes = requiredStableProbes is >= 2 and <= 16
        ? requiredStableProbes
        : throw new ArgumentOutOfRangeException(nameof(requiredStableProbes));
    private readonly long _maximumEncodedBytes = maximumEncodedBytes is >= 1 and <= 1024L * 1024 * 1024
        ? maximumEncodedBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumEncodedBytes));
    private readonly int _maximumTrackedFiles = maximumTrackedFiles is >= 16 and <= 100_000
        ? maximumTrackedFiles
        : throw new ArgumentOutOfRangeException(nameof(maximumTrackedFiles));
    private readonly object _watchStateGate = new();
    private WatchState? _watchState;
    private long _lastListingTicks;
    private long _pollIntervalTicks;

    /// <summary>What the last directory listing cost, for the duty cycle and for diagnostics.</summary>
    public TimeSpan LastListing => new(Interlocked.Read(ref _lastListingTicks));

    /// <summary>How long this watcher is currently waiting between listings.</summary>
    public TimeSpan PollInterval => new(Interlocked.Read(ref _pollIntervalTicks));

    /// <summary>Whichever of two intervals is the shorter, because attentive is never slower.</summary>
    private static TimeSpan Shorter(TimeSpan left, TimeSpan right) => left < right ? left : right;

    public async IAsyncEnumerable<ScreenshotSighting> WatchAsync(
        string screenshotRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenshotRoot);
        var watchState = StateFor(screenshotRoot);
        var seen = watchState.Seen;
        var settling = new Dictionary<string, SettlingCandidate>(StringComparer.OrdinalIgnoreCase);
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - StartupGrace;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!Directory.Exists(screenshotRoot))
            {
                // An absent configured root is availability state, not an empty but healthy
                // directory. The observation owner catches this typed signal, stops claiming
                // screenshot readiness, and performs bounded path rediscovery.
                throw new CaptureSourceUnavailableException();
            }

            var listingStarted = _timeProvider.GetTimestamp();
            var snapshot = Snapshot(screenshotRoot);
            Interlocked.Exchange(
                ref _lastListingTicks,
                _timeProvider.GetElapsedTime(listingStarted).Ticks);
            var now = _timeProvider.GetUtcNow();
            var present = snapshot.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var missing in settling.Keys.Where(path => !present.Contains(path)).ToArray())
            {
                settling.Remove(missing);
            }

            foreach (var path in seen.Keys.Where(present.Contains).ToArray())
            {
                seen[path] = seen[path] with { LastObservedUtc = now };
            }

            foreach (var candidate in snapshot)
            {
                var wasTracked = seen.TryGetValue(candidate.Path, out var delivered);
                if (wasTracked
                    && delivered!.Fingerprint == candidate.Fingerprint)
                {
                    continue;
                }

                var order = new FileOrderKey(candidate.WrittenUtc.Ticks, candidate.Path);
                // A later screenshot can finish first while an older file is still growing or
                // locked. The delivery watermark suppresses newly discovered historical files;
                // it must not turn a candidate already under observation into a delivered file.
                var wasSettling = settling.ContainsKey(candidate.Path);
                if (candidate.WrittenUtc < cutoff
                    || (!wasTracked
                        && !wasSettling
                        && watchState.DeliveryWatermark is { } watermark
                        && order.CompareTo(watermark) <= 0))
                {
                    seen[candidate.Path] = new(candidate.Fingerprint, now);
                    continue;
                }

                var tracked = settling.TryGetValue(candidate.Path, out var state);
                var announced = tracked && state!.NameAnnounced;
                if (!tracked || state!.Fingerprint != candidate.Fingerprint)
                {
                    // A file still growing changes fingerprint between probes and starts the
                    // stability count again. Whether its name has been reported does not start
                    // again with it: the coordinates were complete the first time.
                    state = new(candidate.Fingerprint, 1) { NameAnnounced = announced };
                    settling[candidate.Path] = state;
                    if (!announced)
                    {
                        settling[candidate.Path] = state with { NameAnnounced = true };
                        yield return new(candidate.Path, ScreenshotSightingKind.NameSeen);
                    }

                    continue;
                }

                if (!announced)
                {
                    // Reachable only where a probe was skipped, but said in one place rather
                    // than assumed: nothing settles before its name has been reported.
                    settling[candidate.Path] = state! with { NameAnnounced = true };
                    yield return new(candidate.Path, ScreenshotSightingKind.NameSeen);
                    state = settling[candidate.Path];
                }

                state = state! with
                {
                    StableProbes = Math.Min(_requiredStableProbes, state.StableProbes + 1),
                };
                settling[candidate.Path] = state;
                if (state.StableProbes < _requiredStableProbes
                    || !TryOpenCompleted(candidate, out var completed))
                {
                    continue;
                }

                seen[candidate.Path] = new(completed, now);
                settling.Remove(candidate.Path);
                if (watchState.DeliveryWatermark is null
                    || order.CompareTo(watchState.DeliveryWatermark.Value) > 0)
                {
                    watchState.DeliveryWatermark = order;
                }

                yield return new(candidate.Path, ScreenshotSightingKind.Settled);
            }

            PruneTracking(seen, settling, present);
            if (!await WaitAsync(NextInterval(), cancellationToken).ConfigureAwait(false))
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// How long to wait before looking again: as short as anybody needs, as long as the folder
    /// costs.
    /// </summary>
    /// <remarks>
    /// Asked once per wait rather than continuously. A raid beginning therefore speeds this up
    /// within one idle interval rather than instantly, which costs nothing anybody can see: the
    /// log announces the raid at the loading screen, and the second that takes passes before the
    /// player can photograph anything. Re-checking mid-wait would mean waking four times a
    /// second for the whole time the game is not running, to be ready a second earlier once.
    /// </remarks>
    private TimeSpan NextInterval()
    {
        var wanted = IntervalFor(
            pacer?.Current == ScreenshotWatchPace.Attentive ? _attentivePollInterval : _pollInterval,
            LastListing);
        Interlocked.Exchange(ref _pollIntervalTicks, wanted.Ticks);
        return wanted;
    }

    /// <summary>
    /// The interval that satisfies both the pace somebody asked for and what the folder costs.
    /// </summary>
    /// <remarks>
    /// Public and static because it is the whole of the backing-off policy and is worth checking
    /// on its own: making a real folder slow enough to exercise it takes a pathological directory
    /// and a machine-dependent amount of time, and proves less than the arithmetic does.
    /// </remarks>
    public static TimeSpan IntervalFor(TimeSpan wanted, TimeSpan lastListing)
    {
        if (lastListing <= NegligibleListing)
        {
            return wanted;
        }

        var floor = Shorter(lastListing * ListingDutyCycle, SlowestPollInterval);
        return floor > wanted ? floor : wanted;
    }

    private async Task<bool> WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private WatchState StateFor(string screenshotRoot)
    {
        lock (_watchStateGate)
        {
            if (_watchState is null
                || !string.Equals(_watchState.Root, screenshotRoot, StringComparison.OrdinalIgnoreCase))
            {
                _watchState = new(screenshotRoot);
            }

            return _watchState;
        }
    }

    /// <summary>
    /// Lists the newest bounded population of screenshots in the folder, oldest first.
    /// </summary>
    /// <remarks>
    /// Oldest first so that when several arrive between two polls the newest is reported last
    /// and therefore wins. A folder that has been deleted or become unreadable is reported to the
    /// application as unavailable so readiness cannot remain green while no intake exists.
    /// </remarks>
    private IReadOnlyList<FileCandidate> Snapshot(string screenshotRoot)
    {
        var newest = new PriorityQueue<FileCandidate, FileOrderKey>();
        try
        {
            // Directory.GetFiles and OrderBy used to allocate one path and one candidate for
            // every file before the configured tracking bound was applied. A mistaken folder
            // containing hundreds of thousands of images could therefore exhaust memory even
            // though the retained dictionaries were later pruned. Walk the directory lazily and
            // retain only the newest bounded population; older entries are already behind the
            // delivery watermark and startup grace.
            // DirectoryInfo rather than Directory: enumerating paths and then constructing a
            // FileInfo for each one costs a second stat per file, and on a folder of three
            // thousand screenshots that stat was the listing. Enumerating FileInfo hands back
            // the size and timestamps the directory read already produced, and it samples every
            // file at one moment rather than over the length of the walk.
            foreach (var info in new DirectoryInfo(screenshotRoot).EnumerateFiles())
            {
                if (!SupportedExtensions.Contains(info.Extension)
                    || (!developerMode && info.Name.Contains("EftSimulator", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var candidate = Describe(info);
                if (candidate is null)
                {
                    continue;
                }

                newest.Enqueue(
                    candidate,
                    new(candidate.WrittenUtc.Ticks, candidate.Path));
                if (newest.Count > _maximumTrackedFiles)
                {
                    _ = newest.Dequeue();
                }
            }
        }
        catch (IOException)
        {
            throw new CaptureSourceUnavailableException();
        }
        catch (UnauthorizedAccessException)
        {
            throw new CaptureSourceUnavailableException();
        }

        return newest.UnorderedItems
            .Select(entry => entry.Element)
            .OrderBy(entry => entry.WrittenUtc)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static FileCandidate? TryProbe(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return Describe(info);
    }

    /// <summary>
    /// One enumerated file, from the data the directory read already produced.
    /// </summary>
    /// <remarks>
    /// No Refresh: a FileInfo that came out of an enumeration is already populated, and asking
    /// again is the per-file stat this exists to avoid. The caller that needs a genuinely fresh
    /// read — the one confirming a file has not changed under it — goes through
    /// <see cref="TryProbe"/>, which does refresh.
    /// </remarks>
    private static FileCandidate? Describe(FileInfo info)
    {
        try
        {
            return new(
                info.FullName,
                info.LastWriteTimeUtc,
                new(info.Length, info.LastWriteTimeUtc.Ticks),
                info.Attributes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file deleted between the directory read and this is not an error; it is a file
            // that is no longer there.
            return null;
        }
    }

    private bool TryOpenCompleted(FileCandidate candidate, out FileFingerprint completed)
    {
        completed = default;
        var path = candidate.Path;
        var expected = candidate.Fingerprint;
        if (expected.Length is <= 0 || expected.Length > _maximumEncodedBytes
            || (candidate.Attributes & CloudPlaceholderAttributes) != 0)
        {
            return false;
        }

        try
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       bufferSize: 4096,
                       FileOptions.SequentialScan))
            {
                if (stream.Length != expected.Length || !HasCompleteImageEnvelope(stream, Path.GetExtension(path)))
                {
                    return false;
                }
            }

            var after = TryProbe(path);
            if (after is null || after.Fingerprint != expected
                || (after.Attributes & CloudPlaceholderAttributes) != 0)
            {
                return false;
            }

            completed = after.Fingerprint;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasCompleteImageEnvelope(Stream stream, string extension)
    {
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != header.Length)
        {
            return false;
        }

        if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (!header.SequenceEqual(signature) || stream.Length < 20)
            {
                return false;
            }

            ReadOnlySpan<byte> iend = [0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130];
            return ContainsNearEnd(stream, iend);
        }

        if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            if (header[0] != 0xff || header[1] != 0xd8 || stream.Length < 10)
            {
                return false;
            }

            ReadOnlySpan<byte> endOfImage = [0xff, 0xd9];
            return ContainsNearEnd(stream, endOfImage);
        }

        return false;
    }

    private static bool ContainsNearEnd(Stream stream, ReadOnlySpan<byte> marker)
    {
        const int maximumTailBytes = 64 * 1024;
        var tailLength = checked((int)Math.Min(maximumTailBytes, stream.Length));
        var tail = new byte[tailLength];
        stream.Seek(-tailLength, SeekOrigin.End);
        stream.ReadExactly(tail);
        return tail.AsSpan().IndexOf(marker) >= 0;
    }

    private void PruneTracking(
        Dictionary<string, SeenFile> seen,
        Dictionary<string, SettlingCandidate> settling,
        HashSet<string> present)
    {
        foreach (var path in seen
                     .Where(pair => !present.Contains(pair.Key))
                     .OrderBy(pair => pair.Value.LastObservedUtc)
                     .Take(Math.Max(0, seen.Count - _maximumTrackedFiles))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            seen.Remove(path);
        }

        foreach (var path in seen
                     .OrderBy(pair => pair.Value.LastObservedUtc)
                     .Take(Math.Max(0, seen.Count - _maximumTrackedFiles))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            seen.Remove(path);
        }

        foreach (var path in settling.Keys
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                     .Take(Math.Max(0, settling.Count - _maximumTrackedFiles))
                     .ToArray())
        {
            settling.Remove(path);
        }
    }

    private readonly record struct FileFingerprint(long Length, long WrittenUtcTicks);

    private sealed record FileCandidate(
        string Path,
        DateTime WrittenUtc,
        FileFingerprint Fingerprint,
        FileAttributes Attributes);

    private sealed record SettlingCandidate(FileFingerprint Fingerprint, int StableProbes)
    {
        /// <summary>Whether this path's name has already been reported on sight.</summary>
        public bool NameAnnounced { get; init; }
    }

    private sealed record SeenFile(FileFingerprint Fingerprint, DateTimeOffset LastObservedUtc);

    private sealed class WatchState(string root)
    {
        public string Root { get; } = root;

        public Dictionary<string, SeenFile> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FileOrderKey? DeliveryWatermark { get; set; }
    }

    private readonly record struct FileOrderKey(long WrittenUtcTicks, string Path) : IComparable<FileOrderKey>
    {
        public int CompareTo(FileOrderKey other)
        {
            var byTime = WrittenUtcTicks.CompareTo(other.WrittenUtcTicks);
            return byTime != 0 ? byTime : StringComparer.OrdinalIgnoreCase.Compare(Path, other.Path);
        }
    }
}
