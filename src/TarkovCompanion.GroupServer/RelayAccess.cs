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

    /// <summary>
    /// Whether a path is one that checks the operator's key rather than a member's.
    /// </summary>
    /// <remarks>
    /// [#317] Separate from <see cref="IsGroupPath"/> because the two keys are separate secrets
    /// and the attempt limiter has to count a wrong one of either. /reports is the operator
    /// reading every group's reports; /admin/... is the registry and the update controls behind
    /// the panel.
    ///
    /// The panel page itself, bare /admin, is deliberately not here, and the reason is the
    /// limiter rather than tidiness. It takes no key and always answers 200, so counting it
    /// would let a caller clear its own penalty between guesses by asking for the page — a
    /// throttle with a reset button is not a throttle. It is static markup; there is nothing on
    /// it to guess.
    /// </remarks>
    public static bool IsAdminPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var value = path.AsSpan().TrimStart('/');
        var end = value.IndexOf('/');
        var first = end < 0 ? value : value[..end];
        return first.Equals("reports", StringComparison.OrdinalIgnoreCase)
            || (first.Equals("admin", StringComparison.OrdinalIgnoreCase) && end >= 0 && value.Length > end + 1);
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

        // [#553] A desktop registering for tablet pairing is a member showing its group key, so
        // the room list refuses it and a wrong key is counted, exactly as on the routes below.
        if (path.Equals(RelayCompanionRoutes.RegisterDesktopPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
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
