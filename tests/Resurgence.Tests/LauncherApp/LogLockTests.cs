using Resurgence.Launcher;

namespace Resurgence.Tests.LauncherApp;

public class LogLockTests
{
    const uint Image = 4194304, Page = 67108864;
    // a synthetic exe: the writer's table at 64 holds its three entries, the rest is zero
    static readonly LogLock Synthetic = new("synthetic", Table: 64, Cleanup: 1000, Write: 2000, Flush: 3000, Lock: 4000, Enter: 5000, Leave: 5004);

    static byte[] Le(params uint[] values) => [.. values.SelectMany(BitConverter.GetBytes)];

    static FakeGame Game()
    {
        var memory = new byte[8192];
        Le(Image + 1000, Image + 2000, Image + 3000).CopyTo(memory, 64);
        return new(Image, memory);
    }

    [Fact]
    public void OnTheExpectedBytesTheTablePointsAtTheTwoFunctionsAndTheGameResumes()
    {
        var game = Game();
        Assert.True(Synthetic.ApplyAndResume(game, null));
        Assert.Equal([Page, Image + 68], game.Writes.Select(w => w.Address));
        Assert.Equal(Synthetic.Code(Image), game.Writes[0].Bytes);
        Assert.Equal(Le(Page, Page + LogLock.FlushAt), game.Writes[1].Bytes);
        Assert.Equal(Le(Image + 1000, Page, Page + 44), game.Read(Image + 64, 12));
        Assert.True(game.Resumed);
    }

    [Theory]
    [InlineData(64)] // the cleanup entry
    [InlineData(68)] // the write entry
    [InlineData(72)] // the flush entry
    public void AnyOtherValueInTheTableWritesNothingAndTheGameStillStarts(int at)
    {
        var game = Game();
        game.Memory[at + 1]++;
        Assert.True(Synthetic.ApplyAndResume(game, null));
        Assert.Empty(game.Writes);
        Assert.Equal(0, game.Allocated);
        Assert.True(game.Resumed);
    }

    [Fact]
    public void AnUnreadableGameOrNoMemoryWritesNothingAndTheGameStillStarts()
    {
        var unknownImage = new FakeGame(0, new byte[8192]);
        var noPage = Game();
        noPage.CanAllocate = false;
        var tableOutside = Synthetic with { Table = 8190 };
        foreach (var (patch, game) in new[] { (Synthetic, unknownImage), (Synthetic, noPage), (tableOutside, Game()) })
        {
            Assert.NotNull(patch.Apply(game));
            Assert.Empty(game.Writes);
            Assert.True(patch.ApplyAndResume(game, null));
            Assert.True(game.Resumed);
        }
    }

    [Fact]
    public void TheFunctionsTakeTheLockAroundTheOriginalEntriesAtAnyBase()
    {
        // push ecx; push lock; call [Enter]; pop ecx; push [esp+8] twice; mov eax, Write; call eax; push eax; push lock; call [Leave]; pop eax; ret 8
        // push ecx; push lock; call [Enter]; pop ecx; mov eax, Flush; call eax; push lock; call [Leave]; ret
        Assert.Equal("5168E0E41801FF1540A1D50059FF742408FF742408B800F25900FFD05068E0E41801FF1550A1D50058C20800"
            + "5168E0E41801FF1540A1D50059B890EF5900FFD068E0E41801FF1550A1D500C3", Convert.ToHexString(LogLock.Known.Code(4194304)));
        Assert.Equal("5168E0E4AD01FF1540A16A0159FF742408FF742408B800F2EE00FFD05068E0E4AD01FF1550A16A0158C20800"
            + "5168E0E4AD01FF1540A16A0159B890EFEE00FFD068E0E4AD01FF1550A16A01C3", Convert.ToHexString(LogLock.Known.Code(13959168)));
        Assert.Equal(LogLock.FlushAt, LogLock.Known.Code(0).Length - 32);
    }

    [Fact]
    public void TheExeIsKnownByItsSha256()
    {
        using var dir = new TempDir("loglock-");
        var exe = Path.Combine(dir.Path, "client.exe");
        File.WriteAllText(exe, "abc");
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("abc"u8)), LogLock.Sha256(exe));
        Assert.Null(LogLock.Sha256(Path.Combine(dir.Path, "missing.exe")));
        Assert.Matches("^[0-9a-f]{64}$", LogLock.Known.ExeSha256);
    }

    sealed class FakeGame(uint image, byte[] memory) : ISuspendedGame
    {
        public byte[] Memory => memory;
        public List<(uint Address, byte[] Bytes)> Writes { get; } = [];
        public bool CanAllocate { get; set; } = true;
        public int Allocated { get; private set; }
        public bool Resumed { get; private set; }
        public uint Image => image;

        public byte[]? Read(uint address, int count) =>
            image != 0 && address >= image && address - image + count <= memory.Length ? memory[(int)(address - image)..][..count] : null;

        public bool Write(uint address, byte[] bytes)
        {
            Writes.Add((address, bytes));
            if (address >= image && address - image + bytes.Length <= memory.Length) bytes.CopyTo(memory, address - image);
            return true;
        }

        public uint Allocate(int size)
        {
            Allocated += size;
            return CanAllocate ? Page : 0;
        }

        public bool Resume() => Resumed = true;
    }
}
