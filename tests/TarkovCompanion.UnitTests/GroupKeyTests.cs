using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The key is the room now, so these are the rules the whole access model rests on.
/// </summary>
public sealed class GroupKeyTests
{
    [Fact]
    public void TheSameKeyIsTheSameRoom()
    {
        Assert.Equal(GroupKey.RoomFor("a-shared-phrase"), GroupKey.RoomFor("a-shared-phrase"));
    }

    [Fact]
    public void ADifferentKeyIsADifferentRoom()
    {
        Assert.NotEqual(GroupKey.RoomFor("a-shared-phrase"), GroupKey.RoomFor("a-shared-phrasf"));
    }

    [Fact]
    public void SurroundingSpaceDoesNotSplitAGroup()
    {
        // Somebody pastes the key out of a chat window and brings a trailing space with it.
        // Without this they are silently alone, which is the exact class of invisible mistake
        // that collapsing two values into one was meant to remove.
        Assert.Equal(GroupKey.RoomFor("a-shared-phrase"), GroupKey.RoomFor("  a-shared-phrase\t"));
    }

    [Fact]
    public void TheRoomDoesNotContainTheKey()
    {
        // The server receives the key on every request, but the room identifier is the thing it
        // stores, logs and hands around, so that identifier must not contain the key.
        var room = GroupKey.RoomFor("correct-horse-battery");

        Assert.DoesNotContain("correct", room, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(32, room.Length);
        Assert.All(room, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("   pad   ")]
    public void AKeyTooShortToProtectAnythingIsRefused(string? key)
    {
        // Not access control: an open relay has no registered room to check a key against. This
        // stops somebody believing that "a" keeps strangers out of their group.
        Assert.False(GroupKey.IsAcceptable(key));
    }

    [Theory]
    [InlineData("eightchr")]
    [InlineData("a much longer phrase that a group might actually agree on")]
    public void AKeyLongEnoughIsAccepted(string key)
    {
        Assert.True(GroupKey.IsAcceptable(key));
    }
}
