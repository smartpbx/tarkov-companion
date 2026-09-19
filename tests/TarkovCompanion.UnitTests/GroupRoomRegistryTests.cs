using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The rooms an operator meant to exist.
/// </summary>
/// <remarks>
/// A room was whatever anybody's key hashed to. That protects a group — a room's name is not
/// discoverable from outside, so a stranger cannot join one whose key they do not know — but it
/// says nothing about who may have a room at all, and it left the operator with no list to
/// compare what the relay was holding against.
/// </remarks>
public sealed class GroupRoomRegistryTests
{
    [Fact]
    public void AnEmptyListLeavesTheRelayOpen()
    {
        // The state every existing relay is in. Closing on the day this ships would lock out
        // every group using one.
        var registry = new GroupRoomRegistry(TimeProvider.System);

        Assert.False(registry.IsClosed);
        Assert.True(registry.Allows(GroupKey.RoomFor("anything-at-all")));
    }

    [Fact]
    public void RegisteringOneRoomClosesTheRelayToEveryOther()
    {
        var registry = new GroupRoomRegistry(TimeProvider.System);
        registry.Add("Tuesday group", "a-known-key");

        Assert.True(registry.IsClosed);
        Assert.True(registry.Allows(GroupKey.RoomFor("a-known-key")));
        Assert.False(registry.Allows(GroupKey.RoomFor("some-other-key")));
    }

    [Fact]
    public void AGeneratedKeyIsReturnedOnceAndIsUsable()
    {
        var registry = new GroupRoomRegistry(TimeProvider.System);

        var created = registry.Add("Tuesday group", null);

        Assert.NotNull(created);
        Assert.NotNull(created.Value.Key);
        Assert.True(GroupKey.IsAcceptable(created.Value.Key));
        Assert.Equal(GroupKey.RoomFor(created.Value.Key!), created.Value.Room.Room);
    }

    [Fact]
    public void AGeneratedKeyIsNotKeptAnywhere()
    {
        // The whole reason the relay can hold this list at all. It sees keys on requests but has
        // never stored one, and does not start now: what is stored is the hash, the same as for
        // an adopted room.
        var registry = new GroupRoomRegistry(TimeProvider.System);
        var created = registry.Add("Tuesday group", null)!.Value;

        var listed = Assert.Single(registry.List());

        Assert.DoesNotContain(created.Key!, listed.Room, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(created.Key!, listed.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnExistingKeyIsAdoptedWithoutBeingReturned()
    {
        // The migration path. A relay that already has friends on it should not have to re-key
        // them, and there is nothing to show back: the operator supplied the key.
        var registry = new GroupRoomRegistry(TimeProvider.System);

        var created = registry.Add("Tuesday group", "a-known-key");

        Assert.NotNull(created);
        Assert.Null(created.Value.Key);
        Assert.True(registry.Allows(GroupKey.RoomFor("a-known-key")));
    }

    [Fact]
    public void ARoomAlreadyBeingHeldIsAdoptedByItsHash()
    {
        // The other half of the migration path, and the one that needs no key at all: the relay
        // is already holding the room, so the hash is the thing to register.
        var registry = new GroupRoomRegistry(TimeProvider.System);
        var room = GroupKey.RoomFor("a-key-nobody-typed-here");

        Assert.NotNull(registry.Adopt(room, "Whoever that is"));
        Assert.True(registry.Allows(room));
    }

    [Fact]
    public void TheSameRoomIsNotRegisteredTwice()
    {
        var registry = new GroupRoomRegistry(TimeProvider.System);
        registry.Add("Tuesday group", "a-known-key");

        Assert.Null(registry.Add("The same room again", "a-known-key"));
        Assert.Single(registry.List());
    }

    [Fact]
    public void AKeyTooShortToBeAKeyIsRefused()
    {
        var registry = new GroupRoomRegistry(TimeProvider.System);

        Assert.Null(registry.Add("Tuesday group", "short"));
        Assert.False(registry.IsClosed);
    }

    [Fact]
    public void RemovingTheLastRoomOpensTheRelayAgain()
    {
        // Stated rather than assumed: an operator who removes everything has an open relay, not
        // one nobody can use.
        var registry = new GroupRoomRegistry(TimeProvider.System);
        var created = registry.Add("Tuesday group", "a-known-key")!.Value;

        Assert.True(registry.Remove(created.Room.Room));

        Assert.False(registry.IsClosed);
    }

    [Fact]
    public void TheListSurvivesARestart()
    {
        // The relay replaces its own tree every half hour. A list that did not survive that
        // would lock the group out of their own rooms on a schedule.
        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-registry-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "rooms.json");
        try
        {
            new GroupRoomRegistry(TimeProvider.System, path).Add("Tuesday group", "a-known-key");

            var reopened = new GroupRoomRegistry(TimeProvider.System, path);

            Assert.True(reopened.IsClosed);
            Assert.True(reopened.Allows(GroupKey.RoomFor("a-known-key")));
            Assert.Equal("Tuesday group", Assert.Single(reopened.List()).Label);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// [#317, RISK-RELAY-REGISTRY-FAIL-OPEN] An unreadable list refuses every room.
    /// </summary>
    /// <remarks>
    /// This reverses what the relay used to do, and the reversal is the point. An unreadable file
    /// emptied the list, and an empty list means open — so the one event most likely to accompany
    /// tampering, the file that says which rooms may exist becoming unreadable, turned a closed
    /// relay into one serving every room anybody could invent, silently.
    ///
    /// Both directions of failure cost the group their relay. Only one of them also serves
    /// strangers, and only the refusal says so out loud: the middleware answers 503 with a
    /// different sentence, so an operator looks at the file rather than at their key.
    /// </remarks>
    [Fact]
    public void AnUnreadableListRefusesEveryRoomRatherThanServingAll()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-registry-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "rooms.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ this is not the file");

            var registry = new GroupRoomRegistry(TimeProvider.System, path);

            Assert.True(registry.IsUnreadable);
            Assert.True(registry.IsClosed);
            Assert.Empty(registry.List());
            Assert.False(registry.Allows(GroupKey.RoomFor("a-key-nobody-registered")));
            Assert.True(RelayAccess.Refuses(registry, "/state", "a-key-nobody-registered"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>Registering a room after a failed read repairs the relay, because it rewrites the file.</summary>
    /// <remarks>
    /// Otherwise the only way out of a refusing relay would be to delete a file by hand on the
    /// box, which is a worse answer than the operator using the panel they already have open.
    /// </remarks>
    [Fact]
    public void RegisteringARoomClearsAFailedRead()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tarkov-registry-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "rooms.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ this is not the file");
            var registry = new GroupRoomRegistry(TimeProvider.System, path);
            Assert.True(registry.IsUnreadable);

            var added = registry.Add("Evening group", null);

            Assert.NotNull(added);
            Assert.False(registry.IsUnreadable);
            Assert.True(registry.Allows(added!.Value.Room.Room));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("/reports", true)]
    [InlineData("/reports/abc", true)]
    // The panel page takes no key and always answers 200. Counting it would let a caller clear
    // its own penalty between guesses by asking for the page.
    [InlineData("/admin", false)]
    [InlineData("/admin/", false)]
    [InlineData("/admin/rooms", true)]
    [InlineData("/admin/update", true)]
    [InlineData("/report", false)]
    [InlineData("/state", false)]
    [InlineData("/catalog/items", false)]
    public void TheOperatorsOwnPathsAreTheirOwn(string path, bool isAdmin)
    {
        // [#317] The attempt limiter counts a wrong key of either kind, so it has to be able to
        // tell them apart. /report and /reports differ by one letter and by which secret they take.
        Assert.Equal(isAdmin, RelayAccess.IsAdminPath(path));
    }

    [Fact]
    public void TheListIsBounded()
    {
        var registry = new GroupRoomRegistry(TimeProvider.System);
        for (var index = 0; index < GroupRoomRegistry.Maximum; index++)
        {
            Assert.NotNull(registry.Add($"Room {index}", $"a-known-key-{index}"));
        }

        Assert.Null(registry.Add("One too many", "a-known-key-extra"));
        Assert.Equal(GroupRoomRegistry.Maximum, registry.Count);
    }
}

/// <summary>
/// Which requests the relay refuses once its operator has said which rooms exist.
/// </summary>
public sealed class RelayAccessTests
{
    [Fact]
    public void AnOpenRelayRefusesNothing()
    {
        var registry = new GroupRoomRegistry(TimeProvider.System);

        Assert.False(RelayAccess.Refuses(registry, "/state", "any-old-key"));
    }

    [Fact]
    public void AClosedRelayRefusesARoomItDoesNotKnow()
    {
        var registry = new GroupRoomRegistry(TimeProvider.System);
        registry.Add("Tuesday group", "a-known-key");

        Assert.True(RelayAccess.Refuses(registry, "/state", "a-stranger-key"));
        Assert.False(RelayAccess.Refuses(registry, "/state", "a-known-key"));
    }

    [Theory]
    [InlineData("/state")]
    [InlineData("/state/Clay")]
    [InlineData("/waypoints")]
    [InlineData("/waypoints/12/reached")]
    [InlineData("/pings")]
    [InlineData("/report")]
    public void EveryPathThatActsOnARoomIsGuarded(string path)
    {
        var registry = new GroupRoomRegistry(TimeProvider.System);
        registry.Add("Tuesday group", "a-known-key");

        Assert.True(RelayAccess.Refuses(registry, path, "a-stranger-key"));
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/catalog")]
    [InlineData("/catalog/regular/items")]
    [InlineData("/tablet")]
    [InlineData("/admin")]
    [InlineData("/admin/rooms")]
    public void WhatDoesNotActOnARoomIsNotGuarded(string path)
    {
        var registry = new GroupRoomRegistry(TimeProvider.System);
        registry.Add("Tuesday group", "a-known-key");

        Assert.False(RelayAccess.Refuses(registry, path, "a-stranger-key"));
    }

    [Fact]
    public void ReportsIsNotReport()
    {
        // /report is a member filing a problem with their group's key; /reports is the operator
        // reading every group's with theirs. A prefix match would have made the second one
        // depend on the first one's access rule.
        var registry = new GroupRoomRegistry(TimeProvider.System);
        registry.Add("Tuesday group", "a-known-key");

        Assert.True(RelayAccess.Refuses(registry, "/report", "a-stranger-key"));
        Assert.False(RelayAccess.Refuses(registry, "/reports", "a-stranger-key"));
        Assert.False(RelayAccess.Refuses(registry, "/reports/abc123", "a-stranger-key"));
    }

    [Fact]
    public void AMissingOrShortKeyIsLeftToTheHandler()
    {
        // The handler has always answered that with a 401, and it is the more accurate answer:
        // the key is not a key, which is a different thing from the room not being served here.
        var registry = new GroupRoomRegistry(TimeProvider.System);
        registry.Add("Tuesday group", "a-known-key");

        Assert.False(RelayAccess.Refuses(registry, "/state", null));
        Assert.False(RelayAccess.Refuses(registry, "/state", "short"));
    }
}
