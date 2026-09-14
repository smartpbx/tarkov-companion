using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace TarkovCompanion.GroupServer;

/// <summary>One room somebody was meant to be in, as the admin panel lists it.</summary>
/// <param name="Room">The hash the relay buckets members by. The key itself is not here.</param>
/// <param name="Label">What the operator called it, so a row means something to a person.</param>
public sealed record RegisteredRoom(string Room, string Label, DateTimeOffset CreatedUtc);

/// <summary>
/// The rooms an operator meant to exist, which is what "keep randoms out" needs.
/// </summary>
/// <remarks>
/// <para>
/// A room was whatever anybody's key hashed to. That is a real access model — a room's name is
/// not discoverable from outside, so a stranger cannot join a group whose key they do not know —
/// but it says nothing about who may have a room at all. Anybody who can reach the relay can
/// invent a key and be in one, and the operator has no list of the rooms that were meant to
/// exist, so there is nothing to compare what is there against.
/// </para>
/// <para>
/// This is that list. It holds room hashes and labels: never a key, which the relay has never
/// held and does not start holding now. A key the relay generates is returned once, in the
/// response to the request that made it, and after that only its hash remains.
/// </para>
/// <para>
/// Two ways in, because a relay that already has friends on it should not have to re-key them.
/// An operator can have a key generated, or adopt one the group already uses; both end as the
/// same stored hash.
/// </para>
/// </remarks>
public sealed class GroupRoomRegistry
{
    /// <summary>How many rooms an operator may register.</summary>
    /// <remarks>
    /// Well under <see cref="GroupRooms.MaximumRooms"/>, because this is a list somebody types
    /// rather than one that accumulates.
    /// </remarks>
    public const int Maximum = 64;

    /// <summary>How long a generated key is, in bytes before encoding.</summary>
    /// <remarks>
    /// Sixteen bytes is 26 characters of base32, comfortably above
    /// <see cref="GroupKey.MinimumLength"/>, and it is read off a screen and pasted rather than
    /// typed from memory.
    /// </remarks>
    private const int GeneratedKeyBytes = 16;

    /// <summary>The alphabet a generated key is written in.</summary>
    /// <remarks>
    /// No 0, 1, I or O: the key travels by being read aloud or copied out of a chat window, and
    /// those four are the ones that come back wrong.
    /// </remarks>
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    private readonly TimeProvider _timeProvider;
    private readonly string? _storePath;
    private readonly Lock _saveGate = new();
    private readonly ConcurrentDictionary<string, RegisteredRoom> _rooms = new(StringComparer.Ordinal);

    /// <param name="storePath">Where the list is kept, or null to hold it only in memory.</param>
    /// <remarks>
    /// Persisted, because the relay updates itself every half hour and a list that did not
    /// survive that would lock the group out of their own rooms on a schedule.
    /// </remarks>
    public GroupRoomRegistry(TimeProvider timeProvider, string? storePath = null)
    {
        _timeProvider = timeProvider;
        _storePath = storePath;
        Load();
    }

    /// <summary>Whether any room has been registered, which is what closes the relay.</summary>
    /// <remarks>
    /// An empty list means open, and that is deliberate. Closing the moment an operator sets an
    /// admin key — before they have registered anything — would lock out every group on the
    /// relay at the instant the operator was trying to look at it.
    /// </remarks>
    public bool IsClosed => !_rooms.IsEmpty;

    public int Count => _rooms.Count;

    /// <summary>Whether a room may be used.</summary>
    public bool Allows(string room) => !IsClosed || _rooms.ContainsKey(room);

    public IReadOnlyList<RegisteredRoom> List() =>
        [.. _rooms.Values.OrderBy(registered => registered.Label, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Registers a room, generating a key when none was given.
    /// </summary>
    /// <returns>
    /// The room and the key, where the key is non-null only when this call generated it. It is
    /// the one moment the key exists outside the group, and it is not stored.
    /// </returns>
    public (RegisteredRoom Room, string? Key)? Add(string label, string? key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (_rooms.Count >= Maximum)
        {
            return null;
        }

        var generated = key is null ? Generate() : null;
        var effective = generated ?? key!;
        if (!GroupKey.IsAcceptable(effective))
        {
            return null;
        }

        var room = new RegisteredRoom(
            GroupKey.RoomFor(effective),
            label.Trim(),
            _timeProvider.GetUtcNow());
        if (!_rooms.TryAdd(room.Room, room))
        {
            // Already registered. Reported as a plain failure rather than by relabelling the
            // existing one, because an operator adopting a key they thought was new has learned
            // something either way.
            return null;
        }

        Save();
        return (room, generated);
    }

    /// <summary>
    /// Registers a room the relay is already holding, by its hash.
    /// </summary>
    /// <remarks>
    /// The migration path, and the reason closing a relay that already has friends on it does
    /// not mean re-keying them. The relay knows which rooms are in use because it is holding
    /// them; it does not know their keys and does not need to, because the hash is what the
    /// allowlist is made of.
    /// </remarks>
    public RegisteredRoom? Adopt(string room, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (_rooms.Count >= Maximum)
        {
            return null;
        }

        var registered = new RegisteredRoom(room, label.Trim(), _timeProvider.GetUtcNow());
        if (!_rooms.TryAdd(room, registered))
        {
            return null;
        }

        Save();
        return registered;
    }

    /// <summary>Forgets a room, which stops it being usable when the relay is closed.</summary>
    public bool Remove(string room)
    {
        if (!_rooms.TryRemove(room, out _))
        {
            return false;
        }

        Save();
        return true;
    }

    /// <summary>A key that is awkward to mistype and not worth guessing.</summary>
    private static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(GeneratedKeyBytes);
        var key = new char[bytes.Length + (bytes.Length / 4)];
        var at = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (index > 0 && index % 4 == 0)
            {
                key[at++] = '-';
            }

            key[at++] = Alphabet[bytes[index] % Alphabet.Length];
        }

        return new string(key, 0, at);
    }

    private void Save()
    {
        if (_storePath is null)
        {
            return;
        }

        try
        {
            var snapshot = _rooms.Values.ToArray();
            lock (_saveGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
                var temporary = _storePath + ".writing";
                File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
                File.Move(temporary, _storePath, overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    /// <summary>
    /// Reads the list back.
    /// </summary>
    /// <remarks>
    /// An unreadable file leaves the list empty, which leaves the relay open. That is the right
    /// direction to fail in: the alternative is a corrupt file locking a group out of a relay
    /// whose whole job is to be there when they play.
    /// </remarks>
    private void Load()
    {
        if (_storePath is null || !File.Exists(_storePath))
        {
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<RegisteredRoom[]>(File.ReadAllText(_storePath));
            if (stored is null)
            {
                return;
            }

            foreach (var room in stored.Take(Maximum))
            {
                if (!string.IsNullOrWhiteSpace(room.Room))
                {
                    _rooms[room.Room] = room;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _rooms.Clear();
        }
    }
}
