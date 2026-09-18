namespace TarkovCompanion.Application.Services.Raids;

/// <summary>How much of one of the game's log files is read.</summary>
public enum LogReadMode
{
    /// <summary>Every line, to every parser.</summary>
    Full,

    /// <summary>
    /// Only the lines carrying the game's own quest and flea notifications.
    /// </summary>
    /// <remarks>
    /// For a file that is otherwise not opened at all. See <see cref="EftLogFiles.ChatOnlyPrefixes"/>.
    /// </remarks>
    ChatOnly,
}

/// <summary>
/// Which of the game's log files the companion opens, and how much of each.
/// </summary>
/// <remarks>
/// One definition, shared by the watcher that tails these files for a living and by the Setup
/// self-test that reports what this build understood of a session. Those two had separate lists
/// that disagreed in both directions -- the self-test would open a notifications file the
/// watcher never touches, and refused an output file the watcher tails -- so the self-test's
/// counts never described what the watcher actually sees. A player reading "0 quest
/// notifications" had no way to know it was a statement about one file out of six.
/// </remarks>
public static class EftLogFiles
{
    /// <summary>
    /// The log files read in full.
    /// </summary>
    /// <remarks>
    /// Reading every *.log in the folder was a privacy problem, not just wasted work. The
    /// game's backend and push-notification logs carry large JSON blobs containing real
    /// personal data for the player and for anyone they grouped with: nicknames, account and
    /// profile ids, full inventories, health state, and looted dogtags naming a killer and a
    /// victim. None of that is needed to tell which map a raid is on, so none of it is opened.
    ///
    /// application carries the map and lifecycle markers. output is the only file still
    /// written throughout a raid, so it is what can say the player is still in one. backend
    /// carries the userConfirmed and userMatchOver notifications that give an exact raid
    /// start, end and duration for the player.
    /// </remarks>
    public static readonly string[] FullPrefixes = ["application", "output", "backend"];

    /// <summary>
    /// The log files opened for the game's own quest and flea notifications and nothing else.
    /// </summary>
    /// <remarks>
    /// This matches "notifications" and, through the hyphen, "push-notifications".
    ///
    /// It was added because a session in which the player demonstrably handed in quests produced
    /// no quest at all. The claim that every notification is duplicated into backend was
    /// measured on an older build; on 1.1.5.x a session's backend log can hold seven hundred
    /// lines, two recognised raids and zero ChatMessageReceived.
    ///
    /// The privacy reason for leaving push-notifications shut is real and unchanged: its group
    /// blobs carry teammates' full inventories, health and looted dogtags. So this is not "open
    /// the file". A line from one of these files is looked at only if it already contains one of
    /// <see cref="NotificationMarkers"/>, and it is then offered to the quest and flea parsers
    /// only. The raid parser and the party parser -- the one that reads those group blobs --
    /// never see it, and nothing else in the file is read, kept or reported.
    /// </remarks>
    public static readonly string[] ChatOnlyPrefixes = ["notifications"];

    /// <summary>
    /// The only notifications a chat-only file is opened for.
    /// </summary>
    /// <remarks>
    /// The quest announcement has two spellings depending on which file it is in, so this takes
    /// the set the parser itself recognises rather than restating one of them. Getting that
    /// wrong here would narrow the chat-only reader to nothing in exactly the way the parser's
    /// own single-spelling check narrowed the full readers to nothing.
    /// </remarks>
    public static readonly string[] NotificationMarkers =
        [.. QuestNotificationParser.NotificationMarkers, FleaSaleParser.NotificationMarker];

    /// <summary>
    /// How much of an already-written file is read from its end.
    /// </summary>
    /// <remarks>
    /// output is the largest of these by a wide margin and is mostly keepalives, so reading all
    /// of one is neither affordable nor useful. A session's notifications are at its end, which
    /// is the part this keeps.
    /// </remarks>
    public const long MaximumReplayBytes = 16L * 1024 * 1024;

    /// <summary>How much of this file to read, or null for a file with no reason to be opened.</summary>
    /// <remarks>
    /// The game names each file "&lt;session stamp&gt; &lt;prefix&gt;_000.log", so the prefix is matched
    /// within the name rather than at its start. Full is tested first, so a file matching both
    /// tiers is read fully rather than narrowed.
    /// </remarks>
    public static LogReadMode? ReadMode(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = Path.GetFileNameWithoutExtension(path.AsSpan());
        if (Matches(name, FullPrefixes))
        {
            return LogReadMode.Full;
        }

        return Matches(name, ChatOnlyPrefixes) ? LogReadMode.ChatOnly : null;
    }

    /// <summary>Whether a line carries one of the two notifications a chat-only file is read for.</summary>
    public static bool IsChatNotification(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        foreach (var marker in NotificationMarkers)
        {
            if (line.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(ReadOnlySpan<char> name, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            var index = name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            // "backend" must not match on "end", so the prefix has to begin a word. That same
            // rule is what lets "notifications" match "push-notifications": the hyphen ends the
            // preceding word, and that file is wanted, narrowly.
            if (index == 0 || name[index - 1] is ' ' or '_' or '-' or '.')
            {
                return true;
            }
        }

        return false;
    }
}
