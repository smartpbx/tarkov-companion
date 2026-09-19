using System.Globalization;

namespace TarkovCompanion.UnitTests.Watching;

/// <summary>
/// A game log session folder on disk, shaped like the ones Escape from Tarkov writes.
/// </summary>
/// <remarks>
/// The line shapes are the ones recorded in docs/research/EFT_LOG_FACTS.md and pinned by
/// <c>QuestNotificationParserTests</c>: a pipe-delimited prefix, a <c>ChatMessageReceived</c>
/// announcement, and a <c>new_message</c> payload whose message carries a numeric type and a
/// templateId whose first word is the quest's own 24-character id. The folder name carries the
/// session stamp and the game version the way the game stamps it, because the watcher picks the
/// current session by parsing that name rather than by file times.
///
/// Written rather than checked in: a real session's other lines are full of the player's
/// nickname, profile id and inventory, and none of that is needed to prove a quest is read.
/// </remarks>
internal sealed class QuestLogSessionFixture : IDisposable
{
    private const string Stamp = "2026.09.18_22-11-05";
    private const string Version = "1.1.5.1.47510";

    public QuestLogSessionFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "tarkov-quest-log-" + Guid.NewGuid().ToString("N"));
        Session = Path.Combine(Root, $"log_{Stamp}_{Version}");
        Directory.CreateDirectory(Session);
    }

    /// <summary>The folder the companion is pointed at, holding one session folder.</summary>
    public string Root { get; }

    /// <summary>The session folder itself.</summary>
    public string Session { get; }

    /// <summary>Writes one of the session's log files, named as the game names them.</summary>
    public string Write(string prefix, params string[] lines)
    {
        var path = Path.Combine(Session, $"{Stamp}_{Version} {prefix}_000.log");
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>Appends to a file that already exists, as the game does while it runs.</summary>
    public void Append(string prefix, params string[] lines) =>
        File.AppendAllLines(Path.Combine(Session, $"{Stamp}_{Version} {prefix}_000.log"), lines);

    /// <summary>
    /// A quest notification line, exactly as the game writes one.
    /// </summary>
    /// <param name="messageType">10 started, 11 failed, 12 handed in.</param>
    /// <param name="taskId">The quest's own id, which is the template id's first word.</param>
    /// <param name="messageId">The notification's own id, which is what deduplicates it.</param>
    public static string Quest(int messageType, string taskId, string messageId) =>
        "2026-09-18 22:41:07.113 +00:00|NOTIFICATION|6aa4bd43d4a840ddb8130198|ChatMessageReceived|" +
        "[{\"type\":\"new_message\",\"eventId\":\"e1\",\"dialogId\":\"5935c25fb3acc3127c3d8cd9\"," +
        "\"message\":{\"_id\":\"" + messageId + "\",\"uid\":\"5935c25fb3acc3127c3d8cd9\",\"type\":" +
        messageType.ToString(CultureInfo.InvariantCulture) +
        ",\"text\":\"quest started\",\"templateId\":\"" + taskId + " successMessageText\"}}]";

    /// <summary>
    /// The same quest notification as backend_000.log writes it.
    /// </summary>
    /// <remarks>
    /// The identical event, announced as "new_message" rather than "ChatMessageReceived". This is
    /// the spelling in the files the companion actually reads, and the parser used to reject it,
    /// so this is the shape that matters most: it is what was on the player's disk while his board
    /// stayed still. Prefix and payload are a real line from log_2026.09.18_22-11-05_1.1.5.1.47510,
    /// truncated.
    /// </remarks>
    public static string BackendQuest(int messageType, string taskId, string messageId) =>
        "2026-09-18 22:57:50.161|1.1.5.1.47510|Info|backend|" +
        "WebSocketSharp - message received: NOTIFICATION 6aadc1ee83c0d7b85607ca2d new_message " +
        "[{\"type\":\"new_message\",\"eventId\":\"6aadc1ee83c0d7b85607ca2d\"," +
        "\"dialogId\":\"54cb50c76803fa8b248b4571\",\"message\":{\"_id\":\"" + messageId + "\"," +
        "\"uid\":\"54cb50c76803fa8b248b4571\",\"type\":" +
        messageType.ToString(CultureInfo.InvariantCulture) +
        ",\"dt\":1789772270,\"text\":\"quest started\",\"templateId\":\"" + taskId +
        " successMessageText\",\"items\":{\"data\":[],\"stash\":\"x\"}}}]";

    /// <summary>An ordinary line that is not about a quest, for the parsers to reject.</summary>
    public static string Chatter(string what) =>
        $"2026-09-18 22:40:00.000 +00:00|INFO|application|{what}";

    /// <summary>
    /// A group notification, which is the reason the push-notifications file stays closed.
    /// </summary>
    /// <remarks>
    /// Stands in for the blobs that carry teammates' nicknames, inventories, health and looted
    /// dogtags. Nothing should read this out of a chat-only file, and a test says so.
    /// </remarks>
    public static string GroupBlob() =>
        "2026-09-18 22:39:00.000 +00:00|NOTIFICATION|6aa4bd43d4a840ddb8130198|GroupMatchRaidReady|" +
        "[{\"type\":\"GroupMatchRaidReady\",\"extendedProfile\":{\"Info\":{\"Nickname\":\"Teammate\"," +
        "\"Side\":\"Bear\",\"Level\":42},\"PlayerVisualRepresentation\":{\"Inventory\":{\"items\":[]}}}}]";

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
