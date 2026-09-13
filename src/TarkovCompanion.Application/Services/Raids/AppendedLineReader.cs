using System.Text;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Hands back only the lines the game has finished writing.
/// </summary>
/// <remarks>
/// The watcher used <see cref="StreamReader.ReadLineAsync()"/>, which returns an unterminated
/// tail as though it were a line and leaves the stream positioned past it. The remainder then
/// arrived as a second line on the next poll, so one log entry became two fragments and
/// neither was valid.
///
/// That was harmless for short lines and fatal for the ones that matter most: every group
/// notification and every <c>userMatchOver</c> is multi-kilobyte single-line JSON, and a cut
/// one makes <c>JsonDocument.Parse</c> throw, the parser return null, and the event vanish with
/// no rewind and no second chance.
///
/// So the split is done on bytes here rather than by a reader. A newline byte cannot occur
/// inside a UTF-8 multi-byte sequence, so scanning for it is safe on undecoded input, and only
/// complete lines are ever decoded — which also means a character split across two polls is
/// rejoined rather than replaced.
///
/// This lives in Application rather than in the Windows watcher so the Linux suite can pin it
/// with one notification written in two halves, which is exactly the case that was broken.
/// </remarks>
public sealed class AppendedLineReader(TimeProvider timeProvider, TimeSpan? flushUnterminatedAfter = null)
{
    /// <summary>How long an unterminated tail waits before it is treated as a whole line.</summary>
    /// <remarks>
    /// The game does finish its lines, so a tail that stops growing is almost always the last
    /// line of a file that is no longer being written — a rolled log, or a session that ended.
    /// Holding it forever would lose it; emitting it immediately is the bug. Five seconds is
    /// far longer than the gap inside one write and far shorter than a player notices.
    /// </remarks>
    private static readonly TimeSpan DefaultFlushAfter = TimeSpan.FromSeconds(5);

    private const byte Newline = (byte)'\n';

    private readonly TimeSpan _flushAfter = flushUnterminatedAfter ?? DefaultFlushAfter;
    private readonly Dictionary<string, Source> _sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lines completed since the last read of this source, oldest first.</summary>
    /// <param name="key">Identifies the file across polls; its path.</param>
    /// <param name="stream">Positioned anywhere; this seeks for itself.</param>
    public async Task<IReadOnlyList<string>> ReadAsync(
        string key,
        Stream stream,
        CancellationToken cancellationToken)
    {
        if (!_sources.TryGetValue(key, out var source))
        {
            source = new();
            _sources[key] = source;
        }

        // A file that shrank was rolled, so the offset means nothing and anything held from
        // the old file belongs to a log that no longer exists.
        if (stream.Length < source.Offset)
        {
            source.Offset = 0;
            source.Pending = [];
            source.PendingSince = null;
        }

        var available = stream.Length - source.Offset;
        var fresh = Array.Empty<byte>();
        if (available > 0)
        {
            stream.Seek(source.Offset, SeekOrigin.Begin);
            fresh = new byte[available];
            var read = await stream.ReadAtLeastAsync(fresh, fresh.Length, false, cancellationToken)
                .ConfigureAwait(false);
            if (read < fresh.Length)
            {
                Array.Resize(ref fresh, read);
            }

            source.Offset += fresh.Length;
        }

        var buffer = source.Pending.Length == 0
            ? fresh
            : [.. source.Pending, .. fresh];

        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < buffer.Length; index++)
        {
            if (buffer[index] != Newline)
            {
                continue;
            }

            lines.Add(Decode(buffer, start, index - start));
            start = index + 1;
        }

        var tail = buffer[start..];
        var now = timeProvider.GetUtcNow();
        if (tail.Length == 0)
        {
            source.Pending = [];
            source.PendingSince = null;
            return lines;
        }

        // Only a tail that has stopped changing is a candidate for flushing. One that grew
        // since the last poll is a line still being written, and the clock restarts.
        if (source.PendingSince is not { } since || tail.Length != source.Pending.Length)
        {
            source.Pending = tail;
            source.PendingSince = now;
            return lines;
        }

        if (now - since < _flushAfter)
        {
            source.Pending = tail;
            return lines;
        }

        lines.Add(Decode(tail, 0, tail.Length));
        source.Pending = [];
        source.PendingSince = null;
        return lines;
    }

    /// <summary>
    /// Treats everything already in a file as read.
    /// </summary>
    /// <remarks>
    /// Files present when watching starts are finished sessions; replaying them would re-run
    /// hundreds of old raids through the parser on every launch.
    /// </remarks>
    public void StartAtEnd(string key, long length) => _sources[key] = new() { Offset = length };

    /// <summary>How far into a file this reader has consumed.</summary>
    public long Position(string key) => _sources.TryGetValue(key, out var source) ? source.Offset : 0;

    /// <summary>
    /// Whether a part-written line is being held for this file.
    /// </summary>
    /// <remarks>
    /// A caller that skips unchanged files has to keep polling one with a tail, or the tail is
    /// never flushed and the last line of a rolled log is lost.
    /// </remarks>
    public bool HasPending(string key) => _sources.TryGetValue(key, out var source) && source.Pending.Length > 0;

    /// <summary>Forgets a file, so a replay and a tail do not share an offset.</summary>
    public void Forget(string key) => _sources.Remove(key);

    private static string Decode(byte[] buffer, int start, int length)
    {
        // The game writes CRLF; the terminator is not part of the line.
        if (length > 0 && buffer[start + length - 1] == (byte)'\r')
        {
            length--;
        }

        return Encoding.UTF8.GetString(buffer, start, length);
    }

    private sealed class Source
    {
        public long Offset;

        public byte[] Pending = [];

        public DateTimeOffset? PendingSince;
    }
}
