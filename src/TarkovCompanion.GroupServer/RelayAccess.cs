namespace TarkovCompanion.GroupServer;

/// <summary>
/// Whether a request may use the room its key names.
/// </summary>
/// <remarks>
/// The decision is here rather than inline in the middleware so that it can be stated once and
/// tested directly. What it guards is a real change in what this relay is: a room used to be
/// whatever anybody's key hashed to, and once an operator has registered one, the registered
/// rooms are the only ones served.
/// </remarks>
public static class RelayAccess
{
    /// <summary>
    /// Whether this request is refused because its room is not one the operator registered.
    /// </summary>
    /// <remarks>
    /// Three ways to be allowed through, and each is deliberate. An open relay — one with
    /// nothing registered — is unchanged, which is what every existing deployment is. A path
    /// that does not act on a room is not this decision's business. And a key that is missing or
    /// too short is left to the handler, which has always answered it with a 401: this decides
    /// which room may be used, not whether a key is a key.
    /// </remarks>
    public static bool Refuses(GroupRoomRegistry registry, string? path, string? key)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.IsClosed
            && IsGroupPath(path)
            && GroupKey.IsAcceptable(key)
            && !registry.Allows(GroupKey.RoomFor(key!));
    }

    /// <summary>Whether a path is one that acts on a group's room.</summary>
    /// <remarks>
    /// Matched on the first segment rather than by prefix, because /report and /reports are two
    /// different things: one is a member filing a problem with their group's key, the other is
    /// the operator reading every group's with theirs.
    ///
    /// /catalog is deliberately not here. It mirrors public game data, it takes no key, and
    /// closing the relay to strangers is not a reason to stop it being useful to one.
    /// </remarks>
    public static bool IsGroupPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var value = path.AsSpan().TrimStart('/');
        var end = value.IndexOf('/');
        var first = end < 0 ? value : value[..end];
        return first.Equals("state", StringComparison.OrdinalIgnoreCase)
            || first.Equals("waypoints", StringComparison.OrdinalIgnoreCase)
            || first.Equals("pings", StringComparison.OrdinalIgnoreCase)
            || first.Equals("report", StringComparison.OrdinalIgnoreCase);
    }
}
