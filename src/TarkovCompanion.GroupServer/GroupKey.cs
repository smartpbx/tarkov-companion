using System.Security.Cryptography;
using System.Text;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// Turns the one thing a group agrees between themselves into the room they share.
/// </summary>
/// <remarks>
/// There used to be two values: a room name, and a secret the server was started with. That
/// was worse in every way. The secret lived in the server's environment, so a member could not
/// choose it and the person running the container had to hand it out; a member who typed a
/// different one was refused with a 401 and, because the sharing code logged nothing, saw a
/// blank list and no reason for it. Two values also meant two ways to be wrong, and the
/// failure looked identical either way.
///
/// Now there is one. The key IS the room: every request carries the key to this server, which
/// takes its hash and buckets members by that. The hash is what it stores; the key itself is
/// not persisted, but the server does receive it in plaintext and is not blind to it. On an
/// open relay nobody is refused, because there is no registered room to refuse against. People
/// who type the same key see each other, and people who type a different one are somewhere
/// else, which is what they asked for whether they meant it or not.
///
/// That is a real change in what this guarantees and it is worth stating plainly. A stranger
/// who reaches this server can invent a key and have a room, exactly as they could have
/// invented a room name before. What they cannot do is join a room without knowing or guessing
/// its key. Nothing here limits guesses, so a short or human-chosen key protects little
/// (RISK-RELAY-KEY-BRUTEFORCE); scoped credentials and attempt limits are #304 and #310.
/// </remarks>
public static class GroupKey
{
    /// <summary>Short enough to be typed by a person, long enough not to be stumbled into.</summary>
    /// <remarks>
    /// Eight is the floor because the key is now the only thing standing between a group and a
    /// stranger who guesses it. There is no rate limiting here, so the length is doing all of
    /// the work.
    /// </remarks>
    public const int MinimumLength = 8;

    public const int MaximumLength = 128;

    public static bool IsAcceptable(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key.Trim().Length >= MinimumLength
        && key.Length <= MaximumLength;

    /// <summary>
    /// The room a key names.
    /// </summary>
    /// <remarks>
    /// Hashed rather than used directly so the identifier the server stores, logs and hands
    /// around is not the key, and is a fixed harmless length whatever somebody typed. The key
    /// is still in memory for every request that carries it. Trimmed first, because a trailing
    /// space pasted out of a chat window should not put one member in a different room from
    /// everybody else, which is precisely the kind of invisible mistake this change exists to
    /// remove.
    /// </remarks>
    public static string RoomFor(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()));
        return Convert.ToHexStringLower(hash)[..32];
    }
}
