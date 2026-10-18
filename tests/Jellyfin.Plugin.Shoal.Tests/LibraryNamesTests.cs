using System;
using System.IO;
using Jellyfin.Plugin.Shoal.Visibility;
using Xunit;

namespace Jellyfin.Plugin.Shoal.Tests;

/// <summary>
/// Warden 10 M3: a library name is one plain folder name; a friend-chosen server name is made into one; the rename target is a direct child of the views path.
/// </summary>
public sealed class LibraryNamesTests
{
    [Theory]
    [InlineData("Friends' movies", true)]
    [InlineData("Bob's TV shows", true)]
    [InlineData("../x", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("C:", false)]
    [InlineData("x..y", false)]
    [InlineData(" lead", false)]
    [InlineData("tab\there", false)]
    [InlineData("", false)]
    public void ANameIsSafe_OnlyAsOnePlainSegment(string name, bool safe) => Assert.Equal(safe, LibraryNames.IsSafe(name));

    [Theory]
    [InlineData("../../etc", "etc")]
    [InlineData("Bob/Alice: Server", "Bob Alice Server")]
    [InlineData("...", "Friend")]
    [InlineData("Plain", "Plain")]
    public void AFriendsName_IsMadeSafe(string friend, string expected)
    {
        var name = LibraryNames.FromFriend(friend, "Friend");

        Assert.Equal(expected, name);
        Assert.True(LibraryNames.IsSafe(name));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("lpt9.txt")]
    [InlineData("Aux.movies")]
    public void W11Ib_WindowsReservedNames_AreNotSafe_AndAFriendCalledSoGetsAPrefix(string name)
    {
        Assert.False(LibraryNames.IsSafe(name));
        var made = LibraryNames.FromFriend(name, "Friend");
        Assert.True(LibraryNames.IsSafe(made));
        Assert.StartsWith("Friend", made, StringComparison.Ordinal);
    }

    [Fact]
    public void W11Ib_NamesThatOnlyStartLikeAReservedOne_StaySafe()
    {
        Assert.True(LibraryNames.IsSafe("Conan movies"));
        Assert.True(LibraryNames.IsSafe("COM10"));
        Assert.True(LibraryNames.IsSafe("Null Set"));
    }

    [Fact]
    public void TheRenameTarget_IsADirectChildOfTheRoot_OrRefused()
    {
        using var dir = new TempDirectory();

        Assert.Equal(Path.Combine(Path.GetFullPath(dir.Path), "Pals"), LibraryNames.ChildOf(dir.Path, "Pals"));
        Assert.Throws<InvalidOperationException>(() => LibraryNames.ChildOf(dir.Path, "../Pals"));
        Assert.Throws<InvalidOperationException>(() => LibraryNames.ChildOf(dir.Path, Path.GetFullPath(dir.Path)));
    }
}
