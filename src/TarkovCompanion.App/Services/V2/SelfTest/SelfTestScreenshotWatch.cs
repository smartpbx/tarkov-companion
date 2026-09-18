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

    public async Task<SelfTestScreenshot> WatchAsync(
        string? root,
        TimeSpan patience,
        TimeSpan localUtcOffset,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Nothing(root, TimeSpan.Zero, root is null
                ? "No screenshot folder has been chosen, so nothing could be watched."
                : $"The screenshot folder {root} does not exist.");
        }

        HashSet<string> before;
        try
        {
            before = Listing(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Nothing(root, TimeSpan.Zero, $"The screenshot folder could not be listed: {exception.Message}");
        }

        var startedAt = _clock.GetTimestamp();
        while (_clock.GetElapsedTime(startedAt) < patience)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listedAt = _clock.GetTimestamp();
            string? arrived = null;
            try
            {
                arrived = Listing(root).FirstOrDefault(name => !before.Contains(name));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Nothing(root, _clock.GetElapsedTime(startedAt), $"The screenshot folder could not be listed: {exception.Message}");
            }

            var listingCost = _clock.GetElapsedTime(listedAt);
            if (arrived is not null)
            {
                return Describe(root, arrived, localUtcOffset, _clock.GetElapsedTime(startedAt));
            }

            var wait = listingCost * DutyCycle > PollInterval ? listingCost * DutyCycle : PollInterval;
            await Task.Delay(wait, _clock, cancellationToken).ConfigureAwait(false);
        }

        return Nothing(root, patience, null);
    }

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
                writtenUtc is { } unparsedAt ? noticedUtc - unparsedAt : null);
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
            noticedUtc - position.Timestamp);
    }

    private static SelfTestScreenshot Nothing(string? root, TimeSpan waited, string? problem) =>
        new(root, null, null, null, false, null, null, null, "no clock", waited, null, problem);

    private static HashSet<string> Listing(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
