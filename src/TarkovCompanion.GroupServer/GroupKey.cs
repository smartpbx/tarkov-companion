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
/// Now there is one. The key IS the room: the server takes its hash and buckets members by
/// that, and never learns or stores the key itself. Nobody can be refused, because there is
/// nothing to be refused against. People who type the same key see each other, and people who
/// type a different one are somewhere else, which is what they asked for whether they meant it
/// or not.
///
/// That is a real change in what this guarantees and it is worth stating plainly. A stranger
/// who reaches this server can invent a key and have a room, exactly as they could have
/// invented a room name before. What they cannot do is join a room whose key they do not know,
/// because the room's name is not discoverable from outside: it is the hash of a secret. So
/// the thing that was actually protecting the group before is still protecting it, and the
/// part that was only ceremony is gone.
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
    /// Hashed rather than used directly so the server never holds the key in memory or prints
    /// it in a log, and so a room identifier is a fixed harmless length whatever somebody
    /// typed. Trimmed first, because a trailing space pasted out of a chat window should not
    /// put one member in a different room from everybody else, which is precisely the kind of
    /// invisible mistake this change exists to remove.
    /// </remarks>
    public static string RoomFor(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()));
        return Convert.ToHexStringLower(hash)[..32];
    }
}
