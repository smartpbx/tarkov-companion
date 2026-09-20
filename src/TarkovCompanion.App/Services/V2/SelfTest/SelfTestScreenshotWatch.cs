using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.Services.V2.SelfTest;

/// <summary>
/// Waits for the player to take one screenshot, and measures what happened to it.
/// </summary>
/// <remarks>
/// The only probe that asks for something. Everything else the self-test reads is already on
/// disk; a position exists only once somebody presses the game's screenshot key, so this says
/// so and waits.
///
/// Polled, never watched. A <c>FileSystemWatcher</c> on the screenshot folder was tried and its
/// notifications never arrived on a real installation, which is a failure that looks exactly
/// like a player who did not press the key. The interval is short because the answer is in the
/// file's name, and the duty-cycle floor keeps a folder with thousands of shots in it from
/// being listed back to back.
/// </remarks>
public sealed class SelfTestScreenshotWatch(IScreenshotFilenameParser parser, TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>A listing never repeats sooner than ten times what the last one cost.</summary>
    private const int DutyCycle = 10;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    private readonly IScreenshotFilenameParser _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Looks for a screenshot already on disk from the last few minutes.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 43a] The probe used to demand a screenshot taken while it watched, which
    /// asks somebody to alt-tab into a game and press a key inside a window they cannot see —
    /// Clayton's report was that it "fails the screenshot section only because i cant alt tab back
    /// to the game and screenshot fast enough". A shot from two minutes ago exercises the identical
    /// path, and during a session one nearly always exists.
    ///
    /// Newest first, and the first one carrying a position wins. A shot taken outside a raid is
    /// remembered as the fallback rather than accepted, because it can say why this could not be
    /// measured but it cannot measure it.
    /// </remarks>
    public Task<SelfTestScreenshot> RecentAsync(
        string? root,
        TimeSpan lookBack,
        TimeSpan localUtcOffset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Unusable(root) is { } unusable)
        {
            return Task.FromResult(unusable);
        }

        var nowUtc = _clock.GetUtcNow();
        SelfTestScreenshot? fallback = null;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root!, "*", SearchOption.TopDirectoryOnly)
                         .Where(IsImage)
                         .Select(path => new FileInfo(path))
                         .Where(info => nowUtc - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) <= lookBack)
                         .OrderByDescending(info => info.LastWriteTimeUtc)
                         .Select(info => info.Name))
            {
                var reading = Describe(root!, path, localUtcOffset, TimeSpan.Zero) with { WasAlreadyThere = true };
                reading = reading with { Age = reading.WrittenUtc is { } at ? nowUtc - at : null };
                if (reading.Parsed)
                {
                    return Task.FromResult(reading);
                }

                // An in-raid name that will not parse is the one worth showing somebody, so it
                // outranks a menu shot however much newer the menu shot is.
                fallback = fallback is { NameKind: ScreenshotNameKind.InRaid } ? fallback : reading;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Nothing(root, TimeSpan.Zero, $"The screenshot folder could not be listed: {exception.Message}"));
        }

        return Task.FromResult(fallback ?? Nothing(root, TimeSpan.Zero, null));
    }

    public async Task<SelfTestScreenshot> WatchAsync(
        string? root,
        TimeSpan patience,
        TimeSpan localUtcOffset,
        CancellationToken cancellationToken)
    {
        if (Unusable(root) is { } unusable)
        {
            return unusable;
        }

        HashSet<string> before;
        try
        {
            before = Listing(root!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Nothing(root, TimeSpan.Zero, $"The screenshot folder could not be listed: {exception.Message}");
        }

        var startedAt = _clock.GetTimestamp();
        // Kept so a wait that only ever saw menu screenshots can say so, instead of reporting that
        // nothing arrived when three things did.
        SelfTestScreenshot? withoutPosition = null;
        while (_clock.GetElapsedTime(startedAt) < patience)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listedAt = _clock.GetTimestamp();
            string[] arrived;
            try
            {
                arrived = [.. Listing(root!).Where(name => !before.Contains(name))];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Nothing(root, _clock.GetElapsedTime(startedAt), $"The screenshot folder could not be listed: {exception.Message}");
            }

            foreach (var name in arrived)
            {
                before.Add(name);
                var reading = Describe(root!, name, localUtcOffset, _clock.GetElapsedTime(startedAt));
                if (reading.Parsed)
                {
                    return reading;
                }

                // A shot taken in the menu is not the end of the wait: the player may still walk
                // into a raid and take the one this is actually asking for.
                withoutPosition = withoutPosition is { NameKind: ScreenshotNameKind.InRaid }
                    ? withoutPosition
                    : reading;
            }

            var listingCost = _clock.GetElapsedTime(listedAt);
            var wait = listingCost * DutyCycle > PollInterval ? listingCost * DutyCycle : PollInterval;
            await Task.Delay(wait, _clock, cancellationToken).ConfigureAwait(false);
        }

        return withoutPosition is { } seen
            ? seen with { Waited = patience }
            : Nothing(root, patience, null);
    }

    /// <summary>The reading for a folder that cannot be looked at, or null when it can.</summary>
    private static SelfTestScreenshot? Unusable(string? root) =>
        string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)
            ? Nothing(root, TimeSpan.Zero, root is null
                ? "No screenshot folder has been chosen, so nothing could be watched."
                : $"The screenshot folder {root} does not exist.")
            : null;

    private SelfTestScreenshot Describe(string root, string name, TimeSpan localUtcOffset, TimeSpan waited)
    {
        var path = Path.Combine(root, name);
        var noticedUtc = _clock.GetUtcNow();
        DateTimeOffset? writtenUtc = null;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                writtenUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            writtenUtc = null;
        }

        var kind = ScreenshotFilenameParser.Classify(name);
        if (!_parser.TryParseFile(path, localUtcOffset, out var position) || position is null)
        {
            return new(
                root,
                name,
                writtenUtc,
                noticedUtc,
                Parsed: false,
                null,
                null,
                null,
                "the file's own write time",
                waited,
                writtenUtc is { } unparsedAt ? noticedUtc - unparsedAt : null)
            {
                NameKind = kind,
            };
        }

        // Which clock won is the parser's decision, and it is the difference between a position
        // that lands on the map and one discarded as older than the raid already on screen.
        var clock = writtenUtc is { } at && (position.Timestamp - at).Duration() < TimeSpan.FromSeconds(2)
            ? "the file's own write time"
            : "the clock in the name";
        return new(
            root,
            name,
            position.Timestamp,
            noticedUtc,
            Parsed: true,
            position.Position.X,
            position.Position.Y,
            position.Position.Z,
            clock,
            waited,
            noticedUtc - position.Timestamp)
        {
            NameKind = kind,
        };
    }

    private static SelfTestScreenshot Nothing(string? root, TimeSpan waited, string? problem) =>
        new(root, null, null, null, false, null, null, null, "no clock", waited, null, problem);

    private static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> Listing(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(IsImage)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
