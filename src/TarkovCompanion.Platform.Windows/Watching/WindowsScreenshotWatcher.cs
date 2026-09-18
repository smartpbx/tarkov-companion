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
/// directory listing a second and cannot miss a file, so the same approach is used here.
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
    int maximumTrackedFiles = 16_384)
    : IScreenshotWatcher
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
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

            var snapshot = Snapshot(screenshotRoot);
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
            if (!await WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                yield break;
            }
        }
    }

    private async Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
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
            foreach (var path in Directory.EnumerateFiles(screenshotRoot))
            {
                if (!SupportedExtensions.Contains(Path.GetExtension(path))
                    || (!developerMode && path.Contains("EftSimulator", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var candidate = TryProbe(path);
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
        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            return new(
                path,
                info.LastWriteTimeUtc,
                new(info.Length, info.LastWriteTimeUtc.Ticks),
                info.Attributes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
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
