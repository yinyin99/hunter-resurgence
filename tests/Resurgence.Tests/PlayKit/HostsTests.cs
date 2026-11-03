using System.Text;
using Play;

namespace Resurgence.Tests.PlayKit;

public class HostsTests
{
    static readonly string[] Names = ["api.example.test", "www.example.test", "example.test"];

    [Theory]
    [InlineData("127.0.0.1 localhost\r\n::1 localhost\r\n")]             // the usual file
    [InlineData("127.0.0.1 localhost\r\n::1 localhost")]                  // no newline after the last line
    [InlineData("127.0.0.1 localhost\n10.0.0.2 nas\n")]                   // LF only
    [InlineData("")]
    [InlineData("# comment\r\n\r\n")]
    public void AddThenRemoveGivesTheOriginalText(string original)
    {
        var added = Hosts.Add(original, Names);
        Assert.True(Hosts.Has(added));
        Assert.Contains("127.0.0.1 api.example.test", added);
        Assert.Contains(Hosts.Begin, added);
        Assert.StartsWith(original.Length == 0 ? Hosts.Begin : original.TrimEnd('\r', '\n'), added);
        Assert.Equal(original, Hosts.Remove(added));
    }

    [Fact]
    public void AddingTwiceKeepsOneBlockAndUsesTheNewNames()
    {
        var once = Hosts.Add("127.0.0.1 localhost\r\n", Names);
        var twice = Hosts.Add(once, ["other.example.test"]);
        Assert.Equal(1, twice.Split(Hosts.Begin).Length - 1);
        Assert.DoesNotContain("api.example.test", twice);
        Assert.Equal("127.0.0.1 localhost\r\n", Hosts.Remove(twice));
    }

    [Fact]
    public void RemoveKeepsEveryLineOutsideTheBlockAndAUsersLineInsideIt()
    {
        var text = "1.1.1.1 before\r\n" + Hosts.Begin + "\r\n127.0.0.1 api.example.test\r\n# mine\r\n" + Hosts.End + "\r\n2.2.2.2 after\r\n";
        Assert.Equal("1.1.1.1 before\r\n# mine\r\n2.2.2.2 after\r\n", Hosts.Remove(text));
    }

    // PL16: the block lost its end line; the user's later 127.0.0.1 lines are not ours
    [Fact]
    public void RemoveOfABlockWithoutEndStopsAtTheFirstLineThatIsNotOurs()
    {
        var text = "# user\r\n" + Hosts.Begin + "\r\n127.0.0.1 a.example.test\r\n# mine\r\n127.0.0.1 localhost\r\n";
        Assert.Equal("# user\r\n# mine\r\n127.0.0.1 localhost\r\n", Hosts.Remove(text));
    }

    // PL15: the UAC elevation hinges on this exception
    [Fact]
    public void ApplyOnAReadOnlyFileThrowsUnauthorizedAndLeavesIt()
    {
        using var dir = new TempDir("play-hosts-");
        var path = Path.Combine(dir.Path, "hosts");
        File.WriteAllText(path, "127.0.0.1 localhost\r\n");
        var before = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Hosts.Apply(path, Names));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public void RemoveWithoutABlockChangesNothing() => Assert.Equal("127.0.0.1 localhost\r\n", Hosts.Remove("127.0.0.1 localhost\r\n"));

    [Fact]
    public void ApplyRoundTripOnAFileIsByteExact()
    {
        using var dir = new TempDir("play-hosts-");
        var path = Path.Combine(dir.Path, "hosts");
        var original = Encoding.UTF8.GetBytes("# Copyright\r\n127.0.0.1 localhost\r\n::1 localhost\r\n");
        File.WriteAllBytes(path, original);

        Assert.True(Hosts.Apply(path, Names));
        Assert.True(Hosts.Has(File.ReadAllText(path)));
        Assert.False(Hosts.Apply(path, Names)); // already there: the file is not written again
        Assert.True(Hosts.Apply(path, null));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(Hosts.Apply(path, null)); // nothing left to remove: no write, so no administrator prompt
    }

    [Fact]
    public void ApplyKeepsABomAndBytesThatAreNotUtf8()
    {
        using var dir = new TempDir("play-hosts-");
        var path = Path.Combine(dir.Path, "hosts");
        byte[] original = [0xEF, 0xBB, 0xBF, .. "# caf"u8, 0xE9, .. "\r\n127.0.0.1 localhost\r\n"u8];
        File.WriteAllBytes(path, original);

        Assert.True(Hosts.Apply(path, Names));
        Assert.Equal(original, File.ReadAllBytes(path)[..original.Length]);
        Assert.True(Hosts.Apply(path, null));
        Assert.Equal(original, File.ReadAllBytes(path));
    }
}
